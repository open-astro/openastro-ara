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
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesiredState = OpenAstroAra.Server.Services.DesiredConnection;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §6 / §10.6 row 7 — real Alpaca-backed Switch service. Connects to one or more discovered Alpaca
/// Switch devices, serves each device's live ports (id/name/value/min/max/can-write) over the REST
/// surface, and writes a port value via <see cref="SetValueAsync"/>.
///
/// <para>Multi-instance: ARA addresses switches by their Alpaca <c>UniqueId</c> (the <c>{id}</c> in
/// <c>/api/v1/equipment/switch/{id}</c>). A connection map keyed by that id lets several switches
/// be connected at once — including the common two-host rig (a power box and a relay board on
/// separate Alpaca servers, BOTH device number 0). The previous device-number keying made those
/// collide: the second connect evicted the first and its toggles vanished from the client.</para>
///
/// Each connection holds one <see cref="DeviceLifecycle{TDevice}"/> (ADR 0011): the §60.5 202-Accepted
/// connect lifecycle, generation-based supersede, the §32.4 background-refresh cache (each connection's
/// refresh reads every port of its device) so the GET surface serves the cached port list synchronously,
/// the §42.3 probe, read health and the #1539 desired connection state. The ports-unreadable detector, the
/// §42.4 read-back and the fan catch-up stay here, on top of the module. A write refreshes the cache so
/// the new value is reflected promptly.
///
/// The Sequencer's <c>ISwitchMediator</c> (one singleton per §8.1) targets the lowest-numbered connected
/// switch as its "primary" — see <c>SwitchService.Mediator.cs</c>. Per-device sequencer targeting is a
/// follow-up.
/// </summary>
public sealed partial class SwitchService : ISwitchService, ICoolingFanActuator, IRecoverableDevice, IDisposable {

    /// <summary>Unreadable-ports observations a switch gets for free, once per connection,
    /// before they start counting against it (§42.3). Covers USB re-enumeration and drivers
    /// that answer Connected before their first status frame has landed. Spent by the first
    /// such observations of the connection's LIFETIME rather than by a post-connect clock, so
    /// a switch that runs cleanly for hours still has its two in hand when it first stumbles —
    /// deliberate (an isolated bad read is exactly what shouldn't count), at the cost of
    /// adding these ticks to the worst-case detection time.</summary>
    private const int PortReadGraceTicks = 2;

    /// <summary>Consecutive unreadable-ports ticks before a device that still answers
    /// <c>Connected</c> is declared lost (§42.3). Deliberately longer than the hard probe's
    /// three: this signal fires on ordinary read failures, which recover on their own far
    /// more often than a dead transport does. ≈20 s at the 2 s refresh interval, or ≈24 s
    /// including the <see cref="PortReadGraceTicks"/> a connection has not yet spent.</summary>
    private const int PortsLostThreshold = 10;

    private readonly ILogger<SwitchService> _logger;
    private readonly EquipmentEventPublisher? _events;
    private readonly IEquipmentFaultSink? _faults;
    private readonly IProfileStore? _profileStore;
    private readonly IWsBroadcaster? _ws;
    // Guards the connection map and _disposed only. Each connection's session state (ports, read-back
    // watch, ports-unreadable streak) is guarded by its lifecycle's gate. Order: this gate, then a lifecycle's.
    private readonly object _gate = new();
    // #1265 — bench-test knob: the §42.4 read-back settle window every new connection's watch is
    // built with (null = SwitchReadbackWatch.DefaultSettleWindow), shrunk alongside RefreshPeriod.
    internal TimeSpan? ReadbackSettleWindow { get; set; }
    // #1265 — bench-test knob: the refresh cadence every connection's lifecycle is armed with, so a fixture
    // that needs "several ticks" waits milliseconds, not multiples of the 2 s production interval.
    // Init-only (object initializer), never used by the daemon.
    private TimeSpan _refreshPeriod = DeviceLifecycle<ISwitchDevice>.DefaultRefreshInterval;
    internal TimeSpan RefreshPeriod { init => _refreshPeriod = value; }
    // Test seam (ADR 0011 rule 4): the adapter the lifecycles build for a selected device.
    private Func<DiscoveredDeviceDto, ISwitchDevice> _createDevice = AlpacaSwitchDevice.Create;
    internal Func<DiscoveredDeviceDto, ISwitchDevice> DeviceFactory { init => _createDevice = value; }
    // Known switches keyed by the device's Alpaca UniqueId (globally unique — the AlpacaDeviceNumber is only
    // unique per host). A connection (and its lifecycle) is kept across reconnects of the same device so the
    // user's disconnect is remembered; dropped on Remove, an id rename or Dispose. Mutated only under _gate.
    private readonly Dictionary<string, SwitchConnection> _connections = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Per-device state around one <see cref="DeviceLifecycle{TDevice}"/>. Everything but the
    /// lifecycle is guarded by <c>Lifecycle.Gate</c>.</summary>
    private sealed class SwitchConnection {
        private readonly DiscoveredDeviceDto _initial;
        private volatile IReadOnlyList<SwitchPortSnapshot> _cachedSnapshots = Array.Empty<SwitchPortSnapshot>();

        public SwitchConnection(DiscoveredDeviceDto initial, TimeSpan? readbackSettleWindow) {
            _initial = initial;
            Readback = new SwitchReadbackWatch(readbackSettleWindow);
        }

        public DeviceLifecycle<ISwitchDevice> Lifecycle { get; set; } = null!;
        // The selected record (the lifecycle's, which a reconnect may replace); the connect's until then.
        public DiscoveredDeviceDto Device => Lifecycle.Device ?? _initial;
        public ISwitchDevice? Client => Lifecycle.Live;
        public EquipmentConnectionState State => Lifecycle.State;
        // Full per-port snapshot (superset of SwitchPortDto: + Description, for the ISwitchMediator
        // surface). ProjectPorts projects the DTO view.
        public IReadOnlyList<SwitchPortSnapshot> CachedSnapshots { get => _cachedSnapshots; set => _cachedSnapshots = value; }
        public int DeviceNumber => Device.AlpacaDeviceNumber;
        public string Key => _initial.UniqueId;
        // §42.3 — the weaker "advertises ports, delivers none" signal, on its own longer
        // streak so a transient read burst can't tear down a switch whose Connected is
        // still answering. Cleared whenever a pass reads ports successfully.
        public DeviceConnectionProbe PortsProbe { get; } = new(PortsLostThreshold);
        // §42.3 — UNREADABLE observations seen since this connection went Connected (not
        // every refresh tick: a pass that reads ports fine doesn't spend the budget), so a
        // warming-up device gets its first few bad ticks forgiven however far apart they
        // fall. Reset with the probe.
        public int UnreadableTicksSinceConnect { get; set; }
        // §42.4 — per-connection commanded-value read-back watch (only ports the daemon wrote).
        public SwitchReadbackWatch Readback { get; }

        // Caller holds Lifecycle.Gate: a fresh, ended or tripped session starts clean.
        public void ResetSessionLocked() {
            CachedSnapshots = Array.Empty<SwitchPortSnapshot>(); // don't serve a prior device's ports
            PortsProbe.Reset();
            UnreadableTicksSinceConnect = 0; // and a fresh ports-unreadable grace window
            Readback.Reset();                // §42.4 — commanded values don't survive a session
        }
    }

    public SwitchService(ILogger<SwitchService>? logger = null, EquipmentEventPublisher? events = null,
            IEquipmentFaultSink? faults = null, IProfileStore? profileStore = null, IWsBroadcaster? ws = null,
            Func<ICameraService?>? cameraProbe = null) {
        _logger = logger ?? NullLogger<SwitchService>.Instance;
        _events = events;
        _faults = faults;
        _profileStore = profileStore;
        _ws = ws;
        _cameraProbe = cameraProbe;
        _lastCoolingFan = CoolingFan;
        if (_profileStore is not null) {
            _profileStore.Changed += OnProfileChanged;
        }
    }

    // ct unused by design: ports are served from the §32.4 cache, so there is no per-call HTTP read to
    // cancel. Returns a Task to satisfy the async interface without an await.
    // Lists every KNOWN switch, not only the connected ones: a switch stays in the map as Disconnected
    // (manual disconnect) or Error (failed connect) until it is reconnected or the daemon restarts, so a
    // client can see and re-drive it. Callers wanting only live switches filter on State == Connected.
    public Task<IReadOnlyList<SwitchDto>> GetAllAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var list = new List<SwitchDto>(_connections.Count);
            foreach (var conn in _connections.Values) {
                list.Add(ProjectDto(conn));
            }
            // Stable order (device number, then id for same-numbered hubs on different hosts) so the
            // list (and the client UI built on it) doesn't reshuffle between polls.
            list.Sort(static (a, b) => {
                var byNumber = a.AlpacaDeviceNumber.CompareTo(b.AlpacaDeviceNumber);
                return byNumber != 0 ? byNumber : string.CompareOrdinal(a.DeviceId, b.DeviceId);
            });
            return Task.FromResult<IReadOnlyList<SwitchDto>>(list);
        }
    }

    public async Task<bool> RemoveAsync(string deviceId, CancellationToken ct) {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        SwitchConnection conn;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_connections.TryGetValue(deviceId, out var found)) {
                return false;
            }
            conn = found;
        }
        // #1127 — a live switch (Connecting too: a mid-connect removal would let the adopt path land a
        // client into an entry nobody tracks any more) throws "disconnect it before removing it". An Error
        // reached from Connected (§42.3 trip) still holds the adopted client; the lifecycle releases it with
        // the same teardown a disconnect uses. §60.9 — it publishes Removed so a second open client drops the
        // card on this instead of waiting for its next poll.
        if (!await conn.Lifecycle.ForgetAsync().ConfigureAwait(false)) {
            return false;
        }
        lock (_gate) {
            if (_connections.TryGetValue(deviceId, out var current) && ReferenceEquals(current, conn)) {
                _connections.Remove(deviceId);
            }
        }
        conn.Lifecycle.Dispose();
        return true;
    }

    public Task<SwitchDto?> GetAsync(string deviceId, CancellationToken ct) {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Task.FromResult(_connections.TryGetValue(deviceId, out var conn) ? ProjectDto(conn) : null);
        }
    }

    public DiscoveredDeviceDto? GetDevice(string deviceId) {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _connections.TryGetValue(deviceId, out var conn) ? conn.Device : null;
        }
    }

    private static string EndpointKey(DiscoveredDeviceDto d) => DiscoveredDeviceEndpoint.KeyOf(d);

    // Caller holds _gate.
    private static SwitchDto ProjectDto(SwitchConnection conn) {
        IReadOnlyList<SwitchPortDto> ports = conn.State == EquipmentConnectionState.Connected
            ? ProjectPorts(conn.CachedSnapshots)
            : Array.Empty<SwitchPortDto>();
        return new SwitchDto(conn.Device.UniqueId, conn.DeviceNumber, conn.Device.Name, conn.State, ports);
    }

    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Device);
        var device = request.Device;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Connecting to the device already connecting/connected is a no-op accept (the lifecycle's rule).
            return EnsureConnectionLocked(device).Lifecycle.ConnectAsync(device, idempotencyKey);
        }
    }

    /// <summary>#1539 — the ladder's connect: refuses (false) when the user disconnected this switch, and
    /// dispatches like <see cref="ConnectAsync"/> otherwise. A switch the service does not know yet is a
    /// first connect.</summary>
    public bool TryRecover(DiscoveredDeviceDto device) {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate) {
            if (_disposed) {
                return false;
            }
            var existing = FindConnectionLocked(device);
            // The endpoint under a renamed id: the old entry's wish still holds for the replacement.
            if (existing is not null && existing.Key != device.UniqueId
                && existing.Lifecycle.Desired == DesiredState.DisconnectedByUser) {
                return false;
            }
            return EnsureConnectionLocked(device).Lifecycle.TryRecover(device);
        }
    }

    /// <summary>#1539 — across the known switches: <see cref="DesiredConnection.DisconnectedByUser"/> only
    /// when every one of them was disconnected by the user (the reaction to one switch's fault must not
    /// stop for another's disconnect); otherwise the first wanted-connected state; null with none known.</summary>
    public DesiredConnection? DesiredConnection {
        get {
            lock (_gate) {
                DesiredState? result = null;
                foreach (var conn in _connections.Values) {
                    var desired = conn.Lifecycle.Desired;
                    if (desired is null) {
                        continue;
                    }
                    if (desired != DesiredState.DisconnectedByUser) {
                        return desired;
                    }
                    result = desired;
                }
                return result;
            }
        }
    }

    /// <summary>#1246 — false while any connected switch has not yet read its ports cleanly since its connect.</summary>
    public bool IsCleanlyConnected {
        get {
            lock (_gate) {
                return !_connections.Values.Any(c => c.Lifecycle.State == EquipmentConnectionState.Connected && !c.Lifecycle.HasCleanReadSinceConnect);
            }
        }
    }

    // #1246 — true once a refresh tick since the switch's current connect read its ports cleanly.
    internal bool HasCleanReadSinceConnect(string deviceId) {
        lock (_gate) {
            return _connections.TryGetValue(deviceId, out var conn) && conn.Lifecycle.HasCleanReadSinceConnect;
        }
    }

    // Same PHYSICAL device = same Alpaca endpoint (host:port + device number), not same
    // UniqueId: bridges have been observed changing a device's UniqueId across versions
    // (ZWO dew heater: "ZWO_DEW_1" → "ZWO_DEW_SN_..."), and a remembered old-id entry plus
    // a fresh discovery would otherwise open TWO live connections to one device. Caller holds _gate.
    private SwitchConnection? FindConnectionLocked(DiscoveredDeviceDto device) =>
        _connections.TryGetValue(device.UniqueId, out var byId)
            ? byId
            : _connections.Values.FirstOrDefault(c => EndpointKey(c.Device) == EndpointKey(device));

    // The connection that serves `device`: the known one, or a new one. The same endpoint under a renamed
    // UniqueId replaces the stale entry: §60.9 — its teardown is published before the replacement's
    // Connecting, so a WS subscriber sees a clean transition. Caller holds _gate.
    private SwitchConnection EnsureConnectionLocked(DiscoveredDeviceDto device) {
        var existing = FindConnectionLocked(device);
        if (existing is not null && existing.Key == device.UniqueId) {
            return existing;
        }
        if (existing is not null) {
            _ = existing.Lifecycle.DisconnectAsync(null);
            _connections.Remove(existing.Key);
            existing.Lifecycle.Dispose();
        }
        var conn = new SwitchConnection(device, ReadbackSettleWindow);
        var key = device.UniqueId;
        conn.Lifecycle = new DeviceLifecycle<ISwitchDevice>(this, DeviceType.Switch, "switch",
            d => _createDevice(d), client => ReadPortsRuntime(conn, client), conn.ResetSessionLocked,
            DeviceHealthTracker.DefaultThreshold, _logger, _events, _faults,
            // #1076 — the fan's switch connecting AFTER the cooler was turned on: the fan sync only fires on a
            // cooler write, so catch up from the camera's current state (best-effort; a failure is a logged
            // fault like any fan-sync failure).
            onConnected: () => _ = Task.Run(() => SyncFanToCoolingCameraAsync(key), CancellationToken.None));
        conn.Lifecycle.RefreshPeriod = _refreshPeriod;
        _connections[key] = conn;
        return conn;
    }

    public Task<OperationAcceptedDto> DisconnectAsync(string deviceId, string? idempotencyKey, CancellationToken ct) {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        SwitchConnection? conn;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _connections.TryGetValue(deviceId, out conn);
        }
        // Supersedes any in-flight connect for this slot, drops the client, and keeps the entry as Disconnected
        // (so GET still reports the known device's state) until it is reconnected, removed or the daemon restarts.
        return conn is not null ? conn.Lifecycle.DisconnectAsync(idempotencyKey)
            : Task.FromResult(Accepted("switch.disconnect", idempotencyKey));
    }

    public async Task<OperationAcceptedDto?> ReconnectAsync(string deviceId, string? idempotencyKey, CancellationToken ct) {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        DiscoveredDeviceDto device;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_connections.TryGetValue(deviceId, out var conn)) {
                return null;
            }
            // The registry keeps a disconnected switch's discovery record (DisconnectAsync leaves
            // the entry as Disconnected), so the reconnect is the ordinary connect of that record.
            device = conn.Device;
        }
        // Outside the lock: ConnectAsync takes _gate itself and handles the already-live /
        // known-but-down cases. A Remove racing in between simply re-adds the device — harmless.
        return await ConnectAsync(new ConnectRequestDto(device), idempotencyKey, ct).ConfigureAwait(false);
    }

    // #1065 — lazily resolved (Func<>) to break the CameraService ↔ SwitchService construction
    // cycle: the camera syncs the fan through this service, this service asks the camera whether
    // it is cooling before it lets the fan stop.
    private readonly Func<ICameraService?>? _cameraProbe;

    private CoolingFanDto CoolingFan => _profileStore?.GetCoolingFan() ?? CoolingFanDto.None;
    private CoolingFanDto _lastCoolingFan;

    // #1700 — a fan designated while the camera is already cooling starts now, the same catch-up as
    // a switch connecting late. Raised for every profile section, so only a changed designation acts.
    private void OnProfileChanged(object? sender, EventArgs e) {
        var fan = CoolingFan;
        if (Equals(Interlocked.Exchange(ref _lastCoolingFan, fan), fan) || fan.DeviceId is not { } deviceId) {
            return;
        }
        _ = Task.Run(() => SyncFanToCoolingCameraAsync(deviceId), CancellationToken.None);
    }

    public async Task SetValueAsync(string deviceId, SwitchValueRequestDto request, CancellationToken ct) {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(request);
        // ASCOM addresses ports by a short id; validate before anything else so an out-of-range
        // PortId is the documented 400 regardless of connection or interlock state.
        ThrowIfPortIdOutOfRange(request);
        // §25.5.6 / #1065 fan-off interlock, daemon-side: a write that would stop the cooling fan
        // is refused (→ 409) unless the camera's cooler is KNOWN to be off. Decided from the cached
        // port snapshot (name + min) of a CONNECTED switch; the connection refusal itself stays with
        // the write path.
        var refusal = await FanOffRefusalForAsync(deviceId, request, ct).ConfigureAwait(false);
        if (refusal is not null) {
            throw new InvalidOperationException(refusal);
        }
        await SetValueCoreAsync(deviceId, request, ct).ConfigureAwait(false);
    }

    /// <summary>The interlock decision for one write: null when it is not a fan-off (or the port is
    /// not the cooling fan), else the refusal reason. Fails CLOSED: no camera probe wired, or a
    /// probe that throws, both read as "unknown" and refuse.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Interlock probe boundary: a failing camera status read must refuse the fan-off (fail closed), never surface as a 500 from a switch write. CA1031's log-and-recover boundary applies.")]
    private async Task<string?> FanOffRefusalForAsync(string deviceId, SwitchValueRequestDto request, CancellationToken ct) {
        SwitchDto? device;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            device = _connections.TryGetValue(deviceId, out var conn) ? ProjectDto(conn) : null;
        }
        // Not connected (unknown id, Error, Disconnected): the write path's own "not connected"
        // refusal applies — the user must be told the switch is down, not to check the camera.
        if (device is null || device.State != EquipmentConnectionState.Connected) {
            return null;
        }
        if (!CoolingFanInterlock.IsPlausibleFanOff(device, CoolingFan, request)) {
            return null;
        }
        // Either the designated fan port is being driven to its floor, or it isn't in the snapshot
        // yet and the write is to 0 or below. Both ask the camera.
        return CoolingFanInterlock.FanOffRefusal(await ProbeCoolerStateAsync(ct).ConfigureAwait(false));
    }

    /// <summary>The camera's cooler state for the interlock (#1076): null = unknown (no probe wired,
    /// the probe threw, camera in Error, or an unreadable CoolerOn this pass) — fail closed. No DTO
    /// (no camera device ever configured) reads as off, like a Disconnected camera.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Interlock probe boundary: a failing camera status read must read as unknown (fail closed), never surface as a 500 from a switch write. CA1031's log-and-recover boundary applies.")]
    private async Task<bool?> ProbeCoolerStateAsync(CancellationToken ct) {
        try {
            var camera = _cameraProbe?.Invoke();
            if (camera is null) {
                return null;
            }
            return CoolingFanInterlock.CoolerStateFor(await camera.GetAsync(ct).ConfigureAwait(false));
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw; // a genuine caller cancel (the sequencer's) propagates; it is not an unknown state
        } catch (Exception ex) {
            LogFanInterlockProbeFailed(_logger, ex);
            return null;
        }
    }

    /// <summary>#1076 — the sequencer's SetSwitchValue goes through the same fan-off interlock as the
    /// REST write; the refusal (null = allowed) is the instruction's failure reason.</summary>
    internal Task<string?> FanOffRefusalForSequencerAsync(string deviceId, short portId, double value, CancellationToken ct) =>
        FanOffRefusalForAsync(deviceId, new SwitchValueRequestDto(portId, value), ct);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Cooling-fan interlock: camera cooler state could not be read; refusing the fan-off (fail closed).")]
    private static partial void LogFanInterlockProbeFailed(ILogger logger, Exception ex);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Catch-up fan sync boundary: a failing camera probe or fan write on a late-connecting switch must be a logged fault, never fault the connect task. CA1031's log-and-recover boundary applies.")]
    private async Task SyncFanToCoolingCameraAsync(string deviceId) {
        try {
            var fan = CoolingFan;
            if (fan.DeviceId != deviceId || fan.PortId is not { } portId) {
                return; // not the fan switch (most rigs) — decided on the designation, before any wait
            }
            if (await GetAsync(deviceId, CancellationToken.None).ConfigureAwait(false) is not { State: EquipmentConnectionState.Connected }) {
                return; // designated ahead of its connect (a profile switch): that connect runs this catch-up
            }
            // The connect's own RefreshCacheOnce is a CAS'd no-op while a timer pass or another
            // switch's connect is already refreshing (review of #1084), and a pass can skip a port
            // whose read failed, so the fan port may not be in the snapshot yet. Wait for it (the
            // next 2 s tick at the latest) rather than deciding without it and never coming back —
            // this is a one-shot task.
            var device = await WaitForPortAsync(deviceId, portId, PortsWaitBudget).ConfigureAwait(false);
            if (await ProbeCoolerStateAsync(CancellationToken.None).ConfigureAwait(false) != true) {
                return; // the camera is not (known to be) cooling — nothing to catch up
            }
            var sync = device is null ? null : CoolingFanInterlock.FanSyncRequest([device], fan, cooling: true);
            if (sync is null) {
                throw new InvalidOperationException("the designated fan port was not readable and writable in time");
            }
            await SetValueCoreAsync(sync.Value.DeviceId, sync.Value.Request, CancellationToken.None).ConfigureAwait(false);
            LogFanCaughtUp(_logger, deviceId);
        } catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException) {
            // A daemon shutdown racing the catch-up is not a fan fault (review of #1084).
        } catch (Exception ex) {
            LogFanCatchUpFailed(_logger, ex, deviceId);
            _faults?.Publish(new EquipmentFaultEvent(DeviceType.Switch, deviceId, null, EquipmentFaultKind.OpError,
                $"the camera is cooling, but the cooling fan on the newly connected switch could not be started ({ex.GetType().Name}) — check the fan",
                DateTimeOffset.UtcNow));
        }
    }

    /// <summary>How long the late-connect catch-up waits for the fan port's first read: well
    /// past one refresh interval, so a CAS-skipped seed refresh is covered by the next tick.</summary>
    private static readonly TimeSpan PortsWaitBudget = TimeSpan.FromSeconds(10);

    /// <summary>The switch's DTO once the fan port has been read (cache polling only, no device I/O
    /// of our own); null if it is gone, disposed, or the port never lands within the budget.</summary>
    private async Task<SwitchDto?> WaitForPortAsync(string deviceId, int portId, TimeSpan budget) {
        var deadline = DateTimeOffset.UtcNow + budget;
        while (true) {
            var device = await GetAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
            if (device is null || device.State != EquipmentConnectionState.Connected) {
                return null;
            }
            if (device.Ports.Any(p => p.Id == portId)) {
                return device;
            }
            if (DateTimeOffset.UtcNow >= deadline) {
                return null;
            }
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Cooling fan on switch '{Device}' started to match the camera that was already cooling (#1076).")]
    private static partial void LogFanCaughtUp(ILogger logger, string device);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Cooling fan catch-up on switch '{Device}' failed.")]
    private static partial void LogFanCatchUpFailed(ILogger logger, Exception ex, string device);

    /// <summary>The camera's post-cooler fan sync (<see cref="ICoolingFanActuator"/>): the plain
    /// write, no fan-off interlock — the cooler-off that motivates it has just been committed.</summary>
    public Task SetFanValueAsync(string deviceId, SwitchValueRequestDto request, CancellationToken ct) {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(request);
        return SetValueCoreAsync(deviceId, request, ct);
    }

    // ASCOM addresses ports by a short id; validate before the narrowing cast so an out-of-range
    // PortId fails loudly instead of silently wrapping to a different port (e.g. (short)32768 ==
    // -32768). Called before the connection lookup AND before the interlock so the range contract
    // holds regardless of state.
    private static void ThrowIfPortIdOutOfRange(SwitchValueRequestDto request) {
        if (request.PortId is < 0 or > short.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(request), request.PortId,
                "PortId is out of range for an ASCOM Switch (0..32767).");
        }
    }

    private async Task SetValueCoreAsync(string deviceId, SwitchValueRequestDto request, CancellationToken ct) {
        ThrowIfPortIdOutOfRange(request);
        ISwitchDevice? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _connections.TryGetValue(deviceId, out var conn)
                && conn.State == EquipmentConnectionState.Connected ? conn.Client : null;
        }
        if (client is null) {
            throw new InvalidOperationException($"switch '{deviceId}' is not connected");
        }
        // Write off the request thread (blocking ASCOM HTTP PUT). The port id is a short in ASCOM.
        await Task.Run(() => client.SetSwitchValue((short)request.PortId, request.Value), ct).ConfigureAwait(false);
        // §42.4 — the write succeeded: remember what was commanded so the refresh loop can check
        // the read-back. Only recorded against the still-live connection+client (a concurrent
        // disconnect/replace supersedes the write we just did — same staleness rule as the cache).
        RecordCommandedValue(client, (short)request.PortId, request.Value);
        // Reflect the new value promptly (best-effort; the next timer tick would also pick it up).
        RefreshCacheOnce();
    }

    // Refreshes every connected switch once (a write, or the mediator's Poll, wants the new value promptly;
    // each lifecycle's own timer keeps the cache fresh otherwise). Single-flighted per connection.
    private void RefreshCacheOnce() {
        List<DeviceLifecycle<ISwitchDevice>>? lifecycles = null;
        lock (_gate) {
            if (_disposed) {
                return;
            }
            foreach (var conn in _connections.Values) {
                (lifecycles ??= new()).Add(conn.Lifecycle);
            }
        }
        if (lifecycles is null) {
            return;
        }
        foreach (var lifecycle in lifecycles) {
            lifecycle.Refresh();
        }
    }

    // The ports read the lifecycle runs each tick, after its §42.3 connection probe answered. `Connected`
    // alone is not proof of life. A bridge may implement it as PER-CLIENT session state (our session stays
    // "connected" no matter what the hardware is doing), and a driver may keep serving static metadata —
    // MaxSwitch, GetSwitchName — from a table while the link to the device is dead. Observed on a Gemini PDH
    // ADV3 after a USB replug: Connected true, MaxSwitch 24, and every GetSwitchValue throwing
    // "communications compromised". The device advertised 24 ports and delivered none, yet stayed Connected
    // forever with an empty card. So: advertising ports but reading none is the essential read failing, and
    // a longer streak of it (below) declares the device lost.
    private DeviceRead ReadPortsRuntime(SwitchConnection conn, ISwitchDevice client) {
        // §42.4 — one best-effort policy read per pass (profile IO stays off the gate).
        var tolerancePct = ReadTolerancePct();
        var (advertised, ports) = ReadPorts(client);
        // A device is failing when it won't say how many ports it has, or claims some and delivers none.
        var unreadable = PortsUnreadable(advertised, ports.Count);
        var lost = false;
        List<(SwitchPortSnapshot Port, double Commanded)>? mismatched = null;
        List<(short PortId, double Commanded)>? recommand = null;
        var readError = unreadable
            ? advertised is null ? "the device would not report how many ports it has"
                : $"the device advertises {advertised} ports but none could be read"
            : null;
        return new DeviceRead(readError,
            CommitLocked: () => {
                // This signal is WEAKER than the `Connected` probe and gets its own, longer streak:
                // `Connected` is often served from the bridge's session state and keeps succeeding through a
                // stall, so a transient burst of failing value reads (a bridge under load, a brief network
                // stall, a driver reload) must not tear a working switch down to Error and demand a manual
                // Reconnect. The tick counter and the streak are both mutated under the gate with the
                // live-connection guard, so a concurrent reconnect's reset can't be clobbered by an
                // in-flight slow read.
                if (unreadable) {
                    conn.UnreadableTicksSinceConnect++;
                    // Give a freshly-connected device a few ticks before counting these against it: a USB
                    // re-enumeration or a driver that answers Connected before its first status frame would
                    // otherwise be torn down while it is still coming up.
                    lost = conn.UnreadableTicksSinceConnect > PortReadGraceTicks
                        && conn.PortsProbe.Observe(probeSucceeded: false) == ProbeVerdict.Lost;
                    return; // no ports to cache either way
                }
                conn.PortsProbe.Reset();
                conn.CachedSnapshots = ports;
                // §42.4 — read-back check for ports the daemon wrote, under the same commit lock as the
                // liveness check (the #789 one-critical-section lesson). Verdicts are collected here, acted
                // on off-lock in AfterCommit.
                foreach (var port in ports) {
                    var verdict = conn.Readback.Observe(port.Id, port.Value, port.Min, port.Max,
                        tolerancePct, DateTimeOffset.UtcNow);
                    if (verdict == ReadbackVerdict.Mismatch) {
                        (mismatched ??= []).Add((port, conn.Readback.CommandedFor(port.Id) ?? double.NaN));
                    } else if (verdict == ReadbackVerdict.Recommand) {
                        (recommand ??= []).Add((port.Id, conn.Readback.CommandedFor(port.Id) ?? double.NaN));
                    }
                }
            },
            AfterCommit: () => {
                if (lost) {
                    LogPortsUnreadable(conn.Device.Name, advertised ?? -1);
                    conn.Lifecycle.TripConnectionLost(client,
                        $"answered as connected but delivered no readable ports on {PortsLostThreshold} consecutive reads");
                    return;
                }
                if (recommand is not null) {
                    // §42.2 rows 15/16 — one best-effort re-issue of the same value before the fault fires.
                    // Deliberately a raw device write (not RunSwitchWriteAsync, whose Command() recording
                    // would restart the episode and erase the one-recommand budget); the watch already
                    // re-armed its settle window.
                    foreach (var (portId, commanded) in recommand) {
                        if (!double.IsNaN(commanded)) {
                            RecommandQuietly(conn, client, portId, commanded);
                        }
                    }
                }
                if (mismatched is not null) {
                    foreach (var (port, commanded) in mismatched) {
                        PublishValueMismatch(conn, port, commanded, tolerancePct);
                    }
                }
            });
    }

    // Reads MaxSwitch then each port independently: a port that fails to read is skipped rather
    // than failing the whole snapshot.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-port read boundary: a switch port read can throw driver/HTTP exceptions; that port is skipped, never propagated. CA1031's log-and-recover boundary applies.")]
    private (short? Advertised, IReadOnlyList<SwitchPortSnapshot> Ports) ReadPorts(ISwitchDevice c) {
        short max;
        try {
            max = c.MaxSwitch;
        } catch (Exception ex) {
            LogPortReadFailed(ex);
            // null, NOT 0: "the device never told us" must stay distinguishable
            // from "the device says it has no ports". Reporting 0 here would let
            // a device whose metadata read fails fall through to a SUCCESSFUL
            // probe, resetting the streak every tick so it could never trip —
            // exactly the state this detection exists to catch.
            return (null, Array.Empty<SwitchPortSnapshot>());
        }
        var ports = new List<SwitchPortSnapshot>(max);
        for (short i = 0; i < max; i++) {
            try {
                ports.Add(new SwitchPortSnapshot(
                    Id: i,
                    Name: c.GetSwitchName(i),
                    Description: ReadDescription(c, i),
                    Value: c.GetSwitchValue(i),
                    Min: c.MinSwitchValue(i),
                    Max: c.MaxSwitchValue(i),
                    StepSize: c.SwitchStep(i),
                    CanWrite: c.CanWrite(i)));
            } catch (Exception ex) {
                LogPortUnavailable(i, ex);
            }
        }
        return (max, ports);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: the description is cosmetic — a driver that throws from GetSwitchDescription must not knock the whole port out of the snapshot the way a failed value/range read does. CA1031's log-and-recover boundary applies.")]
    private static string ReadDescription(ISwitchDevice c, short id) {
        try {
            return c.GetSwitchDescription(id) ?? string.Empty;
        } catch (Exception) {
            return string.Empty;
        }
    }

    internal static List<SwitchPortDto> ProjectPorts(IReadOnlyList<SwitchPortSnapshot> snapshots) {
        var ports = new List<SwitchPortDto>(snapshots.Count);
        foreach (var s in snapshots) {
            ports.Add(new SwitchPortDto(s.Id, s.Name, s.Value, s.Min, s.Max, s.CanWrite, s.StepSize));
        }
        return ports;
    }

    // §42.4 — remember a successful write's commanded value against the still-live connection
    // + client (a concurrent disconnect/replace supersedes the write — same staleness rule as
    // the snapshot commit); shared by the REST SetValueAsync and the mediator write path.
    private void RecordCommandedValue(ISwitchDevice client, short portId, double value) {
        List<SwitchConnection> live;
        lock (_gate) {
            live = _connections.Values.ToList();
        }
        foreach (var conn in live) {
            lock (conn.Lifecycle.Gate) {
                if (conn.Lifecycle.IsLive(client)) {
                    conn.Readback.Command(portId, value, DateTimeOffset.UtcNow);
                    return;
                }
            }
        }
    }

    // Fallback when no profile store is wired / the read fails — matches the DTO's ctor default.
    private const double DefaultTolerancePct = 5.0;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort policy read on the refresh tick: a profile store fault falls back to the default tolerance rather than skipping the whole pass. CA1031's log-and-recover boundary applies.")]
    private double ReadTolerancePct() {
        try {
            return _profileStore is null ? DefaultTolerancePct : _profileStore.GetSafetyPolicies().SwitchValueTolerancePct;
        } catch (Exception ex) {
            LogToleranceReadFailed(ex);
            return DefaultTolerancePct;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort; a broadcaster/serialization fault must never abort the refresh pass — the hub fault (published first) already carries the detection. CA1031's log-and-recover boundary applies.")]
    private void PublishValueMismatch(SwitchConnection conn, SwitchPortSnapshot port, double commanded, double tolerancePct) {
        LogValueMismatch(conn.Device.Name, port.Id, port.Name, commanded, port.Value);
        _faults?.Publish(new EquipmentFaultEvent(DeviceType.Switch, conn.Device.UniqueId, conn.Device.Name,
            EquipmentFaultKind.ValueMismatch,
            $"port {port.Id} ('{port.Name}') commanded {commanded:0.##}, reads {port.Value:0.##} (tolerance ±{tolerancePct:0.##}% of range)",
            DateTimeOffset.UtcNow));
        if (_ws is null) {
            return;
        }
        try {
            var payload = new System.Text.Json.Nodes.JsonObject {
                ["device_id"] = conn.Device.UniqueId,
                ["device_name"] = conn.Device.Name,
                ["port_id"] = port.Id,
                ["port_name"] = port.Name,
                ["commanded"] = commanded,
                ["read_back"] = port.Value,
                ["tolerance_pct"] = tolerancePct,
            };
            // ToJsonString()+Parse is the AOT-safe JsonElement construction (EquipmentFaultHub pattern).
            using var doc = System.Text.Json.JsonDocument.Parse(payload.ToJsonString());
            _ = _ws.PublishAsync(WsEventCatalog.SwitchValueMismatch, doc.RootElement.Clone(), CancellationToken.None);
        } catch (Exception ex) {
            LogMismatchPublishFailed(ex);
        }
    }

    // §42.2 rows 15/16 — the one pre-fault recovery attempt. Fire-and-forget off the refresh
    // tick: the outcome is judged by the watch's next read-backs, not by this call.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort recovery write: a failed re-command simply leaves the port mismatched and the watch fires the fault after the re-armed window; the failure is logged, never propagated into the refresh tick or left as an unobserved task exception. CA1031's log-and-recover boundary applies.")]
    private void RecommandQuietly(SwitchConnection conn, ISwitchDevice client, short portId, double commanded) {
        LogRecommand(conn.Device.Name, portId, commanded);
        _ = Task.Run(() => {
            try {
                client.SetSwitchValue(portId, commanded);
            } catch (Exception ex) {
                LogRecommandFailed(ex, conn.Device.Name, portId);
            }
        }, CancellationToken.None);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Switch '{Device}' port {PortId} read-back disagrees with the commanded value — re-issuing {Commanded} once before declaring a fault (§42.2)")]
    private partial void LogRecommand(string device, short portId, double commanded);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Switch '{Device}' port {PortId}: the §42.2 re-command write failed — the read-back watch will fault after the settle window")]
    private partial void LogRecommandFailed(Exception ex, string device, short portId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Switch: safety-policy read for the §42.4 read-back tolerance failed — using the default")]
    private partial void LogToleranceReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Switch: failed to broadcast the switch.value_mismatch WS event — the equipment.fault publish already carried the detection")]
    private partial void LogMismatchPublishFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Switch '{Device}' port {PortId} ('{PortName}') read-back disagrees with the commanded value: commanded {Commanded}, reads {ReadBack} — §42.4 fault published")]
    private partial void LogValueMismatch(string device, short portId, string portName, double commanded, double readBack);

    /// <summary>Whether this tick's read means the device is not answering for its ports:
    /// it would not report a count (null), or it claims some and delivered none. Pure so the
    /// §42.3 rule is testable without an Alpaca device.</summary>
    internal static bool PortsUnreadable(short? advertised, int portsRead) =>
        advertised is null || (advertised > 0 && portsRead == 0);

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    public void Dispose() {
        List<SwitchConnection> connections;
        lock (_gate) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            if (_profileStore is not null) {
                _profileStore.Changed -= OnProfileChanged;
            }
            connections = _connections.Values.ToList();
            _connections.Clear();
        }
        // Each lifecycle stops its timer and releases its client without the courtesy disconnect (a blocking
        // HTTP call that would hang container shutdown if the device is unreachable).
        foreach (var conn in connections) {
            conn.Lifecycle.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Switch '{Device}' advertises {Advertised} ports but none could be read (-1 = it would not report a count) — treating as disconnected (§42.3)")]
    private partial void LogPortsUnreadable(string device, short advertised);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Switch port read failed")]
    private partial void LogPortReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Switch port {Port} unavailable (skipped)")]
    private partial void LogPortUnavailable(int port, Exception ex);
}
