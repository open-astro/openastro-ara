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
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e — ninth real Alpaca-backed device service. Replaces <c>PlaceholderTelescopeService</c>:
/// connects to a discovered Alpaca Telescope (mount), serves its capabilities + live runtime
/// (RA/Dec + tracking/parked/at-home) over the §6 REST surface, and drives it via three §60.5
/// 202-Accepted background operations — <see cref="SlewAsync"/> (goto / sync), <see cref="ParkAsync"/>,
/// <see cref="UnparkAsync"/> — plus two prompt synchronous writes, <see cref="SetTrackingAsync"/> and
/// <see cref="AbortSlewAsync"/> (the §57 panic stop). Follows the established template (generation
/// supersede, §32.4 background-refresh cache, read-once capabilities, AbortSlew-before-disconnect).
/// REST-only — the <c>ITelescopeMediator</c> unification is the follow-up.
/// </summary>
public sealed partial class TelescopeService : ITelescopeService, IDisposable {

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TelescopeStateDto IdleRuntime = new("idle", null, null, false, false, false);

    private readonly ILogger<TelescopeService> _logger;
    private readonly EquipmentEventPublisher? _events;
    private readonly IEquipmentFaultSink? _faults;
    private readonly DeviceConnectionProbe _probe = new();
    private readonly MountTrackingWatch _trackingWatch = new();
    private readonly object _gate = new();
    private readonly Timer _refreshTimer;
    private AlpacaTelescope? _client;
    private DiscoveredDeviceDto? _device;
    private EquipmentConnectionState _state = EquipmentConnectionState.Disconnected;
    private TelescopeCapabilitiesDto? _capabilities;
    private TelescopeStateDto _runtime = IdleRuntime;
    // Mount-native coordinate system, consumed by the ITelescopeMediator partial (coordinate
    // transform + GetInfo epoch). EquatorialCoordinateType has no "unknown" member; Other is the
    // honest pre-read sentinel (the mediator maps it to JNOW). Unlike the read-once capabilities, the
    // read is retried each refresh until ONE confirmed success (_equatorialSystemKnown): a transient
    // failure on the first pass must not permanently freeze the sentinel — a J2000 mount stuck on
    // "Other" would silently receive un-precession-corrected slew targets forever.
    private EquatorialCoordinateType _equatorialSystemRaw = EquatorialCoordinateType.Other;
    private bool _equatorialSystemKnown;
    private int _refreshing;
    private int _refreshPending;
    private long _connectGeneration;
    private bool _disposed;

    public TelescopeService(ILogger<TelescopeService>? logger = null, EquipmentEventPublisher? events = null,
            IEquipmentFaultSink? faults = null) {
        _logger = logger ?? NullLogger<TelescopeService>.Instance;
        _events = events;
        _faults = faults;
        _refreshTimer = new Timer(RefreshTick, state: null, dueTime: RefreshInterval, period: RefreshInterval);
    }

    public Task<TelescopeDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_device is null) {
                return Task.FromResult<TelescopeDto?>(null);
            }
            var state = _state;
            var connected = state == EquipmentConnectionState.Connected;
            var runtime = connected ? _runtime : IdleRuntime;
            var caps = connected ? _capabilities : null;
            return Task.FromResult<TelescopeDto?>(new TelescopeDto(_device.UniqueId, _device.Name, state, caps, runtime));
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
                return Task.FromResult(Accepted("telescope.connect", idempotencyKey));
            }
            DisposeClientLocked();
            _device = device;
            generation = ++_connectGeneration;
            SetState(EquipmentConnectionState.Connecting);
        }
        _ = Task.Run(() => ConnectInBackground(device, generation), CancellationToken.None);
        return Task.FromResult(Accepted("telescope.connect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) {
        AlpacaTelescope? client;
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
        return Task.FromResult(Accepted("telescope.disconnect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> SlewAsync(SlewRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        // Dispose check first, then argument range, then connection state — aligned with the other
        // device services.
        AlpacaTelescope? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _state == EquipmentConnectionState.Connected ? _client : null;
        }
        if (IsCoordinateOutOfRange(request.RightAscensionHours, request.DeclinationDegrees)) {
            throw new ArgumentOutOfRangeException(nameof(request), request,
                "RightAscensionHours must be in [0, 24) and DeclinationDegrees in [-90, 90].");
        }
        if (client is null) {
            throw new InvalidOperationException("telescope is not connected");
        }
        var ra = request.RightAscensionHours;
        var dec = request.DeclinationDegrees;
        var sync = request.Sync ?? false;
        NoteMountCommand(w => w.NoteMotionCommanded());
        _ = Task.Run(() => SlewInBackground(client, ra, dec, sync), CancellationToken.None);
        return Task.FromResult(Accepted(sync ? "telescope.sync" : "telescope.slew", idempotencyKey));
    }

    public Task<OperationAcceptedDto> ParkAsync(ParkRequestDto request, string? idempotencyKey, CancellationToken ct) {
        // No ArgumentNullException.ThrowIfNull(request): the /park endpoint substitutes
        // `request ?? new ParkRequestDto()`, so request is never null here, and the Reason field is
        // unused by the park itself — the param is kept only for the interface contract.
        _ = request;
        var client = RequireConnectedClient();
        NoteMountCommand(w => w.NoteParkCommanded());
        _ = Task.Run(() => RunControlInBackground("telescope.park", client, c => {
            // Some mounts (e.g. iOptron) won't park from a stationary/home state with tracking off —
            // enable tracking first (best-effort), exactly as ConformU/NINA do before parking, then
            // issue the park. The park itself stops tracking once the mount reaches the park position.
            TryEnableTracking(c);
            c.Park();
        }), CancellationToken.None);
        return Task.FromResult(Accepted("telescope.park", idempotencyKey));
    }

    public Task<OperationAcceptedDto> UnparkAsync(string? idempotencyKey, CancellationToken ct) {
        var client = RequireConnectedClient();
        NoteMountCommand(w => w.NoteMotionCommanded());
        _ = Task.Run(() => RunControlInBackground("telescope.unpark", client, c => c.Unpark()), CancellationToken.None);
        return Task.FromResult(Accepted("telescope.unpark", idempotencyKey));
    }

    public Task<OperationAcceptedDto> FindHomeAsync(string? idempotencyKey, CancellationToken ct) {
        var client = RequireConnectedClient();
        // FindHome blocks until the mount reaches its home switch; run it off the request
        // thread like Park/Unpark so the endpoint returns 202 and the refresh cache surfaces
        // AtHome when it settles. The panel gates this on CanFindHome.
        // No TryEnableTracking here (unlike Park): the iOptron homing that motivated the park
        // workaround executes fine from a stationary state — verified on-device, the mount moves
        // to home with tracking off — so homing doesn't need the motors pre-engaged.
        // Park semantics for the watch (matching the mediator's RunMountOpAsync): homing ends
        // idle-not-parked with tracking off, so a motion note would leave the old expectation
        // armed and fire a spurious TrackingLost ~6 s after a normal Find Home (review finding).
        NoteMountCommand(w => w.NoteParkCommanded());
        _ = Task.Run(() => RunControlInBackground("telescope.findhome", client, c => c.FindHome()), CancellationToken.None);
        return Task.FromResult(Accepted("telescope.findhome", idempotencyKey));
    }

    public async Task SetTrackingAsync(bool enabled, CancellationToken ct) {
        var client = RequireConnectedClient();
        // Prompt synchronous write off the request thread (blocking ASCOM HTTP PUT), then reflect.
        // CancellationToken.None (not ct): Task.Run(lambda, ct) never schedules the lambda if ct is
        // already cancelled, which would silently skip the write — the same hazard as the abort path.
        NoteMountCommand(w => w.NoteTrackingCommanded(enabled));
        await Task.Run(() => client.Tracking = enabled, CancellationToken.None).ConfigureAwait(false);
        RefreshCacheOnce();
    }

    public async Task MoveAxisAsync(int axis, double rate, CancellationToken ct) {
        // ct is intentionally NOT honored — see the ITelescopeService.MoveAxisAsync contract: a stop
        // must always run to completion even if the request token cancelled, or a dropped token could
        // strand the mount in motion (the same panic-stop reasoning as AbortSlewAsync). Discarded
        // explicitly so the omission reads as deliberate, not an oversight.
        _ = ct;
        var client = RequireConnectedClient();
        // Manual nudge: start (rate != 0) or stop (rate 0) constant-rate motion on one axis. The
        // direction pad sends a rate on press and 0 on release; AbortSlew (the Stop button) is the
        // backstop that halts all axes.
        NoteMountCommand(w => w.NoteMotionCommanded());
        await Task.Run(() => client.MoveAxis((TelescopeAxis)axis, rate), CancellationToken.None).ConfigureAwait(false);
        RefreshCacheOnce();
    }

    public async Task AbortSlewAsync(CancellationToken ct) {
        var client = RequireConnectedClient();
        // §57 panic stop: issue AbortSlew immediately (universal — every mount supports it).
        // CancellationToken.None (not ct) is critical: Task.Run(lambda, ct) returns a pre-cancelled
        // task WITHOUT running the lambda if ct is already cancelled, so an HTTP-timeout/disconnect
        // that cancelled the token would silently never send the abort and the slew would continue.
        NoteMountCommand(w => w.NoteMotionCommanded());
        await Task.Run(() => client.AbortSlew(), CancellationToken.None).ConfigureAwait(false);
        RefreshCacheOnce();
    }

    private AlpacaTelescope RequireConnectedClient() {
        AlpacaTelescope? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _state == EquipmentConnectionState.Connected ? _client : null;
        }
        return client ?? throw new InvalidOperationException("telescope is not connected");
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background slew boundary: the blocking ASCOM SlewToCoordinatesAsync/SyncToCoordinates can throw arbitrary driver/HTTP exceptions, and a concurrent Disconnect/Dispose can dispose the captured client mid-call; any escape must be contained and logged, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    private void SlewInBackground(AlpacaTelescope client, double raHours, double decDeg, bool sync) {
        try {
            if (sync) {
                // Sync recalibrates the pointing model to the given coordinates without moving.
                client.SyncToCoordinates(raHours, decDeg);
            } else {
                // Async goto: returns immediately, Slewing goes true; the refresh cache surfaces
                // State="slewing" until the mount settles.
                client.SlewToCoordinatesAsync(raHours, decDeg);
            }
            RefreshCacheOnce();
        } catch (Exception ex) {
            LogSlewFailed(ex, raHours, decDeg);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background control boundary: the blocking ASCOM Park/Unpark can throw arbitrary driver/HTTP exceptions, and a concurrent Disconnect/Dispose can dispose the captured client mid-call; any escape must be contained and logged, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    private void RunControlInBackground(string op, AlpacaTelescope client, Action<AlpacaTelescope> action) {
        try {
            action(client);
            RefreshCacheOnce();
        } catch (Exception ex) {
            LogControlFailed(ex, op);
        }
    }

    private void RefreshTick(object? state) => RefreshCacheOnce();

    // Single-flight WITH coalescing: the 2s timer AND the post-write reflects in SetTrackingAsync/
    // AbortSlewAsync all funnel here. A refresh requested while one is already running is NOT dropped
    // — the in-flight owner makes one more pass after it finishes, so a write that lands mid-tick is
    // reflected without waiting out the next 2s period (a tick that started reading before the write
    // would otherwise commit the pre-write value and leave the cache stale for up to 2s). The owner
    // never blocks on HTTP under a lock, so refreshes can't pile up thread-per-tick. (A follow-up
    // request that races in during the window between the drain loop's exit and the _refreshing
    // release is rare and self-heals on the next timer tick — no worse than the pre-coalescing 2s.)
    private void RefreshCacheOnce() {
        Volatile.Write(ref _refreshPending, 1);
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) {
            return; // another thread owns the drain loop; we've flagged a follow-up pass for it
        }
        try {
            while (Interlocked.Exchange(ref _refreshPending, 0) == 1) {
                RefreshPass();
            }
        } finally {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Timer-callback boundary: an unhandled exception escaping here (the timer tick / drain loop) crashes the process. The per-field reads already absorb device failures; this catch-all is a hard backstop so a refresh pass can never fault the daemon. CA1031's log-and-recover boundary applies.")]
    private void RefreshPass() {
        try {
            AlpacaTelescope? client;
            bool needCaps;
            bool needEquatorialSystem;
            lock (_gate) {
                if (_disposed || _state != EquipmentConnectionState.Connected) {
                    return;
                }
                client = _client;
                needCaps = _capabilities is null;
                needEquatorialSystem = !_equatorialSystemKnown;
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
                if (ObserveProbeIfLive(client, probeSucceeded: false) == ProbeVerdict.Lost) {
                    TripConnectionLost(client);
                }
                return; // the device didn't answer — skip this pass's reads
            }
            ObserveProbeIfLive(client, probeSucceeded: true);
            var runtime = ReadRuntime(client);
            var caps = needCaps ? ReadCapabilities(client) : null;
            // Retried every pass until one read succeeds (null = read failed; a genuine "Other" from
            // the device counts as success and stops the retries).
            var equatorialSystem = needEquatorialSystem ? ReadEquatorialSystem(client) : null;
            var trackingVerdict = TrackingWatchVerdict.Idle;
            DiscoveredDeviceDto? watchedDevice = null;
            lock (_gate) {
                if (_state == EquipmentConnectionState.Connected && ReferenceEquals(_client, client)) {
                    _runtime = runtime;
                    if (caps is not null) {
                        _capabilities = caps;
                    }
                    if (equatorialSystem is not null) {
                        _equatorialSystemRaw = equatorialSystem.Value;
                        _equatorialSystemKnown = true;
                    }
                    // §42.2 — tracking-lost watch, observed under the same commit lock so the
                    // liveness check and the observation are one critical section (the #789
                    // probe lesson); command notes take this gate too.
                    trackingVerdict = _trackingWatch.Observe(
                        runtime.Tracking, slewing: runtime.State == "slewing", parked: runtime.Parked);
                    watchedDevice = _device;
                }
            }
            if (trackingVerdict == TrackingWatchVerdict.Lost) {
                LogTrackingLost(watchedDevice?.Name ?? "?");
                _faults?.Publish(new EquipmentFaultEvent(DeviceType.Telescope,
                    watchedDevice?.UniqueId, watchedDevice?.Name, EquipmentFaultKind.TrackingLost,
                    $"tracking reported off for {MountTrackingWatch.DefaultDropThreshold} consecutive status ticks with no daemon command",
                    DateTimeOffset.UtcNow));
            } else if (trackingVerdict == TrackingWatchVerdict.Recovered) {
                LogTrackingRecovered(watchedDevice?.Name ?? "?");
            }
        } catch (Exception ex) {
            LogRuntimeReadFailed(ex);
        }
    }

    // §42.2 — every daemon-issued mount command is noted BEFORE its device write is dispatched,
    // so a refresh tick that commits after the command already sees the grace window (a command
    // noted after dispatch could race a tick observing its effect and accuse the mount).
    private void NoteMountCommand(Action<MountTrackingWatch> note) {
        lock (_gate) {
            note(_trackingWatch);
        }
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Telescope '{Device}' silently dropped tracking — §42.2 fault published")]
    private partial void LogTrackingLost(string device);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Telescope '{Device}' tracking recovered — watch re-armed (§42.2)")]
    private partial void LogTrackingRecovered(string device);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported/transiently-failing telescope property throws; that field falls back to its default rather than failing the whole runtime read. CA1031's log-and-recover boundary applies.")]
    private static TelescopeStateDto ReadRuntime(AlpacaTelescope c) {
        bool slewing;
        try { slewing = c.Slewing; } catch (Exception) { slewing = false; }
        double? ra;
        try { ra = c.RightAscension; } catch (Exception) { ra = null; }
        double? dec;
        try { dec = c.Declination; } catch (Exception) { dec = null; }
        bool tracking;
        try { tracking = c.Tracking; } catch (Exception) { tracking = false; }
        bool parked;
        try { parked = c.AtPark; } catch (Exception) { parked = false; }
        bool atHome;
        try { atHome = c.AtHome; } catch (Exception) { atHome = false; }

        // State precedence: a slew in progress wins (the mount is moving); then a parked mount (a
        // definite rest state); then active tracking; otherwise idle.
        var state = slewing ? "slewing"
            : parked ? "parked"
            : tracking ? "tracking"
            : "idle";
        return new TelescopeStateDto(state, ra, dec, tracking, parked, atHome);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported capability property throws; each flag falls back to false (and the rate list to empty) rather than failing the whole capability read. The RA/Dec range is fixed, so there is no essential field whose failure should null the whole DTO. CA1031's log-and-recover boundary applies.")]
    private static TelescopeCapabilitiesDto ReadCapabilities(AlpacaTelescope c) {
        // Read each slew capability in its OWN try: an older driver can THROW NotImplemented from
        // CanSlewAsync, and a combined `CanSlewAsync || CanSlew` in one try would then fall to false
        // even when sync CanSlew is true. CanSlew is true if the mount supports either goto mode.
        bool canSlewAsync = false;
        try { canSlewAsync = c.CanSlewAsync; } catch (Exception) { canSlewAsync = false; }
        bool canSlewSync = false;
        try { canSlewSync = c.CanSlew; } catch (Exception) { canSlewSync = false; }
        var canSlew = canSlewAsync || canSlewSync;
        bool canSync;
        try { canSync = c.CanSync; } catch (Exception) { canSync = false; }
        bool canPark;
        try { canPark = c.CanPark; } catch (Exception) { canPark = false; }
        bool canUnpark;
        try { canUnpark = c.CanUnpark; } catch (Exception) { canUnpark = false; }
        bool canSetTracking;
        try { canSetTracking = c.CanSetTracking; } catch (Exception) { canSetTracking = false; }
        bool canPulseGuide;
        try { canPulseGuide = c.CanPulseGuide; } catch (Exception) { canPulseGuide = false; }
        bool canFindHome;
        try { canFindHome = c.CanFindHome; } catch (Exception) { canFindHome = false; }
        // Optics: ASCOM reports these in METRES; convert via OpticsMmFromMetres
        // (×1000, non-positive → null). Each in its own try (most mounts
        // NotImplement them → null).
        double? focalLengthMm = null;
        try { focalLengthMm = OpticsMmFromMetres(c.FocalLength); } catch (Exception) { focalLengthMm = null; }
        double? apertureMm = null;
        try { apertureMm = OpticsMmFromMetres(c.ApertureDiameter); } catch (Exception) { apertureMm = null; }
        bool canMoveAxis;
        // The direction pad drives BOTH axes (N/S = secondary, E/W = primary, corners = both), so
        // gate it conservatively on the mount supporting MoveAxis on each — a mount that can move only
        // one axis would silently fail the other half of the pad. The rate list below is read from the
        // primary axis and assumed representative of both (the near-universal case for GEM/alt-az mounts).
        try {
            canMoveAxis = c.CanMoveAxis(TelescopeAxis.Primary) && c.CanMoveAxis(TelescopeAxis.Secondary);
        } catch (Exception) {
            canMoveAxis = false;
        }
        return new TelescopeCapabilitiesDto(
            CanSlew: canSlew, CanSync: canSync, CanPark: canPark, CanUnpark: canUnpark,
            CanSetTracking: canSetTracking, CanPulseGuide: canPulseGuide, CanFindHome: canFindHome,
            SupportedSiderealRates: ReadSiderealRates(c),
            FocalLengthMm: focalLengthMm, ApertureDiameterMm: apertureMm,
            CanMoveAxis: canMoveAxis,
            MoveAxisRatesDegPerSec: ReadAxisRates(c));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Capability read boundary: AxisRates throws on a mount that NotImplements MoveAxis; an empty list (no manual-control speeds offered) is the correct degraded result rather than failing the whole capability read. CA1031's log-and-recover boundary applies.")]
    private static List<double> ReadAxisRates(AlpacaTelescope c) {
        // The direction pad drives BOTH axes with the same picked rate, but ASCOM allows each axis its
        // own rate range, so a primary-only rate could be rejected on the secondary (N/S). Offer only
        // rates the secondary axis can also honour: take the primary set, cap it at the secondary's
        // max. Each read is best-effort (returns empty/null on a NotImplemented throw).
        var primary = ReadAxisRateSet(c, TelescopeAxis.Primary);
        if (primary.Count == 0) {
            return [];
        }
        var secondaryMax = ReadAxisMaxRate(c, TelescopeAxis.Secondary);
        var usable = secondaryMax is > 0
            ? primary.Where(r => r <= secondaryMax.Value + 1e-9).ToList()
            : [.. primary];
        // If the secondary is so much slower that nothing survives the cap, fall back to the primary
        // set rather than show no speeds at all (better a rate the secondary may clamp than none).
        return usable.Count > 0 ? usable : [.. primary];
    }

    // Both [Minimum, Maximum] endpoints of every rate band for one axis (discrete rate → Min==Max),
    // ascending + deduped; empty when the axis NotImplements MoveAxis/AxisRates.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Capability read boundary: AxisRates throws on a mount that NotImplements MoveAxis for the axis; an empty set (no speeds offered) is the correct degraded result. CA1031's log-and-recover boundary applies.")]
    private static SortedSet<double> ReadAxisRateSet(AlpacaTelescope c, TelescopeAxis axis) {
        var rates = new SortedSet<double>();
        try {
            foreach (IRate rate in c.AxisRates(axis)) {
                if (rate.Minimum > 0) {
                    rates.Add(rate.Minimum);
                }
                if (rate.Maximum > 0) {
                    rates.Add(rate.Maximum);
                }
            }
        } catch (Exception) {
            return [];
        }
        return rates;
    }

    // The fastest rate one axis can move (max of its bands' maxima), or null if unreadable.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Capability read boundary: AxisRates throws on a mount that NotImplements the axis; null (no cap applied) is the correct degraded result. CA1031's log-and-recover boundary applies.")]
    private static double? ReadAxisMaxRate(AlpacaTelescope c, TelescopeAxis axis) {
        double max = 0;
        try {
            foreach (IRate rate in c.AxisRates(axis)) {
                if (rate.Maximum > max) {
                    max = rate.Maximum;
                }
            }
        } catch (Exception) {
            return null;
        }
        return max > 0 ? max : null;
    }

    /// <summary>Convert an ASCOM optics property (metres) to mm for the wire, or
    /// null when the driver reports a non-positive value (an unconfigured 0 / a
    /// bogus negative) — so the wizard never auto-fills a useless 0. Internal for
    /// direct unit testing (the read itself sits behind a sealed Alpaca client).</summary>
    internal static double? OpticsMmFromMetres(double metres) =>
        metres > 0 ? metres * 1000.0 : null;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported/transiently-failing EquatorialSystem read throws; report null ('read failed, retry next pass') rather than failing the whole refresh — RefreshPass distinguishes a failed read from a device genuinely reporting Other. CA1031's log-and-recover boundary applies.")]
    private static EquatorialCoordinateType? ReadEquatorialSystem(AlpacaTelescope c) {
        try {
            return c.EquatorialSystem;
        } catch (Exception) {
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: TrackingRates (or enumerating it) can throw on a driver that does not implement it; the supported-rate list falls back to empty rather than failing the whole capability read. CA1031's log-and-recover boundary applies.")]
    private static List<string> ReadSiderealRates(AlpacaTelescope c) {
        var rates = new List<string>();
        try {
            foreach (DriveRate rate in c.TrackingRates) {
                rates.Add(rate.ToString());
            }
        } catch (Exception) {
            // Discard any rates accumulated before a mid-enumeration failure so we never report a
            // partial/torn list — an all-or-nothing read (empty == "rates unknown").
            rates.Clear();
        }
        return rates;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background connect boundary: constructing the Alpaca client and setting Connected can throw arbitrary driver/HTTP/socket exceptions; any escape must surface as the Error state and be contained, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of the AlpacaTelescope is managed explicitly: on every non-adopt path SafeDisconnectDispose disposes it; on the adopt path it is stored in _client (local set to null) and disposed later by DisconnectAsync/Dispose. CA2000 cannot follow the transfer through the lock + helper.")]
    private void ConnectInBackground(DiscoveredDeviceDto device, long generation) {
        AlpacaTelescope? client = null;
        var adopted = false;
        try {
            var host = string.IsNullOrWhiteSpace(device.IpAddress) ? device.HostName : device.IpAddress;
            if (string.IsNullOrWhiteSpace(host)) {
                throw new InvalidOperationException(
                    $"discovered device '{device.Name}' carries neither an IP address nor a host name");
            }
            client = new AlpacaTelescope(
                device.UseHttps ? ServiceType.Https : ServiceType.Http,
                host, device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null);
            client.Connected = true;
            lock (_gate) {
                if (!_disposed && _connectGeneration == generation) {
                    _client = client;
                    _capabilities = null;       // re-read for the new device
                    _equatorialSystemRaw = EquatorialCoordinateType.Other; // "not yet read" until the first successful read
                    _equatorialSystemKnown = false;
                    _runtime = IdleRuntime;     // don't serve a prior device's runtime
                    _probe.Reset();             // §42.3 — a fresh session starts a fresh streak
                    _trackingWatch.Reset();     // §42.2 — no expectations carry across sessions
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
        Justification = "Best-effort teardown: AbortSlew / Connected = false can throw driver/HTTP exceptions, which must be swallowed so Dispose always runs and cleanup completes. CA1031's log-and-recover boundary applies.")]
    private void SafeDisconnectDispose(AlpacaTelescope client) {
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
    private void DisposeQuietly(AlpacaTelescope client) {
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
    private static bool ProbeConnected(AlpacaTelescope c) {
        try { return c.Connected; } catch (Exception) { return false; }
    }

    // Only a probe of the LIVE client may feed the streak: the blocking probe runs outside
    // _gate, so a concurrent reconnect can have swapped the client (and reset the probe)
    // while a stale probe of the dead session was still in flight. The liveness check and
    // the Observe are ONE operation under _gate — a separate check-then-observe leaves a
    // gap for ConnectInBackground's Reset to land between them (review finding). Returns
    // null when the probed client is stale (the observation is discarded).
    private ProbeVerdict? ObserveProbeIfLive(AlpacaTelescope client, bool probeSucceeded) {
        lock (_gate) {
            return ReferenceEquals(_client, client) ? _probe.Observe(probeSucceeded) : null;
        }
    }

    // §42.3 — declare the device lost: Connected → Error (keeps _device remembered so a
    // reconnect can find it; the state publisher already maps Error to
    // equipment.connection_failed for WILMA's chips) + publish the §42.2 fault event.
    // Re-checks the probed client's identity under the gate: a reconnect between the probe
    // and this trip must never flip the NEW, healthy session to Error (review finding).
    private void TripConnectionLost(AlpacaTelescope probed) {
        DiscoveredDeviceDto? device;
        lock (_gate) {
            if (_state != EquipmentConnectionState.Connected || !ReferenceEquals(_client, probed)) {
                return;
            }
            device = _device;
            SetState(EquipmentConnectionState.Error);
            _probe.Reset();
            _trackingWatch.Reset();
        }
        LogConnectionLost(device?.Name ?? "?");
        _faults?.Publish(new EquipmentFaultEvent(DeviceType.Telescope, device?.UniqueId, device?.Name,
            EquipmentFaultKind.Disconnected,
            $"stopped answering {DeviceConnectionProbe.DefaultLostThreshold} consecutive connection probes",
            DateTimeOffset.UtcNow));
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Telescope '{Device}' stopped answering — marked Error (§42.3)")]
    private partial void LogConnectionLost(string device);

    // Caller must hold _gate (every call site already does), so no inner lock here.
    private void SetState(EquipmentConnectionState state) {
        if (_state == state) {
            return;
        }
        _state = state;
        // Callers hold the service lock; the publisher's synchronous part only
        // serializes a small payload and hands off (see EquipmentEventPublisher).
        _events?.StateChanged(DeviceType.Telescope, _device?.UniqueId, _device?.Name, state);
    }

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    // Extracted (internal) for direct unit testing. ASCOM equatorial coordinates: RA in [0, 24)
    // hours, Dec in [-90, 90] degrees (the poles are valid).
    internal static bool IsCoordinateOutOfRange(double raHours, double decDeg) =>
        raHours < 0 || raHours >= 24 || decDeg < -90 || decDeg > 90;

    public void Dispose() {
        AlpacaTelescope? client;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telescope runtime read failed")]
    private partial void LogRuntimeReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telescope slew to RA {Ra}h Dec {Dec}° failed")]
    private partial void LogSlewFailed(Exception ex, double ra, double dec);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telescope control op {Op} failed")]
    private partial void LogControlFailed(Exception ex, string op);

    [LoggerMessage(Level = LogLevel.Information, Message = "Telescope connected: {Name} at {Host}:{Port}/{Device}")]
    private partial void LogConnected(string name, string host, int port, int device);

    [LoggerMessage(Level = LogLevel.Error, Message = "Telescope connect failed for {Name}")]
    private partial void LogConnectFailed(Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ignored error while disconnecting Telescope during teardown")]
    private partial void LogTeardownIgnored(Exception ex);
}
