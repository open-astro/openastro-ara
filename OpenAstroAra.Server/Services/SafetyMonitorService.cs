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
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// First real Alpaca-backed equipment service (§14e). Replaces
/// <c>PlaceholderSafetyMonitorService</c>: connects to a discovered Alpaca
/// SafetyMonitor (the host/port/device-number carried by the
/// <see cref="DiscoveredDeviceDto"/> from <see cref="AlpacaEquipmentDiscoveryService"/>)
/// and exposes its live connection state + <c>IsSafe</c> over the §6 REST surface.
///
/// Connection lifecycle follows the §60.5 202-Accepted contract: <see cref="ConnectAsync"/>
/// transitions to <see cref="EquipmentConnectionState.Connecting"/> and does the actual
/// Alpaca connect on a background task, so the endpoint returns immediately; callers poll
/// <see cref="GetAsync"/> to observe <see cref="EquipmentConnectionState.Connected"/> /
/// <see cref="EquipmentConnectionState.Error"/>. The ASCOM Alpaca client calls
/// (<c>Connected</c>, <c>IsSafe</c>) are blocking HTTP, so they run off the request thread.
///
/// This wires the REST (<see cref="ISafetyMonitorService"/>) surface only. Unifying this
/// with the Sequencer's <c>ISafetyMonitorMediator</c> (so <c>WaitUntilSafe</c> sees the live
/// device) is the next increment — tracked in design/PORT_TODO.md.
/// </summary>
public sealed class SafetyMonitorService : ISafetyMonitorService, IDisposable {

    private readonly ILogger<SafetyMonitorService>? _logger;
    private readonly object _gate = new();
    private AlpacaSafetyMonitor? _client;
    private DiscoveredDeviceDto? _device;
    private EquipmentConnectionState _state = EquipmentConnectionState.Disconnected;
    private DateTimeOffset _lastTransition = DateTimeOffset.UtcNow;
    private bool _disposed;

    public SafetyMonitorService(ILogger<SafetyMonitorService>? logger = null) {
        _logger = logger;
    }

    public async Task<SafetyMonitorDto?> GetAsync(CancellationToken ct) {
        AlpacaSafetyMonitor? client;
        DiscoveredDeviceDto? device;
        EquipmentConnectionState state;
        lock (_gate) {
            client = _client;
            device = _device;
            state = _state;
        }
        if (device is null) {
            // No device has ever been selected — GET maps this to 404 at the endpoint.
            return null;
        }
        var safe = false;
        if (client is not null && state == EquipmentConnectionState.Connected) {
            safe = await Task.Run(() => ReadIsSafe(client), ct).ConfigureAwait(false);
            lock (_gate) {
                state = _state; // ReadIsSafe may have demoted to Error
            }
        }
        string transition;
        lock (_gate) {
            transition = _lastTransition.ToString("O", CultureInfo.InvariantCulture);
        }
        return new SafetyMonitorDto(device.UniqueId, device.Name, state, safe, transition);
    }

    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        var device = request.Device;
        lock (_gate) {
            // Idempotent: connecting to the same device while already connecting/connected
            // is a no-op accept (§60.5) — don't tear down a good connection.
            if ((_state == EquipmentConnectionState.Connecting || _state == EquipmentConnectionState.Connected)
                && _device?.UniqueId == device.UniqueId) {
                return Task.FromResult(Accepted("safety-monitor.connect", idempotencyKey));
            }
            DisposeClientLocked();
            _device = device;
            SetState(EquipmentConnectionState.Connecting);
        }
        // 202 contract: do the blocking Alpaca connect off-thread; GetAsync reports the outcome.
        _ = Task.Run(() => ConnectInBackground(device));
        return Task.FromResult(Accepted("safety-monitor.connect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) {
        AlpacaSafetyMonitor? client;
        lock (_gate) {
            client = _client;
            _client = null;
            if (_device is not null) {
                SetState(EquipmentConnectionState.Disconnected);
            }
            // A connect still in flight (Connecting, _client null) will observe the state
            // change on completion and dispose its own client (supersede check below).
        }
        if (client is not null) {
            _ = Task.Run(() => SafeDisconnectDispose(client));
        }
        return Task.FromResult(Accepted("safety-monitor.disconnect", idempotencyKey));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Device-read boundary: an Alpaca IsSafe call can throw arbitrary driver/HTTP exceptions; any escape must demote the connection to Error and be contained, not fault the GET. CA1031's log-and-recover boundary applies.")]
    private bool ReadIsSafe(AlpacaSafetyMonitor client) {
        try {
            return client.IsSafe;
        } catch (Exception ex) {
            SetState(EquipmentConnectionState.Error);
            _logger?.LogWarning(ex, "SafetyMonitor IsSafe read failed; marking connection Error");
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background connect boundary: constructing the Alpaca client and setting Connected can throw arbitrary driver/HTTP/socket exceptions; any escape must surface as the Error state and be contained, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    private void ConnectInBackground(DiscoveredDeviceDto device) {
        AlpacaSafetyMonitor? client = null;
        try {
            var host = string.IsNullOrWhiteSpace(device.IpAddress) ? device.HostName : device.IpAddress;
            client = new AlpacaSafetyMonitor(
                device.UseHttps ? ServiceType.Https : ServiceType.Http,
                host, device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null);
            client.Connected = true;
            if (!client.Connected) {
                throw new InvalidOperationException("device reported not connected after setting Connected = true");
            }
            var superseded = false;
            lock (_gate) {
                // Only adopt this connection if it is still the one we want — a disconnect or a
                // newer connect may have superseded it while the blocking connect was running.
                if (_state == EquipmentConnectionState.Connecting && _device?.UniqueId == device.UniqueId) {
                    _client = client;
                    SetState(EquipmentConnectionState.Connected);
                } else {
                    superseded = true;
                }
            }
            if (superseded) {
                SafeDisconnectDispose(client);
                return;
            }
            client = null; // ownership transferred to _client
            _logger?.LogInformation("SafetyMonitor connected: {Name} at {Host}:{Port}/{Device}",
                device.Name, host, device.IpPort, device.AlpacaDeviceNumber);
        } catch (Exception ex) {
            if (client is not null) {
                SafeDisconnectDispose(client);
            }
            lock (_gate) {
                // Only demote to Error if this attempt is still the current one.
                if (_state == EquipmentConnectionState.Connecting && _device?.UniqueId == device.UniqueId) {
                    _client = null;
                    SetState(EquipmentConnectionState.Error);
                }
            }
            _logger?.LogError(ex, "SafetyMonitor connect failed for {Name}", device.Name);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort teardown: Connected = false can throw driver/HTTP exceptions, which must be swallowed so Dispose always runs and cleanup completes. CA1031's log-and-recover boundary applies.")]
    private void SafeDisconnectDispose(AlpacaSafetyMonitor client) {
        try {
            client.Connected = false;
        } catch (Exception ex) {
            _logger?.LogDebug(ex, "ignored error while disconnecting SafetyMonitor during teardown");
        }
        client.Dispose();
    }

    // Caller holds _gate; offloads the blocking disconnect/dispose so we never do I/O under the lock.
    private void DisposeClientLocked() {
        var c = _client;
        _client = null;
        if (c is not null) {
            _ = Task.Run(() => SafeDisconnectDispose(c));
        }
    }

    // lock is reentrant, so this is safe to call whether or not the caller already holds _gate.
    private void SetState(EquipmentConnectionState state) {
        lock (_gate) {
            _state = state;
            _lastTransition = DateTimeOffset.UtcNow;
        }
    }

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    public void Dispose() {
        AlpacaSafetyMonitor? client;
        lock (_gate) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            client = _client;
            _client = null;
        }
        if (client is not null) {
            SafeDisconnectDispose(client); // synchronous on dispose so teardown completes before exit
        }
    }
}
