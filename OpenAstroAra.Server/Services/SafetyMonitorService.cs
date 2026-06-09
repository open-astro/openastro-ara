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
/// <see cref="EquipmentConnectionState.Error"/>.
///
/// §32.4 cache: a background timer reads the (blocking HTTP) <c>IsSafe</c> every
/// <see cref="RefreshInterval"/> while Connected and caches it, so <see cref="GetAsync"/> and the
/// mediator's <c>GetInfo</c> serve the value synchronously without a per-poll HTTP read (no
/// thread-per-poll accumulation under aggressive polling).
///
/// This class also serves the Sequencer's <c>ISafetyMonitorMediator</c> (so <c>WaitUntilSafe</c>
/// reads the live device) — that surface lives in the <c>SafetyMonitorService.Mediator.cs</c>
/// partial; one singleton is registered for both interfaces per playbook §8.1.
/// </summary>
public sealed partial class SafetyMonitorService : ISafetyMonitorService, IDisposable {

    // §32.4 — how often the background loop refreshes the cached IsSafe while connected. The
    // cache is therefore at most this stale; GetAsync/GetInfo serve it without a per-call HTTP read.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly ILogger<SafetyMonitorService> _logger;
    private readonly object _gate = new();
    private readonly Timer _refreshTimer;
    private AlpacaSafetyMonitor? _client;
    private DiscoveredDeviceDto? _device;
    private EquipmentConnectionState _state = EquipmentConnectionState.Disconnected;
    private DateTimeOffset _lastTransition = DateTimeOffset.UtcNow;
    // Last IsSafe read from the device; refreshed by the background loop while Connected and
    // served (synchronously) by GetAsync/GetInfo. Only meaningful while _state == Connected.
    private bool _cachedSafe;
    // 0 = idle, 1 = a refresh read is in flight. Guards against overlapping reads if a read
    // outruns the timer interval (Interlocked, not under _gate).
    private int _refreshing;
    // Bumped by every ConnectAsync/DisconnectAsync so a late-completing background connect can
    // tell whether it is still the current attempt before adopting its client (supersede check).
    private long _connectGeneration;
    private bool _disposed;

    public SafetyMonitorService(ILogger<SafetyMonitorService>? logger = null) {
        _logger = logger ?? NullLogger<SafetyMonitorService>.Instance;
        // The refresh loop runs for the service lifetime; each tick is a no-op unless Connected.
        _refreshTimer = new Timer(RefreshTick, state: null, dueTime: RefreshInterval, period: RefreshInterval);
    }

    // ct is unused by design: the value is served from the cache the background loop maintains
    // (§32.4), so there is no per-call HTTP read to cancel. Kept to match the IEquipmentServices
    // contract. Returns a Task to satisfy the async interface without an await.
    public Task<SafetyMonitorDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            // All public operations throw after Dispose() (uniform with Connect/Disconnect).
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_device is null) {
                // No device has ever been selected — GET maps this to 404 at the endpoint.
                return Task.FromResult<SafetyMonitorDto?>(null);
            }
            // Single consistent snapshot: device, state, cached IsSafe, and timestamp all under
            // one lock. safe is only meaningful while Connected; the background loop keeps
            // _cachedSafe at most RefreshInterval stale.
            var state = _state;
            var safe = state == EquipmentConnectionState.Connected && _cachedSafe;
            var dto = new SafetyMonitorDto(
                _device.UniqueId, _device.Name, state, safe,
                _lastTransition.ToString("O", CultureInfo.InvariantCulture));
            return Task.FromResult<SafetyMonitorDto?>(dto);
        }
    }

    private void RefreshTick(object? state) => RefreshCacheOnce();

    // Reads IsSafe once and updates the cache. Used by BOTH the background timer and the
    // connect-time seed; the _refreshing guard makes them mutually exclusive, so there is never
    // more than one concurrent client.IsSafe read on the same AlpacaSafetyMonitor instance.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Timer-callback boundary: an unhandled exception in a System.Threading.Timer callback crashes the process. ReadIsSafe already contains device-read failures, but this catch-all is a hard backstop so a refresh tick can never fault the daemon. CA1031's log-and-recover boundary applies.")]
    private void RefreshCacheOnce() {
        // Skip if a refresh read is already in flight (a slow read outran the interval, or the
        // connect-time seed is racing a timer tick).
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) {
            return;
        }
        try {
            AlpacaSafetyMonitor? client;
            lock (_gate) {
                if (_disposed || _state != EquipmentConnectionState.Connected) {
                    return;
                }
                client = _client;
            }
            if (client is null) {
                return;
            }
            var safe = ReadIsSafe(client); // bounded read; demotes to Error + returns false on throw
            lock (_gate) {
                // Only adopt the reading if this is still the live, Connected client.
                if (_state == EquipmentConnectionState.Connected && ReferenceEquals(_client, client)) {
                    _cachedSafe = safe;
                }
            }
        } catch (Exception ex) {
            LogIsSafeReadFailed(ex);
        } finally {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    // ct is unused by design: under the §60.5 202-Accepted contract this method returns
    // immediately and the work continues on a detached background task, so there is nothing for
    // the request token to cancel. Kept in the signature to match the IEquipmentServices contract.
    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Device); // guard a null Device from a malformed body
        var device = request.Device;
        long generation;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Idempotent: connecting to the same device while already connecting/connected
            // is a no-op accept (§60.5) — don't tear down a good connection.
            if ((_state == EquipmentConnectionState.Connecting || _state == EquipmentConnectionState.Connected)
                && _device?.UniqueId == device.UniqueId) {
                return Task.FromResult(Accepted("safety-monitor.connect", idempotencyKey));
            }
            DisposeClientLocked();
            _device = device;
            generation = ++_connectGeneration; // this attempt's id; later attempts/disconnects bump it
            SetState(EquipmentConnectionState.Connecting);
        }
        // 202 contract: do the blocking Alpaca connect off-thread; GetAsync reports the outcome.
        // CancellationToken.None: this is intentionally fire-and-forget, not tied to the request.
        _ = Task.Run(() => ConnectInBackground(device, generation), CancellationToken.None);
        return Task.FromResult(Accepted("safety-monitor.connect", idempotencyKey));
    }

    // ct is unused by design (see ConnectAsync): the 202 contract makes teardown fire-and-forget.
    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) {
        AlpacaSafetyMonitor? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _connectGeneration++; // invalidate any in-flight connect so it won't adopt its client
            client = _client;
            _client = null;
            if (_device is not null) {
                SetState(EquipmentConnectionState.Disconnected);
            }
            // A connect still in flight will see the bumped generation on completion and dispose
            // its own client (supersede check in ConnectInBackground).
        }
        if (client is not null) {
            _ = Task.Run(() => SafeDisconnectDispose(client), CancellationToken.None);
        }
        return Task.FromResult(Accepted("safety-monitor.disconnect", idempotencyKey));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Device-read boundary: an Alpaca IsSafe call can throw arbitrary driver/HTTP exceptions; any escape must demote the connection to Error and be contained, not fault the GET. CA1031's log-and-recover boundary applies.")]
    private bool ReadIsSafe(AlpacaSafetyMonitor client) {
        try {
            return client.IsSafe;
        } catch (Exception ex) {
            lock (_gate) {
                // Only demote to Error if this is still the live connection. A concurrent
                // DisconnectAsync sets Disconnected and disposes the client off-thread; the
                // resulting disposed-client throw here must NOT clobber Disconnected back to
                // Error. Guarding on the same client instance also covers a reconnect.
                if (_state == EquipmentConnectionState.Connected && ReferenceEquals(_client, client)) {
                    SetState(EquipmentConnectionState.Error); // reentrant lock; keeps all transitions on one path
                }
            }
            LogIsSafeReadFailed(ex);
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background connect boundary: constructing the Alpaca client and setting Connected can throw arbitrary driver/HTTP/socket exceptions; any escape must surface as the Error state and be contained, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of the AlpacaSafetyMonitor is managed explicitly: on every non-adopt path (supersede or exception) SafeDisconnectDispose disposes it; on the adopt path it is stored in _client (local set to null) and disposed later by DisconnectAsync/Dispose. CA2000 cannot follow the transfer through the lock + helper.")]
    private void ConnectInBackground(DiscoveredDeviceDto device, long generation) {
        AlpacaSafetyMonitor? client = null;
        var adopted = false; // declared outside try so the catch can tell ownership already transferred
        try {
            var host = string.IsNullOrWhiteSpace(device.IpAddress) ? device.HostName : device.IpAddress;
            if (string.IsNullOrWhiteSpace(host)) {
                throw new InvalidOperationException(
                    $"discovered device '{device.Name}' carries neither an IP address nor a host name");
            }
            client = new AlpacaSafetyMonitor(
                device.UseHttps ? ServiceType.Https : ServiceType.Http,
                host, device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null);
            // The blocking connect is bounded by the ASCOM client's establishConnectionTimeout
            // (default 3s; standardDeviceResponseTimeout also 3s), so an unreachable or
            // black-holed device surfaces as Error within seconds rather than wedging the
            // service in Connecting. (SafetyMonitorServiceTest's dead-port case verifies the
            // fast-fail path.)
            //
            // Trust the setter: the Alpaca `Connected = true` PUT is authoritative and throws on
            // a real connect failure. We deliberately do NOT re-GET `Connected` to confirm — a
            // second round-trip can transiently read false on a slow device that already accepted
            // the SET, which would falsely demote a good connection to Error while leaving the
            // device connected on its side.
            client.Connected = true;
            lock (_gate) {
                // Adopt only if this is still the current attempt: a newer connect, a disconnect,
                // or a Dispose() bumps the generation / sets _disposed, in which case this
                // late-completing connect must not adopt (which would leak its client and/or
                // resurrect a torn-down connection). The generation check also covers the
                // connect -> disconnect -> reconnect-to-same-device sequence that a UniqueId
                // comparison alone could not distinguish.
                if (!_disposed && _connectGeneration == generation) {
                    _client = client;
                    SetState(EquipmentConnectionState.Connected);
                    adopted = true;
                }
            }
            if (!adopted) {
                SafeDisconnectDispose(client);
                return;
            }
            client = null; // ownership transferred to _client
            // Seed the IsSafe cache immediately (so the first GetAsync/GetInfo after connect
            // reflects the device, not a stale default), through the SAME guarded path as the
            // timer — so the seed and a racing timer tick can never issue two concurrent IsSafe
            // reads on the client.
            RefreshCacheOnce();
            // Only log "connected" if the seed read didn't fault the connection into Error
            // (ReadIsSafe demotes on a throw); otherwise the failure is already logged.
            bool stillConnected;
            lock (_gate) {
                stillConnected = _state == EquipmentConnectionState.Connected;
            }
            if (stillConnected) {
                LogConnected(device.Name, host, device.IpPort, device.AlpacaDeviceNumber);
            }
        } catch (Exception ex) {
            // Once adopted, the connection is live and owned by _client; a later throw (today only
            // LogConnected, which doesn't throw) must NOT tear it down or log a failure — just
            // return. If not adopted, dispose this attempt's client, demote to Error when still
            // current, and log the failure.
            if (!adopted) {
                if (client is not null) {
                    SafeDisconnectDispose(client);
                }
                lock (_gate) {
                    // No _client = null here: a non-adopted attempt never stored its client in
                    // _client, and the generation guard ensures a newer connect's client is not
                    // touched either.
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
    private void SafeDisconnectDispose(AlpacaSafetyMonitor client) {
        try {
            client.Connected = false;
        } catch (Exception ex) {
            LogTeardownIgnored(ex);
        }
        DisposeQuietly(client);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort teardown: ASCOM/COM-backed clients have been known to throw from Dispose(); the throw must be swallowed (and logged) rather than escape into a fire-and-forget Task.Run as an unobserved exception. CA1031's log-and-recover boundary applies.")]
    private void DisposeQuietly(AlpacaSafetyMonitor client) {
        try {
            client.Dispose();
        } catch (Exception ex) {
            LogTeardownIgnored(ex);
        }
    }

    // Caller holds _gate; offloads the blocking disconnect/dispose so we never do I/O under the
    // lock. The discarded task is intentionally untracked — it may outlive the service instance,
    // but SafeDisconnectDispose swallows everything, so there is no unobserved-fault risk.
    private void DisposeClientLocked() {
        var c = _client;
        _client = null;
        if (c is not null) {
            _ = Task.Run(() => SafeDisconnectDispose(c), CancellationToken.None);
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
        // Stop the refresh loop. Timer.Dispose() does not wait for an in-flight RefreshTick, but
        // that tick re-checks _disposed/_client under the lock (and the ReferenceEquals guard), so
        // it cannot write the cache or touch a disposed client after this point.
        _refreshTimer.Dispose();
        // Dispose the client directly (guarded) rather than via SafeDisconnectDispose: the
        // courtesy "Connected = false" is a blocking HTTP call (up to the ASCOM ~3s
        // establishConnectionTimeout) that would hang container shutdown if the device is
        // unreachable. DisposeQuietly releases the HttpClient resources without network I/O and
        // swallows any Dispose() throw so it can't escape IDisposable.Dispose() at shutdown.
        if (client is not null) {
            DisposeQuietly(client);
        }
        GC.SuppressFinalize(this);
    }

    // Logged whenever an IsSafe read throws. Whether it demotes the connection to Error is
    // conditional (only if it's still the live client), so the message doesn't claim it does.
    [LoggerMessage(Level = LogLevel.Warning, Message = "SafetyMonitor IsSafe read failed")]
    private partial void LogIsSafeReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "SafetyMonitor connected: {Name} at {Host}:{Port}/{Device}")]
    private partial void LogConnected(string name, string host, int port, int device);

    [LoggerMessage(Level = LogLevel.Error, Message = "SafetyMonitor connect failed for {Name}")]
    private partial void LogConnectFailed(Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ignored error while disconnecting SafetyMonitor during teardown")]
    private partial void LogTeardownIgnored(Exception ex);
}
