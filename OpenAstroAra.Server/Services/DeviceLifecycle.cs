#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>What the daemon last heard about a device's connection (#1539). Fault reaction reads it
/// before it touches hardware: a device the user disconnected is not reconnected behind their back.</summary>
public enum DesiredConnection {
    /// <summary>The user (or auto-connect on the user's behalf) asked for the device to be connected.</summary>
    ConnectedByUser,

    /// <summary>The user disconnected the device. Recovery leaves it alone.</summary>
    DisconnectedByUser,

    /// <summary>The device was lost (§42.3 trip) while the user wanted it connected. Recovery may reconnect it.</summary>
    Faulted,
}

/// <summary>#1539 — a device service whose lifecycle records the desired connection state, so the
/// §42.3 ladder can ask it instead of inferring a user disconnect from the observed state.</summary>
public interface IRecoverableDevice {
    /// <summary>Null while no device is selected.</summary>
    DesiredConnection? DesiredConnection { get; }

    /// <summary>The ladder's connect: dispatches like <c>ConnectAsync</c>, but refuses (false) when
    /// the user has disconnected the device. The check and the dispatch are one step under the
    /// device's gate, so a user disconnect cannot land between them.</summary>
    bool TryRecover(DiscoveredDeviceDto device);
}

/// <summary>What a device class hands its <see cref="DeviceLifecycle{TDevice}"/> per refresh tick
/// (ADR 0011). <paramref name="EssentialReadError"/> is the essential read's failure (null when it
/// worked or the driver does not implement it); <paramref name="CommitLocked"/> stores the read
/// under the gate, and runs only while the read device is still the live one;
/// <paramref name="AfterCommit"/> runs off the gate once the commit happened.</summary>
internal readonly record struct DeviceRead(string? EssentialReadError, Action CommitLocked, Action? AfterCommit = null);

/// <summary>
/// ADR 0011 — a device's lifecycle in one module: connect (generation supersede), disconnect,
/// forget, the §32.4 refresh loop, the §42.3 connection probe and trip, the second-channel read
/// health, the #1246 clean-read gate, the #1539 desired connection state and the §60.9 state
/// publish. A device class holds one, and supplies its device adapter factory, its runtime read
/// (naming the essential read) and its failed-read threshold.
///
/// <see cref="Gate"/> is the one lock for the lifecycle and for the device class's own session
/// state (runtime cache, capabilities): the class takes it when it needs its fields and the
/// connection state in one critical section. Monitor is reentrant, so the properties below may be
/// read inside it.
/// </summary>
internal sealed class DeviceLifecycle<TDevice> : IDisposable where TDevice : class, IDeviceAdapter {

    internal static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly object _owner;
    private readonly DeviceType _type;
    private readonly string _noun;
    private readonly Func<DiscoveredDeviceDto, TDevice> _createDevice;
    private readonly Func<TDevice, DeviceRead> _read;
    private readonly Action _resetSessionLocked;
    private readonly Func<TDevice, Action>? _prepareSession;
    private readonly Action? _onConnected;
    private readonly bool _failedReadTrips;
    private readonly ILogger _logger;
    private readonly EquipmentEventPublisher? _events;
    private readonly IEquipmentFaultSink? _faults;
    private readonly DeviceConnectionProbe _probe = new();
    private readonly DeviceHealthWatch _health;
    private readonly Timer _refreshTimer;
    private TDevice? _client;
    private DiscoveredDeviceDto? _device;
    private EquipmentConnectionState _state = EquipmentConnectionState.Disconnected;
    private DesiredConnection? _desired;
    private DateTimeOffset _lastTransition = DateTimeOffset.UtcNow;
    private bool _cleanReadSinceConnect;
    private int _refreshing;
    private long _connectGeneration;
    private bool _disposed;

    /// <param name="owner">The device class, named by <see cref="ObjectDisposedException"/>.</param>
    /// <param name="noun">The device in messages and operation types ("rotator").</param>
    /// <param name="read">The runtime read, off the gate, on a device that answered its probe.</param>
    /// <param name="resetSessionLocked">Clears the class's session state; called under the gate on
    /// adopt, disconnect and trip.</param>
    /// <param name="failedReadThreshold">Consecutive failed essential reads that make the device degraded.</param>
    /// <param name="prepareSession">Optional read-once work (capabilities), off the gate after the connect;
    /// the action it returns runs under the gate on adopt, before the device reads Connected.</param>
    /// <param name="onConnected">Optional, runs off the gate once a connect adopted and its seed read
    /// finished, while the device still reads Connected (Switch: the late cooling-fan catch-up).</param>
    /// <param name="failedReadTrips">A failed essential read or a failed connection probe is a lost connection:
    /// the device goes to Error with a Disconnected fault on that read or probe, instead of a degraded episode
    /// or a lost streak after their thresholds (SafetyMonitor, ADR 0011 rule 3: a dead monitor is never read as safe).</param>
    public DeviceLifecycle(object owner, DeviceType type, string noun,
            Func<DiscoveredDeviceDto, TDevice> createDevice, Func<TDevice, DeviceRead> read,
            Action resetSessionLocked, int failedReadThreshold, ILogger logger,
            EquipmentEventPublisher? events, IEquipmentFaultSink? faults,
            Func<TDevice, Action>? prepareSession = null, Action? onConnected = null, bool failedReadTrips = false) {
        _owner = owner;
        _type = type;
        _noun = noun;
        _createDevice = createDevice;
        _read = read;
        _resetSessionLocked = resetSessionLocked;
        _prepareSession = prepareSession;
        _onConnected = onConnected;
        _failedReadTrips = failedReadTrips;
        _logger = logger;
        _events = events;
        _faults = faults;
        _health = new DeviceHealthWatch(type, faults, failedReadThreshold);
        _refreshTimer = new Timer(_ => Refresh(), state: null, DefaultRefreshInterval, DefaultRefreshInterval);
    }

    public object Gate { get; } = new();

    // #1265 — bench-test knob: a fixture that needs "several ticks" waits milliseconds.
    public TimeSpan RefreshPeriod { set => _refreshTimer.Change(value, value); }

    public bool IsDisposed { get { lock (Gate) { return _disposed; } } }

    public DiscoveredDeviceDto? Device { get { lock (Gate) { return _device; } } }

    // #1127 — the record the card names; the reconnector falls back to it.
    public DiscoveredDeviceDto? RetainedDevice { get { lock (Gate) { return _disposed ? null : _device; } } }

    public EquipmentConnectionState State { get { lock (Gate) { return _state; } } }

    /// <summary>When the connection state last changed (the SafetyMonitor DTO reports it).</summary>
    public DateTimeOffset LastTransition { get { lock (Gate) { return _lastTransition; } } }

    public DesiredConnection? Desired { get { lock (Gate) { return _device is null ? null : _desired; } } }

    /// <summary>#1246 — true once a refresh tick since the current connect read the essential read cleanly.</summary>
    public bool HasCleanReadSinceConnect {
        get { lock (Gate) { return _state == EquipmentConnectionState.Connected && _cleanReadSinceConnect; } }
    }

    /// <summary>The live device while connected and not disposed, else null.</summary>
    public TDevice? Live {
        get { lock (Gate) { return !_disposed && _state == EquipmentConnectionState.Connected ? _client : null; } }
    }

    /// <summary>The live device, or null when not connected. Throws once disposed.</summary>
    public TDevice? ConnectedDevice() {
        lock (Gate) {
            ObjectDisposedException.ThrowIf(_disposed, _owner);
            return _state == EquipmentConnectionState.Connected ? _client : null;
        }
    }

    /// <summary>True while <paramref name="device"/> is the connected session's device.</summary>
    public bool IsLive(TDevice device) {
        lock (Gate) {
            return !_disposed && _state == EquipmentConnectionState.Connected && ReferenceEquals(_client, device);
        }
    }

    /// <summary>True while <paramref name="device"/> is the held device (connected or tripped):
    /// work started on a superseded device must not touch the current session.</summary>
    public bool IsCurrent(TDevice device) {
        lock (Gate) {
            return ReferenceEquals(_client, device);
        }
    }

    /// <summary>The selected device record while <paramref name="device"/> is current, else null.</summary>
    public DiscoveredDeviceDto? DeviceIfCurrent(TDevice device) {
        lock (Gate) {
            return ReferenceEquals(_client, device) ? _device : null;
        }
    }

    public Task<OperationAcceptedDto> ConnectAsync(DiscoveredDeviceDto device, string? idempotencyKey) {
        ArgumentNullException.ThrowIfNull(device);
        lock (Gate) {
            ObjectDisposedException.ThrowIf(_disposed, _owner);
            _desired = DesiredConnection.ConnectedByUser;
            Dispatch(device);
        }
        return Task.FromResult(Accepted($"{_noun}.connect", idempotencyKey));
    }

    public bool TryRecover(DiscoveredDeviceDto device) {
        ArgumentNullException.ThrowIfNull(device);
        lock (Gate) {
            // #1763 — also after Remove: the store may still remember a device the service forgot.
            if (_disposed || _desired == DesiredConnection.DisconnectedByUser) {
                return false;
            }
            Dispatch(device);
            return true;
        }
    }

    // Caller holds Gate. A connect to the device already connecting or connected is a no-op.
    private void Dispatch(DiscoveredDeviceDto device) {
        if ((_state == EquipmentConnectionState.Connecting || _state == EquipmentConnectionState.Connected)
            && _device?.UniqueId == device.UniqueId) {
            return;
        }
        DisposeClientLocked();
        // §60.9 — a lost or failed device is torn down before the replacement's Connecting, so a WS
        // subscriber sees a clean transition (Error → Disconnected → Connecting).
        if (_state == EquipmentConnectionState.Error) {
            SetState(EquipmentConnectionState.Disconnected);
        }
        _device = device;
        var generation = ++_connectGeneration;
        SetState(EquipmentConnectionState.Connecting);
        _ = Task.Run(() => ConnectInBackground(device, generation), CancellationToken.None);
    }

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey) {
        TDevice? client;
        lock (Gate) {
            ObjectDisposedException.ThrowIf(_disposed, _owner);
            _connectGeneration++;
            client = _client;
            _client = null;
            _health.Reset(); // a standing degraded episode ends with the session (resolved, not left to seed)
            _resetSessionLocked();
            if (_device is not null) {
                _desired = DesiredConnection.DisconnectedByUser;
                SetState(EquipmentConnectionState.Disconnected);
            }
        }
        if (client is not null) {
            _ = Task.Run(() => SafeDisconnectDispose(client), CancellationToken.None);
        }
        return Task.FromResult(Accepted($"{_noun}.disconnect", idempotencyKey));
    }

    public async Task<bool> ForgetAsync() {
        lock (Gate) {
            ObjectDisposedException.ThrowIf(_disposed, _owner);
            if (_device is null) {
                return false;
            }
            ThrowIfLiveLocked();
        }
        // The SAME teardown as a disconnect. An Error reached from Connected (§42.3 trip) still holds
        // a live, adopted client, and the card offers no Disconnect there — so Remove must release
        // the driver itself (#1108 review).
        await DisconnectAsync(null).ConfigureAwait(false);
        lock (Gate) {
            ObjectDisposedException.ThrowIf(_disposed, _owner);
            if (_device is null) {
                return false;
            }
            // A connect that raced in between the two locks is live again — refuse, don't drop.
            ThrowIfLiveLocked();
            var removed = _device;
            _device = null;
            // _desired stays DisconnectedByUser (set by the disconnect above) until the user's next connect (#1763).
            // §60.9 — a second open client drops its card on this instead of waiting for its next poll (#1127).
            _events?.Removed(_type, removed.UniqueId, removed.Name);
            return true;
        }
    }

    private void ThrowIfLiveLocked() {
        if (_state == EquipmentConnectionState.Connecting || _state == EquipmentConnectionState.Connected) {
            throw new InvalidOperationException($"the {_noun} is connected — disconnect it before removing it");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Timer-callback boundary: an unhandled exception in a System.Threading.Timer callback crashes the process. The device class's reads already absorb device failures; this catch-all is a hard backstop so a refresh tick can never fault the daemon. CA1031's log-and-recover boundary applies.")]
    public void Refresh() {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) {
            return;
        }
        try {
            TDevice? client;
            lock (Gate) {
                if (_disposed || _state != EquipmentConnectionState.Connected) {
                    return;
                }
                client = _client;
            }
            if (client is null) {
                return;
            }
            // §42.3 — the ONE deliberate connection probe per tick. The runtime reads swallow
            // failures (an unsupported property must stay benign), so the probe is the only
            // disconnect-detection source; a consecutive-failure streak trips the device to Error.
            if (!ProbeConnected(client)) {
                if (_failedReadTrips) {
                    // ADR 0011 rule 3 — a failed probe is a failed essential read: no safe verdict outlives it.
                    TripConnectionLost(client, "stopped answering its connection probe");
                } else if (ObserveProbeIfLive(client, probeSucceeded: false) == ProbeVerdict.Lost) {
                    TripConnectionLost(client,
                        $"stopped answering {DeviceConnectionProbe.DefaultLostThreshold} consecutive connection probes");
                }
                return; // the device didn't answer — skip this tick's reads
            }
            ObserveProbeIfLive(client, probeSucceeded: true);
            var read = _read(client);
            var committed = false;
            var tripOnRead = false;
            lock (Gate) {
                if (_state == EquipmentConnectionState.Connected && ReferenceEquals(_client, client)) {
                    read.CommitLocked();
                    tripOnRead = _failedReadTrips && read.EssentialReadError is not null;
                    if (!tripOnRead) {
                        _health.Observe(read.EssentialReadError is null, read.EssentialReadError, _device);
                    }
                    _cleanReadSinceConnect |= read.EssentialReadError is null;
                    committed = true;
                }
            }
            if (tripOnRead) {
                TripConnectionLost(client, $"its essential read failed: {read.EssentialReadError}");
            }
            if (committed) {
                read.AfterCommit?.Invoke();
            }
        } catch (Exception ex) {
            DeviceLifecycleLog.RuntimeReadFailed(_logger, ex, _type);
        } finally {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background connect boundary: building the device and setting Connected can throw arbitrary driver/HTTP/socket exceptions; any escape must surface as the Error state and be contained, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership is explicit: on every non-adopt path SafeDisconnectDispose disposes the device; on the adopt path it is stored in _client (local set to null) and disposed later by DisconnectAsync/Dispose.")]
    private void ConnectInBackground(DiscoveredDeviceDto device, long generation) {
        TDevice? client = null;
        var adopted = false;
        try {
            client = _createDevice(device);
            client.Connect();
            var commitPrepared = _prepareSession?.Invoke(client);
            lock (Gate) {
                if (!_disposed && _connectGeneration == generation) {
                    _client = client;
                    _probe.Reset();      // §42.3 — a fresh session starts a fresh streak
                    _health.Reset();
                    _cleanReadSinceConnect = false;
                    _resetSessionLocked(); // don't serve a prior device's runtime
                    commitPrepared?.Invoke();
                    _desired = DesiredConnection.ConnectedByUser;
                    SetState(EquipmentConnectionState.Connected);
                    adopted = true;
                }
            }
            if (!adopted) {
                SafeDisconnectDispose(client);
                return;
            }
            client = null; // ownership transferred to _client
            Refresh(); // seed runtime through the guarded path
            bool stillConnected;
            lock (Gate) {
                stillConnected = _state == EquipmentConnectionState.Connected;
                if (stillConnected) {
                    // The Connected push went out before this seed read; tell clients the details
                    // are cached so they re-read now.
                    _events?.DetailsReady(_type, _device?.UniqueId, _device?.Name);
                }
            }
            if (stillConnected) {
                var host = AlpacaDeviceAddress.Host(device);
                DeviceLifecycleLog.Connected(_logger, _type, device.Name, host, device.IpPort, device.AlpacaDeviceNumber);
                _onConnected?.Invoke();
            }
        } catch (Exception ex) {
            if (!adopted) {
                if (client is not null) {
                    SafeDisconnectDispose(client);
                }
                lock (Gate) {
                    if (!_disposed && _connectGeneration == generation) {
                        SetState(EquipmentConnectionState.Error);
                    }
                }
                DeviceLifecycleLog.ConnectFailed(_logger, ex, _type, device.Name);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort teardown: Stop / Disconnect can throw driver/HTTP exceptions, which must be swallowed so Dispose always runs and cleanup completes. CA1031's log-and-recover boundary applies.")]
    private void SafeDisconnectDispose(TDevice client) {
        try {
            // Interrupt any in-flight motion so a disconnect mid-move fails fast rather than waiting
            // out the full ASCOM timeout.
            client.Stop();
        } catch (Exception ex) {
            DeviceLifecycleLog.TeardownIgnored(_logger, ex, _type);
        }
        try {
            client.Disconnect();
        } catch (Exception ex) {
            DeviceLifecycleLog.TeardownIgnored(_logger, ex, _type);
        }
        DisposeQuietly(client);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort teardown: ASCOM-backed clients have been known to throw from Dispose(); the throw must be swallowed (and logged) rather than escape into a fire-and-forget Task.Run. CA1031's log-and-recover boundary applies.")]
    private void DisposeQuietly(TDevice client) {
        try {
            client.Dispose();
        } catch (Exception ex) {
            DeviceLifecycleLog.TeardownIgnored(_logger, ex, _type);
        }
    }

    private void DisposeClientLocked() {
        var c = _client;
        _client = null;
        if (c is not null) {
            _ = Task.Run(() => SafeDisconnectDispose(c), CancellationToken.None);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The probe's whole job is turning any transport/driver exception into a failed-probe observation. CA1031's log-and-recover boundary applies.")]
    private static bool ProbeConnected(TDevice c) {
        try { return c.Connected; } catch (Exception) { return false; }
    }

    // Only a probe of the LIVE client may feed the streak: the blocking probe runs outside the gate,
    // so a concurrent reconnect can have swapped the client (and reset the probe) meanwhile. The
    // liveness check and the Observe are ONE operation; null means the observation was discarded.
    private ProbeVerdict? ObserveProbeIfLive(TDevice client, bool probeSucceeded) {
        lock (Gate) {
            return ReferenceEquals(_client, client) ? _probe.Observe(probeSucceeded) : null;
        }
    }

    /// <summary>§42.3 — declare the device lost: Connected → Error (keeps _device so a reconnect can find
    /// it), desired state Faulted, and publish the §42.2 fault. Re-checks the probed client under the gate:
    /// a reconnect between the read and this trip must never flip the NEW, healthy session to Error.
    /// <paramref name="reason"/> is the user-facing fault text (the probe, a class's own detector).</summary>
    public void TripConnectionLost(TDevice probed, string reason) {
        DiscoveredDeviceDto? device;
        lock (Gate) {
            if (_state != EquipmentConnectionState.Connected || !ReferenceEquals(_client, probed)) {
                return;
            }
            device = _device;
            _desired = DesiredConnection.Faulted;
            SetState(EquipmentConnectionState.Error);
            _probe.Reset();
            _health.Reset();
            _resetSessionLocked();
        }
        DeviceLifecycleLog.ConnectionLost(_logger, _type, device?.Name ?? "?");
        _faults?.Publish(new EquipmentFaultEvent(_type, device?.UniqueId, device?.Name,
            EquipmentFaultKind.Disconnected, reason, DateTimeOffset.UtcNow));
    }

    // Caller holds Gate; the publisher's synchronous part only serializes a small payload and hands off.
    private void SetState(EquipmentConnectionState state) {
        if (_state == state) {
            return;
        }
        _state = state;
        _lastTransition = DateTimeOffset.UtcNow;
        _events?.StateChanged(_type, _device?.UniqueId, _device?.Name, state);
    }

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    public void Dispose() {
        TDevice? client;
        lock (Gate) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            client = _client;
            _client = null;
        }
        _refreshTimer.Dispose();
        // Release resources only, not SafeDisconnectDispose: the courtesy Stop/Disconnect are
        // blocking HTTP calls that would hang container shutdown if the device is unreachable.
        if (client is not null) {
            DisposeQuietly(client);
        }
    }
}

internal static partial class DeviceLifecycleLog {
    [LoggerMessage(Level = LogLevel.Information, Message = "{DeviceType} connected: {Name} at {Host}:{Port}/{Device}")]
    public static partial void Connected(ILogger logger, DeviceType deviceType, string name, string host, int port, int device);

    [LoggerMessage(Level = LogLevel.Error, Message = "{DeviceType} connect failed for {Name}")]
    public static partial void ConnectFailed(ILogger logger, Exception ex, DeviceType deviceType, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{DeviceType} '{Device}' stopped answering — marked Error (§42.3)")]
    public static partial void ConnectionLost(ILogger logger, DeviceType deviceType, string device);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{DeviceType} runtime read failed")]
    public static partial void RuntimeReadFailed(ILogger logger, Exception ex, DeviceType deviceType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ignored error while disconnecting {DeviceType} during teardown")]
    public static partial void TeardownIgnored(ILogger logger, Exception ex, DeviceType deviceType);
}
