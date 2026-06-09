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
/// §14e — third real Alpaca-backed device service and the first with a control action.
/// Replaces <c>PlaceholderSwitchService</c>: connects to a discovered Alpaca Switch device,
/// serves its live ports (id/name/value/min/max/can-write) over the §6 REST surface, and writes
/// a port value via <see cref="SetValueAsync"/>.
///
/// Follows the SafetyMonitor/ObservingConditions template: §60.5 202-Accepted connect lifecycle;
/// generation-based supersede; §32.4 background-refresh cache (a timer reads every port every
/// <see cref="RefreshInterval"/> while Connected) so <see cref="GetAsync"/> serves the cached port
/// list synchronously. A write refreshes the cache so the new value is reflected promptly.
///
/// REST-only for now: unifying with the Sequencer's <c>ISwitchMediator</c> (so the
/// <c>SetSwitchValue</c> sequence instruction drives the live device) is the follow-up increment.
/// </summary>
public sealed partial class SwitchService : ISwitchService, IDisposable {

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly ILogger<SwitchService> _logger;
    private readonly object _gate = new();
    private readonly Timer _refreshTimer;
    private AlpacaSwitch? _client;
    private DiscoveredDeviceDto? _device;
    private EquipmentConnectionState _state = EquipmentConnectionState.Disconnected;
    private DateTimeOffset _lastTransition = DateTimeOffset.UtcNow;
    private IReadOnlyList<SwitchPortDto> _cachedPorts = Array.Empty<SwitchPortDto>();
    private int _refreshing;
    private long _connectGeneration;
    private bool _disposed;

    public SwitchService(ILogger<SwitchService>? logger = null) {
        _logger = logger ?? NullLogger<SwitchService>.Instance;
        _refreshTimer = new Timer(RefreshTick, state: null, dueTime: RefreshInterval, period: RefreshInterval);
    }

    // ct unused by design: the ports are served from the §32.4 cache, so there is no per-call
    // HTTP read to cancel. Returns a Task to satisfy the async interface without an await.
    public Task<SwitchDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_device is null) {
                return Task.FromResult<SwitchDto?>(null);
            }
            var state = _state;
            var ports = state == EquipmentConnectionState.Connected ? _cachedPorts : Array.Empty<SwitchPortDto>();
            return Task.FromResult<SwitchDto?>(new SwitchDto(_device.UniqueId, _device.Name, state, ports));
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
                return Task.FromResult(Accepted("switch.connect", idempotencyKey));
            }
            DisposeClientLocked();
            _device = device;
            generation = ++_connectGeneration;
            SetState(EquipmentConnectionState.Connecting);
        }
        _ = Task.Run(() => ConnectInBackground(device, generation), CancellationToken.None);
        return Task.FromResult(Accepted("switch.connect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) {
        AlpacaSwitch? client;
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
        return Task.FromResult(Accepted("switch.disconnect", idempotencyKey));
    }

    public async Task SetValueAsync(SwitchValueRequestDto request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        AlpacaSwitch? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _state == EquipmentConnectionState.Connected ? _client : null;
        }
        if (client is null) {
            throw new InvalidOperationException("switch is not connected");
        }
        // Write off the request thread (blocking ASCOM HTTP PUT). The port id is a short in ASCOM.
        await Task.Run(() => client.SetSwitchValue((short)request.PortId, request.Value), ct).ConfigureAwait(false);
        // Reflect the new value promptly (best-effort; the next timer tick would also pick it up).
        RefreshCacheOnce();
    }

    private void RefreshTick(object? state) => RefreshCacheOnce();

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Timer-callback boundary: an unhandled exception in a System.Threading.Timer callback crashes the process. The per-port reads already absorb device failures; this catch-all is a hard backstop so a refresh tick can never fault the daemon. CA1031's log-and-recover boundary applies.")]
    private void RefreshCacheOnce() {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) {
            return;
        }
        try {
            AlpacaSwitch? client;
            lock (_gate) {
                if (_disposed || _state != EquipmentConnectionState.Connected) {
                    return;
                }
                client = _client;
            }
            if (client is null) {
                return;
            }
            var ports = ReadPorts(client);
            lock (_gate) {
                if (_state == EquipmentConnectionState.Connected && ReferenceEquals(_client, client)) {
                    _cachedPorts = ports;
                }
            }
        } catch (Exception ex) {
            LogPortReadFailed(ex);
        } finally {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    // Reads MaxSwitch then each port independently: a port that fails to read is skipped rather
    // than failing the whole snapshot.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-port read boundary: a switch port read can throw driver/HTTP exceptions; that port is skipped, never propagated. CA1031's log-and-recover boundary applies.")]
    private IReadOnlyList<SwitchPortDto> ReadPorts(AlpacaSwitch c) {
        short max;
        try {
            max = c.MaxSwitch;
        } catch (Exception ex) {
            LogPortReadFailed(ex);
            return Array.Empty<SwitchPortDto>();
        }
        var ports = new List<SwitchPortDto>(max);
        for (short i = 0; i < max; i++) {
            try {
                ports.Add(new SwitchPortDto(
                    Id: i,
                    Name: c.GetSwitchName(i),
                    Value: c.GetSwitchValue(i),
                    Min: c.MinSwitchValue(i),
                    Max: c.MaxSwitchValue(i),
                    CanWrite: c.CanWrite(i)));
            } catch (Exception ex) {
                LogPortUnavailable(i, ex);
            }
        }
        return ports;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background connect boundary: constructing the Alpaca client and setting Connected can throw arbitrary driver/HTTP/socket exceptions; any escape must surface as the Error state and be contained, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of the AlpacaSwitch is managed explicitly: on every non-adopt path SafeDisconnectDispose disposes it; on the adopt path it is stored in _client (local set to null) and disposed later by DisconnectAsync/Dispose. CA2000 cannot follow the transfer through the lock + helper.")]
    private void ConnectInBackground(DiscoveredDeviceDto device, long generation) {
        AlpacaSwitch? client = null;
        var adopted = false;
        try {
            var host = string.IsNullOrWhiteSpace(device.IpAddress) ? device.HostName : device.IpAddress;
            if (string.IsNullOrWhiteSpace(host)) {
                throw new InvalidOperationException(
                    $"discovered device '{device.Name}' carries neither an IP address nor a host name");
            }
            client = new AlpacaSwitch(
                device.UseHttps ? ServiceType.Https : ServiceType.Http,
                host, device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null);
            client.Connected = true;
            lock (_gate) {
                if (!_disposed && _connectGeneration == generation) {
                    _client = client;
                    _cachedPorts = Array.Empty<SwitchPortDto>(); // don't serve a prior device's ports
                    SetState(EquipmentConnectionState.Connected);
                    adopted = true;
                }
            }
            if (!adopted) {
                SafeDisconnectDispose(client);
                return;
            }
            client = null; // ownership transferred to _client
            RefreshCacheOnce(); // seed the port cache through the guarded path
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
    private void SafeDisconnectDispose(AlpacaSwitch client) {
        try {
            client.Connected = false;
        } catch (Exception ex) {
            LogTeardownIgnored(ex);
        }
        DisposeQuietly(client);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort teardown: ASCOM/COM-backed clients have been known to throw from Dispose(); the throw must be swallowed (and logged) rather than escape into a fire-and-forget Task.Run as an unobserved exception. CA1031's log-and-recover boundary applies.")]
    private void DisposeQuietly(AlpacaSwitch client) {
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
        _lastTransition = DateTimeOffset.UtcNow;
    }

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    public void Dispose() {
        AlpacaSwitch? client;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Switch port read failed")]
    private partial void LogPortReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Switch port {Port} unavailable (skipped)")]
    private partial void LogPortUnavailable(int port, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Switch connected: {Name} at {Host}:{Port}/{Device}")]
    private partial void LogConnected(string name, string host, int port, int device);

    [LoggerMessage(Level = LogLevel.Error, Message = "Switch connect failed for {Name}")]
    private partial void LogConnectFailed(Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ignored error while disconnecting Switch during teardown")]
    private partial void LogTeardownIgnored(Exception ex);
}
