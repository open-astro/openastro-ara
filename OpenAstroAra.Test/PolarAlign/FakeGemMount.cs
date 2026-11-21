#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test.PolarAlign {

    /// <summary>
    /// #2064 (#2048 "Fake mount") — a hardware-free German equatorial mount that keeps its TRUE mechanical pose
    /// (<see cref="GemPose"/>: the RA axis, the Dec axis past the pole allowed, tracking, park, home) apart from what it
    /// REPORTS, and records every motion against that truth in a ledger. A test can then say "the counterweight never
    /// went past L" or "nothing moved after the run ended" about the real mount, not about its readings.
    /// <para>It is the daemon's mount as Polar Align sees it: <see cref="ITelescopeMediator"/>, the
    /// <see cref="ITelescopeService"/>, and through it the <see cref="IMountMotionControl"/> seam PA moves it by (fresh
    /// timestamped reads, the leased MoveAxis, the abort and session epochs, the pose-trust check), plus the motion owner
    /// the seam will gain (<see cref="AcquireMotion"/>, #2048 ARA-2). The <c>TelescopeService</c>
    /// behaviour PA relies on is modelled here: a lease in 250–5000 ms that stops the axis when it lapses, a renewal
    /// refused after an abort, a routine's move refused while the pose is unverified.</para>
    /// <para>Physics is integrated from the clock (<see cref="TimeProvider"/>: a <see cref="ManualMountClock"/> moved by
    /// hand in the fake's own tests, or by the routine's own reads through <see cref="FreshReadTakes"/> in a service test
    /// that measures where a turn lands; the system clock otherwise) in steps of at most 10 ms, lazily on every call: motor
    /// rates, tracking at the sidereal rate, a firmware GoTo that takes the counterweight-down side, soft limits. The
    /// mount reports from the pose it BELIEVES it has (the true pose plus a zero error), through its own clock
    /// (<see cref="MountClockOffsetHours"/>), its driver cache (<see cref="DriverCache"/>) and the lies a test sets.</para>
    /// <para>Calls PA must never make (ARA-15: Sync, a meridian flip, CommandX, any Action but <c>motionsafety</c>)
    /// throw and are recorded in <see cref="Forbidden"/>. ITelescopeMediator has no pier-side write; the only way to
    /// one is the meridian flip, which throws. Members PA does not use and the fake does not model throw
    /// <see cref="NotSupportedException"/>.</para>
    /// </summary>
    internal sealed partial class FakeGemMount : ITelescopeMediator, ITelescopeService {

        private static readonly TimeSpan MaxStep = TimeSpan.FromMilliseconds(10);
        private static readonly TimeSpan WaitPoll = TimeSpan.FromMilliseconds(2);
        /// <summary>A sample of the true trajectory is kept each time the pose moved this far (degrees).</summary>
        private const double SampleEveryDeg = 0.1;

        private readonly object _lock = new();
        private readonly TimeProvider _clock;
        private readonly DateTimeOffset _t0;
        private readonly double _lst0Deg;
        private readonly List<MotionEntry> _motions = new();
        private readonly List<StopEntry> _stops = new();
        private readonly List<CommandEntry> _commands = new();
        private readonly List<ForbiddenCall> _forbidden = new();
        private readonly List<PoseSample> _samples = new();
        private readonly double[] _rate = new double[2];
        private readonly bool[] _runaway = new bool[2];
        private readonly TimeSpan?[] _leaseUntil = new TimeSpan?[2];
        // #2067 — TelescopeService's own lease per axis (the rate it last sent and until when): a renewal at that rate while
        // it lasts only extends it and never reaches the driver, even when the firmware stopped the axis meanwhile (a soft
        // limit). Cleared by a stop, an abort and its own lapse.
        private readonly (double Rate, TimeSpan Until)?[] _serviceLease = new (double Rate, TimeSpan Until)?[2];
        private readonly int[] _openMotor = { -1, -1 };

        private TimeSpan _t;
        private GemPose _true;
        private double _zeroErrHaDeg;
        private double _zeroErrDecDeg;
        private bool _tracking;
        private bool _atPark;
        private bool _homeFlag;
        private bool _connected = true;
        private GoToRun? _goto;
        private long _abortEpoch;
        private long _sessionEpoch;
        private long _trackingWrites;
        private int _abortsToIgnore;
        private int _axisStopsToIgnore;
        private int _freshReadsFail;
        private bool _daemonCacheLost;
        private bool _slewReadsThrow;
        private bool _everMoved;
        private TimeSpan _motionStartedAt;
        private (double RaDeg, double DecDeg)? _frozenReading;
        private DriverSample? _sample;
        private double _motorTravelDeg;
        private double _maxPhiDeg;
        private bool _limitHit;
        private string? _limitReason;
        private int _abortCount;
        private string? _motionOwner;
        private object? _motionClaim;
        private bool _inhibited;
        private int _cachedReadsWhileMoving;
        private int _sentinelReads;
        private int _staleReads;
        private int _reconnectAtMovingRead;
        private int? _commandsAtReconnect;
        private readonly List<(long Epoch, string Reason)> _abortReasons = new();

        /// <param name="truePose">Where the mount really is.</param>
        /// <param name="lstHours">The true local sidereal time at the fake's start (hours); it then runs with the clock.</param>
        /// <param name="latitudeDeg">The site; negative for the southern hemisphere.</param>
        /// <param name="tracking">Whether the mount tracks at the start.</param>
        /// <param name="clock">The clock the physics runs on; the system clock by default.</param>
        public FakeGemMount(GemPose truePose, double lstHours, double latitudeDeg = 45.0, double longitudeDeg = -75.0,
                bool tracking = true, TimeProvider? clock = null) {
            if (!double.IsFinite(lstHours)) {
                throw new ArgumentOutOfRangeException(nameof(lstHours), "the fake mount needs a real LST; NaN models no site in the daemon, not in the mount");
            }
            _clock = clock ?? TimeProvider.System;
            _t0 = _clock.GetUtcNow();
            _lst0Deg = GemGeometry.Wrap360(lstHours * 15.0);
            LatitudeDeg = latitudeDeg;
            LongitudeDeg = longitudeDeg;
            _true = Normalized(truePose);
            _tracking = tracking;
            InitialTrueSide = GemGeometry.Side(_true, South);
            _maxPhiDeg = GemGeometry.CounterweightAngleDeg(_true);
            AddSampleLocked(force: true);
        }

        /// <summary>A mount the user pointed at (<paramref name="haHours"/>, <paramref name="decDeg"/>) on its
        /// counterweight-down side, which is the side the hour-angle model reports.</summary>
        public static FakeGemMount CounterweightDownAt(double haHours, double decDeg, double lstHours, double latitudeDeg = 45.0,
                bool tracking = true, TimeProvider? clock = null) =>
            new(GemGeometry.CounterweightDown(haHours * 15.0, decDeg, latitudeDeg < 0), lstHours, latitudeDeg, tracking: tracking, clock: clock);

        // ── the site and its clocks ─────────────────────────────────────────────────────────

        public double LatitudeDeg { get; }
        public double LongitudeDeg { get; }
        private bool South => LatitudeDeg < 0;

        /// <summary>The mount's own clock is off by this much (#2048 <c>lst_offset_h</c>): its reported RA and its GoTo
        /// targets use it. The true pose does not care.</summary>
        public double MountClockOffsetHours { get; set; }

        /// <summary>The daemon's site longitude is off by this much (#2048 <c>site_lon_err_deg</c>): the LST the daemon
        /// computes (<see cref="TelescopeInfo.SiderealTime"/>) is off by it / 15 h.</summary>
        public double SiteLonErrDeg { get; set; }

        private double TrueLstDeg => GemGeometry.Wrap360(_lst0Deg + (_t.TotalSeconds * GemGeometry.SiderealDegPerSecond));
        private double MountLstDeg => GemGeometry.Wrap360(TrueLstDeg + (MountClockOffsetHours * 15.0));
        private double AraLstDeg => GemGeometry.Wrap360(TrueLstDeg + SiteLonErrDeg);

        /// <summary>The true local sidereal time now (hours).</summary>
        public double TrueLstHours {
            get {
                lock (_lock) {
                    IntegrateLocked();
                    return TrueLstDeg / 15.0;
                }
            }
        }

        // ── behaviours a test sets (#2048 "Reporting behaviours", "Motor model", "Firmware GoTo model") ──────────

        public PierSideSource PierSource { get; set; } = PierSideSource.HourAngleModel;
        public DestinationSideMode Dsop { get; set; } = DestinationSideMode.HourAngleModel;
        /// <summary>The driver answers position and Slewing from a cache refreshed at most this often (<c>cache_ms</c>;
        /// AlpacaBridge iOptron: 1 s). Zero: every read is live.</summary>
        public TimeSpan DriverCache { get; set; }
        /// <summary>Slewing reads false once a motion has run this long, while it still moves (<c>slewing_false_early</c>).</summary>
        public TimeSpan? SlewingFalseAfter { get; set; }
        /// <summary>Slewing reads true from the first motion on, whatever the mount does (<c>slewing_stuck</c>).</summary>
        public bool SlewingStuck { get; set; }
        /// <summary>Every Slewing read fails, an HTTP 500 (<c>slewing_http=500</c>).</summary>
        public bool SlewingReadFails { get; set; }
        /// <summary>A GoTo is accepted (the PUT answers 200), nothing moves, and Slewing reads fail until the next stop or
        /// motion command (<c>async_refuse</c>).</summary>
        public bool AsyncRefuseGoTo { get; set; }
        /// <summary>The reported RA is off by this much (<c>ra_jump_h</c>).</summary>
        public double RaJumpHours { get; set; }
        /// <summary>The motor's true speed once a stop was ignored (<c>runaway_deg_s</c>); null keeps its last rate.</summary>
        public double? RunawayDegPerSec { get; set; }
        /// <summary>The Dec axis really drifts at this rate while the RA motor runs (<c>dec_drift_deg_s</c>).</summary>
        public double DecDriftDegPerSec { get; set; }
        /// <summary>Every non-zero MoveAxis is refused (<c>moveaxis_refused</c>).</summary>
        public bool MoveAxisRefused { get; set; }
        /// <summary>The driver's RA motor sign (<c>ra_motor_sign</c>): +1 = a positive rate raises the reported RA (the hour
        /// angle falls), the way PA assumes; −1 inverts it.</summary>
        public double RaMotorSign { get; set; } = 1.0;
        /// <summary>The driver's Dec motor sign: +1 = a positive rate lowers the Dec axis angle (from the pole toward the
        /// equator on the normal side), the way PA's tilt off the axis assumes; −1 inverts it.</summary>
        public double DecMotorSign { get; set; } = 1.0;
        /// <summary>The firmware GoTo's speed on each axis (degrees per second; the slew rate).</summary>
        public double GoToRateDegPerSec { get; set; } = 4.0;
        /// <summary>The fastest motor rate the mount takes; a faster one is clamped (TelescopeService snaps into AxisRates).</summary>
        public double? MaxAxisRateDegPerSec { get; set; }
        public SoftLimits Limits { get; set; } = new();
        /// <summary>A GoTo the soft limits refuse is re-sent counterweight-up and executed with the mount's limits lowered
        /// (AlpacaBridge 4.x iOptron's <c>:MS2#</c> fallback, <c>ms2_fallback</c>).</summary>
        public bool Ms2Fallback { get; set; }
        /// <summary>The <c>motionsafety</c> Action's answer (the AlpacaBridge marker version); null = the driver lacks it.</summary>
        public string? MotionSafetyMarker { get; set; } = "1";
        /// <summary>#2070 — why the daemon does not trust the reported pose (a routine's motor move is refused); null = trusted.</summary>
        public string? PositionUnverifiedReason { get; set; }
        public bool CanFindHome { get; set; } = true;
        /// <summary>Where a park takes the mount, as the mount believes it (Home by default).</summary>
        public GemPose? ParkPose { get; set; }
        /// <summary>How long one fresh driver read (position, Slewing, <see cref="ReadNowAsync"/>) takes on a
        /// <see cref="ManualMountClock"/>, an Alpaca round trip: the read moves the clock on by it before it answers. A
        /// routine that polls the mount then sees it move a fixed amount per poll, whatever the scheduler does, so where a
        /// turn lands does not depend on the test machine's load. Zero (the default) leaves the clock alone.</summary>
        public TimeSpan FreshReadTakes { get; set; }

        /// <summary>Each fresh driver read (position, Slewing, <see cref="ReadNowAsync"/>) first waits this long in real
        /// time and ignores the caller's token: a slow Alpaca GET (#2048 M06's 1 s variant). The physics runs on meanwhile.</summary>
        public TimeSpan SlowReads { get; set; }

        /// <summary>While set, each fresh driver read (position, Slewing, <see cref="ReadNowAsync"/>) hangs until the
        /// source completes, ignoring the caller's token: an Alpaca GET stuck on the wire. <see cref="ReadHung"/> completes
        /// when the first read hangs on it.</summary>
        public TaskCompletionSource? HangReads { get; set; }

        /// <summary>Called with the read's kind (<c>pointing</c>, <c>slewing</c>, <c>now</c>, <c>position</c>) as each fresh read starts,
        /// before <see cref="HangReads"/> is checked: a test can set the hang for a chosen read.</summary>
        public Action<string>? OnFreshRead { get; set; }

        /// <summary>Completes when a fresh read first hangs on <see cref="HangReads"/>.</summary>
        public TaskCompletionSource ReadHung { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Called with each AbortSlew's reason before the mount sees it; true refuses it the way the daemon refuses a
        /// stop it cannot send (InvalidOperationException "not connected", not counted in <see cref="AbortEpoch"/>). The hook
        /// may act on the mount itself: another caller's stop racing the refused one.</summary>
        public Func<string, bool>? RefuseAbort { get; set; }

        /// <summary>How many WS events were published when a command arrived: orders the ledger against a run's events.</summary>
        public Func<int>? EventCount { get; set; }

        /// <summary>Completes at the first motor move or GoTo the mount takes.</summary>
        public TaskCompletionSource FirstMove { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The next this many aborts sent while something moves are ignored (<c>abort_ignored=n</c>;
        /// <see cref="int.MaxValue"/>: always). An abort to a still mount does not count.</summary>
        public int AbortIgnored {
            get {
                lock (_lock) {
                    return _abortsToIgnore;
                }
            }
            set {
                lock (_lock) {
                    _abortsToIgnore = value;
                }
            }
        }

        /// <summary>The next this many MoveAxis 0 on a moving axis are ignored, the lease's own stop included
        /// (<c>moveaxis_stop_ignored=n</c>; <see cref="int.MaxValue"/>: always).</summary>
        public int MoveAxisStopIgnored {
            get {
                lock (_lock) {
                    return _axisStopsToIgnore;
                }
            }
            set {
                lock (_lock) {
                    _axisStopsToIgnore = value;
                }
            }
        }

        /// <summary>The next this many fresh position reads fail (a WiFi hiccup); the daemon's cache then answers the
        /// (0, 0) J2000 placeholder until a read succeeds again.</summary>
        public int FreshReadsFail {
            get {
                lock (_lock) {
                    return _freshReadsFail;
                }
            }
            set {
                lock (_lock) {
                    _freshReadsFail = value;
                }
            }
        }

        /// <summary>#2066 — the next this many fresh readings answer the (0, 0) placeholder (a path that lost the
        /// position), stamped now.</summary>
        public int FreshReadsSentinel {
            get {
                lock (_lock) {
                    return _sentinelReads;
                }
            }
            set {
                lock (_lock) {
                    _sentinelReads = value;
                }
            }
        }

        /// <summary>#2066 — the next this many fresh readings are stamped <see cref="StaleReadAge"/> before the read (a
        /// driver answering from a cache that stopped refreshing).</summary>
        public int FreshReadsStale {
            get {
                lock (_lock) {
                    return _staleReads;
                }
            }
            set {
                lock (_lock) {
                    _staleReads = value;
                }
            }
        }

        /// <summary>How old a <see cref="FreshReadsStale"/> reading is.</summary>
        public static readonly TimeSpan StaleReadAge = TimeSpan.FromSeconds(3);

        /// <summary>#2066 — while a motor or a GoTo moves the mount, the this-many-th fresh reading (from when it is set)
        /// comes after a reconnect (<see cref="Reconnect"/>): a new mount session between a routine's checks and its
        /// read. <see cref="CommandsAtReconnect"/> then says how many commands had reached the mount. Zero: never.</summary>
        public int ReconnectAtMovingRead {
            get {
                lock (_lock) {
                    return _reconnectAtMovingRead;
                }
            }
            set {
                lock (_lock) {
                    _reconnectAtMovingRead = value;
                }
            }
        }

        /// <summary>The ledger's command count when <see cref="ReconnectAtMovingRead"/> reconnected; null before.</summary>
        public int? CommandsAtReconnect {
            get {
                lock (_lock) {
                    return _commandsAtReconnect;
                }
            }
        }

        /// <summary>#2066 — the reported Dec is off by this much and never folded over the pole: a synced mount whose Dec
        /// axis zero is off reads past ±90° near the pole (an iOptron HAE29C read 90.9°, #2075).</summary>
        public double ReportedDecOffsetDeg { get; set; }

        /// <summary>#2066 — how often the daemon's refresh cache (<see cref="GetCurrentPosition"/>, <see cref="GetInfo"/>) was
        /// read while a motor or a GoTo truly moved the mount.</summary>
        public int CachedReadsWhileMoving {
            get {
                lock (_lock) {
                    return _cachedReadsWhileMoving;
                }
            }
        }

        /// <summary>The clock the fake runs on: a routine that ages its readings must read the same one.</summary>
        public TimeProvider Clock => _clock;

        /// <summary>The reported position stops changing at what it reads now (<c>ra_freeze</c>); false lets it follow again.</summary>
        public bool ReadingFrozen {
            get {
                lock (_lock) {
                    return _frozenReading is not null;
                }
            }
            set {
                lock (_lock) {
                    IntegrateLocked();
                    _frozenReading = null;
                    _frozenReading = value ? ReportedLiveLocked() : null;
                }
            }
        }

        // ── what a test does to the mount from outside ──────────────────────────────────────

        /// <summary>The fake's clock, from its start. Ledger times are on it.</summary>
        public TimeSpan Now {
            get {
                lock (_lock) {
                    IntegrateLocked();
                    return _t;
                }
            }
        }

        /// <summary>Brings the physics up to the clock (a <see cref="ManualMountClock"/> test calls it after Advance;
        /// every member does it too).</summary>
        public void Integrate() {
            lock (_lock) {
                IntegrateLocked();
            }
        }

        /// <summary>The mount is moved by other means: by its own motors (the hand pad, another app's slew), so it
        /// knows where it is (<paramref name="encodersFollow"/>), or by hand with the clutches open, so it does not.</summary>
        public void MoveByOtherMeans(GemPose truePose, bool encodersFollow = true) {
            lock (_lock) {
                IntegrateLocked();
                var believed = BelievedLocked();
                _true = Normalized(truePose);
                if (!encodersFollow) {
                    _zeroErrHaDeg = GemGeometry.Wrap180(believed.MechHaDeg - _true.MechHaDeg);
                    _zeroErrDecDeg = believed.DecAxisDeg - _true.DecAxisDeg;
                }
                _homeFlag = false;
                _sample = null;
                NotePoseLocked();
                AddSampleLocked(force: true);
            }
        }

        /// <summary>The mount loses power and comes back: every motion stops for real, tracking is off and the daemon sees
        /// a new mount session. With <paramref name="reportsHome"/> the firmware wakes up believing it is at Home
        /// (<c>power_cycle_reports_home</c>, the false zero of #2070) wherever it really is.</summary>
        public void PowerCycle(bool reportsHome) {
            lock (_lock) {
                IntegrateLocked();
                for (var axis = 0; axis < 2; axis++) {
                    if (_rate[axis] != 0) {
                        EndMotorLocked(axis, MotionEnd.PowerLost);
                    }
                    _leaseUntil[axis] = null;
                }
                if (_goto is { } g) {
                    _goto = null;
                    CloseMotionLocked(g.Entry, MotionEnd.PowerLost);
                }
                _tracking = false;
                _sessionEpoch++;
                if (reportsHome) {
                    var home = GemGeometry.Home(South);
                    _zeroErrHaDeg = GemGeometry.Wrap180(home.MechHaDeg - _true.MechHaDeg);
                    _zeroErrDecDeg = home.DecAxisDeg - _true.DecAxisDeg;
                    _homeFlag = true;
                }
                _sample = null;
                AddSampleLocked(force: true);
            }
        }

        /// <summary>The mount disconnects and connects again: a new mount session (#2089), nothing moves.</summary>
        public void Reconnect() {
            lock (_lock) {
                _sessionEpoch++;
                _sample = null;
            }
        }

        /// <summary>Sets the motion inhibit latch (ARA-2; Emergency Stop's first step): new motion and tracking-on are refused.</summary>
        public void Inhibit() {
            lock (_lock) {
                _inhibited = true;
            }
        }

        public void ClearInhibit() {
            lock (_lock) {
                _inhibited = false;
            }
        }

        // ── the truth ───────────────────────────────────────────────────────────────────────

        public GemPose TruePose {
            get {
                lock (_lock) {
                    IntegrateLocked();
                    return _true;
                }
            }
        }

        /// <summary>The true counterweight angle now.</summary>
        public double TruePhiDeg {
            get {
                lock (_lock) {
                    IntegrateLocked();
                    return GemGeometry.CounterweightAngleDeg(_true);
                }
            }
        }

        public PierSide TrueSide {
            get {
                lock (_lock) {
                    IntegrateLocked();
                    return GemGeometry.Side(_true, South);
                }
            }
        }

        /// <summary>The true side when the fake was made.</summary>
        public PierSide InitialTrueSide { get; }

        /// <summary>Where the tube really points on the sky: RA (degrees, the true LST's frame) and Dec — what a plate
        /// solve of the frame would say.</summary>
        public (double RaDeg, double DecDeg) TrueSky {
            get {
                lock (_lock) {
                    IntegrateLocked();
                    var (ha, dec) = GemGeometry.Pointing(_true, South);
                    return (GemGeometry.Wrap360(TrueLstDeg - ha), dec);
                }
            }
        }

        /// <summary>Whether the mount tracks.</summary>
        public bool Tracking {
            get {
                lock (_lock) {
                    IntegrateLocked();
                    return _tracking;
                }
            }
        }

        /// <summary>Whether anything really moves: a motor (a runaway included) or a GoTo.</summary>
        public bool IsMoving {
            get {
                lock (_lock) {
                    IntegrateLocked();
                    return TrulyMovingLocked();
                }
            }
        }

        /// <summary>A motor's true rate now (degrees per second, as commanded; a runaway's own speed when set).</summary>
        public double MotorRate(int axis) {
            lock (_lock) {
                IntegrateLocked();
                return EffectiveRateLocked(axis);
            }
        }

        public string? MotionOwner {
            get {
                lock (_lock) {
                    return _motionOwner;
                }
            }
        }

        // ── ITelescopeService + IMountMotionControl: the members PA uses ───────────────────

        public long AbortEpoch => Interlocked.Read(ref _abortEpoch);

        public long SessionEpoch => Interlocked.Read(ref _sessionEpoch);

        /// <summary>The syncs the mount accepted (#2083): a test bumps it to play a plate-solve sync (Sync itself is forbidden here).</summary>
        public long SyncEpoch { get; set; }

        /// <summary>A plate-solve sync as TelescopeService takes one (#2066, #2083): not sent while a routine holds the
        /// mount's motion owner (false); else the mount accepts it and <see cref="SyncEpoch"/> moves (true).</summary>
        public bool SyncAsTheDaemonDoes() {
            lock (_lock) {
                if (_motionOwner is not null) {
                    CommandLocked("sync refused: " + _motionOwner + " holds the mount", isMotion: false);
                    return false;
                }
                SyncEpoch++;
                CommandLocked("sync", isMotion: false);
                return true;
            }
        }

        public ASCOM.Common.DeviceInterfaces.AlignmentMode? MountAlignmentMode => ASCOM.Common.DeviceInterfaces.AlignmentMode.GermanPolar;

        public Task MoveAxisAsync(int axis, double rate, int? leaseMs, CancellationToken ct) =>
            MoveAxisAsync(axis, rate, leaseMs, unlessAbortedSince: null, ct);

        public Task MoveAxisAsync(int axis, double rate, int? leaseMs, long? unlessAbortedSince, CancellationToken ct) {
            // As TelescopeService: the token is not honoured, a stop always reaches the mount (when it is connected).
            _ = ct;
            try {
                MoveAxisCore(axis, rate, leaseMs, unlessAbortedSince);
                return Task.CompletedTask;
            } catch (InvalidOperationException ex) {
                return Task.FromException(ex);
            }
        }

        public Task AbortSlewAsync(CancellationToken ct) => AbortSlewAsync("user_request", ct);

        public Task AbortSlewAsync(string reason, CancellationToken ct) {
            _ = ct;
            // As TelescopeService: a mount that is not connected refuses the stop before it is counted.
            if (RefuseAbort?.Invoke(reason) == true || !IsConnected) {
                lock (_lock) {
                    CommandLocked("abort refused " + reason, isMotion: false);
                }
                return Task.FromException(new InvalidOperationException("telescope is not connected"));
            }
            Abort(reason);
            return Task.CompletedTask;
        }

        /// <summary>As TelescopeService (#2066): a stop counted in <see cref="AbortEpoch"/> with no command to the mount.</summary>
        public void FenceMotion(string reason) {
            lock (_lock) {
                IntegrateLocked();
                CommandLocked("fence " + reason, isMotion: false);
                CountStopLocked(reason);
            }
        }

        /// <summary>As TelescopeService (#2066): the first reason counted after <paramref name="abortEpoch"/>, other than
        /// <paramref name="except"/>.</summary>
        public string? AbortReasonSince(long abortEpoch, string? except) {
            lock (_lock) {
                return _abortReasons.Where(r => r.Epoch > abortEpoch && !string.Equals(r.Reason, except, StringComparison.Ordinal))
                    .Select(r => r.Reason).FirstOrDefault();
            }
        }

        private void CountStopLocked(string reason) => _abortReasons.Add((Interlocked.Increment(ref _abortEpoch), reason));

        /// <summary>Whether the daemon has the mount: false between <see cref="Disconnect"/> and <see cref="Connect"/>.</summary>
        public bool IsConnected {
            get {
                lock (_lock) {
                    return _connected;
                }
            }
        }

        public async Task<bool?> ReadSlewingNowAsync(CancellationToken ct) {
            await SlowReadAsync("slewing").ConfigureAwait(false);
            TakeFreshReadTime();
            lock (_lock) {
                if (!_connected || SlewingReadFails || _slewReadsThrow) {
                    return null;
                }
                return ReportedSlewingLocked(SampleLocked());
            }
        }

        public async Task<(double RaHours, double DecDegrees)?> ReadPointingNowAsync(CancellationToken ct) {
            await SlowReadAsync("pointing").ConfigureAwait(false);
            TakeFreshReadTime();
            lock (_lock) {
                if (FreshReadFailsLocked()) {
                    return null;
                }
                var s = SampleLocked();
                return (s.RaDeg / 15.0, s.DecDeg);
            }
        }

        /// <summary>#2066 (ARA-2) — runs at the start of every <see cref="ReadNowAsync"/>, before the mount is read: something
        /// that happens between two of the routine's full readings (the mount moved by other means).</summary>
        public Action? OnFullRead { get; set; }

        public async Task<MountReading?> ReadNowAsync(CancellationToken ct) {
            OnFullRead?.Invoke();
            await SlowReadAsync("now").ConfigureAwait(false);
            TakeFreshReadTime();
            lock (_lock) {
                IntegrateLocked();
                if (TrulyMovingLocked()) {
                    _fullReadsWhileMoving++;
                }
                return ReadLocked(readSlewing: true, readSide: true);
            }
        }

        /// <summary>A slow GET (<see cref="SlowReads"/>): the wait ignores the caller's token, as a blocking HTTP call does.</summary>
        private async Task SlowReadAsync(string kind) {
            OnFreshRead?.Invoke(kind);
            if (HangReads is { } hang) {
                ReadHung.TrySetResult();
                await hang.Task.ConfigureAwait(false);
            }
            if (SlowReads > TimeSpan.Zero) {
                await Task.Delay(SlowReads, CancellationToken.None).ConfigureAwait(false);
            }
        }

        public async Task<MountReading?> ReadPositionNowAsync(CancellationToken ct) {
            await SlowReadAsync("position").ConfigureAwait(false);
            TakeFreshReadTime();
            lock (_lock) {
                IntegrateLocked();
                if (TrulyMovingLocked()) {
                    _positionReadsWhileMoving++;
                }
                return ReadLocked(readSlewing: false, readSide: false);
            }
        }

        /// <summary>#2067 — runs at the start of every <see cref="ReadPoseNowAsync"/> (a turn's poll), before the mount is
        /// read: something that happens to the mount mid-turn.</summary>
        public Action? OnPoseRead { get; set; }

        /// <summary>#2067 — the pier side every reading reports instead of its own (a driver that flips its answer); null:
        /// the mount's own.</summary>
        public PierSide? ReportedSideOverride { get; set; }

        /// <summary>#2067 — the position and the pier side, no Slewing (a turn's poll). Counted on its own.</summary>
        public async Task<MountReading?> ReadPoseNowAsync(CancellationToken ct) {
            OnPoseRead?.Invoke();
            await SlowReadAsync("pose").ConfigureAwait(false);
            TakeFreshReadTime();
            lock (_lock) {
                IntegrateLocked();
                if (TrulyMovingLocked()) {
                    _poseReadsWhileMoving++;
                }
                return ReadLocked(readSlewing: false, readSide: true);
            }
        }

        /// <summary>#2083 — the rate bands the mount reports for its axes (AxisRates); null: any rate is taken as asked.</summary>
        public IReadOnlyList<(double Min, double Max)>? AxisRateBands { get; set; }

        /// <summary>As TelescopeService: the fastest band rate at or below the request, never above; the request itself
        /// when the fake reports no bands.</summary>
        public double? MatchMoveAxisRate(int axis, double requested) =>
            AxisRateBands is null ? requested : TelescopeService.MatchMoveAxisRateAtOrBelow(requested, AxisRateBands);

        /// <summary>#2066 — how often <see cref="ReadNowAsync"/> (position, Slewing and pier side),
        /// <see cref="ReadPositionNowAsync"/> and (#2067) <see cref="ReadPoseNowAsync"/> (position and pier side) were called
        /// while a motor or a GoTo truly moved the mount.</summary>
        public (int Full, int PositionOnly, int Pose) FreshReadsWhileMoving {
            get {
                lock (_lock) {
                    return (_fullReadsWhileMoving, _positionReadsWhileMoving, _poseReadsWhileMoving);
                }
            }
        }

        private int _fullReadsWhileMoving;
        private int _positionReadsWhileMoving;
        private int _poseReadsWhileMoving;

        private MountReading? ReadLocked(bool readSlewing, bool readSide) {
            if (FreshReadFailsLocked()) {
                return null;
            }
            if (_reconnectAtMovingRead > 0 && TrulyMovingLocked() && --_reconnectAtMovingRead == 0) {
                _sessionEpoch++;
                _sample = null;
                _commandsAtReconnect = _commands.Count;
            }
            var s = SampleLocked();
            bool? slewing = !readSlewing || SlewingReadFails || _slewReadsThrow ? null : ReportedSlewingLocked(s);
            var side = !readSide || PierSource is PierSideSource.Unknown or PierSideSource.Error ? PierSide.pierUnknown : ReportedSideOverride ?? s.Side;
            if (_sentinelReads > 0) {
                _sentinelReads--;
                return new MountReading(0.0, 0.0, slewing, side, _t0 + _t, _sessionEpoch);
            }
            var age = TimeSpan.Zero;
            if (_staleReads > 0) {
                _staleReads--;
                age = StaleReadAge;
            }
            return new MountReading(s.RaDeg / 15.0, s.DecDeg, slewing, side, _t0 + s.At - age, _sessionEpoch, Epoch.JNOW);
        }

        private void TakeFreshReadTime() {
            if (FreshReadTakes <= TimeSpan.Zero) {
                return;
            }
            var clock = _clock as ManualMountClock
                ?? throw new InvalidOperationException("FreshReadTakes moves a ManualMountClock; this fake runs on another clock");
            clock.Advance(FreshReadTakes);
        }

        public Task<string?> PositionUnverifiedReasonAsync(CancellationToken ct) => Task.FromResult(PositionUnverifiedReason);

        public Task ConfirmHomeAsync(CancellationToken ct) {
            lock (_lock) {
                IntegrateLocked();
                if (!_connected || !ReportedAtHomeLocked()) {
                    return Task.FromException(new InvalidOperationException("the mount does not report AtHome"));
                }
                PositionUnverifiedReason = null;
                return Task.CompletedTask;
            }
        }

        /// <summary>Called with the reason as a routine marks the position unverified, before the mount records it: a test
        /// can hold the routine there.</summary>
        public Action<string>? OnMarkPositionUnverified { get; set; }

        /// <summary>As TelescopeService (#2066): a routine marks the pose unverified; an earlier reason stays.</summary>
        public void MarkPositionUnverified(string reason) {
            OnMarkPositionUnverified?.Invoke(reason);
            lock (_lock) {
                CommandLocked("pose unverified " + reason, isMotion: false);
                PositionUnverifiedReason ??= reason;
            }
        }

        public IDisposable AcquireMotion(string owner) {
            lock (_lock) {
                if (_inhibited) {
                    throw new InvalidOperationException("the motion latch is set (Emergency Stop): no new motion");
                }
                // As TelescopeService: another owner refuses; the same owner's new hold replaces its old one.
                if (_motionOwner is not null && _motionOwner != owner) {
                    throw new InvalidOperationException($"{_motionOwner} is moving the mount");
                }
                _motionOwner = owner;
                CommandLocked("acquire motion " + owner, isMotion: false);
                var claim = new MotionClaim(this);
                _motionClaim = claim;
                return claim;
            }
        }

        private sealed class MotionClaim(FakeGemMount mount) : IDisposable {
            public void Dispose() {
                lock (mount._lock) {
                    if (ReferenceEquals(mount._motionClaim, this)) {
                        mount._motionClaim = null;
                        mount._motionOwner = null;
                        mount.CommandLocked("release motion", isMotion: false);
                    }
                }
            }
        }

        // ── ITelescopeMediator ──────────────────────────────────────────────────────────────

        public TelescopeInfo GetInfo() {
            lock (_lock) {
                IntegrateLocked();
                if (TrulyMovingLocked()) {
                    _cachedReadsWhileMoving++;
                }
                if (!_connected) {
                    return new TelescopeInfo { Connected = false };
                }
                var s = SampleLocked();
                return new TelescopeInfo {
                    Connected = true,
                    SiderealTime = AraLstDeg / 15.0,
                    TrackingEnabled = _tracking,
                    Slewing = !SlewingReadFails && !_slewReadsThrow && ReportedSlewingLocked(s),
                    SideOfPier = PierSource is PierSideSource.Unknown or PierSideSource.Error ? PierSide.pierUnknown : s.Side,
                    AtPark = _atPark,
                    AtHome = ReportedAtHomeLocked(),
                    CanFindHome = CanFindHome,
                    CanPark = true,
                    CanSlew = true,
                    CanSetTrackingEnabled = true,
                    CanMovePrimaryAxis = true,
                    CanMoveSecondaryAxis = true,
                    AlignmentMode = AlignmentMode.GermanPolar,
                    SiteLatitude = LatitudeDeg,
                    SiteLongitude = LongitudeDeg,
                    RightAscension = s.RaDeg / 15.0,
                    Declination = s.DecDeg,
                    EquatorialSystem = Epoch.JNOW,
                };
            }
        }

        public Coordinates GetCurrentPosition() {
            lock (_lock) {
                IntegrateLocked();
                if (TrulyMovingLocked()) {
                    _cachedReadsWhileMoving++;
                }
                if (!_connected || _daemonCacheLost) {
                    return new Coordinates(Angle.ByDegree(0), Angle.ByDegree(0), Epoch.J2000);
                }
                var s = SampleLocked();
                return new Coordinates(s.RaDeg, s.DecDeg, Epoch.JNOW, Coordinates.RAType.Degrees).FoldedOverPole(); // as TelescopeService
            }
        }

        public long TrackingWriteCount => Interlocked.Read(ref _trackingWrites);

        public bool SetTrackingEnabled(bool trackingEnabled) {
            lock (_lock) {
                IntegrateLocked();
                Interlocked.Increment(ref _trackingWrites);
                CommandLocked(trackingEnabled ? "tracking on" : "tracking off", isMotion: false);
                if (!_connected || (trackingEnabled && (_atPark || _inhibited))) {
                    return false;
                }
                _tracking = trackingEnabled;
                AddSampleLocked(force: true);
                return true;
            }
        }

        /// <summary>The mediator's unleased motor command (production's is a no-op stub; PA takes it only off the daemon).</summary>
        public void MoveAxis(TelescopeAxes axis, double rate) => MoveAxisCore((int)axis, rate, leaseMs: null, unlessAbortedSince: null);

        /// <summary>The mediator's fire-and-forget abort (tagged <c>watchdog</c> on the daemon).</summary>
        public void StopSlew() => Abort("watchdog");

        /// <summary>The mediator's GoTo (the sequencer's, Center's). As TelescopeService (#2066 ARA-2): refused, with
        /// nothing sent, while a routine holds the motion owner.</summary>
        public async Task<bool> SlewToCoordinatesAsync(Coordinates coords, CancellationToken token) {
            ArgumentNullException.ThrowIfNull(coords);
            var jnow = coords.Epoch == Epoch.JNOW ? coords : coords.Transform(Epoch.JNOW);
            int? entry;
            lock (_lock) {
                if (_motionOwner is { } owner) {
                    CommandLocked("goto refused: " + owner + " holds the mount", isMotion: false);
                    throw new SequenceEntityFailedException(TelescopeService.MotionOwnedMessage(owner, "GoTo"));
                }
                entry = BeginGoToLocked(MotionKind.GoTo, (jnow.RADegrees, jnow.Dec), fixedTrueTarget: null);
            }
            return entry is int e && await WaitForMotionAsync(e, token).ConfigureAwait(false);
        }

        /// <summary>A GoTo by other means (the hand controller, another program on the driver): the daemon's motion owner
        /// does not see it. Starts it (JNOW RA and Dec, degrees) and returns at once; false when the mount refused it.</summary>
        public bool GoToByOtherMeans(double raDeg, double decDeg) {
            lock (_lock) {
                return BeginGoToLocked(MotionKind.GoTo, (raDeg, decDeg), fixedTrueTarget: null) is int e && !_motions[e].Refused;
            }
        }

        /// <summary>#2066 (ARA-2) — opt-in: the mediator's <see cref="FindHome"/> and <see cref="ParkTelescope"/> refuse, as
        /// TelescopeService's do, while a routine holds the motion owner (the shutdown ladder's). Off by default: other tests
        /// call <see cref="ParkTelescope"/> as the safety park, which goes through the service's ParkAsync and is never refused.</summary>
        public bool MediatorRefusesHomeAndParkWhileOwned { get; set; }

        private void ThrowIfMediatorMoveOwnedLocked(string what) {
            if (MediatorRefusesHomeAndParkWhileOwned && _motionOwner is { } owner) {
                CommandLocked(what + " refused: " + owner + " holds the mount", isMotion: false);
                throw new SequenceEntityFailedException(TelescopeService.MotionOwnedMessage(owner, what));
            }
        }

        public async Task<bool> FindHome(IProgress<ApplicationStatus> progress, CancellationToken token) {
            int? entry;
            lock (_lock) {
                ThrowIfMediatorMoveOwnedLocked("Find Home");
                entry = CanFindHome ? BeginGoToLocked(MotionKind.FindHome, target: null, fixedTrueTarget: GemGeometry.Home(South)) : null;
            }
            return entry is int e && await WaitForMotionAsync(e, token).ConfigureAwait(false);
        }

        public async Task<bool> ParkTelescope(IProgress<ApplicationStatus> progress, CancellationToken token) {
            int? entry;
            lock (_lock) {
                ThrowIfMediatorMoveOwnedLocked("park");
                IntegrateLocked();
                var park = ParkPose ?? GemGeometry.Home(South);
                entry = BeginGoToLocked(MotionKind.Park, target: null, fixedTrueTarget: TrueFromBelieved(park));
            }
            return entry is int e && await WaitForMotionAsync(e, token).ConfigureAwait(false);
        }

        public Task<bool> UnparkTelescope(IProgress<ApplicationStatus> progress, CancellationToken token) {
            lock (_lock) {
                IntegrateLocked();
                CommandLocked("unpark", isMotion: true);
                _atPark = false;
                return Task.FromResult(_connected);
            }
        }

        public async Task WaitForSlew(CancellationToken token) {
            while (true) {
                lock (_lock) {
                    IntegrateLocked();
                    if (_goto is null) {
                        return;
                    }
                }
                await Task.Delay(WaitPoll, token).ConfigureAwait(false);
            }
        }

        public PierSide DestinationSideOfPier(Coordinates coordinates) {
            ArgumentNullException.ThrowIfNull(coordinates);
            var jnow = coordinates.Epoch == Epoch.JNOW ? coordinates : coordinates.Transform(Epoch.JNOW);
            lock (_lock) {
                IntegrateLocked();
                CommandLocked("destination side of pier", isMotion: false);
                var haFw = GemGeometry.Wrap180(MountLstDeg - jnow.RADegrees);
                var plan = GemGeometry.CounterweightDown(haFw, jnow.Dec, South);
                if (Ms2Fallback && GoToLimitViolationLocked(plan, haFw, jnow.Dec) is not null) {
                    plan = GemGeometry.OtherSide(plan, South);
                }
                var landing = GemGeometry.Side(plan, South);
                return Dsop switch {
                    DestinationSideMode.HourAngleModel => GemGeometry.HourAngleModelSide(haFw),
                    DestinationSideMode.Mechanical => landing,
                    DestinationSideMode.NotImplemented => PierSide.pierUnknown,
                    DestinationSideMode.Lie => landing == PierSide.pierEast ? PierSide.pierWest : PierSide.pierEast,
                    _ => throw new InvalidOperationException("DestinationSideOfPier: the driver's read failed"),
                };
            }
        }

        public string Action(string actionName, string actionParameters) {
            if (string.Equals(actionName, "motionsafety", StringComparison.OrdinalIgnoreCase)) {
                lock (_lock) {
                    CommandLocked("action motionsafety", isMotion: false);
                }
                return MotionSafetyMarker ?? throw new NotSupportedException("ActionNotImplemented: motionsafety");
            }
            throw Forbid("Action " + actionName);
        }

        public string SendCommandString(string command, bool raw = true) => throw Forbid("CommandString");

        public bool SendCommandBool(string command, bool raw = true) => throw Forbid("CommandBool");

        public void SendCommandBlind(string command, bool raw = true) => throw Forbid("CommandBlind");

        public Task<bool> Sync(Coordinates coordinates) => throw Forbid("Sync");

        public Task<bool> Sync(Coordinates coordinates, CancellationToken token) => throw Forbid("Sync");

        public Task<bool> MeridianFlip(Coordinates targetCoordinates, CancellationToken token) => throw Forbid("MeridianFlip");

        public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());

        public Task<bool> Connect() {
            lock (_lock) {
                _connected = true;
                _sessionEpoch++;
                _sample = null;
            }
            return Task.FromResult(true);
        }

        public Task Disconnect() {
            lock (_lock) {
                _connected = false;
                _sessionEpoch++;
                _sample = null;
            }
            return Task.CompletedTask;
        }

        public void RegisterHandler(object handler) {
        }

        public void RegisterConsumer(ITelescopeConsumer consumer) {
        }

        public void RemoveConsumer(ITelescopeConsumer consumer) {
        }

        public void Broadcast(TelescopeInfo deviceInfo) {
        }

        public IDevice GetDevice() => throw Unmodelled(nameof(GetDevice));

        public void PulseGuide(GuideDirections direction, int duration) => throw Unmodelled(nameof(PulseGuide));

        [Obsolete("Use SlewToTopocentricCoordinates instead.")]
        public Task<bool> SlewToCoordinatesAsync(TopocentricCoordinates coords, CancellationToken token) => throw Unmodelled("SlewToCoordinatesAsync(topocentric)");

        public Task<bool> SlewToTopocentricCoordinates(TopocentricCoordinates coords, CancellationToken token) => throw Unmodelled(nameof(SlewToTopocentricCoordinates));

        public bool SetTrackingMode(TrackingMode trackingMode) => throw Unmodelled(nameof(SetTrackingMode));

        public bool SetCustomTrackingRate(SiderealShiftTrackingRate rate) => throw Unmodelled(nameof(SetCustomTrackingRate));

        public bool SendToSnapPort(bool start) => throw Unmodelled(nameof(SendToSnapPort));

        public Task RaiseMeridianFlipping(BeforeMeridianFlipEventArgs e) => Task.CompletedTask;

        public Task RaiseMeridianFlipped(AfterMeridianFlipEventArgs e) => Task.CompletedTask;

        public event Func<object, BeforeMeridianFlipEventArgs, Task> MeridianFlipping { add { } remove { } }
        public event Func<object, AfterMeridianFlipEventArgs, Task> MeridianFlipped { add { } remove { } }
        public event Func<object, EventArgs, Task> Parked { add { } remove { } }
        public event Func<object, EventArgs, Task> Homed { add { } remove { } }
        public event Func<object, EventArgs, Task> Unparked { add { } remove { } }
        public event Func<object, MountSlewedEventArgs, Task> Slewed { add { } remove { } }
        public event Func<object, EventArgs, Task> Connected { add { } remove { } }
        public event Func<object, EventArgs, Task> Disconnected { add { } remove { } }

        // ── ITelescopeService: the REST surface PA never touches ────────────────────────────

        public Task<TelescopeDto?> GetAsync(CancellationToken ct) => throw Unmodelled(nameof(GetAsync));
        public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) => throw Unmodelled(nameof(ConnectAsync));
        public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) => throw Unmodelled(nameof(DisconnectAsync));
        public Task<bool> ForgetAsync(CancellationToken ct) => throw Unmodelled(nameof(ForgetAsync));
        public Task<OperationAcceptedDto> SlewAsync(SlewRequestDto request, string? idempotencyKey, CancellationToken ct) => throw Unmodelled(nameof(SlewAsync));
        public Task<OperationAcceptedDto> ParkAsync(ParkRequestDto request, string? idempotencyKey, CancellationToken ct) => throw Unmodelled(nameof(ParkAsync));
        public Task<OperationAcceptedDto> ParkUnlessMotionOwnedAsync(ParkRequestDto request, string? idempotencyKey, CancellationToken ct) => throw Unmodelled(nameof(ParkUnlessMotionOwnedAsync));
        public Task<OperationAcceptedDto> UnparkAsync(string? idempotencyKey, CancellationToken ct) => throw Unmodelled(nameof(UnparkAsync));
        public Task<OperationAcceptedDto> FindHomeAsync(string? idempotencyKey, CancellationToken ct) => throw Unmodelled(nameof(FindHomeAsync));
        public Task SetTrackingAsync(bool enabled, CancellationToken ct) => throw Unmodelled(nameof(SetTrackingAsync));
        public Task SetTrackingRateAsync(string rate, CancellationToken ct) => throw Unmodelled(nameof(SetTrackingRateAsync));

        // ── commands ────────────────────────────────────────────────────────────────────────

        private InvalidOperationException Forbid(string member) {
            lock (_lock) {
                IntegrateLocked();
                _forbidden.Add(new ForbiddenCall(member, _t, Events()));
                CommandLocked("FORBIDDEN " + member, isMotion: false);
            }
            return new InvalidOperationException($"FakeGemMount: {member} is forbidden to polar alignment (#2048 ARA-15)");
        }

        private NotSupportedException Unmodelled(string member) {
            lock (_lock) {
                CommandLocked("unmodelled " + member, isMotion: false);
            }
            return new NotSupportedException($"FakeGemMount does not model {member}");
        }

        private void MoveAxisCore(int axis, double rate, int? leaseMs, long? unlessAbortedSince) {
            if (axis is not (0 or 1)) {
                throw new ArgumentOutOfRangeException(nameof(axis), axis, "0 = primary (RA), 1 = secondary (Dec)");
            }
            if (!double.IsFinite(rate)) {
                throw new ArgumentOutOfRangeException(nameof(rate), rate, "a finite rate");
            }
            lock (_lock) {
                IntegrateLocked();
                var text = string.Create(CultureInfo.InvariantCulture, $"move {(axis == 0 ? "primary" : "secondary")} {rate}");
                // As TelescopeService: a renewal at the rate the service last sent, while its lease lasts, extends the lease
                // only (after the pose and abort checks a renewal is refused by) and the driver is not asked again.
                if (rate != 0 && leaseMs is int renewMs && _serviceLease[axis] is { } held && held.Rate == rate && held.Until > _t
                        && !(unlessAbortedSince is not null && PositionUnverifiedReason is not null)
                        && !(unlessAbortedSince is long renewSince && renewSince != _abortEpoch)) {
                    var until = _t + TimeSpan.FromMilliseconds(Math.Clamp(renewMs, 250, 5000));
                    _serviceLease[axis] = (rate, until);
                    if (_rate[axis] != 0 && !_runaway[axis]) {
                        _leaseUntil[axis] = until;
                    }
                    return;
                }
                _serviceLease[axis] = null;
                if (rate == 0) {
                    if (!_connected) {
                        // As TelescopeService: a stop to a mount it lost is refused, and the axis runs on.
                        CommandLocked(text + " refused", isMotion: false);
                        throw new InvalidOperationException("telescope is not connected");
                    }
                    CommandLocked(text, isMotion: false);
                    _slewReadsThrow = false;
                    StopAxisLocked(axis, StopKind.AxisStop, reason: null);
                    return;
                }
                CommandLocked(text, isMotion: true);
                _slewReadsThrow = false;
                var kind = axis == 0 ? MotionKind.RaMotor : MotionKind.DecMotor;
                // A renewal or a reversal of this axis's own move is not "sent while slewing"; another motion is.
                var otherMotion = _goto is not null || _rate[1 - axis] != 0 || _runaway[axis];
                // As TelescopeService (#2070): a routine's move (unlessAbortedSince set) on an unverified pose is refused
                // first, with the daemon's own refusal; the pad's nudge is not.
                if (unlessAbortedSince is not null && PositionUnverifiedReason is { } why) {
                    RefusedMotionLocked(kind, rate, target: null, "the mount's position is unverified (" + why + ")", otherMotion);
                    throw TelescopeService.PositionUnverifiedRefusal(why);
                }
                var refusal = !_connected ? "the mount is not connected"
                    : _atPark ? "the mount is parked"
                    : _inhibited ? "the motion latch is set (Emergency Stop): no new motion"
                    : MoveAxisRefused ? "the mount refused the move"
                    : unlessAbortedSince is long since && since != _abortEpoch ? "the mount was stopped (Stop Mount) after this move began; the move is not restarted"
                    : null;
                if (refusal is not null) {
                    RefusedMotionLocked(kind, rate, target: null, refusal, otherMotion);
                    throw new InvalidOperationException(refusal);
                }
                var effective = MaxAxisRateDegPerSec is double max ? Math.CopySign(Math.Min(Math.Abs(rate), max), rate) : rate;
                var open = _openMotor[axis];
                if (open >= 0 && Math.Sign(_rate[axis]) == Math.Sign(effective) && !_runaway[axis]) {
                    _rate[axis] = effective; // a renewal, or a new speed the same way: the same motion
                    _motions[open] = _motions[open] with { Rate = rate };
                } else {
                    if (open >= 0) {
                        CloseMotionLocked(open, MotionEnd.Reversed);
                    }
                    _rate[axis] = effective;
                    _runaway[axis] = false;
                    _openMotor[axis] = OpenMotionLocked(kind, rate, target: null, otherMotion);
                }
                _leaseUntil[axis] = leaseMs is int ms ? _t + TimeSpan.FromMilliseconds(Math.Clamp(ms, 250, 5000)) : null;
                _serviceLease[axis] = _leaseUntil[axis] is TimeSpan leased ? (rate, leased) : null;
                FirstMove.TrySetResult();
            }
        }

        private void Abort(string reason) {
            lock (_lock) {
                IntegrateLocked();
                CommandLocked("abort " + reason, isMotion: false);
                _slewReadsThrow = false;
                CountStopLocked(reason);
                _abortCount++;
                _leaseUntil[0] = null; // TelescopeService cancels every lease on an abort
                _leaseUntil[1] = null;
                _serviceLease[0] = null;
                _serviceLease[1] = null;
                var honoured = true;
                // Only an abort with something to stop is ignored: one sent to a still mount would use up the count
                // and leave the abort the scenario is about honoured (as MoveAxisStopIgnored counts moving axes only).
                if (_abortsToIgnore > 0 && TrulyMovingLocked()) {
                    _abortsToIgnore--;
                    honoured = false;
                    for (var axis = 0; axis < 2; axis++) {
                        if (_rate[axis] != 0) {
                            _runaway[axis] = true;
                        }
                    }
                } else {
                    for (var axis = 0; axis < 2; axis++) {
                        if (_rate[axis] != 0) {
                            EndMotorLocked(axis, MotionEnd.Aborted);
                        }
                    }
                    if (_goto is { } g) {
                        _goto = null;
                        CloseMotionLocked(g.Entry, MotionEnd.Aborted);
                    }
                }
                _stops.Add(new StopEntry(_stops.Count, StopKind.Abort, Axis: null, reason, _t, Events(), honoured));
                AddSampleLocked(force: true);
            }
        }

        private void StopAxisLocked(int axis, StopKind kind, string? reason) {
            _leaseUntil[axis] = null;
            var honoured = true;
            if (_rate[axis] != 0) {
                if (_axisStopsToIgnore > 0) {
                    _axisStopsToIgnore--;
                    honoured = false;
                    _runaway[axis] = true;
                } else {
                    EndMotorLocked(axis, kind == StopKind.LeaseStop ? MotionEnd.LeaseLapsed : MotionEnd.Stopped);
                }
            }
            _stops.Add(new StopEntry(_stops.Count, kind, axis, reason, _t, Events(), honoured));
            AddSampleLocked(force: true);
        }

        private void EndMotorLocked(int axis, MotionEnd end) {
            _rate[axis] = 0;
            _runaway[axis] = false;
            _leaseUntil[axis] = null;
            if (_openMotor[axis] >= 0) {
                CloseMotionLocked(_openMotor[axis], end);
                _openMotor[axis] = -1;
            }
            AddSampleLocked(force: true);
        }

        /// <summary>The firmware's GoTo: from the target's hour angle by the MOUNT's clock, the counterweight-down pose
        /// (or a fixed true pose: Find Home, park). Null when refused.</summary>
        private int? BeginGoToLocked(MotionKind kind, (double RaDeg, double DecDeg)? target, GemPose? fixedTrueTarget) {
            IntegrateLocked();
            var text = target is { } t
                ? string.Create(CultureInfo.InvariantCulture, $"goto {t.RaDeg:F3} {t.DecDeg:F3}")
                : kind == MotionKind.FindHome ? "findhome" : "park";
            CommandLocked(text, isMotion: true);
            _slewReadsThrow = false;
            var whileSlewing = TrulyMovingLocked();
            var refusal = !_connected ? "the mount is not connected"
                : _atPark && kind != MotionKind.Park ? "the mount is parked"
                : _inhibited ? "the motion latch is set (Emergency Stop): no new motion"
                : null;
            if (refusal is not null) {
                RefusedMotionLocked(kind, 0, target, refusal, whileSlewing);
                return null;
            }
            GemPose believedTarget;
            if (fixedTrueTarget is { } fixedPose) {
                believedTarget = BelievedFrom(fixedPose);
            } else {
                var (ra, dec) = target!.Value;
                var haFw = GemGeometry.Wrap180(MountLstDeg - ra);
                believedTarget = GemGeometry.CounterweightDown(haFw, dec, South);
                if (GoToLimitViolationLocked(believedTarget, haFw, dec) is { } limit) {
                    MarkLimitHitLocked(limit);
                    RefusedMotionLocked(kind, 0, target, limit, whileSlewing);
                    if (!Ms2Fallback) {
                        return null;
                    }
                    // AlpacaBridge 4.x: the refused GoTo goes out again counterweight-up, with the mount's limits lowered.
                    CommandLocked("goto ms2 re-aim", isMotion: true);
                    believedTarget = GemGeometry.OtherSide(believedTarget, South);
                    kind = MotionKind.GoToCounterweightUp;
                }
            }
            if (AsyncRefuseGoTo && kind is MotionKind.GoTo or MotionKind.GoToCounterweightUp) {
                RefusedMotionLocked(kind, 0, target, "accepted, then refused: Slewing reads fail", whileSlewing);
                _slewReadsThrow = true;
                return _motions.Count - 1;
            }
            var entry = OpenMotionLocked(kind, 0, target, whileSlewing);
            _goto = new GoToRun(believedTarget, entry, kind, fixedTrueTarget is not null && kind == MotionKind.FindHome);
            FirstMove.TrySetResult();
            return entry;
        }

        /// <summary>Waits until the GoTo entry ends (like the mediator, a cancelled wait does not abort it); true when it arrived.</summary>
        private async Task<bool> WaitForMotionAsync(int entry, CancellationToken token) {
            while (true) {
                lock (_lock) {
                    IntegrateLocked();
                    var e = _motions[entry];
                    if (e.End == MotionEnd.Refused && !_slewReadsThrow) {
                        return false;
                    }
                    if (e.End is not (MotionEnd.Running or MotionEnd.Refused)) {
                        return e.End == MotionEnd.Arrived;
                    }
                }
                await Task.Delay(WaitPoll, token).ConfigureAwait(false);
            }
        }

        private string? GoToLimitViolationLocked(GemPose believedTarget, double haFwDeg, double decDeg) {
            var l = Limits;
            var phi = GemGeometry.CounterweightAngleDeg(believedTarget);
            if (l.MaxCounterweightDeg is double maxPhi && phi > maxPhi) {
                return string.Create(CultureInfo.InvariantCulture, $"counterweight angle {phi:F1}° past the limit {maxPhi:F1}°");
            }
            if (l.MaxAbsHourAngleHours is double maxHa && Math.Abs(haFwDeg) > maxHa * 15.0) {
                return string.Create(CultureInfo.InvariantCulture, $"hour angle {haFwDeg / 15.0:F2} h past the limit {maxHa:F2} h");
            }
            if (l.MinAltitudeDeg is double minAlt && GemGeometry.AltitudeDeg(haFwDeg, decDeg, LatitudeDeg) < minAlt) {
                return string.Create(CultureInfo.InvariantCulture, $"target below the altitude limit {minAlt:F1}°");
            }
            if (l.MaxGoToDeg is double maxGo) {
                var from = BelievedLocked();
                var travel = Math.Max(Math.Abs(AxisAngle(believedTarget) - AxisAngle(from)), Math.Abs(believedTarget.DecAxisDeg - from.DecAxisDeg));
                if (travel > maxGo) {
                    return string.Create(CultureInfo.InvariantCulture, $"a {travel:F1}° GoTo past the limit {maxGo:F1}°");
                }
            }
            return null;
        }

        /// <summary>How far the believed pose is past any soft limit (degrees); 0 inside them all.</summary>
        private double LimitExcessLocked(GemPose believed) {
            var l = Limits;
            var excess = 0.0;
            if (l.MaxCounterweightDeg is double maxPhi) {
                excess = Math.Max(excess, GemGeometry.CounterweightAngleDeg(believed) - maxPhi);
            }
            var (ha, dec) = GemGeometry.Pointing(believed, South);
            if (l.MaxAbsHourAngleHours is double maxHa) {
                excess = Math.Max(excess, Math.Abs(ha) - (maxHa * 15.0));
            }
            if (l.MinAltitudeDeg is double minAlt) {
                excess = Math.Max(excess, minAlt - GemGeometry.AltitudeDeg(ha, dec, LatitudeDeg));
            }
            return excess;
        }

        private void MarkLimitHitLocked(string reason) {
            _limitHit = true;
            _limitReason ??= reason;
        }

        // ── physics ─────────────────────────────────────────────────────────────────────────

        private sealed record GoToRun(GemPose BelievedTarget, int Entry, MotionKind Kind, bool ResetsZero);

        private sealed record DriverSample(TimeSpan At, double RaDeg, double DecDeg, bool Moving, PierSide Side);

        /// <summary>The RA axis's rotation from counterweight-down, signed, in (−180, 180]: the coordinate a firmware GoTo
        /// moves linearly, so it never passes through counterweight-up.</summary>
        private static double AxisAngle(GemPose pose) => GemGeometry.Wrap180(pose.MechHaDeg - 90.0);

        private static GemPose Normalized(GemPose pose) => new(GemGeometry.Wrap180(pose.MechHaDeg), pose.DecAxisDeg);

        private GemPose BelievedLocked() => BelievedFrom(_true);

        private GemPose BelievedFrom(GemPose truePose) =>
            new(GemGeometry.Wrap180(truePose.MechHaDeg + _zeroErrHaDeg), truePose.DecAxisDeg + _zeroErrDecDeg);

        private GemPose TrueFromBelieved(GemPose believed) =>
            new(GemGeometry.Wrap180(believed.MechHaDeg - _zeroErrHaDeg), believed.DecAxisDeg - _zeroErrDecDeg);

        private double EffectiveRateLocked(int axis) =>
            _runaway[axis] && RunawayDegPerSec is double r ? Math.CopySign(r, _rate[axis]) : _rate[axis];

        private bool TrulyMovingLocked() => _rate[0] != 0 || _rate[1] != 0 || _goto is not null;

        private void IntegrateLocked() {
            var now = _clock.GetUtcNow() - _t0;
            while (true) {
                ExpireLeasesLocked();
                if (_t >= now) {
                    return;
                }
                var dt = now - _t;
                if (dt > MaxStep && (TrulyMovingLocked() || (_tracking && !_atPark))) {
                    dt = MaxStep; // nothing moves otherwise: one step to now
                }
                foreach (var until in _leaseUntil) {
                    if (until is TimeSpan u && u > _t && u - _t < dt) {
                        dt = u - _t;
                    }
                }
                StepLocked(dt);
                _t += dt;
            }
        }

        private void ExpireLeasesLocked() {
            for (var axis = 0; axis < 2; axis++) {
                if (_leaseUntil[axis] is TimeSpan until && until <= _t) {
                    _leaseUntil[axis] = null;
                    // As TelescopeService's lease timer: with no client it drops the lease and cannot stop the axis.
                    if (_connected) {
                        StopAxisLocked(axis, StopKind.LeaseStop, "lease lapsed");
                    }
                }
            }
        }

        private void StepLocked(TimeSpan dt) {
            var s = dt.TotalSeconds;
            var before = _true;
            var believedBefore = BelievedLocked();
            var next = before;
            var travel = 0.0;
            var arrived = false;
            if (_goto is { } g) {
                var dA = AxisAngle(g.BelievedTarget) - AxisAngle(believedBefore);
                var dD = g.BelievedTarget.DecAxisDeg - believedBefore.DecAxisDeg;
                var most = GoToRateDegPerSec * s;
                var mA = Math.Clamp(dA, -most, most);
                var mD = Math.Clamp(dD, -most, most);
                arrived = Math.Abs(dA - mA) < 1e-9 && Math.Abs(dD - mD) < 1e-9;
                var nb = arrived ? g.BelievedTarget : new GemPose(GemGeometry.Wrap180(believedBefore.MechHaDeg + mA), believedBefore.DecAxisDeg + mD);
                next = TrueFromBelieved(nb);
                travel += Math.Abs(mA) + Math.Abs(mD);
            }
            var raRate = EffectiveRateLocked(0);
            var decRate = EffectiveRateLocked(1);
            var dHa = -RaMotorSign * raRate * s;
            var dDec = (-(South ? -1.0 : 1.0) * DecMotorSign * decRate * s) + (raRate != 0 ? DecDriftDegPerSec * s : 0.0);
            travel += Math.Abs(raRate * s) + Math.Abs(dDec);
            if (_tracking && !_atPark && _goto is null) {
                dHa += GemGeometry.SiderealDegPerSecond * s;
            }
            next = new GemPose(GemGeometry.Wrap180(next.MechHaDeg + dHa), next.DecAxisDeg + dDec);
            // The firmware's soft limits, on the pose it believes; suspended while an :MS2# re-aim runs (AlpacaBridge 4.x
            // lowered the mount's own limits to make it).
            if (_goto?.Kind != MotionKind.GoToCounterweightUp) {
                var excessBefore = LimitExcessLocked(believedBefore);
                var excessAfter = LimitExcessLocked(BelievedFrom(next));
                if (excessAfter > 1e-9 && excessAfter > excessBefore + 1e-12) {
                    LimitStopLocked();
                    return;
                }
            }
            _true = next;
            _motorTravelDeg += travel;
            NotePoseLocked();
            if (arrived && _goto is { } done) {
                _goto = null;
                if (done.ResetsZero) {
                    _zeroErrHaDeg = 0;
                    _zeroErrDecDeg = 0;
                    _homeFlag = true;
                }
                if (done.Kind == MotionKind.Park) {
                    _atPark = true;
                    _tracking = false;
                }
                CloseMotionLocked(done.Entry, MotionEnd.Arrived);
                AddSampleLocked(force: true);
            } else {
                AddSampleLocked(force: false);
            }
        }

        /// <summary>A soft limit was reached: the motors and any GoTo stop where they are; when nothing but tracking moved,
        /// tracking stops (a meridian limit). <c>limit_hit</c> is set.</summary>
        private void LimitStopLocked() {
            var stopped = false;
            for (var axis = 0; axis < 2; axis++) {
                if (_rate[axis] != 0) {
                    EndMotorLocked(axis, MotionEnd.LimitHit);
                    stopped = true;
                }
            }
            if (_goto is { } g) {
                _goto = null;
                CloseMotionLocked(g.Entry, MotionEnd.LimitHit);
                stopped = true;
            }
            if (!stopped) {
                _tracking = false;
            }
            MarkLimitHitLocked(stopped ? "a soft limit stopped the motion" : "a soft limit stopped tracking");
            AddSampleLocked(force: true);
        }

        /// <summary>Keeps the trajectory's maximum φ, overall and for each open motion.</summary>
        private void NotePoseLocked() {
            var phi = GemGeometry.CounterweightAngleDeg(_true);
            if (phi > _maxPhiDeg) {
                _maxPhiDeg = phi;
            }
            foreach (var open in OpenEntriesLocked()) {
                if (phi > _motions[open].TruePhiMaxDeg) {
                    _motions[open] = _motions[open] with { TruePhiMaxDeg = phi };
                }
            }
        }

        private IEnumerable<int> OpenEntriesLocked() {
            if (_openMotor[0] >= 0) {
                yield return _openMotor[0];
            }
            if (_openMotor[1] >= 0) {
                yield return _openMotor[1];
            }
            if (_goto is { } g) {
                yield return g.Entry;
            }
        }

        private void AddSampleLocked(bool force) {
            if (!force && _samples.Count > 0) {
                var last = _samples[^1];
                if (Math.Abs(GemGeometry.Wrap180(_true.MechHaDeg - last.Pose.MechHaDeg)) < SampleEveryDeg
                        && Math.Abs(_true.DecAxisDeg - last.Pose.DecAxisDeg) < SampleEveryDeg
                        && GemGeometry.Side(_true, South) == last.Side) {
                    return;
                }
            }
            _samples.Add(new PoseSample(_t, _true, GemGeometry.CounterweightAngleDeg(_true), GemGeometry.Side(_true, South), _tracking, _motorTravelDeg));
        }

        // ── what the mount reports ──────────────────────────────────────────────────────────

        private (double RaDeg, double DecDeg) ReportedLiveLocked() {
            if (_frozenReading is { } frozen) {
                return frozen;
            }
            var (ha, dec) = GemGeometry.Pointing(BelievedLocked(), South);
            return (GemGeometry.Wrap360(MountLstDeg - ha + (RaJumpHours * 15.0)), dec + ReportedDecOffsetDeg);
        }

        private PierSide ReportedSideLocked(double reportedRaDeg) => PierSource switch {
            PierSideSource.HourAngleModel => GemGeometry.HourAngleModelSide(MountLstDeg - reportedRaDeg),
            PierSideSource.Mechanical => GemGeometry.Side(BelievedLocked(), South),
            _ => PierSide.pierUnknown,
        };

        /// <summary>The driver's answer: live, or its cache when that is younger than <see cref="DriverCache"/>.</summary>
        private DriverSample SampleLocked() {
            IntegrateLocked();
            if (_sample is { } cached && DriverCache > TimeSpan.Zero && _t - cached.At < DriverCache) {
                return cached;
            }
            var (ra, dec) = ReportedLiveLocked();
            var fresh = new DriverSample(_t, ra, dec, TrulyMovingLocked(), ReportedSideLocked(ra));
            _sample = fresh;
            return fresh;
        }

        private bool ReportedSlewingLocked(DriverSample s) {
            if (SlewingStuck && _everMoved) {
                return true;
            }
            if (s.Moving && SlewingFalseAfter is TimeSpan early && s.At - _motionStartedAt >= early) {
                return false;
            }
            return s.Moving;
        }

        private bool ReportedAtHomeLocked() {
            if (TrulyMovingLocked()) {
                return false;
            }
            var b = BelievedLocked();
            var home = GemGeometry.Home(South);
            return _homeFlag || (Math.Abs(GemGeometry.Wrap180(b.MechHaDeg - home.MechHaDeg)) < 0.1 && Math.Abs(b.DecAxisDeg - home.DecAxisDeg) < 0.1);
        }

        private bool FreshReadFailsLocked() {
            IntegrateLocked();
            if (!_connected) {
                return true;
            }
            if (_freshReadsFail > 0) {
                _freshReadsFail--;
                _daemonCacheLost = true;
                return true;
            }
            _daemonCacheLost = false;
            return false;
        }

        // ── the ledger's writes ─────────────────────────────────────────────────────────────

        private int Events() => EventCount?.Invoke() ?? 0;

        private void CommandLocked(string text, bool isMotion) =>
            _commands.Add(new CommandEntry(_commands.Count, text, _t, Events(), isMotion));

        private MotionEntry NewEntryLocked(MotionKind kind, double rate, (double RaDeg, double DecDeg)? target, bool sentWhileSlewing) {
            var (ra, _) = ReportedLiveLocked();
            var phi = GemGeometry.CounterweightAngleDeg(_true);
            var trueHa = GemGeometry.Pointing(_true, South).HaDeg;
            var reportedHa = GemGeometry.Wrap180(AraLstDeg - ra);
            var side = GemGeometry.Side(_true, South);
            var reportedSide = ReportedSideLocked(ra);
            return new MotionEntry {
                Index = _motions.Count, Kind = kind, StartedAt = _t, EventsBefore = Events(), Rate = rate,
                TrueFrom = _true, TrueTo = _true, Target = target,
                ReportedRaFromDeg = ra, ReportedRaToDeg = ra, TrueHaFromDeg = trueHa, TrueHaToDeg = trueHa,
                ReportedHaFromDeg = reportedHa, ReportedHaToDeg = reportedHa,
                TruePhiBeforeDeg = phi, TruePhiAfterDeg = phi, TruePhiMaxDeg = phi,
                TrueSideBefore = side, TrueSideAfter = side, ReportedSideBefore = reportedSide, ReportedSideAfter = reportedSide,
                SentWhileSlewing = sentWhileSlewing, End = MotionEnd.Running,
            };
        }

        private int OpenMotionLocked(MotionKind kind, double rate, (double RaDeg, double DecDeg)? target, bool sentWhileSlewing) {
            var entry = NewEntryLocked(kind, rate, target, sentWhileSlewing);
            _motions.Add(entry);
            _everMoved = true;
            _motionStartedAt = _t;
            _homeFlag = false;
            AddSampleLocked(force: true);
            return entry.Index;
        }

        private void RefusedMotionLocked(MotionKind kind, double rate, (double RaDeg, double DecDeg)? target, string reason, bool sentWhileSlewing) {
            var entry = NewEntryLocked(kind, rate, target, sentWhileSlewing);
            _motions.Add(entry with { EndedAt = _t, Refused = true, RefusedReason = reason, End = MotionEnd.Refused });
        }

        private void CloseMotionLocked(int index, MotionEnd end) {
            if (_motions[index].End != MotionEnd.Running) {
                return;
            }
            _motions[index] = WithCurrentLocked(_motions[index]) with { EndedAt = _t, End = end };
        }

        /// <summary>An entry's "to" fields filled from the mount now.</summary>
        private MotionEntry WithCurrentLocked(MotionEntry e) {
            var (ra, _) = ReportedLiveLocked();
            return e with {
                TrueTo = _true,
                ReportedRaToDeg = ra,
                TrueHaToDeg = GemGeometry.Pointing(_true, South).HaDeg,
                ReportedHaToDeg = GemGeometry.Wrap180(AraLstDeg - ra),
                TruePhiAfterDeg = GemGeometry.CounterweightAngleDeg(_true),
                TrueSideAfter = GemGeometry.Side(_true, South),
                ReportedSideAfter = ReportedSideLocked(ra),
            };
        }

        // ── the ledger's reads ──────────────────────────────────────────────────────────────

        /// <summary>Every motion, in the order sent; a running one's "to" fields read the mount now.</summary>
        public IReadOnlyList<MotionEntry> Motions {
            get {
                lock (_lock) {
                    IntegrateLocked();
                    return _motions.Select(e => e.End == MotionEnd.Running ? WithCurrentLocked(e) : e).ToArray();
                }
            }
        }

        public IReadOnlyList<StopEntry> Stops {
            get {
                lock (_lock) {
                    IntegrateLocked();
                    return _stops.ToArray();
                }
            }
        }

        public IReadOnlyList<CommandEntry> Commands {
            get {
                lock (_lock) {
                    IntegrateLocked();
                    return _commands.ToArray();
                }
            }
        }

        public IReadOnlyList<ForbiddenCall> Forbidden {
            get {
                lock (_lock) {
                    return _forbidden.ToArray();
                }
            }
        }

        /// <summary>The true trajectory: a sample at every motion boundary and every <see cref="SampleEveryDeg"/> of travel.</summary>
        public IReadOnlyList<PoseSample> Trajectory {
            get {
                lock (_lock) {
                    IntegrateLocked();
                    AddSampleLocked(force: true);
                    return _samples.ToArray();
                }
            }
        }

        /// <summary>A soft limit refused a GoTo or stopped a motion (<c>limit_hit</c>).</summary>
        public bool LimitHit {
            get {
                lock (_lock) {
                    IntegrateLocked();
                    return _limitHit;
                }
            }
        }

        public string? LimitReason {
            get {
                lock (_lock) {
                    return _limitReason;
                }
            }
        }

        /// <summary>How many aborts reached the mount (<c>abort_count</c>), honoured or not.</summary>
        public int AbortCount {
            get {
                lock (_lock) {
                    return _abortCount;
                }
            }
        }

        /// <summary>The largest true counterweight angle since the fake was made (<c>max_true_phi</c>).</summary>
        public double MaxTruePhiDeg {
            get {
                lock (_lock) {
                    IntegrateLocked();
                    return _maxPhiDeg;
                }
            }
        }

        /// <summary>The motors' cumulative true travel (degrees, both axes, tracking excluded).</summary>
        public double MotorTravelDeg {
            get {
                lock (_lock) {
                    IntegrateLocked();
                    return _motorTravelDeg;
                }
            }
        }
    }
}
