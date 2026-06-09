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
/// §14e — fifth real Alpaca-backed device service. Replaces <c>PlaceholderRotatorService</c>:
/// connects to a discovered Alpaca Rotator, serves its live runtime (state + mechanical/sky angle)
/// over the §6 REST surface, and rotates it via <see cref="MoveAsync"/> (a §60.5 202-Accepted
/// background operation). Follows the established template (generation supersede, §32.4
/// background-refresh cache, Halt-before-disconnect). REST-only — the <c>IRotatorMediator</c>
/// unification is the follow-up.
/// </summary>
public sealed partial class RotatorService : IRotatorService, IDisposable {

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly RotatorStateDto IdleRuntime = new("idle", null, null);

    private readonly ILogger<RotatorService> _logger;
    private readonly object _gate = new();
    private readonly Timer _refreshTimer;
    private AlpacaRotator? _client;
    private DiscoveredDeviceDto? _device;
    private EquipmentConnectionState _state = EquipmentConnectionState.Disconnected;
    private RotatorStateDto _runtime = IdleRuntime;
    private int _refreshing;
    private long _connectGeneration;
    private bool _disposed;

    public RotatorService(ILogger<RotatorService>? logger = null) {
        _logger = logger ?? NullLogger<RotatorService>.Instance;
        _refreshTimer = new Timer(RefreshTick, state: null, dueTime: RefreshInterval, period: RefreshInterval);
    }

    public Task<RotatorDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // null ONLY when no device has ever been selected (-> 404). After a disconnect the
            // device is retained and the DTO is returned with Disconnected state + idle runtime
            // ("device selected but not connected"), consistent across all the device services.
            if (_device is null) {
                return Task.FromResult<RotatorDto?>(null);
            }
            var state = _state;
            var runtime = state == EquipmentConnectionState.Connected ? _runtime : IdleRuntime;
            return Task.FromResult<RotatorDto?>(new RotatorDto(_device.UniqueId, _device.Name, state, runtime));
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
                return Task.FromResult(Accepted("rotator.connect", idempotencyKey));
            }
            DisposeClientLocked();
            _device = device;
            generation = ++_connectGeneration;
            SetState(EquipmentConnectionState.Connecting);
        }
        _ = Task.Run(() => ConnectInBackground(device, generation), CancellationToken.None);
        return Task.FromResult(Accepted("rotator.connect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) {
        AlpacaRotator? client;
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
        return Task.FromResult(Accepted("rotator.disconnect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> MoveAsync(RotatorMoveRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        if (IsAngleOutOfRange(request.TargetAngleDeg)) {
            throw new ArgumentOutOfRangeException(nameof(request), request.TargetAngleDeg,
                "TargetAngleDeg must be in [0, 360).");
        }
        AlpacaRotator? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _state == EquipmentConnectionState.Connected ? _client : null;
        }
        if (client is null) {
            throw new InvalidOperationException("rotator is not connected");
        }
        var useSkyAngle = request.UseSkyAngle;
        // ASCOM rotator Move APIs take a float; the cast from the DTO's double loses ~1e-5° at most,
        // negligible for a rotator angle.
        var target = (float)request.TargetAngleDeg;
        _ = Task.Run(() => MoveInBackground(client, target, useSkyAngle), CancellationToken.None);
        return Task.FromResult(Accepted("rotator.move", idempotencyKey));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background move boundary: the blocking ASCOM MoveAbsolute/MoveMechanical can throw arbitrary driver/HTTP exceptions, and a concurrent Disconnect/Dispose can dispose the captured client before/during the move; any escape must be contained and logged, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    private void MoveInBackground(AlpacaRotator client, float target, bool useSkyAngle) {
        try {
            // UseSkyAngle moves to the offset-corrected sky angle (Position); otherwise to the raw
            // mechanical angle (MechanicalPosition).
            if (useSkyAngle) {
                client.MoveAbsolute(target);
            } else {
                client.MoveMechanical(target);
            }
            RefreshCacheOnce();
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
            AlpacaRotator? client;
            lock (_gate) {
                if (_disposed || _state != EquipmentConnectionState.Connected) {
                    return;
                }
                client = _client;
            }
            if (client is null) {
                return;
            }
            var runtime = ReadRuntime(client);
            lock (_gate) {
                if (_state == EquipmentConnectionState.Connected && ReferenceEquals(_client, client)) {
                    _runtime = runtime;
                }
            }
        } catch (Exception ex) {
            LogRuntimeReadFailed(ex);
        } finally {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported/transiently-failing rotator property throws; that field is reported null rather than failing the whole runtime read. CA1031's log-and-recover boundary applies.")]
    private static RotatorStateDto ReadRuntime(AlpacaRotator c) {
        bool moving;
        try { moving = c.IsMoving; } catch (Exception) { moving = false; }
        double? mechanical;
        try { mechanical = c.MechanicalPosition; } catch (Exception) { mechanical = null; }
        double? sky;
        try { sky = c.Position; } catch (Exception) { sky = null; }
        return new RotatorStateDto(moving ? "moving" : "idle", mechanical, sky);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background connect boundary: constructing the Alpaca client and setting Connected can throw arbitrary driver/HTTP/socket exceptions; any escape must surface as the Error state and be contained, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of the AlpacaRotator is managed explicitly: on every non-adopt path SafeDisconnectDispose disposes it; on the adopt path it is stored in _client (local set to null) and disposed later by DisconnectAsync/Dispose. CA2000 cannot follow the transfer through the lock + helper.")]
    private void ConnectInBackground(DiscoveredDeviceDto device, long generation) {
        AlpacaRotator? client = null;
        var adopted = false;
        try {
            var host = string.IsNullOrWhiteSpace(device.IpAddress) ? device.HostName : device.IpAddress;
            if (string.IsNullOrWhiteSpace(host)) {
                throw new InvalidOperationException(
                    $"discovered device '{device.Name}' carries neither an IP address nor a host name");
            }
            client = new AlpacaRotator(
                device.UseHttps ? ServiceType.Https : ServiceType.Http,
                host, device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null);
            client.Connected = true;
            lock (_gate) {
                if (!_disposed && _connectGeneration == generation) {
                    _client = client;
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
            RefreshCacheOnce(); // seed runtime through the guarded path
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
        Justification = "Best-effort teardown: Halt / Connected = false can throw driver/HTTP exceptions, which must be swallowed so Dispose always runs and cleanup completes. CA1031's log-and-recover boundary applies.")]
    private void SafeDisconnectDispose(AlpacaRotator client) {
        try {
            // Interrupt any in-flight move so a disconnect-during-move fails fast rather than
            // waiting out the full ASCOM timeout.
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
    private void DisposeQuietly(AlpacaRotator client) {
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

    // Extracted (internal) for direct unit testing. ASCOM rotator angles are degrees in [0, 360).
    internal static bool IsAngleOutOfRange(double angleDeg) => angleDeg < 0 || angleDeg >= 360;

    public void Dispose() {
        AlpacaRotator? client;
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
        // Halt/Connected=false are blocking HTTP calls (up to ~3s ASCOM timeout) that would hang
        // container shutdown if the device is unreachable. DisposeQuietly releases resources only.
        if (client is not null) {
            DisposeQuietly(client);
        }
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotator runtime read failed")]
    private partial void LogRuntimeReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotator move to {Target} failed")]
    private partial void LogMoveFailed(Exception ex, float target);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rotator connected: {Name} at {Host}:{Port}/{Device}")]
    private partial void LogConnected(string name, string host, int port, int device);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rotator connect failed for {Name}")]
    private partial void LogConnectFailed(Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ignored error while disconnecting Rotator during teardown")]
    private partial void LogTeardownIgnored(Exception ex);
}
