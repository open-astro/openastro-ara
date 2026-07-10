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
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §6 / §10.6 row 7 — real Alpaca-backed Switch service. Connects to one or more discovered Alpaca
/// Switch devices, serves each device's live ports (id/name/value/min/max/can-write) over the REST
/// surface, and writes a port value via <see cref="SetValueAsync"/>.
///
/// <para>Multi-instance: ARA addresses switches by their <c>AlpacaDeviceNumber</c> (the <c>{n}</c> in
/// <c>/api/v1/equipment/switch/{n}</c>). A connection map keyed by that number lets several switches
/// be connected at once — the common multi-switch rig (a power box plus a relay board, each a distinct
/// device number on the bridge). Two switches that share a device number across different Alpaca hosts
/// collide on the key; the later connect replaces the earlier and the collision is logged (a documented
/// limit of device-number addressing).</para>
///
/// Follows the SafetyMonitor/ObservingConditions template: §60.5 202-Accepted connect lifecycle;
/// per-connection generation-based supersede; §32.4 background-refresh cache (a single timer reads
/// every port of every connected device every <see cref="RefreshInterval"/>) so the GET surface serves
/// the cached port list synchronously. A write refreshes the cache so the new value is reflected promptly.
///
/// The Sequencer's <c>ISwitchMediator</c> (one singleton per §8.1) targets the lowest-numbered connected
/// switch as its "primary" — see <c>SwitchService.Mediator.cs</c>. Per-device sequencer targeting is a
/// follow-up.
/// </summary>
public sealed partial class SwitchService : ISwitchService, IDisposable {

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly ILogger<SwitchService> _logger;
    private readonly EquipmentEventPublisher? _events;
    private readonly IEquipmentFaultSink? _faults;
    private readonly IProfileStore? _profileStore;
    private readonly IWsBroadcaster? _ws;
    private readonly object _gate = new();
    private readonly Timer _refreshTimer;
    // Connected (and recently-disconnected) switches keyed by AlpacaDeviceNumber. Mutated only under
    // _gate. A single shared refresh timer reads every entry's ports — keep the one-lock discipline of
    // the original single-instance service, just one record per device.
    private readonly Dictionary<int, SwitchConnection> _connections = new();
    private int _refreshing;
    private bool _disposed;

    /// <summary>Per-device mutable connection state. All fields are read/written under the service
    /// <see cref="_gate"/> (no inner lock), so the generation-supersede + adopt guards of the original
    /// single-instance service carry over unchanged — one record per connected switch.</summary>
    private sealed class SwitchConnection {
        public required DiscoveredDeviceDto Device { get; init; }
        public AlpacaSwitch? Client { get; set; }
        public EquipmentConnectionState State { get; set; } = EquipmentConnectionState.Disconnected;
        public DateTimeOffset LastTransition { get; set; } = DateTimeOffset.UtcNow;
        // Full per-port snapshot (superset of SwitchPortDto: + Description/StepSize, which the
        // ISwitchMediator surface needs for SetSwitchValue.Validate). ProjectPorts projects the DTO view.
        public IReadOnlyList<SwitchPortSnapshot> CachedSnapshots { get; set; } = Array.Empty<SwitchPortSnapshot>();
        public long Generation { get; set; }
        public int DeviceNumber => Device.AlpacaDeviceNumber;
        // §42.3 — per-connection disconnect-detection streak (multi-instance: each connected switch
        // is probed independently). Only touched from the single-flighted refresh path.
        public DeviceConnectionProbe Probe { get; } = new();
        // §42.4 — per-connection commanded-value read-back watch (only ports the daemon wrote).
        public SwitchReadbackWatch Readback { get; } = new();
    }

    public SwitchService(ILogger<SwitchService>? logger = null, EquipmentEventPublisher? events = null,
            IEquipmentFaultSink? faults = null, IProfileStore? profileStore = null, IWsBroadcaster? ws = null) {
        _logger = logger ?? NullLogger<SwitchService>.Instance;
        _events = events;
        _faults = faults;
        _profileStore = profileStore;
        _ws = ws;
        _refreshTimer = new Timer(RefreshTick, state: null, dueTime: RefreshInterval, period: RefreshInterval);
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
            // Stable order by device number so the list (and the client UI built on it) doesn't reshuffle
            // between polls.
            list.Sort(static (a, b) => a.AlpacaDeviceNumber.CompareTo(b.AlpacaDeviceNumber));
            return Task.FromResult<IReadOnlyList<SwitchDto>>(list);
        }
    }

    public Task<SwitchDto?> GetAsync(int deviceNumber, CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Task.FromResult(_connections.TryGetValue(deviceNumber, out var conn) ? ProjectDto(conn) : null);
        }
    }

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
        long generation;
        SwitchConnection conn;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connections.TryGetValue(device.AlpacaDeviceNumber, out var existing)) {
                if ((existing.State == EquipmentConnectionState.Connecting || existing.State == EquipmentConnectionState.Connected)
                    && existing.Device.UniqueId == device.UniqueId) {
                    // Already connecting/connected to this exact device — idempotent, no teardown.
                    return Task.FromResult(Accepted("switch.connect", idempotencyKey));
                }
                if (existing.Device.UniqueId != device.UniqueId) {
                    // Cross-host device-number collision: device-number addressing can only hold one
                    // switch per number, so the new device takes the slot. Logged so it isn't silent.
                    LogSwitchNumberCollision(device.AlpacaDeviceNumber, existing.Device.Name, device.Name);
                }
                // §60.9 — surface the superseded connection's teardown before the
                // replacement's Connecting, so a WS subscriber tracking state deltas
                // sees the old device go away instead of it silently vanishing.
                SetState(existing, EquipmentConnectionState.Disconnected);
                DisposeClientLocked(existing);
            }
            conn = new SwitchConnection { Device = device };
            _connections[device.AlpacaDeviceNumber] = conn;
            generation = ++conn.Generation;
            SetState(conn, EquipmentConnectionState.Connecting);
        }
        _ = Task.Run(() => ConnectInBackground(conn, device, generation), CancellationToken.None);
        return Task.FromResult(Accepted("switch.connect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> DisconnectAsync(int deviceNumber, string? idempotencyKey, CancellationToken ct) {
        AlpacaSwitch? client = null;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connections.TryGetValue(deviceNumber, out var conn)) {
                // Supersede any in-flight connect for this slot, drop the client, and keep the entry as
                // Disconnected (so GET still reports the known device's state, mirroring the single-instance
                // service) until it is reconnected or the daemon restarts.
                conn.Generation++;
                client = conn.Client;
                conn.Client = null;
                conn.CachedSnapshots = Array.Empty<SwitchPortSnapshot>();
                conn.Readback.Reset(); // §42.4 — commanded values don't survive a disconnect
                SetState(conn, EquipmentConnectionState.Disconnected);
            }
        }
        if (client is not null) {
            _ = Task.Run(() => SafeDisconnectDispose(client), CancellationToken.None);
        }
        return Task.FromResult(Accepted("switch.disconnect", idempotencyKey));
    }

    public async Task SetValueAsync(int deviceNumber, SwitchValueRequestDto request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        // ASCOM addresses ports by a short id; validate before the narrowing cast so an out-of-range
        // PortId fails loudly instead of silently wrapping to a different port (e.g. (short)32768 ==
        // -32768). Checked before the connection lookup so the range contract holds regardless of state.
        if (request.PortId is < 0 or > short.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(request), request.PortId,
                "PortId is out of range for an ASCOM Switch (0..32767).");
        }
        AlpacaSwitch? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _connections.TryGetValue(deviceNumber, out var conn)
                && conn.State == EquipmentConnectionState.Connected ? conn.Client : null;
        }
        if (client is null) {
            throw new InvalidOperationException($"switch {deviceNumber} is not connected");
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

    private void RefreshTick(object? state) => RefreshCacheOnce();

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Timer-callback boundary: an unhandled exception in a System.Threading.Timer callback crashes the process. The per-port reads already absorb device failures; this catch-all is a hard backstop so a refresh tick can never fault the daemon. CA1031's log-and-recover boundary applies.")]
    private void RefreshCacheOnce() {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) {
            return;
        }
        try {
            // Snapshot the connected (connection, client) pairs under the gate, then read each device's
            // ports off-lock. One pass refreshes every connected switch SEQUENTIALLY under the single
            // _refreshing guard, so the effective per-device refresh rate degrades with device count (a
            // slow ASCOM read on switch A delays switch B's). Fine for the handful-of-switches case this
            // targets; if large switch farms ever appear, parallelize the reads or guard per-connection.
            List<(SwitchConnection Conn, AlpacaSwitch Client)>? targets = null;
            lock (_gate) {
                if (_disposed) {
                    return;
                }
                foreach (var conn in _connections.Values) {
                    if (conn.State == EquipmentConnectionState.Connected && conn.Client is not null) {
                        (targets ??= new List<(SwitchConnection, AlpacaSwitch)>()).Add((conn, conn.Client));
                    }
                }
            }
            if (targets is null) {
                return;
            }
            // §42.4 — one best-effort policy read per pass (profile IO stays off the gate).
            var tolerancePct = ReadTolerancePct();
            foreach (var (conn, client) in targets) {
                // §42.3 — the ONE deliberate connection probe per device per tick. The per-port reads
                // below deliberately swallow failures (an unsupported port must stay benign), so
                // this probe is the only disconnect-detection source: Connected throws on transport
                // death and reads false on a driver-side disconnect; a consecutive-failure streak
                // (not one blip) trips the device to Error + publishes the §42.2 fault.
                if (!ProbeConnected(client)) {
                    if (ObserveProbeIfLive(conn, client, probeSucceeded: false) == ProbeVerdict.Lost) {
                        TripConnectionLost(conn);
                    }
                    continue; // this device didn't answer — skip its reads, not the whole tick
                }
                ObserveProbeIfLive(conn, client, probeSucceeded: true);
                // Isolate each device: a read failure (ASCOM timeout / network hiccup) on one switch
                // must not drop the rest of this tick's devices from the refresh.
                try {
                    var ports = ReadPorts(client);
                    List<(SwitchPortSnapshot Port, double Commanded)>? mismatched = null;
                    lock (_gate) {
                        // Write back only if this exact connection + client is still the live one (a
                        // concurrent disconnect/replace supersedes the read we just did).
                        if (!_disposed
                            && _connections.TryGetValue(conn.DeviceNumber, out var current)
                            && ReferenceEquals(current, conn)
                            && conn.State == EquipmentConnectionState.Connected
                            && ReferenceEquals(conn.Client, client)) {
                            conn.CachedSnapshots = ports;
                            // §42.4 — read-back check for ports the daemon wrote, under the same
                            // commit lock as the liveness check (the #789 one-critical-section
                            // lesson). Verdicts are collected here, published off-lock below.
                            foreach (var port in ports) {
                                if (conn.Readback.Observe(port.Id, port.Value, port.Min, port.Max,
                                        tolerancePct, DateTimeOffset.UtcNow) == ReadbackVerdict.Mismatch) {
                                    (mismatched ??= []).Add((port, conn.Readback.CommandedFor(port.Id) ?? double.NaN));
                                }
                            }
                        }
                    }
                    if (mismatched is not null) {
                        foreach (var (port, commanded) in mismatched) {
                            PublishValueMismatch(conn, port, commanded, tolerancePct);
                        }
                    }
                } catch (Exception ex) {
                    LogPortReadFailed(ex);
                }
            }
        } catch (Exception ex) {
            // Backstop for anything outside the per-device loop (the timer callback must never fault).
            LogPortReadFailed(ex);
        } finally {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    // Reads MaxSwitch then each port independently: a port that fails to read is skipped rather
    // than failing the whole snapshot.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-port read boundary: a switch port read can throw driver/HTTP exceptions; that port is skipped, never propagated. CA1031's log-and-recover boundary applies.")]
    private IReadOnlyList<SwitchPortSnapshot> ReadPorts(AlpacaSwitch c) {
        short max;
        try {
            max = c.MaxSwitch;
        } catch (Exception ex) {
            LogPortReadFailed(ex);
            return Array.Empty<SwitchPortSnapshot>();
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
        return ports;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: the description is cosmetic — a driver that throws from GetSwitchDescription must not knock the whole port out of the snapshot the way a failed value/range read does. CA1031's log-and-recover boundary applies.")]
    private static string ReadDescription(AlpacaSwitch c, short id) {
        try {
            return c.GetSwitchDescription(id) ?? string.Empty;
        } catch (Exception) {
            return string.Empty;
        }
    }

    private static List<SwitchPortDto> ProjectPorts(IReadOnlyList<SwitchPortSnapshot> snapshots) {
        var ports = new List<SwitchPortDto>(snapshots.Count);
        foreach (var s in snapshots) {
            ports.Add(new SwitchPortDto(s.Id, s.Name, s.Value, s.Min, s.Max, s.CanWrite));
        }
        return ports;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background connect boundary: constructing the Alpaca client and setting Connected can throw arbitrary driver/HTTP/socket exceptions; any escape must surface as the Error state and be contained, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of the AlpacaSwitch is managed explicitly: on every non-adopt path SafeDisconnectDispose disposes it; on the adopt path it is stored in the connection (local set to null) and disposed later by DisconnectAsync/Dispose. CA2000 cannot follow the transfer through the lock + helper.")]
    private void ConnectInBackground(SwitchConnection conn, DiscoveredDeviceDto device, long generation) {
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
                if (!_disposed
                    && _connections.TryGetValue(conn.DeviceNumber, out var current)
                    && ReferenceEquals(current, conn)
                    && conn.Generation == generation) {
                    conn.Client = client;
                    conn.CachedSnapshots = Array.Empty<SwitchPortSnapshot>(); // don't serve a prior device's ports
                    conn.Probe.Reset();    // §42.3 — a fresh session starts a fresh streak
                    conn.Readback.Reset(); // §42.4 — a fresh session has no commanded values
                    SetState(conn, EquipmentConnectionState.Connected);
                    adopted = true;
                }
            }
            if (!adopted) {
                SafeDisconnectDispose(client);
                return;
            }
            client = null; // ownership transferred to the connection
            RefreshCacheOnce(); // seed the port cache through the guarded path
            bool stillConnected;
            lock (_gate) {
                stillConnected = conn.State == EquipmentConnectionState.Connected
                    && _connections.TryGetValue(conn.DeviceNumber, out var current)
                    && ReferenceEquals(current, conn);
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
                    if (!_disposed
                        && _connections.TryGetValue(conn.DeviceNumber, out var current)
                        && ReferenceEquals(current, conn)
                        && conn.Generation == generation) {
                        SetState(conn, EquipmentConnectionState.Error);
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

    // Caller holds _gate. Drops the connection's client off-thread (blocking teardown must not run
    // under the lock).
    private void DisposeClientLocked(SwitchConnection conn) {
        var c = conn.Client;
        conn.Client = null;
        if (c is not null) {
            _ = Task.Run(() => SafeDisconnectDispose(c), CancellationToken.None);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The probe's whole job is turning any transport/driver exception into a failed-probe observation. CA1031's log-and-recover boundary applies.")]
    private static bool ProbeConnected(AlpacaSwitch c) {
        try { return c.Connected; } catch (Exception) { return false; }
    }

    // §42.3 — declare the device lost: Connected → Error (keeps the connection entry remembered so
    // a reconnect can find it; the state publisher already maps Error to
    // equipment.connection_failed for WILMA's chips) + publish the §42.2 fault event.
    // Only a probe of the LIVE connection + client may feed its streak: the blocking probe runs
    // outside _gate, so a concurrent disconnect/replace can have superseded the entry while the
    // stale probe was still in flight. The liveness check and the Observe are ONE operation under
    // _gate — a separate check-then-observe leaves a gap for ConnectInBackground's Reset to land
    // between them (review finding; the connect path's fresh-SwitchConnection-per-reconnect
    // invariant makes that benign today, but the probe path now defends it itself). Returns null
    // when the probed pair is stale (the observation is discarded).
    // §42.4 — remember a successful write's commanded value against the still-live connection
    // + client (a concurrent disconnect/replace supersedes the write — same staleness rule as
    // the snapshot commit); shared by the REST SetValueAsync and the mediator write path.
    private void RecordCommandedValue(AlpacaSwitch client, short portId, double value) {
        lock (_gate) {
            foreach (var conn in _connections.Values) {
                if (conn.State == EquipmentConnectionState.Connected && ReferenceEquals(conn.Client, client)) {
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Switch: safety-policy read for the §42.4 read-back tolerance failed — using the default")]
    private partial void LogToleranceReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Switch: failed to broadcast the switch.value_mismatch WS event — the equipment.fault publish already carried the detection")]
    private partial void LogMismatchPublishFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Switch '{Device}' port {PortId} ('{PortName}') read-back disagrees with the commanded value: commanded {Commanded}, reads {ReadBack} — §42.4 fault published")]
    private partial void LogValueMismatch(string device, short portId, string portName, double commanded, double readBack);

    private ProbeVerdict? ObserveProbeIfLive(SwitchConnection conn, AlpacaSwitch client, bool probeSucceeded) {
        lock (_gate) {
            if (_disposed
                || conn.State != EquipmentConnectionState.Connected
                || !_connections.TryGetValue(conn.DeviceNumber, out var current)
                || !ReferenceEquals(current, conn)
                || !ReferenceEquals(conn.Client, client)) {
                return null;
            }
            return conn.Probe.Observe(probeSucceeded);
        }
    }

    private void TripConnectionLost(SwitchConnection conn) {
        lock (_gate) {
            // Only trip the connection we probed if it is still the live entry for its device
            // number and still Connected (a concurrent disconnect/replace supersedes the probe).
            if (conn.State != EquipmentConnectionState.Connected
                || !_connections.TryGetValue(conn.DeviceNumber, out var current)
                || !ReferenceEquals(current, conn)) {
                return;
            }
            SetState(conn, EquipmentConnectionState.Error);
            conn.Probe.Reset();
            conn.Readback.Reset();
        }
        LogConnectionLost(conn.Device.Name);
        _faults?.Publish(new EquipmentFaultEvent(DeviceType.Switch, conn.Device.UniqueId, conn.Device.Name,
            EquipmentFaultKind.Disconnected,
            $"stopped answering {DeviceConnectionProbe.DefaultLostThreshold} consecutive connection probes",
            DateTimeOffset.UtcNow));
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Switch '{Device}' stopped answering — marked Error (§42.3)")]
    private partial void LogConnectionLost(string device);

    // Caller must hold _gate (every call site already does), so no inner lock here.
    // Instance (not static) since §60.9: transitions publish equipment.* events.
    private void SetState(SwitchConnection conn, EquipmentConnectionState state) {
        if (conn.State == state) {
            return;
        }
        conn.State = state;
        conn.LastTransition = DateTimeOffset.UtcNow;
        // Callers hold the service lock; the publisher's synchronous part only
        // serializes a small payload and hands off (see EquipmentEventPublisher).
        _events?.StateChanged(DeviceType.Switch, conn.Device.UniqueId, conn.Device.Name, state);
    }

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    public void Dispose() {
        var clients = new List<AlpacaSwitch>();
        lock (_gate) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            foreach (var conn in _connections.Values) {
                if (conn.Client is not null) {
                    clients.Add(conn.Client);
                    conn.Client = null;
                }
            }
            _connections.Clear();
        }
        _refreshTimer.Dispose();
        // Dispose each client directly (guarded), not via SafeDisconnectDispose: the courtesy
        // "Connected = false" is a blocking HTTP call (up to ~3s ASCOM timeout) that would hang
        // container shutdown if the device is unreachable. DisposeQuietly releases resources only.
        foreach (var client in clients) {
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Two Switch devices share Alpaca device number {Number} ('{Existing}' replaced by '{Incoming}'); device-number addressing keeps only the latest.")]
    private partial void LogSwitchNumberCollision(int number, string existing, string incoming);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ignored error while disconnecting Switch during teardown")]
    private partial void LogTeardownIgnored(Exception ex);
}
