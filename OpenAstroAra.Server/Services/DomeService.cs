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
using ASCOM.Common.DeviceInterfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e — eighth real Alpaca-backed device service. Replaces <c>PlaceholderDomeService</c>:
/// connects to a discovered Alpaca Dome, serves its live runtime (state + azimuth + shutter/home/park
/// flags) over the §6 REST surface, and drives it via four §60.5 202-Accepted background operations —
/// <see cref="SlewAsync"/>, <see cref="ParkAsync"/>, <see cref="OpenShutterAsync"/>,
/// <see cref="CloseShutterAsync"/>. Follows the established control-device template (generation
/// supersede, §32.4 background-refresh cache, AbortSlew-before-disconnect). REST-only — the
/// <c>IDomeMediator</c> unification is the follow-up.
/// </summary>
public sealed partial class DomeService : IDomeService, IDisposable {

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly DomeStateDto IdleRuntime = new("idle", null, false, false, false);

    private readonly ILogger<DomeService> _logger;
    private readonly EquipmentEventPublisher? _events;
    private readonly IEquipmentFaultSink? _faults;
    private readonly DeviceConnectionProbe _probe = new();
    private readonly object _gate = new();
    private readonly Timer _refreshTimer;
    private AlpacaDome? _client;
    private DiscoveredDeviceDto? _device;
    private EquipmentConnectionState _state = EquipmentConnectionState.Disconnected;
    private DomeStateDto _runtime = IdleRuntime;
    // Read-once capabilities + the raw ASCOM shutter status, cached for the IDomeMediator GetInfo()
    // snapshot (the REST DomeDto doesn't expose them). Populated by the §32.4 refresh; reset on adopt.
    private DomeCaps? _domeCaps;
    // Error maps to NINA ShutterError ("not yet read") so GetInfo honestly signals "unknown" in the
    // ≤2s window between connect and the first refresh, rather than a possibly-false ShutterClosed.
    private ShutterState _shutterStatusRaw = ShutterState.Error;
    private int _refreshing;
    private long _connectGeneration;
    private bool _disposed;

    public DomeService(ILogger<DomeService>? logger = null, EquipmentEventPublisher? events = null,
            IEquipmentFaultSink? faults = null) {
        _logger = logger ?? NullLogger<DomeService>.Instance;
        _events = events;
        _faults = faults;
        _refreshTimer = new Timer(RefreshTick, state: null, dueTime: RefreshInterval, period: RefreshInterval);
    }

    public Task<DomeDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // null ONLY when no device has ever been selected (-> 404). After a disconnect the
            // device is retained and the DTO is returned with Disconnected state + idle runtime,
            // consistent across all the device services.
            if (_device is null) {
                return Task.FromResult<DomeDto?>(null);
            }
            var state = _state;
            var runtime = state == EquipmentConnectionState.Connected ? _runtime : IdleRuntime;
            // Surface the read-once capabilities so the client can enable only the
            // controls the dome supports (shutter / azimuth-slew / park / home).
            var caps = state == EquipmentConnectionState.Connected && _domeCaps is { } c
                ? new DomeCapabilitiesDto(
                    c.CanSetShutter, c.CanSetAzimuth, c.CanSyncAzimuth, c.CanPark, c.CanFindHome, c.CanSetPark)
                : null;
            return Task.FromResult<DomeDto?>(
                new DomeDto(_device.UniqueId, _device.Name, state, caps, runtime));
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
                return Task.FromResult(Accepted("dome.connect", idempotencyKey));
            }
            DisposeClientLocked();
            _device = device;
            generation = ++_connectGeneration;
            SetState(EquipmentConnectionState.Connecting);
        }
        _ = Task.Run(() => ConnectInBackground(device, generation), CancellationToken.None);
        return Task.FromResult(Accepted("dome.connect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) {
        AlpacaDome? client;
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
        return Task.FromResult(Accepted("dome.disconnect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> SlewAsync(DomeSlewRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        // Dispose check first (a disposed service can't be operated regardless of the argument),
        // then argument range, then connection state — aligned with the other device services.
        AlpacaDome? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _state == EquipmentConnectionState.Connected ? _client : null;
        }
        if (IsAzimuthOutOfRange(request.TargetAzimuthDeg)) {
            throw new ArgumentOutOfRangeException(nameof(request), request.TargetAzimuthDeg,
                "TargetAzimuthDeg must be in [0, 360).");
        }
        if (client is null) {
            throw new InvalidOperationException("dome is not connected");
        }
        var target = request.TargetAzimuthDeg;
        RunControl("dome.slew", client, c => c.SlewToAzimuth(target));
        return Task.FromResult(Accepted("dome.slew", idempotencyKey));
    }

    public Task<OperationAcceptedDto> ParkAsync(string? idempotencyKey, CancellationToken ct) {
        var client = RequireConnectedClient();
        RunControl("dome.park", client, c => c.Park());
        return Task.FromResult(Accepted("dome.park", idempotencyKey));
    }

    public Task<OperationAcceptedDto> OpenShutterAsync(string? idempotencyKey, CancellationToken ct) {
        var client = RequireConnectedClient();
        RunControl("dome.shutter.open", client, c => c.OpenShutter());
        return Task.FromResult(Accepted("dome.shutter.open", idempotencyKey));
    }

    public Task<OperationAcceptedDto> CloseShutterAsync(string? idempotencyKey, CancellationToken ct) {
        var client = RequireConnectedClient();
        RunControl("dome.shutter.close", client, c => c.CloseShutter());
        return Task.FromResult(Accepted("dome.shutter.close", idempotencyKey));
    }

    // §25.5.5 — the remaining dome motions. FindHome is a long motion like Slew/Park, so it runs
    // through the same fire-and-forget RunControl (202 semantics, cache refresh on completion).
    public Task<OperationAcceptedDto> FindHomeAsync(string? idempotencyKey, CancellationToken ct) {
        var client = RequireConnectedClient();
        RunControl("dome.findhome", client, c => c.FindHome());
        return Task.FromResult(Accepted("dome.findhome", idempotencyKey));
    }

    // §57 panic-stop shape (mirrors TelescopeService.AbortSlewAsync): issue AbortSlew NOW and await
    // it — not fire-and-forget — so the 202 means the stop was actually sent. CancellationToken.None
    // is critical: Task.Run(lambda, ct) returns a pre-cancelled task WITHOUT running the lambda if
    // ct is already cancelled, so an HTTP-timeout that cancelled the token would silently never send
    // the abort and the dome would keep rotating.
    public async Task<OperationAcceptedDto> AbortSlewAsync(string? idempotencyKey, CancellationToken ct) {
        var client = RequireConnectedClient();
        await Task.Run(() => client.AbortSlew(), CancellationToken.None).ConfigureAwait(false);
        RefreshCacheOnce();
        return Accepted("dome.abort", idempotencyKey);
    }

    // SetPark is a prompt register write (no motion) — await it so a driver rejection surfaces as
    // the request's error instead of a silent background log line.
    public async Task<OperationAcceptedDto> SetParkAsync(string? idempotencyKey, CancellationToken ct) {
        var client = RequireConnectedClient();
        await Task.Run(() => client.SetPark(), CancellationToken.None).ConfigureAwait(false);
        return Accepted("dome.setpark", idempotencyKey);
    }

    // SyncToAzimuth re-labels the current position (no motion) — prompt write, same validation
    // range as Slew.
    public async Task<OperationAcceptedDto> SyncToAzimuthAsync(DomeSlewRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        if (IsAzimuthOutOfRange(request.TargetAzimuthDeg)) {
            throw new ArgumentOutOfRangeException(nameof(request), request.TargetAzimuthDeg,
                "TargetAzimuthDeg must be in [0, 360).");
        }
        var client = RequireConnectedClient();
        var target = request.TargetAzimuthDeg;
        await Task.Run(() => client.SyncToAzimuth(target), CancellationToken.None).ConfigureAwait(false);
        RefreshCacheOnce();
        return Accepted("dome.sync", idempotencyKey);
    }

    private AlpacaDome RequireConnectedClient() {
        AlpacaDome? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _state == EquipmentConnectionState.Connected ? _client : null;
        }
        return client ?? throw new InvalidOperationException("dome is not connected");
    }

    // Shared launcher for the four control ops: fire-and-forget the blocking ASCOM call on the
    // captured client, then refresh the cache so the read-back reflects the new motion immediately.
    private void RunControl(string op, AlpacaDome client, Action<AlpacaDome> action) {
        _ = Task.Run(() => RunControlInBackground(op, client, action), CancellationToken.None);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background control boundary: the blocking ASCOM SlewToAzimuth/Park/OpenShutter/CloseShutter can throw arbitrary driver/HTTP exceptions, and a concurrent Disconnect/Dispose can dispose the captured client mid-call; any escape must be contained and logged, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    private void RunControlInBackground(string op, AlpacaDome client, Action<AlpacaDome> action) {
        try {
            action(client);
            RefreshCacheOnce();
        } catch (Exception ex) {
            LogControlFailed(ex, op);
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
            AlpacaDome? client;
            lock (_gate) {
                if (_disposed || _state != EquipmentConnectionState.Connected) {
                    return;
                }
                client = _client;
            }
            bool needCaps;
            lock (_gate) {
                needCaps = _domeCaps is null;
            }
            if (client is null) {
                return;
            }
            // §42.3 — the ONE deliberate connection probe per tick. The per-field runtime reads
            // below deliberately swallow failures (an unsupported property must stay benign), so
            // this probe is the only disconnect-detection source: Connected throws on transport
            // death and reads false on a driver-side disconnect; a consecutive-failure streak
            // (not one blip) trips the device to Error + publishes the §42.2 fault.
            if (!ProbeConnected(client)) {
                if (_probe.Observe(false) == ProbeVerdict.Lost) {
                    TripConnectionLost();
                }
                return; // the device didn't answer — skip this tick's reads
            }
            _probe.Observe(true);
            var (runtime, rawShutter) = ReadRuntime(client);
            var caps = needCaps ? ReadCaps(client) : (DomeCaps?)null;
            lock (_gate) {
                if (_state == EquipmentConnectionState.Connected && ReferenceEquals(_client, client)) {
                    _runtime = runtime;
                    _shutterStatusRaw = rawShutter;
                    if (caps is not null) {
                        _domeCaps = caps;
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
        Justification = "Per-field read boundary: an unsupported capability property throws; each flag falls back to false rather than failing the whole capability read. Capabilities are static, so this runs once per connection. CA1031's log-and-recover boundary applies.")]
    private static DomeCaps ReadCaps(AlpacaDome c) {
        bool canSetShutter;
        try { canSetShutter = c.CanSetShutter; } catch (Exception) { canSetShutter = false; }
        bool canSetAzimuth;
        try { canSetAzimuth = c.CanSetAzimuth; } catch (Exception) { canSetAzimuth = false; }
        bool canSyncAzimuth;
        try { canSyncAzimuth = c.CanSyncAzimuth; } catch (Exception) { canSyncAzimuth = false; }
        bool canPark;
        try { canPark = c.CanPark; } catch (Exception) { canPark = false; }
        bool canFindHome;
        try { canFindHome = c.CanFindHome; } catch (Exception) { canFindHome = false; }
        bool canSetPark;
        try { canSetPark = c.CanSetPark; } catch (Exception) { canSetPark = false; }
        return new DomeCaps(canSetShutter, canSetAzimuth, canSyncAzimuth, canPark, canFindHome, canSetPark);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported/transiently-failing dome property throws; that field falls back to its default rather than failing the whole runtime read. CA1031's log-and-recover boundary applies.")]
    private static (DomeStateDto Runtime, ShutterState RawShutter) ReadRuntime(AlpacaDome c) {
        bool slewing;
        try { slewing = c.Slewing; } catch (Exception) { slewing = false; }
        double? azimuth;
        try { azimuth = c.Azimuth; } catch (Exception) { azimuth = null; }
        ShutterState shutter;
        try { shutter = c.ShutterStatus; } catch (Exception) { shutter = ShutterState.Error; }
        bool atHome;
        try { atHome = c.AtHome; } catch (Exception) { atHome = false; }
        bool parked;
        try { parked = c.AtPark; } catch (Exception) { parked = false; }

        var shutterOpen = shutter == ShutterState.Open;
        // State precedence: a shutter fault wins over everything so a caller polling for error
        // recovery sees it even mid-slew (the shutter and azimuth drive are independent mechanisms —
        // a jam can co-occur with rotation; azimuth still updates, so motion stays observable). Then
        // motion (slew / shutter transit), then the resting flags.
        var state = shutter == ShutterState.Error ? "error"
            : slewing ? "slewing"
            : (shutter is ShutterState.Opening or ShutterState.Closing) ? "shutter_moving"
            : parked ? "parked"
            : shutterOpen ? "shutter_open"
            : "idle";
        return (new DomeStateDto(state, azimuth, shutterOpen, atHome, parked), shutter);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background connect boundary: constructing the Alpaca client and setting Connected can throw arbitrary driver/HTTP/socket exceptions; any escape must surface as the Error state and be contained, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of the AlpacaDome is managed explicitly: on every non-adopt path SafeDisconnectDispose disposes it; on the adopt path it is stored in _client (local set to null) and disposed later by DisconnectAsync/Dispose. CA2000 cannot follow the transfer through the lock + helper.")]
    private void ConnectInBackground(DiscoveredDeviceDto device, long generation) {
        AlpacaDome? client = null;
        var adopted = false;
        try {
            var host = string.IsNullOrWhiteSpace(device.IpAddress) ? device.HostName : device.IpAddress;
            if (string.IsNullOrWhiteSpace(host)) {
                throw new InvalidOperationException(
                    $"discovered device '{device.Name}' carries neither an IP address nor a host name");
            }
            client = new AlpacaDome(
                device.UseHttps ? ServiceType.Https : ServiceType.Http,
                host, device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null);
            client.Connected = true;
            lock (_gate) {
                if (!_disposed && _connectGeneration == generation) {
                    _client = client;
                    _runtime = IdleRuntime;                  // don't serve a prior device's runtime
                    _domeCaps = null;                        // re-read capabilities for the new device
                    _shutterStatusRaw = ShutterState.Error; // "not yet read" until the first refresh, not a false Closed
                    _probe.Reset();                          // §42.3 — a fresh session starts a fresh streak
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
        Justification = "Best-effort teardown: AbortSlew / Connected = false can throw driver/HTTP exceptions, which must be swallowed so Dispose always runs and cleanup completes. CA1031's log-and-recover boundary applies.")]
    private void SafeDisconnectDispose(AlpacaDome client) {
        try {
            // Interrupt any in-flight slew so a disconnect-during-slew fails fast rather than
            // waiting out the full ASCOM timeout.
            client.AbortSlew();
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
    private void DisposeQuietly(AlpacaDome client) {
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

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The probe's whole job is turning any transport/driver exception into a failed-probe observation. CA1031's log-and-recover boundary applies.")]
    private static bool ProbeConnected(AlpacaDome c) {
        try { return c.Connected; } catch (Exception) { return false; }
    }

    // §42.3 — declare the device lost: Connected → Error (keeps _device remembered so a
    // reconnect can find it; the state publisher already maps Error to
    // equipment.connection_failed for WILMA's chips) + publish the §42.2 fault event.
    private void TripConnectionLost() {
        DiscoveredDeviceDto? device;
        lock (_gate) {
            if (_state != EquipmentConnectionState.Connected) {
                return;
            }
            device = _device;
            SetState(EquipmentConnectionState.Error);
        }
        _probe.Reset();
        LogConnectionLost(device?.Name ?? "?");
        _faults?.Publish(new EquipmentFaultEvent(DeviceType.Dome, device?.UniqueId, device?.Name,
            EquipmentFaultKind.Disconnected,
            $"stopped answering {DeviceConnectionProbe.DefaultLostThreshold} consecutive connection probes",
            DateTimeOffset.UtcNow));
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Dome '{Device}' stopped answering — marked Error (§42.3)")]
    private partial void LogConnectionLost(string device);

    // Caller must hold _gate (every call site already does), so no inner lock here.
    private void SetState(EquipmentConnectionState state) {
        if (_state == state) {
            return;
        }
        _state = state;
        // Callers hold the service lock; the publisher's synchronous part only
        // serializes a small payload and hands off (see EquipmentEventPublisher).
        _events?.StateChanged(DeviceType.Dome, _device?.UniqueId, _device?.Name, state);
    }

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    // Extracted (internal) for direct unit testing. ASCOM dome azimuth is degrees in [0, 360).
    internal static bool IsAzimuthOutOfRange(double azimuthDeg) => azimuthDeg < 0 || azimuthDeg >= 360;

    public void Dispose() {
        AlpacaDome? client;
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
        // AbortSlew/Connected=false are blocking HTTP calls (up to ~3s ASCOM timeout) that would hang
        // container shutdown if the device is unreachable. DisposeQuietly releases resources only.
        if (client is not null) {
            DisposeQuietly(client);
        }
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dome runtime read failed")]
    private partial void LogRuntimeReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dome control op {Op} failed")]
    private partial void LogControlFailed(Exception ex, string op);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dome connected: {Name} at {Host}:{Port}/{Device}")]
    private partial void LogConnected(string name, string host, int port, int device);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dome connect failed for {Name}")]
    private partial void LogConnectFailed(Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ignored error while disconnecting Dome during teardown")]
    private partial void LogTeardownIgnored(Exception ex);
}
