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
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e — fourth real Alpaca-backed device service. Replaces <c>PlaceholderFocuserService</c>:
/// connects to a discovered Alpaca Focuser, serves its capabilities + live runtime
/// (position/moving/temperature/temp-comp) over the §6 REST surface, and moves it via
/// <see cref="MoveAsync"/> (a §60.5 202-Accepted background operation).
///
/// Follows the established template: generation-based supersede; §32.4 background-refresh cache
/// (a timer reads runtime every <see cref="RefreshInterval"/> while Connected). Capabilities are
/// static, so they are read once (lazily) and cached separately from the runtime.
/// REST-only for now — the <c>IFocuserMediator</c> unification (MoveFocuser/autofocus
/// instructions) is the follow-up.
/// </summary>
public sealed partial class FocuserService : IFocuserService, IDisposable {

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly FocuserStateDto IdleRuntime = new("idle", null, null, false);

    private readonly ILogger<FocuserService> _logger;
    private readonly object _gate = new();
    private readonly Timer _refreshTimer;
    private AlpacaFocuser? _client;
    private DiscoveredDeviceDto? _device;
    private EquipmentConnectionState _state = EquipmentConnectionState.Disconnected;
    private FocuserCapabilitiesDto? _capabilities;
    private FocuserStateDto _runtime = IdleRuntime;
    private int _refreshing;
    private long _connectGeneration;
    private bool _disposed;

    public FocuserService(ILogger<FocuserService>? logger = null) {
        _logger = logger ?? NullLogger<FocuserService>.Instance;
        _refreshTimer = new Timer(RefreshTick, state: null, dueTime: RefreshInterval, period: RefreshInterval);
    }

    public Task<FocuserDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_device is null) {
                return Task.FromResult<FocuserDto?>(null);
            }
            var state = _state;
            var connected = state == EquipmentConnectionState.Connected;
            var runtime = connected ? _runtime : IdleRuntime;
            var caps = connected ? _capabilities : null;
            return Task.FromResult<FocuserDto?>(new FocuserDto(_device.UniqueId, _device.Name, state, caps, runtime));
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
                return Task.FromResult(Accepted("focuser.connect", idempotencyKey));
            }
            DisposeClientLocked();
            _device = device;
            generation = ++_connectGeneration;
            SetState(EquipmentConnectionState.Connecting);
        }
        _ = Task.Run(() => ConnectInBackground(device, generation), CancellationToken.None);
        return Task.FromResult(Accepted("focuser.connect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) {
        AlpacaFocuser? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _connectGeneration++;
            client = _client;
            _client = null;
            if (_device is not null) {
                SetState(EquipmentConnectionState.Disconnected);
            }
        }
        if (client is not null) {
            _ = Task.Run(() => SafeDisconnectDispose(client), CancellationToken.None);
        }
        return Task.FromResult(Accepted("focuser.disconnect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> MoveAsync(FocuserMoveRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        AlpacaFocuser? client;
        FocuserCapabilitiesDto? caps;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _state == EquipmentConnectionState.Connected ? _client : null;
            caps = _capabilities;
        }
        if (client is null) {
            throw new InvalidOperationException("focuser is not connected");
        }
        // Validate the target against known capabilities when available; the device validates the
        // bound itself otherwise (ASCOM Move throws InvalidValue out of range).
        var min = caps?.MinPosition ?? 0;
        // Only enforce the upper bound when we have a real (non-zero) max; otherwise let the device
        // validate it. (ReadCapabilities never caches a zero max now, but keep this defensive.)
        if (request.TargetPosition < min || (caps is not null && caps.MaxPosition > 0 && request.TargetPosition > caps.MaxPosition)) {
            throw new ArgumentOutOfRangeException(nameof(request), request.TargetPosition,
                $"TargetPosition is out of range ({min}..{caps?.MaxPosition.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}).");
        }
        // 202 contract: the move runs in the background; GetAsync reports State="moving" until the
        // device settles (the refresh cache picks up IsMoving / Position).
        var useTempComp = request.UseTempComp;
        _ = Task.Run(() => MoveInBackground(client, request.TargetPosition, useTempComp), CancellationToken.None);
        return Task.FromResult(Accepted("focuser.move", idempotencyKey));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background move boundary: the blocking ASCOM Move / TempComp set can throw arbitrary driver/HTTP exceptions; any escape must be contained and logged, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    private void MoveInBackground(AlpacaFocuser client, int target, bool? useTempComp) {
        try {
            if (useTempComp is bool tc) {
                // Best-effort: temperature-compensation behaviour during a manual move is
                // driver-dependent; honour an explicit request where the driver allows it.
                try { client.TempComp = tc; } catch (Exception ex) { LogTempCompIgnored(ex); }
            }
            client.Move(target);
            RefreshCacheOnce(); // surface IsMoving/Position promptly
        } catch (Exception ex) {
            LogMoveFailed(ex, target);
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
            AlpacaFocuser? client;
            bool needCaps;
            lock (_gate) {
                if (_disposed || _state != EquipmentConnectionState.Connected) {
                    return;
                }
                client = _client;
                needCaps = _capabilities is null;
            }
            if (client is null) {
                return;
            }
            var runtime = ReadRuntime(client);
            var caps = needCaps ? ReadCapabilities(client) : null;
            lock (_gate) {
                if (_state == EquipmentConnectionState.Connected && ReferenceEquals(_client, client)) {
                    _runtime = runtime;
                    if (caps is not null) {
                        _capabilities = caps;
                    }
                }
            }
        } catch (Exception ex) {
            LogRuntimeReadFailed(ex);
        } finally {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported/transiently-failing focuser property (e.g. Temperature on a focuser with no probe) throws; that field is reported absent/default rather than failing the whole runtime read. CA1031's log-and-recover boundary applies.")]
    private static FocuserStateDto ReadRuntime(AlpacaFocuser c) {
        bool moving;
        try { moving = c.IsMoving; } catch (Exception) { moving = false; }
        int? position;
        try { position = c.Position; } catch (Exception) { position = null; }
        double? temperature;
        try { temperature = c.Temperature; } catch (Exception) { temperature = null; }
        bool tempComp;
        try { tempComp = c.TempComp; } catch (Exception) { tempComp = false; }
        return new FocuserStateDto(moving ? "moving" : "idle", position, temperature, tempComp);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported capability property throws; a non-essential field falls back to a default, while a failed MaxStep returns null so capabilities are not cached with a bogus zero range. CA1031's log-and-recover boundary applies.")]
    private static FocuserCapabilitiesDto? ReadCapabilities(AlpacaFocuser c) {
        int maxStep;
        // MaxStep is essential — if it fails, return null so we DON'T cache a [0,0] range (which
        // would permanently reject every Move). RefreshCacheOnce leaves _capabilities null and the
        // next tick retries; MoveAsync device-validates the bound until then.
        try { maxStep = c.MaxStep; } catch (Exception) { return null; }
        double stepSize;
        try { stepSize = c.StepSize; } catch (Exception) { stepSize = 0; }
        bool canTempComp;
        try { canTempComp = c.TempCompAvailable; } catch (Exception) { canTempComp = false; }
        bool absolute;
        try { absolute = c.Absolute; } catch (Exception) { absolute = false; }
        return new FocuserCapabilitiesDto(MinPosition: 0, MaxPosition: maxStep, StepSizeUm: stepSize,
            CanTempComp: canTempComp, AbsoluteFocuser: absolute);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background connect boundary: constructing the Alpaca client and setting Connected can throw arbitrary driver/HTTP/socket exceptions; any escape must surface as the Error state and be contained, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of the AlpacaFocuser is managed explicitly: on every non-adopt path SafeDisconnectDispose disposes it; on the adopt path it is stored in _client (local set to null) and disposed later by DisconnectAsync/Dispose. CA2000 cannot follow the transfer through the lock + helper.")]
    private void ConnectInBackground(DiscoveredDeviceDto device, long generation) {
        AlpacaFocuser? client = null;
        var adopted = false;
        try {
            var host = string.IsNullOrWhiteSpace(device.IpAddress) ? device.HostName : device.IpAddress;
            if (string.IsNullOrWhiteSpace(host)) {
                throw new InvalidOperationException(
                    $"discovered device '{device.Name}' carries neither an IP address nor a host name");
            }
            client = new AlpacaFocuser(
                device.UseHttps ? ServiceType.Https : ServiceType.Http,
                host, device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null);
            client.Connected = true;
            lock (_gate) {
                if (!_disposed && _connectGeneration == generation) {
                    _client = client;
                    _capabilities = null;       // re-read for the new device
                    _runtime = IdleRuntime;     // don't serve a prior device's runtime
                    SetState(EquipmentConnectionState.Connected);
                    adopted = true;
                }
            }
            if (!adopted) {
                SafeDisconnectDispose(client);
                return;
            }
            client = null; // ownership transferred to _client
            RefreshCacheOnce(); // seed capabilities + runtime through the guarded path
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
    private void SafeDisconnectDispose(AlpacaFocuser client) {
        try {
            // Interrupt any in-flight Move so a disconnect-during-move doesn't wait out the full
            // ASCOM timeout (the blocking Move on the background task fails fast once halted).
            client.Halt();
        } catch (Exception ex) {
            LogTeardownIgnored(ex);
        }
        try {
            client.Connected = false;
        } catch (Exception ex) {
            LogTeardownIgnored(ex);
        }
        DisposeQuietly(client);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort teardown: ASCOM/COM-backed clients have been known to throw from Dispose(); the throw must be swallowed (and logged) rather than escape into a fire-and-forget Task.Run as an unobserved exception. CA1031's log-and-recover boundary applies.")]
    private void DisposeQuietly(AlpacaFocuser client) {
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
        _state = state;
    }

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    public void Dispose() {
        AlpacaFocuser? client;
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
        // "Connected = false" is a blocking HTTP call (up to ~3s ASCOM timeout) that would hang
        // container shutdown if the device is unreachable. DisposeQuietly releases resources only.
        if (client is not null) {
            DisposeQuietly(client);
        }
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Focuser runtime read failed")]
    private partial void LogRuntimeReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Focuser move to {Target} failed")]
    private partial void LogMoveFailed(Exception ex, int target);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Focuser TempComp set ignored")]
    private partial void LogTempCompIgnored(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Focuser connected: {Name} at {Host}:{Port}/{Device}")]
    private partial void LogConnected(string name, string host, int port, int device);

    [LoggerMessage(Level = LogLevel.Error, Message = "Focuser connect failed for {Name}")]
    private partial void LogConnectFailed(Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ignored error while disconnecting Focuser during teardown")]
    private partial void LogTeardownIgnored(Exception ex);
}
