#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Alpaca.Clients;
using ASCOM.Common.Alpaca;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e — sixth real Alpaca-backed device service. Replaces <c>PlaceholderFilterWheelService</c>:
/// connects to a discovered Alpaca FilterWheel, serves its slots (position/name/focus-offset, read
/// once) + live runtime (state + current slot, §32.4-cached) over the §6 REST surface, and selects
/// a slot via <see cref="ChangeFilterAsync"/> (a §60.5 202-Accepted background operation).
/// Mirrors the established control-device template. REST-only — the <c>IFilterWheelMediator</c>
/// unification (SwitchFilter instruction) is the follow-up.
/// </summary>
public sealed partial class FilterWheelService : IFilterWheelService, IDisposable {

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly FilterWheelStateDto IdleRuntime = new("idle", null);

    private readonly ILogger<FilterWheelService> _logger;
    private readonly EquipmentEventPublisher? _events;
    private readonly object _gate = new();
    private readonly Timer _refreshTimer;
    // The Sequencer-facing profile (null in REST-only unit tests): the §14e mediator partial imports
    // the connected wheel's slot list into ActiveProfile.FilterWheelSettings.FilterWheelFilters
    // (NINA's import-on-connect semantics) so SwitchFilter can resolve its filter by name/position.
    private readonly OpenAstroAra.Profile.Interfaces.IProfileService? _profileService;
    private AlpacaFilterWheel? _client;
    private DiscoveredDeviceDto? _device;
    private EquipmentConnectionState _state = EquipmentConnectionState.Disconnected;
    private IReadOnlyList<FilterSlotDto>? _slots; // configured names/offsets, read once on connect
    private FilterWheelStateDto _runtime = IdleRuntime;
    private int _refreshing;
    private long _connectGeneration;
    private bool _disposed;

    public FilterWheelService(
        ILogger<FilterWheelService>? logger = null,
        OpenAstroAra.Profile.Interfaces.IProfileService? profileService = null,
        EquipmentEventPublisher? events = null) {
        _logger = logger ?? NullLogger<FilterWheelService>.Instance;
        _events = events;
        _profileService = profileService;
        _refreshTimer = new Timer(RefreshTick, state: null, dueTime: RefreshInterval, period: RefreshInterval);
    }

    public Task<FilterWheelDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // null ONLY when no device has ever been selected (-> 404); a retained device after
            // disconnect returns Disconnected + idle runtime, consistent across the device services.
            if (_device is null) {
                return Task.FromResult<FilterWheelDto?>(null);
            }
            var connected = _state == EquipmentConnectionState.Connected;
            var runtime = connected ? _runtime : IdleRuntime;
            var slots = (connected ? _slots : null) ?? Array.Empty<FilterSlotDto>();
            return Task.FromResult<FilterWheelDto?>(new FilterWheelDto(_device.UniqueId, _device.Name, _state, runtime, slots));
        }
    }

    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Device);
        var device = request.Device;
        long generation;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((_state == EquipmentConnectionState.Connecting || _state == EquipmentConnectionState.Connected)
                && _device?.UniqueId == device.UniqueId) {
                return Task.FromResult(Accepted("filter-wheel.connect", idempotencyKey));
            }
            DisposeClientLocked();
            _device = device;
            generation = ++_connectGeneration;
            SetState(EquipmentConnectionState.Connecting);
        }
        _ = Task.Run(() => ConnectInBackground(device, generation), CancellationToken.None);
        return Task.FromResult(Accepted("filter-wheel.connect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) {
        AlpacaFilterWheel? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _connectGeneration++;
            client = _client;
            _client = null;
            // Clear the slot list too: it must not outlive the connection, so a ChangeFilterAsync
            // while disconnected validates against the live (absent) slots and reports "not
            // connected" rather than an ArgumentOutOfRange against a prior session's slots.
            _slots = null;
            if (_device is not null) {
                SetState(EquipmentConnectionState.Disconnected);
            }
        }
        if (client is not null) {
            _ = Task.Run(() => SafeDisconnectDispose(client), CancellationToken.None);
        }
        return Task.FromResult(Accepted("filter-wheel.disconnect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> ChangeFilterAsync(FilterChangeRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        // Dispose -> range -> connected ordering, aligned with the other device services.
        AlpacaFilterWheel? client;
        IReadOnlyList<FilterSlotDto>? slots;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _state == EquipmentConnectionState.Connected ? _client : null;
            slots = _slots;
        }
        // ASCOM addresses slots by a short; validate before the narrowing cast so an out-of-range
        // position fails loudly (with a 4xx) instead of silently wrapping (e.g. (short)32768 ==
        // -32768) and getting a 202 for a logically-invalid request. This also covers the window
        // before _slots is loaded, where IsPositionOutOfRange defers to the device.
        if (request.Position is < 0 or > short.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(request), request.Position,
                "Position is out of range for an ASCOM FilterWheel (0..32767).");
        }
        if (IsPositionOutOfRange(slots, request.Position)) {
            throw new ArgumentOutOfRangeException(nameof(request), request.Position,
                $"Position is out of range (0..{(slots!.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}).");
        }
        if (client is null) {
            throw new InvalidOperationException("filter wheel is not connected");
        }
        var position = request.Position;
        _ = Task.Run(() => ChangeInBackground(client, position), CancellationToken.None);
        return Task.FromResult(Accepted("filter-wheel.change", idempotencyKey));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background change boundary: setting the blocking ASCOM Position can throw arbitrary driver/HTTP exceptions, and a concurrent Disconnect/Dispose can dispose the captured client before/during the change; any escape must be contained and logged, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    private void ChangeInBackground(AlpacaFilterWheel client, int position) {
        try {
            client.Position = (short)position; // triggers the wheel to rotate; Position reads -1 while moving
            RefreshCacheOnce();
        } catch (Exception ex) {
            LogChangeFailed(ex, position);
        }
    }

    private void RefreshTick(object? state) => RefreshCacheOnce();

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Timer-callback boundary: an unhandled exception in a System.Threading.Timer callback crashes the process. The per-field reads already absorb device failures; this catch-all is a hard backstop so a refresh tick can never fault the daemon. CA1031's log-and-recover boundary applies.")]
    private void RefreshCacheOnce() {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) {
            return;
        }
        try {
            AlpacaFilterWheel? client;
            bool needSlots;
            lock (_gate) {
                if (_disposed || _state != EquipmentConnectionState.Connected) {
                    return;
                }
                client = _client;
                needSlots = _slots is null;
            }
            if (client is null) {
                return;
            }
            var runtime = ReadRuntime(client);
            var slots = needSlots ? ReadSlots(client) : null;
            var adoptedSlots = false;
            lock (_gate) {
                if (_state == EquipmentConnectionState.Connected && ReferenceEquals(_client, client)) {
                    _runtime = runtime;
                    if (slots is not null) {
                        _slots = slots;
                        adoptedSlots = true;
                    }
                }
            }
            if (adoptedSlots) {
                // First slot read for this connection: import the device's filter list into the
                // active profile (outside the lock — it walks an observable collection).
                ImportProfileFilters(slots!);
            }
        } catch (Exception ex) {
            LogRuntimeReadFailed(ex);
        } finally {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a transiently-failing Position read reports idle (and logs at Debug) rather than failing the snapshot. CA1031's log-and-recover boundary applies.")]
    private FilterWheelStateDto ReadRuntime(AlpacaFilterWheel c) {
        short pos;
        // Log at Debug, not silently: a persistent Position fault would otherwise present as a
        // steady "idle" with no diagnostic trail.
        try { pos = c.Position; } catch (Exception ex) { LogPositionReadFailed(ex); return IdleRuntime; }
        // ASCOM reports Position == -1 while the wheel is moving between slots.
        return pos < 0 ? new FilterWheelStateDto("moving", null) : new FilterWheelStateDto("idle", pos);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Slots read boundary: if Names fails, return null so we DON'T cache an empty/partial slot list (the next tick retries). CA1031's log-and-recover boundary applies.")]
    private List<FilterSlotDto>? ReadSlots(AlpacaFilterWheel c) {
        string[] names;
        try { names = c.Names; } catch (Exception) { return null; }
        if (names is null) {
            return null;
        }
        int[] offsets;
        // Log at Debug: unlike Names (which returns null and retries), a FocusOffsets failure
        // persists (offsets are read-once), so a driver that doesn't implement them would silently
        // show all-zero offsets forever with no diagnostic trail.
        try { offsets = c.FocusOffsets; } catch (Exception ex) { LogFocusOffsetsFailed(ex); offsets = Array.Empty<int>(); }
        var slots = new List<FilterSlotDto>(names.Length);
        for (var i = 0; i < names.Length; i++) {
            var offset = i < (offsets?.Length ?? 0) ? offsets![i] : 0;
            slots.Add(new FilterSlotDto(Position: i, Name: names[i] ?? string.Empty, FocusOffset: offset));
        }
        return slots;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background connect boundary: constructing the Alpaca client and setting Connected can throw arbitrary driver/HTTP/socket exceptions; any escape must surface as the Error state and be contained, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of the AlpacaFilterWheel is managed explicitly: on every non-adopt path SafeDisconnectDispose disposes it; on the adopt path it is stored in _client (local set to null) and disposed later by DisconnectAsync/Dispose. CA2000 cannot follow the transfer through the lock + helper.")]
    private void ConnectInBackground(DiscoveredDeviceDto device, long generation) {
        AlpacaFilterWheel? client = null;
        var adopted = false;
        try {
            var host = string.IsNullOrWhiteSpace(device.IpAddress) ? device.HostName : device.IpAddress;
            if (string.IsNullOrWhiteSpace(host)) {
                throw new InvalidOperationException(
                    $"discovered device '{device.Name}' carries neither an IP address nor a host name");
            }
            client = new AlpacaFilterWheel(
                device.UseHttps ? ServiceType.Https : ServiceType.Http,
                host, device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null);
            client.Connected = true;
            lock (_gate) {
                if (!_disposed && _connectGeneration == generation) {
                    _client = client;
                    _slots = null;          // re-read for the new device
                    _runtime = IdleRuntime; // don't serve a prior device's runtime
                    SetState(EquipmentConnectionState.Connected);
                    adopted = true;
                }
            }
            if (!adopted) {
                SafeDisconnectDispose(client);
                return;
            }
            client = null; // ownership transferred to _client
            RefreshCacheOnce(); // seed slots + runtime through the guarded path
            bool stillConnected;
            lock (_gate) {
                stillConnected = _state == EquipmentConnectionState.Connected;
            }
            if (stillConnected) {
                LogConnected(device.Name, host, device.IpPort, device.AlpacaDeviceNumber);
            }
        } catch (Exception ex) {
            if (!adopted) {
                if (client is not null) {
                    SafeDisconnectDispose(client);
                }
                lock (_gate) {
                    if (!_disposed && _connectGeneration == generation) {
                        SetState(EquipmentConnectionState.Error);
                    }
                }
                LogConnectFailed(ex, device.Name);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort teardown: Connected = false can throw driver/HTTP exceptions, which must be swallowed so Dispose always runs and cleanup completes. CA1031's log-and-recover boundary applies.")]
    private void SafeDisconnectDispose(AlpacaFilterWheel client) {
        try {
            client.Connected = false;
        } catch (Exception ex) {
            LogTeardownIgnored(ex);
        }
        DisposeQuietly(client);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort teardown: ASCOM/COM-backed clients have been known to throw from Dispose(); the throw must be swallowed (and logged) rather than escape into a fire-and-forget Task.Run as an unobserved exception. CA1031's log-and-recover boundary applies.")]
    private void DisposeQuietly(AlpacaFilterWheel client) {
        try {
            client.Dispose();
        } catch (Exception ex) {
            LogTeardownIgnored(ex);
        }
    }

    private void DisposeClientLocked() {
        var c = _client;
        _client = null;
        if (c is not null) {
            _ = Task.Run(() => SafeDisconnectDispose(c), CancellationToken.None);
        }
    }

    // Caller must hold _gate (every call site already does), so no inner lock here.
    private void SetState(EquipmentConnectionState state) {
        if (_state == state) {
            return;
        }
        _state = state;
        // Callers hold the service lock; the publisher's synchronous part only
        // serializes a small payload and hands off (see EquipmentEventPublisher).
        _events?.StateChanged(DeviceType.FilterWheel, _device?.UniqueId, _device?.Name, state);
    }

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    // Extracted (internal) for direct unit testing. A position is out of range only when the slot
    // list is known and non-empty; otherwise the device validates the bound.
    internal static bool IsPositionOutOfRange(IReadOnlyList<FilterSlotDto>? slots, int position) =>
        slots is not null && slots.Count > 0 && (position < 0 || position >= slots.Count);

    public void Dispose() {
        AlpacaFilterWheel? client;
        lock (_gate) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            client = _client;
            _client = null;
        }
        _refreshTimer.Dispose();
        // Dispose the client directly (guarded), not via SafeDisconnectDispose: the courtesy
        // Connected=false is a blocking HTTP call (up to ~3s ASCOM timeout) that would hang
        // container shutdown if the device is unreachable. DisposeQuietly releases resources only.
        if (client is not null) {
            DisposeQuietly(client);
        }
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "FilterWheel runtime read failed")]
    private partial void LogRuntimeReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "FilterWheel Position read failed (reported idle)")]
    private partial void LogPositionReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "FilterWheel FocusOffsets read failed (offsets default to 0)")]
    private partial void LogFocusOffsetsFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "FilterWheel change to slot {Position} failed")]
    private partial void LogChangeFailed(Exception ex, int position);

    [LoggerMessage(Level = LogLevel.Information, Message = "FilterWheel connected: {Name} at {Host}:{Port}/{Device}")]
    private partial void LogConnected(string name, string host, int port, int device);

    [LoggerMessage(Level = LogLevel.Error, Message = "FilterWheel connect failed for {Name}")]
    private partial void LogConnectFailed(Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ignored error while disconnecting FilterWheel during teardown")]
    private partial void LogTeardownIgnored(Exception ex);
}
