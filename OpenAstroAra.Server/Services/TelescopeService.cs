#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Common.DeviceInterfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using PierSide = OpenAstroAra.Core.Enums.PierSide;

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
public sealed partial class TelescopeService : ITelescopeService, IRetainedDeviceSource, IRecoverableDevice, IMountSpecSheetSource, IDisposable {

    // #1127 — the record the card names. The remembered-selection store and this can diverge
    // (a store-only DELETE /remembered keeps the device here), so the reconnector falls back to it.
    public DiscoveredDeviceDto? RetainedDevice => _lifecycle.RetainedDevice;

    public DesiredConnection? DesiredConnection => _lifecycle.Desired;

    public bool TryRecover(DiscoveredDeviceDto device) => _lifecycle.TryRecover(device);

    private static readonly TelescopeStateDto IdleRuntime = new("idle", null, null, false, false, false, null, null);

    // §57.9 — masks the mount's TargetRA/TargetDec read after a FindHome/Park until a new
    // target command (Ara's or an external one) takes over; see TargetDisplayLatch.
    // Internal for tests, like SlewWatch.
    internal TargetDisplayLatch TargetLatch { get; } = new();

    private readonly ILogger<TelescopeService> _logger;
    private readonly IEquipmentFaultSink? _faults;
    private readonly IWsBroadcaster? _ws;
    // #1229 — the site longitude and the flip window for the meridian bookkeeping GetInfo serves
    // the MeridianFlipTrigger (SiderealTime / TimeToMeridianFlip). Optional: without it both stay
    // NaN and the trigger's own NaN guard keeps it inert, never "flip now".
    private readonly IProfileService? _profileService;
    private readonly MountTrackingWatch _trackingWatch = new();
    // §57.8 — slew lifecycle events from observed IsSlewing transitions (internal for tests).
    internal SlewEventWatch SlewWatch { get; } = new();
    // ADR 0011 — connect, disconnect, forget, supersede, refresh, probe, read health, the clean-read
    // gate and the desired connection state. The class keeps what is the mount's own.
    private readonly DeviceLifecycle<ITelescopeDevice> _lifecycle;
    private Func<DiscoveredDeviceDto, ITelescopeDevice> _createDevice = AlpacaTelescopeDevice.Create;
    private object _gate => _lifecycle.Gate;
    // #1649 — leased direction-pad presses: an axis whose renewals stop is stopped by CheckLeases.
    // The clock and the check cadence are init knobs for tests (a manual clock, no timer).
    internal MoveAxisLease Lease { get; } = new();
    internal TimeProvider LeaseClock { get; init; } = TimeProvider.System;
    internal TimeSpan LeaseCheckInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    private readonly Timer _leaseTimer;
    // #1265 — bench-test knob: re-arms the refresh timer at a shorter cadence. Init-only, never used by the daemon.
    internal TimeSpan RefreshPeriod { init => _lifecycle.RefreshPeriod = value; }

    // Test seam (ADR 0011 rule 4): the adapter the lifecycle builds for a selected device.
    internal Func<DiscoveredDeviceDto, ITelescopeDevice> DeviceFactory { init => _createDevice = value; }
    private TelescopeCapabilitiesDto? _capabilities;
    // #1064/#1072/#1078 — per-axis MoveAxis rate BANDS (deg/s, ASCOM AxisRates [Min,Max] pairs;
    // index = TelescopeAxis), cached for the session so the nudge start path stays a single driver
    // call: an inline AxisRates GET on the press leg let a fast release's MoveAxis(0) overtake the
    // start and leave the axis running (review of #1069). Filled from the SAME two AxisRates reads
    // the capabilities pass makes for the pad's rate list; re-read on later passes only until
    // SETTLED (_axisRatesSettled): both reads completed (an honestly-empty axis is terminal), the
    // mount reports CanMoveAxis false (its AxisRates throw by spec), or AxisRatesSettleWindow of
    // wall clock passed since connect with a read still throwing (time-based, so a burst of
    // command-triggered refreshes cannot burn the retries in a second). Never read on the 2 s poll
    // path once settled; settling with an axis still unknown is logged once. null = unknown,
    // empty = the mount offers no rates for that axis.
    private IReadOnlyList<(double Min, double Max)>?[]? _axisBands;
    private bool _axisRatesSettled;
    private DateTimeOffset _axisRatesDeadline;
    private static readonly TimeSpan AxisRatesSettleWindow = TimeSpan.FromSeconds(30);
    private int _secondaryFallbackLogged;
    private TelescopeStateDto _runtime = IdleRuntime;
    // #1229 — the mount's reported pointing state, read on the same refresh as the runtime. Not
    // part of the REST TelescopeStateDto (no client consumer); the MeridianFlipTrigger reads it
    // through GetInfo for its pier-side projection and the §58.5 flip verification compares it
    // before/after the flip slew. pierUnknown until read, and whenever the mount cannot say.
    private PierSide _sideOfPier = PierSide.pierUnknown;
    // Latched (under _gate) once the driver answers SideOfPier with a not-implemented error, so the
    // 2 s refresh stops spending a GET per tick on a property it will never get (#1238 review);
    // reset with the rest of the per-device state on connect.
    private bool _sideOfPierUnsupported;
    // #1193 — AlpacaBridge latches "Mount communications compromised" after a mount Wi-Fi blip and
    // keeps answering Connected=true, so the §42.3 probe streak never trips; only a disconnect +
    // reconnect of the telescope clears it. The refresh tick recognises the latched error on the
    // per-field reads and trips the mount to Error with a Disconnected fault, which hands it to the
    // §42.3 ladder (bounded reconnects, fault log, notification, card). Guarded by _gate: true from
    // the trip until a tick reads the mount with no driver error, so a bridge that stays latched
    // after the reconnect is one episode, not a trip per tick.
    private bool _bridgeFaultTripped;
    // #1246 — when the Slewing read itself is latched the never-during-a-slew guard cannot see a goto
    // in flight, so SlewWatch's open episode stands in — for at most this long, after which the trip
    // proceeds (a latch that never clears must still reach the ladder).
    internal TimeSpan LatchedSlewHold { get; set; } = TimeSpan.FromMinutes(5);
    private DateTimeOffset? _latchedSlewHoldSince;
    private bool _latchedSlewHoldExpiredLogged;

    public bool IsCleanlyConnected => _lifecycle.HasCleanReadSinceConnect;

    // Mount-native coordinate system, consumed by the ITelescopeMediator partial (coordinate
    // transform + GetInfo epoch). EquatorialCoordinateType has no "unknown" member, so Other is only
    // a placeholder and _equatorialSystemKnown is the truth: until it is set, a mediator slew/sync
    // reads the system on demand and refuses if that fails, and GetInfo reports no pointing (#1124),
    // instead of treating the placeholder as JNOW. Unlike the read-once capabilities, the read is
    // retried each refresh until ONE confirmed success: a transient failure on the first pass must
    // not leave the mount unslewable for the session.
    private EquatorialCoordinateType _equatorialSystemRaw = EquatorialCoordinateType.Other;
    private bool _equatorialSystemKnown;

    public TelescopeService(ILogger<TelescopeService>? logger = null, EquipmentEventPublisher? events = null,
            IEquipmentFaultSink? faults = null, IWsBroadcaster? ws = null, IProfileService? profileService = null,
            Func<IGuiderMediator?>? guiderResolver = null) {
        _logger = logger ?? NullLogger<TelescopeService>.Instance;
        _guiderResolver = guiderResolver;
        _faults = faults;
        _ws = ws;
        _profileService = profileService;
        // onConnected pushes the profile's site into the mount (one source of truth).
        _lifecycle = new DeviceLifecycle<ITelescopeDevice>(this, DeviceType.Telescope, "telescope",
            d => _createDevice(d), ReadTick, ResetSessionLocked, DeviceHealthTracker.DefaultThreshold, _logger, events, faults,
            onConnected: () => PushProfileSite(force: true));
        _leaseTimer = new Timer(_ => CheckLeases(), state: null, Timeout.Infinite, Timeout.Infinite);
    }

    public Task<TelescopeDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_lifecycle.IsDisposed, this);
            var device = _lifecycle.Device;
            if (device is null) {
                return Task.FromResult<TelescopeDto?>(null);
            }
            var state = _lifecycle.State;
            var connected = state == EquipmentConnectionState.Connected;
            var runtime = connected ? _runtime : IdleRuntime;
            var caps = connected ? _capabilities : null;
            return Task.FromResult<TelescopeDto?>(new TelescopeDto(device.UniqueId, device.Name, state, caps, runtime));
        }
    }

    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Device);
        return _lifecycle.ConnectAsync(request.Device, idempotencyKey);
    }

    public Task<bool> ForgetAsync(CancellationToken ct) => _lifecycle.ForgetAsync();

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) {
        ObjectDisposedException.ThrowIf(_lifecycle.IsDisposed, this);
        Lease.CancelAll(); // a user disconnect ends the leases; a §42.3 trip keeps them (an expiry stops the reconnected mount)
        return _lifecycle.DisconnectAsync(idempotencyKey);
    }

    // Caller holds the gate (adopt, disconnect and trip): a fresh, ended or tripped session forgets the last one's reads.
    private void ResetSessionLocked() {
        _capabilities = null;       // re-read for the new device
        _axisBands = null;          // #1064 — per-axis rate bands re-read for the new device
        _axisRatesSettled = false;
        _axisRatesDeadline = DateTimeOffset.UtcNow + AxisRatesSettleWindow;
        Interlocked.Exchange(ref _secondaryFallbackLogged, 0); // same primitive as the read side
        _equatorialSystemRaw = EquatorialCoordinateType.Other; // "not yet read" until the first successful read
        _equatorialSystemKnown = false;
        _positionFrameReadAttempted = false; // #1222 — one on-demand read per connection
        _runtime = IdleRuntime;     // don't serve a prior device's runtime
        _sideOfPier = PierSide.pierUnknown; // #1229 — nor its pier side
        _sideOfPierUnsupported = false;      // the new device may answer it
        _latchedSlewHoldSince = null;
        _latchedSlewHoldExpiredLogged = false;
        // _bridgeFaultTripped is deliberately NOT reset here: the §42.3 ladder's reconnect
        // comes through the adopt, and a bridge still latched after it must stay one
        // episode (#1193). A hand-connected different device clears it on its first clean tick.
        _trackingWatch.Reset();     // §42.2 — no expectations carry across sessions
        SlewWatch.Reset();          // §57.8 — no slew episode carries across sessions
        TargetLatch.Reset();        // §57.9 — a fresh session trusts the mount's own target registers again
        ForgetPushedSite();
    }

    public Task<OperationAcceptedDto> SlewAsync(SlewRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        // Dispose check first, then argument range, then connection state — aligned with the other
        // device services.
        ITelescopeDevice? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_lifecycle.IsDisposed, this);
            client = _lifecycle.Live;
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
        // A sync moves nothing, so guiding carries on. The checks above still throw synchronously.
        return sync ? Task.FromResult(DispatchSlew(client, ra, dec, sync: true, idempotencyKey)) : SlewAfterGuidingStoppedAsync(client, ra, dec, idempotencyKey);
    }

    private async Task<OperationAcceptedDto> SlewAfterGuidingStoppedAsync(ITelescopeDevice client, double ra, double dec, string? idempotencyKey) {
        await StopGuidingBeforeMountMoveAsync("slew").ConfigureAwait(false);
        return DispatchSlew(client, ra, dec, sync: false, idempotencyKey);
    }

    private OperationAcceptedDto DispatchSlew(ITelescopeDevice client, double ra, double dec, bool sync, string? idempotencyKey) {
        NoteMountCommand(w => w.NoteMotionCommanded());
        lock (_gate) {
            // §57.9 — a goto or sync re-arms the target display (a Home/Park may have cleared it).
            TargetLatch.NoteTargetCommand();
            if (!sync) {
                // §57.8 — the upcoming slew_started event carries the commanded target (a sync
                // moves nothing, so it never opens a slew episode).
                SlewWatch.NoteSlewTarget(ra, dec);
            }
        }
        _ = Task.Run(() => SlewInBackground(client, ra, dec, sync), CancellationToken.None);
        return Accepted(sync ? "telescope.sync" : "telescope.slew", idempotencyKey);
    }

    // The guider is resolved lazily (it is built after the mount in the composition root). A REST
    // GoTo / Park / Find Home stops guiding first, exactly as the sequencer's Park and Find Home instructions
    // do: on 2026-10-16 the client's Home button moved the mount under a guiding PHD2, which chased a
    // lost star for nine minutes (313 dropped frames) until the daemon was restarted.
    private readonly Func<IGuiderMediator?>? _guiderResolver;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort pre-step: a guider that will not stop must not block a goto, park or home (the mount move is the safety action); logged and the move proceeds.")]
    private async Task StopGuidingBeforeMountMoveAsync(string op) {
        var guider = _guiderResolver?.Invoke();
        if (guider is null || !guider.GetInfo().Connected) {
            return;
        }
        try {
            // A guider that is not guiding (the safety and emergency parks stop guiding themselves first;
            // an idle guider between targets) is left alone: no RPC, no wait. The read is in-memory.
            if (guider is IGuiderService readable
                    && (await readable.GetAsync(CancellationToken.None).ConfigureAwait(false))?.Runtime.State is "stopped" or "looping" or "star_selected") {
                return;
            }
            // PHD2Guider.StopGuiding largely ignores its token (its RPCs carry their own timeouts, about
            // 13 s on a hung guider), so the bound is enforced here: the move goes ahead when the limit
            // passes, inside the emergency stop's 10 s rung.
            using var cts = new CancellationTokenSource();
            var stop = guider.StopGuiding(cts.Token);
            var finished = await Task.WhenAny(stop, Task.Delay(StopGuidingBeforeMoveTimeout, cts.Token)).ConfigureAwait(false);
            if (!ReferenceEquals(finished, stop)) {
                LogGuidingStopBeforeMoveTimedOut(op, (int)StopGuidingBeforeMoveTimeout.TotalSeconds);
                _ = stop.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); // observe a late fault
                return;
            }
            await cts.CancelAsync().ConfigureAwait(false); // retire the delay
            var stopped = await stop.ConfigureAwait(false);
            LogGuidingStoppedBeforeMove(op, stopped);
        } catch (Exception ex) {
            LogGuidingStopBeforeMoveFailed(ex, op);
        }
    }

    /// <summary>How long a REST goto/park/home waits for the guider to stop before moving anyway. Short on
    /// purpose: a healthy stop takes milliseconds, and the emergency stop's park rung allows 10 s.</summary>
    internal TimeSpan StopGuidingBeforeMoveTimeout { get; set; } = TimeSpan.FromSeconds(3);

    [LoggerMessage(EventId = 6370, Level = LogLevel.Warning, Message = "telescope.{Op}: the guider did not stop within {Seconds} s; moving anyway")]
    private partial void LogGuidingStopBeforeMoveTimedOut(string op, int seconds);

    [LoggerMessage(EventId = 6368, Level = LogLevel.Information, Message = "telescope.{Op}: guiding stopped first (stopped={Stopped})")]
    private partial void LogGuidingStoppedBeforeMove(string op, bool stopped);

    [LoggerMessage(EventId = 6369, Level = LogLevel.Warning, Message = "telescope.{Op}: could not stop guiding first; moving anyway")]
    private partial void LogGuidingStopBeforeMoveFailed(Exception exception, string op);

    public Task<OperationAcceptedDto> ParkAsync(ParkRequestDto request, string? idempotencyKey, CancellationToken ct) {
        // No ArgumentNullException.ThrowIfNull(request): the /park endpoint substitutes
        // `request ?? new ParkRequestDto()`, so request is never null here, and the Reason field is
        // unused by the park itself — the param is kept only for the interface contract.
        _ = request;
        // Not-connected / disposed still throw synchronously (the endpoint and tests rely on it); only
        // the guider stop and the move itself are deferred.
        var client = RequireConnectedClient();
        Lease.CancelAll();
        return ParkAfterGuidingStoppedAsync(client, idempotencyKey);
    }

    private async Task<OperationAcceptedDto> ParkAfterGuidingStoppedAsync(ITelescopeDevice client, string? idempotencyKey) {
        await StopGuidingBeforeMountMoveAsync("park").ConfigureAwait(false);
        lock (_gate) { TargetLatch.NoteNonTargetCommand(); } // §57.9 — parking isn't a target command
        NoteMountCommand(w => w.NoteParkCommanded());
        _ = Task.Run(() => RunControlInBackground("telescope.park", client, c => {
            // Some mounts (e.g. iOptron) won't park from a stationary/home state with tracking off —
            // enable tracking first (best-effort), exactly as ConformU/NINA do before parking, then
            // issue the park. The park itself stops tracking once the mount reaches the park position.
            TryEnableTracking(c);
            c.Park();
        }), CancellationToken.None);
        return Accepted("telescope.park", idempotencyKey);
    }

    public Task<OperationAcceptedDto> UnparkAsync(string? idempotencyKey, CancellationToken ct) {
        var client = RequireConnectedClient();
        NoteMountCommand(w => w.NoteMotionCommanded());
        _ = Task.Run(() => RunControlInBackground("telescope.unpark", client, c => c.Unpark()), CancellationToken.None);
        return Task.FromResult(Accepted("telescope.unpark", idempotencyKey));
    }

    public Task<OperationAcceptedDto> FindHomeAsync(string? idempotencyKey, CancellationToken ct) {
        var client = RequireConnectedClient(); // synchronous throw when not connected / disposed, as before
        return FindHomeAfterGuidingStoppedAsync(client, idempotencyKey);
    }

    private async Task<OperationAcceptedDto> FindHomeAfterGuidingStoppedAsync(ITelescopeDevice client, string? idempotencyKey) {
        await StopGuidingBeforeMountMoveAsync("findhome").ConfigureAwait(false);
        lock (_gate) { TargetLatch.NoteNonTargetCommand(); } // §57.9 — homing isn't a target command
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
        return Accepted("telescope.findhome", idempotencyKey);
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

    public async Task SetTrackingRateAsync(string rate, CancellationToken ct) {
        var client = RequireConnectedClient();
        if (!Enum.TryParse<DriveRate>(rate, ignoreCase: false, out var driveRate) || !Enum.IsDefined(driveRate)) {
            throw new ArgumentException($"Unknown tracking rate '{rate}'.", nameof(rate));
        }
        IReadOnlyList<string>? offered;
        lock (_gate) {
            offered = _capabilities?.SupportedSiderealRates;
        }
        // An empty list means the mount didn't say; let the driver decide then.
        if (offered is { Count: > 0 } && !offered.Contains(driveRate.ToString())) {
            throw new InvalidOperationException($"This mount doesn't offer the {driveRate} tracking rate.");
        }
        try {
            await Task.Run(() => client.TrackingRate = driveRate, CancellationToken.None).ConfigureAwait(false);
        } catch (ASCOM.DriverException ex) {
            // The driver's refusal (a mount that lists no rates lets it decide; InvalidValue and
            // NotImplemented derive from DriverException) is a refusal with its words (409), not a crash.
            throw new InvalidOperationException(ex.Message, ex);
        }
        RefreshCacheOnce();
    }

    public async Task MoveAxisAsync(int axis, double rate, int? leaseMs, CancellationToken ct) {
        // ct is intentionally NOT honored — see the ITelescopeService.MoveAxisAsync contract: a stop
        // must always run to completion even if the request token cancelled, or a dropped token could
        // strand the mount in motion (the same panic-stop reasoning as AbortSlewAsync). Discarded
        // explicitly so the omission reads as deliberate, not an oversight.
        _ = ct;
        var client = RequireConnectedClient();
        // Manual nudge: start (rate != 0) or stop (rate 0) constant-rate motion on one axis. The
        // direction pad sends a rate on press and 0 on release; AbortSlew (the Stop button) is the
        // backstop that halts all axes.
        // #1064/#1072 — the daemon is the guard on hardware motion, not the client's speed picker: a
        // nonzero rate is SNAPPED into the axis's reported rate bands (sign preserved): above the top
        // band → its max; below the lowest band → its min while within 4x of it (#1085: any slower
        // is refused rather than sped up); in a gap between bands → the nearest band edge. An axis
        // with no usable rate refuses the nudge (409) rather than forwarding an unbounded rate. A
        // stop (rate 0) is never gated — it must always reach the driver.
        // The bands come from the session cache (read with the capabilities), never a device
        // round-trip here: the press leg must reach the driver as fast as the release leg.
        // #1078 — a secondary axis whose AxisRates never answered (driver bug: CanMoveAxis true,
        // AxisRates(Secondary) throws) borrows the primary's bands rather than losing N/S for the
        // session (BandsForAxis); the picker applies the same "primary set as-is" fallback.
        IReadOnlyList<(double Min, double Max)>? bands;
        bool borrowedPrimary;
        lock (_gate) {
            bands = BandsForAxis(_axisBands, axis, out borrowedPrimary);
        }
        var axisEnum = (TelescopeAxis)axis;
        var effective = SnapMoveAxisRate(rate, bands);
        if (borrowedPrimary && rate != 0 && Interlocked.Exchange(ref _secondaryFallbackLogged, 1) == 0) {
            LogSecondaryBandsBorrowed(_logger);
        }
        if (rate != 0 && !double.IsNaN(rate) && effective != rate) {
            LogMoveAxisRateSnapped(_logger, axis, rate, effective);
        }
        // #1649 — a stop or an unleased move ends any lease on this axis before the driver call; a
        // leased move starts its deadline only once the mount took it. A renewal at the speed the
        // axis already moves at only extends the lease: the driver is not asked again.
        if (effective == 0 || leaseMs is null) {
            Lease.Cancel(axis);
        } else if (Lease.TryRenew(axis, effective, leaseMs.Value, LeaseClock.GetUtcNow())) {
            return;
        }
        NoteMountCommand(w => w.NoteMotionCommanded());
        try {
            await Task.Run(() => client.MoveAxis(axisEnum, effective), CancellationToken.None).ConfigureAwait(false);
        } catch (ASCOM.DriverException ex) when (IsSuperseded(ex)) {
            // A driver that queues motion (AlpacaBridge) replaced this move with a newer one before
            // it reached the mount: the newer command decides, so this is not a failure (rig,
            // 2026-11-11: a short pad press read "Couldn't move the mount").
            LogMoveAxisSuperseded(_logger, axis, effective);
            return;
        }
        if (effective != 0 && leaseMs is int ms) {
            Lease.Renew(axis, effective, ms, LeaseClock.GetUtcNow());
            ArmLeaseTimer(true);
        }
        RefreshCacheOnce();
    }

    internal static bool IsSuperseded(Exception ex) =>
        ex.Message.Contains("superseded", StringComparison.OrdinalIgnoreCase);

    /// <summary>#1649 — stops every leased axis whose renewals stopped (the timer's tick; internal for tests).</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Timer-callback boundary: the expired-lease stop is a blocking ASCOM MoveAxis(0) that can throw arbitrary driver/HTTP exceptions; an exception escaping a timer callback crashes the process. The failure is logged and the next axis is still stopped. CA1031's log-and-recover boundary applies.")]
    internal void CheckLeases() {
        var expired = Lease.TakeExpired(LeaseClock.GetUtcNow());
        if (!Lease.Any) {
            ArmLeaseTimer(false);
        }
        if (expired.Count == 0) {
            return;
        }
        ITelescopeDevice? client;
        lock (_gate) {
            client = _lifecycle.Live;
        }
        if (client is null) {
            return;
        }
        foreach (var axis in expired) {
            try {
                NoteMountCommand(w => w.NoteMotionCommanded());
                client.MoveAxis((TelescopeAxis)axis, 0);
                LogMoveAxisLeaseExpired(_logger, axis);
            } catch (Exception ex) {
                LogMoveAxisLeaseStopFailed(_logger, ex, axis);
            }
        }
        RefreshCacheOnce();
    }

    private void ArmLeaseTimer(bool on) {
        lock (_gate) {
            // Re-checked under the gate: a renewal racing this tick's "nothing left" must keep the timer.
            if (_lifecycle.IsDisposed || (!on && Lease.Any)) {
                return;
            }
            var period = on ? LeaseCheckInterval : Timeout.InfiniteTimeSpan;
            _leaseTimer.Change(period, period);
        }
    }

    /// <summary>#1078 — the bands the nudge on <paramref name="axis"/> is snapped into: the axis's own
    /// when known; for the secondary axis whose AxisRates never ANSWERED (null — a driver that
    /// throws on it while CanMoveAxis is true) the primary's bands are borrowed (<paramref name="borrowed"/>).
    /// An honestly EMPTY secondary (the mount says it offers no rates) is not borrowed for: by spec
    /// MoveAxis(Secondary) throws on such a mount, so the nudge must stay refused (409). Null when
    /// nothing usable is known. Internal static so the rule is unit-testable.</summary>
    internal static IReadOnlyList<(double Min, double Max)>? BandsForAxis(IReadOnlyList<(double Min, double Max)>?[]? cached, int axis, out bool borrowed) {
        borrowed = false;
        if (cached is null || axis < 0 || axis >= cached.Length) {
            return null;
        }
        var own = cached[axis];
        if (axis == 1 && own is null && cached[0] is { Count: > 0 } primary) {
            borrowed = true;
            return primary;
        }
        return own;
    }

    /// <summary>The #1064 rate-cache settle rule: stop re-reading AxisRates once both pad-axis reads
    /// completed (an empty answer is the mount's and terminal), once the mount reports CanMoveAxis
    /// false (its AxisRates throw by spec), or once <see cref="AxisRatesSettleWindow"/> of wall clock
    /// has passed since connect with a read still throwing (#1078: time-based, not pass-counted).
    /// Internal static so the rule is unit-testable like the snap itself.</summary>
    internal static bool ShouldSettleAxisRates(bool readsCompleted, bool mountCannotMoveAxis, bool windowElapsed) =>
        readsCompleted || mountCannotMoveAxis || windowElapsed;

    /// <summary>#1085 — how far below the lowest band's minimum a request may sit and still be
    /// raised to it. A request slower than <c>Min / SnapUpBoundFactor</c> is refused instead: on a
    /// mount whose lowest band starts high (a single band <c>(2.0, 6.0)</c>, say) the picker's 1 %
    /// preset would otherwise become a press-and-hold many times faster than the user chose, with
    /// only a daemon log to say so.</summary>
    internal const double SnapUpBoundFactor = 4.0;

    /// <summary>Snap a MoveAxis request into the axis's reported rate bands (#1064/#1072). <c>0</c>
    /// (stop) passes through untouched. A nonzero rate with no known bands (the axis reports none,
    /// or the capabilities have not been read yet) throws — the driver would either reject it or,
    /// worse, honour it. Otherwise the magnitude is kept when a band contains it, capped at the top
    /// band's max, raised to the lowest band's min while it is within <see cref="SnapUpBoundFactor"/>
    /// of it (#1085: any slower is refused rather than sped up), or moved to the nearest edge of a
    /// gap between bands (a discrete-rate mount has Min == Max bands). Sign preserved. Internal for
    /// direct unit testing (the rate read itself sits behind a sealed Alpaca client).</summary>
    internal static double SnapMoveAxisRate(double requested, IReadOnlyList<(double Min, double Max)>? bands) {
        if (requested == 0 || double.IsNaN(requested)) {
            return 0;
        }
        if (bands is not { Count: > 0 }) {
            throw new InvalidOperationException(
                "Mount reports no MoveAxis rate for this axis (or its capabilities are still being read); manual nudge refused.");
        }
        var magnitude = Math.Abs(requested);
        // ReadAxisBands returns the bands ascending by Min; no re-sort on the press leg.
        var top = bands.Max(b => b.Max);
        var bottom = bands.Min(b => b.Min);
        double snapped;
        if (magnitude >= top) {
            snapped = top;
        } else if (magnitude <= bottom) {
            if (magnitude * SnapUpBoundFactor < bottom) {
                throw new InvalidOperationException(
                    $"Requested rate {magnitude:G4} deg/s is more than {SnapUpBoundFactor:0}x slower than the mount's slowest rate ({bottom:G4} deg/s); manual nudge refused. Pick a faster speed.");
            }
            snapped = bottom;
        } else if (bands.Any(b => magnitude >= b.Min && magnitude <= b.Max)) {
            snapped = magnitude;
        } else {
            // In a gap: nearest edge — the max of the band below or the min of the band above.
            var below = bands.Where(b => b.Max < magnitude).Max(b => b.Max);
            var above = bands.Where(b => b.Min > magnitude).Min(b => b.Min);
            snapped = magnitude - below <= above - magnitude ? below : above;
        }
        return requested < 0 ? -snapped : snapped;
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "MoveAxis rate cache settled with an axis still unknown (primary max={Primary}, secondary max={Secondary}): {Reason}. A nudge on an unknown primary is refused (409) for this session; an unanswered secondary borrows the primary's bands (#1064/#1078).")]
    private static partial void LogAxisRatesUnknown(ILogger logger, double? primary, double? secondary, string reason);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "MoveAxis: the secondary axis's AxisRates never answered; borrowing the primary axis's bands for N/S nudges this session (#1078).")]
    private static partial void LogSecondaryBandsBorrowed(ILogger logger);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "MoveAxis rate {Requested} deg/s on axis {Axis} is outside the mount's rate bands; snapped to {Effective} deg/s.")]
    private static partial void LogMoveAxisRateSnapped(ILogger logger, int axis, double requested, double effective);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug,
        Message = "MoveAxis {Rate} deg/s on axis {Axis} was superseded by a newer motion command before it reached the mount.")]
    private static partial void LogMoveAxisSuperseded(ILogger logger, int axis, double rate);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "MoveAxis lease on axis {Axis} expired with no renewal; stopped the axis (#1649).")]
    private static partial void LogMoveAxisLeaseExpired(ILogger logger, int axis);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error,
        Message = "MoveAxis lease on axis {Axis} expired, but stopping the axis failed (#1649).")]
    private static partial void LogMoveAxisLeaseStopFailed(ILogger logger, Exception ex, int axis);

    public async Task AbortSlewAsync(CancellationToken ct) {
        var client = RequireConnectedClient();
        // §57 panic stop: issue AbortSlew immediately (universal — every mount supports it).
        // CancellationToken.None (not ct) is critical: Task.Run(lambda, ct) returns a pre-cancelled
        // task WITHOUT running the lambda if ct is already cancelled, so an HTTP-timeout/disconnect
        // that cancelled the token would silently never send the abort and the slew would continue.
        await AbortSlewCoreAsync(client, reason: "user_request", rethrow: true).ConfigureAwait(false);
    }

    /// <summary>The shared §57 abort flow — the user panic stop (<c>user_request</c>, throwing)
    /// and the flip watchdog's internal abort (<c>watchdog</c>, fire-and-forget) both run it, so
    /// every abort source gets the same watch-noting races and lifecycle guarantees.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fire-and-forget abort boundary (rethrow=false): the watchdog's StopSlew must never throw into the flip-recovery path; the failure is logged. The throwing path (rethrow=true) re-raises after the lifecycle bookkeeping. CA1031's log-and-recover boundary applies.")]
    private async Task AbortSlewCoreAsync(ITelescopeDevice client, string reason, bool rethrow) {
        Lease.CancelAll();
        NoteMountCommand(w => w.NoteMotionCommanded());
        // §57.5 — measure issue latency (request-received → device call returned) per Stop Mount
        // incident so the panic budget is tunable from the logs. The tap→request network leg is
        // client-side and unmeasurable here.
        var issueTimer = System.Diagnostics.Stopwatch.StartNew();
        // The abort is noted on the watch BEFORE the device call (#836 r2): the mount's IsSlewing
        // can read false the instant the physical abort lands, and a poll tick racing into that
        // gap would otherwise close the episode as a normal slew_complete and leave no episode
        // for the aborted event. Noting first makes the abort win by design, not by timing. An
        // abort with no open episode publishes nothing (#836 r1): there is nothing to close, and
        // a latched flag would swallow a later park/home slew's complete.
        bool episodeOpen;
        lock (_gate) {
            episodeOpen = SlewWatch.NoteAborted();
        }
        try {
            await Task.Run(() => client.AbortSlew(), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            bool noteStillLatched;
            lock (_gate) {
                noteStillLatched = SlewWatch.ClearAbortNote();
            }
            if (episodeOpen && !noteStillLatched) {
                // #836 r6 — the poll consumed the abort note while the FAILING device call was
                // in flight: the mount genuinely stopped (its complete was suppressed), so the
                // aborted event must still close the lifecycle for clients before we rethrow.
                await PublishAbortedAsync(client, reason).ConfigureAwait(false);
            }
            // Otherwise the note was unwound — the slew is still running and its eventual
            // complete must not be suppressed by the pre-noted abort (#836 r2).
            if (rethrow) {
                throw;
            }
            LogStopSlewFailed(ex);
            return;
        }
        issueTimer.Stop();
        LogAbortIssued(reason, issueTimer.ElapsedMilliseconds, episodeOpen);
        // §57.4 step 3 — the aborted event fires NOW (not a poll-tick later), with the position
        // read FRESH after the physical stop (#836 r4 — the cached runtime is up to a poll tick
        // stale, which is several degrees of travel mid-slew; "where did the mount stop" is the
        // event's whole point). A failed read falls back to the cache rather than delaying the
        // panic event.
        if (episodeOpen) {
            await PublishAbortedAsync(client, reason).ConfigureAwait(false);
        }
        RefreshCacheOnce();
    }

    private ITelescopeDevice RequireConnectedClient() {
        ITelescopeDevice? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_lifecycle.IsDisposed, this);
            client = _lifecycle.Live;
        }
        return client ?? throw new InvalidOperationException("telescope is not connected");
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background slew boundary: the blocking ASCOM SlewToCoordinatesAsync/SyncToCoordinates can throw arbitrary driver/HTTP exceptions, and a concurrent Disconnect/Dispose can dispose the captured client mid-call; any escape must be contained and logged, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    private void SlewInBackground(ITelescopeDevice client, double raHours, double decDeg, bool sync) {
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
            // The dispatch failed — no episode will open for this command, so its noted target
            // must not ride an unrelated later episode's slew_started (#836 r2).
            lock (_gate) {
                SlewWatch.ClearPendingTarget();
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background control boundary: the blocking ASCOM Park/Unpark can throw arbitrary driver/HTTP exceptions, and a concurrent Disconnect/Dispose can dispose the captured client mid-call; any escape must be contained and logged, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    private void RunControlInBackground(string op, ITelescopeDevice client, Action<ITelescopeDevice> action) {
        try {
            action(client);
            RefreshCacheOnce();
        } catch (Exception ex) {
            LogControlFailed(ex, op);
        }
    }

    // The post-write reflects in SetTrackingAsync/AbortSlewAsync/MoveAxis and the settle polls funnel
    // here: a refresh requested while a tick is in flight is not dropped — the module makes one more
    // pass after it, so a write that lands mid-tick is reflected without waiting out the next period.
    private void RefreshCacheOnce() => _lifecycle.RequestRefresh();

    // The mount's runtime read (ADR 0011): the module has probed the mount, this reads it off the gate and
    // hands back the commit. The essential read is RA/Dec (both mandatory ITelescope members): any failure
    // there, or the #1193 latch on any read, is a failed read for read health and the clean-read gate. A driver
    // that always refuses an optional property (a not-implemented AtHome or SideOfPier) still reads clean.
    private DeviceRead ReadTick(ITelescopeDevice client) {
        bool needCaps;
        bool needAxisRates;
        bool needEquatorialSystem;
        bool readPierSide;
        lock (_gate) {
            needCaps = _capabilities is null;
            // #1064 — retried only until settled (see _axisRatesSettled): a thrown read is
            // transient; an empty result is the mount's answer and terminal.
            needAxisRates = !_axisRatesSettled;
            needEquatorialSystem = !_equatorialSystemKnown;
            readPierSide = !_sideOfPierUnsupported;
        }
        var runtime = ReadRuntime(client, out var readError, out var positionError, out var slewingUnknown);
        // #1732 — asked only while tracking reads off on an unparked mount; the watch ignores a parked one.
        var pulseGuiding = !runtime.Tracking && !runtime.Parked && ReadPulseGuiding(client);
        var (sideOfPier, pierSideUnsupported) = readPierSide ? ReadSideOfPier(client) : (PierSide.pierUnknown, true);
        // #1193 — the bridge's latched fault answers the probe but fails the reads. Never
        // during a slew (the reconnect would abandon a moving mount mid-goto; the slew's own
        // watchdog and the next tick get it), and once per episode.
        if (readError is not null && IsLatchedBridgeFault(readError)) {
            if (!SlewMayBeInFlight(runtime, slewingUnknown) && TripLatchedBridgeFault(client, readError)) {
                return new DeviceRead(null, () => { }); // tripped to Error — nothing from this pass is worth committing
            }
        } else {
            // A tick with no latched error ends the episode — including one where some other
            // read threw (a driver that always refuses AtHome, say): only the latch itself may
            // hold the flag, or that driver's next latch episode would never be detected.
            lock (_gate) {
                _bridgeFaultTripped = false;
                _latchedSlewHoldSince = null;
                _latchedSlewHoldExpiredLogged = false;
            }
        }
        // The two pad-axis AxisRates reads feed BOTH the caps DTO's rate list and the #1064
        // clamp cache — one pair of GETs, only while either still needs them.
        var pad = needCaps || needAxisRates ? ReadPadAxisBands(client) : default;
        var caps = needCaps ? ReadCapabilities(client, pad) : null;
        var axisReadsCompleted = needAxisRates && pad.Primary is not null && pad.Secondary is not null;
        // Retried every pass until one read succeeds (null = read failed; a genuine "Other" from
        // the device counts as success and stops the retries).
        var equatorialSystem = needEquatorialSystem ? ReadEquatorialSystem(client) : null;
        var trackingVerdict = TrackingWatchVerdict.Idle;
        var slewVerdict = new SlewEventWatch.Verdict(SlewEventWatch.Kind.None);
        DiscoveredDeviceDto? watchedDevice = null;
        string? axisRatesUnknownReason = null;
        int? republishedBands = null;
        (double? Primary, double? Secondary) axisRatesUnknownValues = default;
        // #1246 — the ladder's "recovered" waits for a tick without the latch and with RA/Dec read (the module's
        // clean-read gate); the same failure feeds read health (ADR 0011 rule 2).
        var latched = readError is not null && IsLatchedBridgeFault(readError);
        return new DeviceRead(latched ? LatchedBridgeFaultMessage(readError!) : positionError?.Message,
            CommitLocked: () => {
                // §57.9 — mask the stale post-Park/Home target; the latch self-releases
                // when the registers change (an external goto/sync wrote a new target).
                _runtime = TargetLatch.Observe(runtime.TargetRightAscensionHours, runtime.TargetDeclinationDegrees)
                    ? runtime with { TargetRightAscensionHours = null, TargetDeclinationDegrees = null }
                    : runtime;
                _sideOfPier = sideOfPier;
                if (pierSideUnsupported && !_sideOfPierUnsupported) {
                    _sideOfPierUnsupported = true;
                    LogSideOfPierUnsupported(_lifecycle.Device?.Name ?? "?");
                }
                if (caps is not null) {
                    _capabilities = caps;
                }
                if (needAxisRates) {
                    // Keep any axis already known; fill the rest from this pass.
                    _axisBands ??= new IReadOnlyList<(double Min, double Max)>?[3];
                    // Keep an axis once it answered with bands; a null (threw) or empty read
                    // never overwrites a known set, and an empty read is not latched — a later
                    // pass may still return the bands (AxisRates is static per mount, so this
                    // is belt-and-braces, not a retry path).
                    _axisBands[0] = _axisBands[0] is { Count: > 0 } ? _axisBands[0] : pad.Primary ?? _axisBands[0];
                    _axisBands[1] = _axisBands[1] is { Count: > 0 } ? _axisBands[1] : pad.Secondary ?? _axisBands[1];
                    republishedBands = RepublishPadBandsLocked(); // #1230 — caps built before an axis answered catch up here
                    var mountCannotMoveAxis = (caps ?? _capabilities)?.CanMoveAxis == false; // under _gate like every caps read
                    var windowElapsed = DateTimeOffset.UtcNow >= _axisRatesDeadline;
                    // Both axes known across passes (primary on one, secondary on a later one)
                    // counts as completed too — no point re-reading until the window closes.
                    var bothKnown = _axisBands[0] is { Count: > 0 } && _axisBands[1] is { Count: > 0 };
                    if (ShouldSettleAxisRates(axisReadsCompleted || bothKnown, mountCannotMoveAxis, windowElapsed)) {
                        _axisRatesSettled = true; // settled: no more AxisRates reads this session
                        if (_axisBands[0] is not { Count: > 0 } || _axisBands[1] is not { Count: > 0 }) {
                            // Logged after the lock (below): the nudge/stop path takes _gate too.
                            axisRatesUnknownReason = (mountCannotMoveAxis, axisReadsCompleted) switch {
                                (true, true) => "CanMoveAxis is false and the mount reported no rates",
                                (true, false) => "CanMoveAxis is false",
                                (false, true) => "the mount reported no rates",
                                _ => $"AxisRates kept throwing for {AxisRatesSettleWindow.TotalSeconds:0} s after connect",
                            };
                            axisRatesUnknownValues = (MaxOf(_axisBands[0]), MaxOf(_axisBands[1]));
                        }
                    }
                }
                if (equatorialSystem is not null) {
                    _equatorialSystemRaw = equatorialSystem.Value;
                    _equatorialSystemKnown = true;
                }
                // §42.2 — tracking-lost watch, observed under the same commit lock so the
                // liveness check and the observation are one critical section (the #789
                // probe lesson); command notes take this gate too.
                trackingVerdict = _trackingWatch.Observe(
                    runtime.Tracking,
                    // #1246 — a latched Slewing read is a fallback; a goto the daemon saw start still counts.
                    slewing: runtime.State == "slewing" || (slewingUnknown && SlewWatch.InSlew),
                    parked: runtime.Parked,
                    pulseGuiding: pulseGuiding);
                // §57.8 — slew lifecycle from the same observed snapshot.
                // #1246 — a latched Slewing read is a fallback false, not an answer: feeding it would
                // close the episode the guard above relies on. The watch waits for a real read. (A
                // driver whose Slewing is never implemented never opens an episode either, since
                // the runtime state can never read "slewing" — so nothing is left unfinished.)
                slewVerdict = slewingUnknown ? new SlewEventWatch.Verdict(SlewEventWatch.Kind.None) : SlewWatch.Observe(runtime.State == "slewing");
                watchedDevice = _lifecycle.Device;
            },
            AfterCommit: () => {
                if (republishedBands is { } republished) {
                    LogPadBandsRepublished(_logger, republished);
                }
                if (axisRatesUnknownReason is not null) {
                    LogAxisRatesUnknown(_logger, axisRatesUnknownValues.Primary, axisRatesUnknownValues.Secondary, axisRatesUnknownReason);
                }
                if (slewVerdict.Kind == SlewEventWatch.Kind.Started) {
                    PublishSlewEvent(WsEventCatalog.TelescopeSlewStarted, new System.Text.Json.Nodes.JsonObject {
                        ["target_ra_hours"] = slewVerdict.TargetRaHours,
                        ["target_dec_degrees"] = slewVerdict.TargetDecDegrees,
                        ["ra_hours"] = runtime.RightAscensionHours,
                        ["dec_degrees"] = runtime.DeclinationDegrees,
                    });
                } else if (slewVerdict.Kind == SlewEventWatch.Kind.Completed) {
                    PublishSlewEvent(WsEventCatalog.TelescopeSlewComplete, new System.Text.Json.Nodes.JsonObject {
                        ["final_ra_hours"] = runtime.RightAscensionHours,
                        ["final_dec_degrees"] = runtime.DeclinationDegrees,
                        ["duration_seconds"] = slewVerdict.DurationSeconds,
                    });
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
            });
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Event publication is best-effort UX freshness: serialization or channel faults must be logged and dropped, never propagated into a status-poll or abort path. CA1031's log-and-recover boundary applies.")]
    private void PublishSlewEvent(WsEvent eventType, System.Text.Json.Nodes.JsonObject payload) {
        if (_ws is null) {
            return;
        }
        try {
            // ToJsonString()+Parse is the AOT-safe JsonElement construction (EquipmentFaultHub pattern).
            using var doc = System.Text.Json.JsonDocument.Parse(payload.ToJsonString());
            var task = _ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None);
            // Observe async faults too (the EquipmentEventPublisher pattern) — a bare discard
            // would silently drop a failure inside the publish path.
            _ = task.ContinueWith(
                t => LogSlewEventPublishFailed(t.Exception!.GetBaseException(), eventType),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        } catch (Exception ex) {
            LogSlewEventPublishFailed(ex, eventType);
        }
    }

    private async Task PublishAbortedAsync(ITelescopeDevice client, string reason) {
        var (freshRa, freshDec) = await Task.Run(() => ReadPositionBestEffort(client), CancellationToken.None)
            .ConfigureAwait(false);
        TelescopeStateDto cached;
        lock (_gate) {
            cached = _runtime;
        }
        PublishSlewEvent(WsEventCatalog.TelescopeSlewAborted, new System.Text.Json.Nodes.JsonObject {
            ["halted_ra_hours"] = freshRa ?? cached.RightAscensionHours,
            ["halted_dec_degrees"] = freshDec ?? cached.DeclinationDegrees,
            ["reason"] = reason,
        });
    }

    // §57.5 — one structured line per Stop Mount incident (reason, server-side issue latency,
    // whether an episode was open). Kept in the log rather than the faults table: an abort is a
    // measurement, not a fault, and a fault-feed row per panic press would read as equipment
    // trouble in the §42.6 UI.
    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Slew abort issued (reason={Reason}) in {IssueMs} ms; episodeOpen={EpisodeOpen}")]
    private partial void LogAbortIssued(string reason, long issueMs, bool episodeOpen);

    // Fresh post-abort position for the slew_aborted payload — per-field best-effort, same
    // shape as ReadRuntime's field reads (an unsupported/transient property must stay benign).
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a transiently-failing telescope property throws; the field falls back to null (the caller then uses the cached runtime) rather than failing the panic-stop event. CA1031's log-and-recover boundary applies.")]
    private static (double? Ra, double? Dec) ReadPositionBestEffort(ITelescopeDevice c) {
        double? ra;
        try { ra = c.RightAscension; } catch (Exception) { ra = null; }
        double? dec;
        try { dec = c.Declination; } catch (Exception) { dec = null; }
        return (ra, dec);
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Telescope: failed to broadcast the {EventType} WS event")]
    private partial void LogSlewEventPublishFailed(Exception ex, string eventType);

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

    private static TelescopeStateDto ReadRuntime(ITelescopeDevice c) => ReadRuntime(c, out _, out _, out _);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a mount that cannot report it counts as not pulsing, so the tracking watch keeps working.")]
    private static bool ReadPulseGuiding(ITelescopeDevice c) {
        try {
            return c.IsPulseGuiding;
        } catch (Exception) {
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported/transiently-failing telescope property throws; that field falls back to its default rather than failing the whole runtime read (every exception is reported to the caller, aggregated when several, for #1193/#1246). CA1031's log-and-recover boundary applies.")]
    // readError: EVERY exception the per-field reads threw (null when all answered; one exception
    // as-is, several as an AggregateException), so the refresh can recognise a driver-reported
    // latched fault (#1193) even when an unrelated read (a not-implemented AtHome) threw first
    // (#1246), without changing the per-field fallback semantics. The target registers are left
    // out of it: they legitimately throw before the first target is set. slewingUnknown: the
    // Slewing read itself threw, so the "slewing" state is a fallback, not an answer. positionError: the
    // first exception the RA/Dec reads threw (the mount's essential read), null when both answered.
    private static TelescopeStateDto ReadRuntime(ITelescopeDevice c, out Exception? readError, out Exception? positionError,
            out bool slewingUnknown) {
        List<Exception>? errors = null;
        T Read<T>(Func<T> read, T fallback) {
            try {
                return read();
            } catch (Exception ex) {
                (errors ??= []).Add(ex);
                return fallback;
            }
        }
        bool slewing;
        try {
            slewing = c.Slewing;
            slewingUnknown = false;
        } catch (Exception ex) {
            errors = [ex]; // the first read: nothing has failed yet
            slewing = false;
            slewingUnknown = true; // its own try/catch: not inferred from the error count (#1246)
        }
        var errorsBeforePosition = errors?.Count ?? 0;
        var ra = Read<double?>(() => c.RightAscension, null);
        var dec = Read<double?>(() => c.Declination, null);
        positionError = errors is not null && errors.Count > errorsBeforePosition ? errors[errorsBeforePosition] : null;
        // §57.9 — the slew/sync destination, read back from the mount's own target registers.
        // Same per-field best-effort boundary as RA/Dec: a mount that throws
        // PropertyNotImplemented (or InvalidOperationException before the first target is set)
        // simply reports null rather than failing the whole runtime read.
        double? targetRa;
        try { targetRa = c.TargetRightAscension; } catch (Exception) { targetRa = null; }
        double? targetDec;
        try { targetDec = c.TargetDeclination; } catch (Exception) { targetDec = null; }
        // #1649 — the drive rate, best-effort like the targets (many mounts NotImplement it).
        string? trackingRate;
        try { trackingRate = c.TrackingRate.ToString(); } catch (Exception) { trackingRate = null; }
        var tracking = Read(() => c.Tracking, false);
        var parked = Read(() => c.AtPark, false);
        var atHome = Read(() => c.AtHome, false);
        readError = errors is null ? null : errors.Count == 1 ? errors[0] : new AggregateException(errors);

        // §42.9 — resolve the runtime-state token from the raw mount flags via
        // the shared helper so the precedence (incl. the parked-over-slewing
        // contradiction guard) is unit-tested in one place.
        var state = ResolveRuntimeState(slewing, parked, tracking);
        return new TelescopeStateDto(state, ra, dec, tracking, parked, atHome, targetRa, targetDec, trackingRate);
    }

    // #1229 — the mount's SideOfPier (ASCOM PointingState), per-field best-effort like ReadRuntime:
    // an optional property on many drivers, so a throw (not implemented, transient) reads as
    // pierUnknown, which every consumer already treats as "cannot say" (the trigger ignores the
    // pier side, the §58.5 verification only warns).
    // Unsupported = the driver said so (ASCOM's not-implemented family), which is terminal for the
    // session; any other failure is transient and read again next tick.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: SideOfPier is optional and throws PropertyNotImplemented on many drivers; the read falls back to pierUnknown rather than failing the whole refresh. CA1031's log-and-recover boundary applies.")]
    private static (PierSide SideOfPier, bool Unsupported) ReadSideOfPier(ITelescopeDevice c) {
        try {
            return (MapPointingState(c.SideOfPier), false);
        } catch (ASCOM.NotImplementedException) {
            return (PierSide.pierUnknown, true);
        } catch (Exception) {
            return (PierSide.pierUnknown, false);
        }
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Telescope '{Device}' does not implement SideOfPier — pier side stays unknown this session (no further reads)")]
    private partial void LogSideOfPierUnsupported(string device);

    // ASCOM PointingState → NINA PierSide: the library renamed the Platform's PierSide enum, and its
    // own docs pin Normal = pierEast, ThroughThePole = pierWest. Anything else is unknown.
    // Extracted (internal) for direct unit testing.
    internal static PierSide MapPointingState(PointingState state) => state switch {
        PointingState.Normal => PierSide.pierEast,
        PointingState.ThroughThePole => PierSide.pierWest,
        _ => PierSide.pierUnknown,
    };

    /// <summary>
    /// Maps raw mount flags to the runtime-state token. Precedence: a slew in
    /// progress wins (the mount is moving), then a parked mount (a definite rest
    /// state), then active tracking, otherwise idle.
    /// </summary>
    /// <remarks>
    /// §42.9 contradiction guard: <c>AtPark</c> is a definite rest state — a
    /// parked mount cannot be slewing. A driver/bridge that reports
    /// <c>Slewing == true</c> while <c>AtPark == true</c> is lying or stale (the
    /// AlpacaBridge ZWO vendor did exactly this with no mount attached), so
    /// "parked" wins over "slewing" — a buggy/phantom device can never paint
    /// the mount as moving.
    /// </remarks>
    internal static string ResolveRuntimeState(bool slewing, bool parked, bool tracking) =>
        (slewing && !parked) ? "slewing"
        : parked ? "parked"
        : tracking ? "tracking"
        : "idle";

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported capability property throws; each flag falls back to false (and the rate list to empty) rather than failing the whole capability read. The RA/Dec range is fixed, so there is no essential field whose failure should null the whole DTO. CA1031's log-and-recover boundary applies.")]
    private static TelescopeCapabilitiesDto ReadCapabilities(ITelescopeDevice c, (IReadOnlyList<(double Min, double Max)>? Primary, IReadOnlyList<(double Min, double Max)>? Secondary) pad) {
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
        // one axis would silently fail the other half of the pad. The bands below are the primary
        // axis's bands intersected with the secondary's (PadBandsFrom, #1126/#1230).
        try {
            canMoveAxis = c.CanMoveAxis(TelescopeAxis.Primary) && c.CanMoveAxis(TelescopeAxis.Secondary);
        } catch (Exception) {
            canMoveAxis = false;
        }
        var padBands = PadBandsFrom(pad);
        return new TelescopeCapabilitiesDto(
            CanSlew: canSlew, CanSync: canSync, CanPark: canPark, CanUnpark: canUnpark,
            CanSetTracking: canSetTracking, CanPulseGuide: canPulseGuide, CanFindHome: canFindHome,
            SupportedSiderealRates: ReadSiderealRates(c),
            FocalLengthMm: focalLengthMm, ApertureDiameterMm: apertureMm,
            CanMoveAxis: canMoveAxis,
            MoveAxisRatesDegPerSec: [.. EndpointsOf(padBands)],
            MoveAxisRateBandsDegPerSec: BandDtosOf(padBands),
            // #1264 — the bridge's spec sheet rides the same once-per-connect read; null without one.
            MountSpecs: MountSpecSheet.TryRead(c));
    }

    /// <inheritdoc/>
    public MountSpecSheetDto? CurrentMountSpecs {
        get {
            lock (_gate) {
                return MountSpecsWhileConnected(_lifecycle.State, _capabilities);
            }
        }
    }

    /// <summary>Only while connected: a disconnect keeps the last capabilities for a fast reconnect,
    /// but the prior must not be built from a mount that is not there. Pure (the test project has no
    /// fake Alpaca harness to drive a real connect/disconnect through).</summary>
    internal static MountSpecSheetDto? MountSpecsWhileConnected(EquipmentConnectionState state, TelescopeCapabilitiesDto? capabilities) =>
        state == EquipmentConnectionState.Connected ? capabilities?.MountSpecs : null;

    /// <summary>The direction pad's two AxisRates reads (Primary, Secondary) as [Min,Max] bands, each
    /// null when the read threw (transient or NotImplemented) and empty when the mount answered with
    /// no bands. Read once per need by RefreshPass and shared by the caps DTO and the #1064 rate
    /// cache. Tertiary is never read (it throws on most mounts and the pad never drives it).</summary>
    private static (IReadOnlyList<(double Min, double Max)>? Primary, IReadOnlyList<(double Min, double Max)>? Secondary) ReadPadAxisBands(ITelescopeDevice c) =>
        (ReadAxisBands(c, TelescopeAxis.Primary), ReadAxisBands(c, TelescopeAxis.Secondary));

    // Both endpoints of every band (discrete rate → Min==Max), ascending + deduped, positive only —
    // the LEGACY picker shape (`move_axis_rates_deg_per_sec`), kept for clients that predate the
    // bands. It cannot tell (0, 6) from (6, 6) — both flatten to [6] — which is why the bands
    // themselves ride the wire since #1126 (BandDtosOf).
    internal static SortedSet<double> EndpointsOf(IReadOnlyList<(double Min, double Max)> bands) {
        var rates = new SortedSet<double>();
        foreach (var (min, max) in bands) {
            if (min > 0) {
                rates.Add(min);
            }
            if (max > 0) {
                rates.Add(max);
            }
        }
        return rates;
    }

    /// <summary>The bands on the wire (#1126): <c>[{min, max}]</c> in the order given.</summary>
    internal static List<MoveAxisRateBandDto> BandDtosOf(IReadOnlyList<(double Min, double Max)> bands) =>
        bands.Select(b => new MoveAxisRateBandDto(b.Min, b.Max)).ToList();

    /// <summary>The bands the direction pad may pick from: every non-empty intersection of a primary
    /// axis band with a secondary axis band, ascending by Min. The pad drives BOTH axes with the same
    /// picked rate, but ASCOM allows each axis its own range, so a rate the primary honours could be
    /// refused on the secondary (N/S): above its ceiling it is capped, below its floor by more than
    /// <see cref="SnapUpBoundFactor"/> it is REFUSED (#1085) — which used to leave a slow diagonal
    /// press moving E/W while N/S 409'd (#1126). Clipping to the secondary's [floor, ceiling] alone
    /// stopped the 409 but not a large mismatch: a continuous primary over a discrete secondary
    /// (0.002),(0.008),(0.033),(1–4) published (0.002, 4.0), and a 5 % chip (0.2 °/s) snapped to
    /// 0.033 on N/S, so a diagonal press ran E/W 6× faster than N/S (#1230). Intersecting with the
    /// secondary's bands publishes exactly the rates BOTH axes honour as given: a rate inside any
    /// published band is never refused and never snapped on either axis. A thrown (null) or
    /// honestly-empty secondary applies no clip. When no intersection is non-empty (two discrete
    /// ladders with no step in common) the #1126 clip to the secondary's [floor, ceiling] is used
    /// instead — inside that window the secondary snaps but never refuses — and only when that is
    /// empty too is the primary set offered as-is rather than no speeds at all. Internal static so
    /// the rule is unit-testable.</summary>
    internal static List<(double Min, double Max)> PadBandsFrom((IReadOnlyList<(double Min, double Max)>? Primary, IReadOnlyList<(double Min, double Max)>? Secondary) pad) {
        if (pad.Primary is not { Count: > 0 }) {
            return [];
        }
        var primary = pad.Primary.OrderBy(b => b.Min).ToList();
        if (pad.Secondary is not { Count: > 0 }) {
            return primary;
        }
        var usable = new List<(double Min, double Max)>();
        foreach (var (pMin, pMax) in primary) {
            foreach (var (sMin, sMax) in pad.Secondary) {
                var lo = Math.Max(pMin, sMin);
                var hi = Math.Min(pMax, sMax);
                if (lo > hi + 1e-9) {
                    continue; // disjoint
                }
                var both = (lo, Math.Max(lo, hi));
                // Two pairs that intersect to the same window would publish twice.
                if (!usable.Contains(both)) {
                    usable.Add(both);
                }
            }
        }
        usable.Sort((a, b) => a.Min != b.Min ? a.Min.CompareTo(b.Min) : a.Max.CompareTo(b.Max));
        if (usable.Count > 0) {
            return usable;
        }
        var clipped = ClipToWindow(primary, pad.Secondary.Min(b => b.Min), pad.Secondary.Max(b => b.Max));
        return clipped.Count > 0 ? clipped : primary;
    }

    // The #1126 rule, kept as the fallback: each primary band clipped to [floor, ceiling], deduped.
    private static List<(double Min, double Max)> ClipToWindow(List<(double Min, double Max)> primary, double floor, double ceiling) {
        var usable = new List<(double Min, double Max)>();
        foreach (var (min, max) in primary) {
            var lo = Math.Max(min, floor);
            var hi = Math.Min(max, ceiling);
            var clipped = (lo, Math.Max(lo, hi));
            if (lo <= hi + 1e-9 && !usable.Contains(clipped)) {
                usable.Add(clipped);
            }
        }
        return usable;
    }

    /// <summary>#1230 — the capabilities are built on the first pass whose reads answered; if the
    /// secondary's AxisRates threw on that pass, the unclipped primary bands went on the wire for the
    /// session while the snap later used the real secondary bands (a chip under its floor 409'd).
    /// Called under <c>_gate</c> after the band cache is updated: re-publishes the two rate lists from
    /// the cache whenever they differ from what is published.</summary>
    /// <returns>The number of bands re-published, or null when nothing changed — logged by the
    /// caller after <c>_gate</c> is released (the nudge/stop path takes the gate too).</returns>
    private int? RepublishPadBandsLocked() {
        if (_capabilities is null || _axisBands is null) {
            return null;
        }
        var padBands = PadBandsFrom((_axisBands[0], _axisBands[1]));
        var bandDtos = BandDtosOf(padBands);
        if (_capabilities.MoveAxisRateBandsDegPerSec is { } published && published.SequenceEqual(bandDtos)) {
            return null;
        }
        _capabilities = _capabilities with {
            MoveAxisRatesDegPerSec = [.. EndpointsOf(padBands)],
            MoveAxisRateBandsDegPerSec = bandDtos,
        };
        return bandDtos.Count;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "MoveAxis pad bands re-published from the settled AxisRates cache ({Count} band(s)); the first capabilities pass had an axis still unread (#1230).")]
    private static partial void LogPadBandsRepublished(ILogger logger, int count);

    // Every [Minimum, Maximum] rate band for one axis (discrete rate → Min==Max), ascending by Min,
    // bands with a non-positive maximum dropped; empty when the mount answers with no bands; NULL
    // when the read threw (NotImplemented or a transient failure) so the #1064 cache can retry a
    // throw but settle on an honest empty answer.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Capability read boundary: AxisRates throws on a mount that NotImplements MoveAxis for the axis; null is the honest degraded result — the caps DTO offers no speeds for it and the #1064 rate cache retries on the next pass. CA1031's log-and-recover boundary applies.")]
    private static List<(double Min, double Max)>? ReadAxisBands(ITelescopeDevice c, TelescopeAxis axis) {
        var bands = new List<(double Min, double Max)>();
        try {
            foreach (IRate rate in c.AxisRates(axis)) {
                // Finite, positive maximum only: a NaN endpoint would reach the gap branch and throw.
                if (double.IsFinite(rate.Maximum) && rate.Maximum > 0) {
                    var min = double.IsFinite(rate.Minimum) ? Math.Max(0, Math.Min(rate.Minimum, rate.Maximum)) : rate.Maximum;
                    bands.Add((min, rate.Maximum));
                }
            }
        } catch (Exception) {
            return null;
        }
        return bands.OrderBy(b => b.Min).ToList();
    }

    private static double? MaxOf(IReadOnlyList<(double Min, double Max)>? bands) =>
        bands is { Count: > 0 } ? bands.Max(b => b.Max) : null;

    /// <summary>Convert an ASCOM optics property (metres) to mm for the wire, or
    /// null when the driver reports a non-positive value (an unconfigured 0 / a
    /// bogus negative) — so the wizard never auto-fills a useless 0. Internal for
    /// direct unit testing (the read itself sits behind a sealed Alpaca client).</summary>
    internal static double? OpticsMmFromMetres(double metres) =>
        metres > 0 ? metres * 1000.0 : null;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported/transiently-failing EquatorialSystem read throws; report null ('read failed, retry next pass') rather than failing the whole refresh — RefreshPass distinguishes a failed read from a device genuinely reporting Other. CA1031's log-and-recover boundary applies.")]
    private static EquatorialCoordinateType? ReadEquatorialSystem(ITelescopeDevice c) {
        try {
            return c.EquatorialSystem;
        } catch (Exception) {
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: TrackingRates (or enumerating it) can throw on a driver that does not implement it; the supported-rate list falls back to empty rather than failing the whole capability read. CA1031's log-and-recover boundary applies.")]
    private static List<string> ReadSiderealRates(ITelescopeDevice c) {
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

    // #1193 — the phrases AlpacaBridge reports once it has latched after a mount link blip. Matched
    // on the whole exception chain (the Alpaca client wraps the bridge's text in a DriverException).
    // Extracted (internal) for direct unit testing.
    private static readonly string[] LatchedBridgeFaultPhrases = ["communications compromised"];

    // The message of the exception (in the chain or aggregate) that carries the latch phrase, so the
    // fault row says "Mount communications compromised", not "One or more errors occurred" (#1246).
    internal static string LatchedBridgeFaultMessage(Exception ex) {
        for (var e = ex; e is not null; e = e.InnerException) {
            if (e is AggregateException aggregate) {
                foreach (var inner in aggregate.InnerExceptions) {
                    if (IsLatchedBridgeFault(inner)) {
                        return LatchedBridgeFaultMessage(inner);
                    }
                }
            }
            foreach (var phrase in LatchedBridgeFaultPhrases) {
                if (e.Message.Contains(phrase, StringComparison.OrdinalIgnoreCase)) {
                    return e.Message;
                }
            }
        }
        return ex.Message;
    }

    internal static bool IsLatchedBridgeFault(Exception? ex) {
        for (var e = ex; e is not null; e = e.InnerException) {
            // #1246 — ReadRuntime reports every failed read; any one of them carrying the phrase is the latch.
            if (e is AggregateException aggregate) {
                foreach (var inner in aggregate.InnerExceptions) {
                    if (IsLatchedBridgeFault(inner)) {
                        return true;
                    }
                }
            }
            foreach (var phrase in LatchedBridgeFaultPhrases) {
                if (e.Message.Contains(phrase, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
        }
        return false;
    }

    // #1193's never-during-a-slew rule. The runtime's own Slewing answer decides when it answered;
    // when the Slewing read is latched too (#1246) its false is a fallback, so SlewWatch's open
    // episode (the goto this daemon saw start) stands in — bounded by LatchedSlewHold, after which
    // the latch trips anyway, since a bridge that never clears must still reach the ladder.
    private bool SlewMayBeInFlight(TelescopeStateDto runtime, bool slewingUnknown) {
        if (runtime.State.Equals("slewing", StringComparison.Ordinal)) {
            return true;
        }
        if (!slewingUnknown) {
            return false;
        }
        string? logExpired = null;
        try {
            lock (_gate) {
                if (!SlewWatch.InSlew) {
                    _latchedSlewHoldSince = null;
                    _latchedSlewHoldExpiredLogged = false;
                    return false;
                }
                _latchedSlewHoldSince ??= DateTimeOffset.UtcNow;
                // Once expired it stays expired while the latch persists (only a clean tick or a closed
                // episode resets it), so every later latched tick trips at once rather than holding again.
                if (DateTimeOffset.UtcNow - _latchedSlewHoldSince.Value >= LatchedSlewHold) {
                    if (!_latchedSlewHoldExpiredLogged) {
                        _latchedSlewHoldExpiredLogged = true; // once per hold, not per tick
                        logExpired = _lifecycle.Device?.Name ?? "?";
                    }
                    return false;
                }
                return true;
            }
        } finally {
            if (logExpired is not null) {
                LogLatchedSlewHoldExpired(logExpired); // off-lock, like the file's other logs
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telescope '{Device}': the Slewing read has been latched for the whole slew hold with a goto open — tripping the latched fault anyway (#1246)")]
    private partial void LogLatchedSlewHoldExpired(string device);

    // Same transition as the probe streak (the module's trip), with its own fault details so the log
    // says what happened, and latched once per episode (see _bridgeFaultTripped). The §42.3 ladder's
    // reconnect is the disconnect + reconnect that clears the bridge. Returns true when it tripped.
    private bool TripLatchedBridgeFault(ITelescopeDevice probed, Exception error) {
        lock (_gate) {
            if (_bridgeFaultTripped || !_lifecycle.IsLive(probed)) {
                return false;
            }
            _bridgeFaultTripped = true; // before the trip: the ladder's reconnect may follow at once
        }
        var message = LatchedBridgeFaultMessage(error); // the matching read's text, not an aggregate's wrapper
        if (!_lifecycle.TripConnectionLost(probed,
                $"bridge reports a latched mount fault ({message}) — reconnecting clears it (#1193)")) {
            lock (_gate) {
                _bridgeFaultTripped = false; // a reconnect swapped the session first: nothing was tripped
            }
            return false;
        }
        LogBridgeFaultLatched(_lifecycle.Device?.Name ?? "?", message);
        return true;
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Telescope '{Device}' reports a latched bridge fault ('{Error}') — marked Error for the §42.3 reconnect (#1193)")]
    private partial void LogBridgeFaultLatched(string device, string error);

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
        _leaseTimer.Dispose();
        _lifecycle.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telescope slew to RA {Ra}h Dec {Dec}° failed")]
    private partial void LogSlewFailed(Exception ex, double ra, double dec);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telescope control op {Op} failed")]
    private partial void LogControlFailed(Exception ex, string op);
}