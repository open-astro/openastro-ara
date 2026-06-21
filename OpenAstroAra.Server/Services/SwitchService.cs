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
    }

    public SwitchService(ILogger<SwitchService>? logger = null) {
        _logger = logger ?? NullLogger<SwitchService>.Instance;
        _refreshTimer = new Timer(RefreshTick, state: null, dueTime: RefreshInterval, period: RefreshInterval);
    }

    // ct unused by design: ports are served from the §32.4 cache, so there is no per-call HTTP read to
    // cancel. Returns a Task to satisfy the async interface without an await.
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
            // ports off-lock. One pass refreshes every connected switch.
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
            foreach (var (conn, client) in targets) {
                var ports = ReadPorts(client);
                lock (_gate) {
                    // Write back only if this exact connection + client is still the live one (a concurrent
                    // disconnect/replace supersedes the read we just did).
                    if (!_disposed
                        && _connections.TryGetValue(conn.DeviceNumber, out var current)
                        && ReferenceEquals(current, conn)
                        && conn.State == EquipmentConnectionState.Connected
                        && ReferenceEquals(conn.Client, client)) {
                        conn.CachedSnapshots = ports;
                    }
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

    // Caller must hold _gate (every call site already does), so no inner lock here.
    private static void SetState(SwitchConnection conn, EquipmentConnectionState state) {
        conn.State = state;
        conn.LastTransition = DateTimeOffset.UtcNow;
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
