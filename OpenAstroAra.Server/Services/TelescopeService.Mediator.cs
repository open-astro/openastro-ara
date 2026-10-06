// The device-event members satisfy the equipment mediator interface but are never raised server-side
// (the Flutter client drives state over REST/WS), so CS0067 "event is never used" is expected here
// and intentionally suppressed for the whole file — same as the other *Service.Mediator.cs partials.
#pragma warning disable CS0067

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
using ASCOM.Common.DeviceInterfaces;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e — the real <see cref="TelescopeService"/> also serves the Sequencer's
/// <see cref="ITelescopeMediator"/> (playbook §8.1: one singleton backs both the REST service and the
/// mediator), so the telescope instructions (<c>SetTracking</c>, <c>UnparkScope</c>, <c>ParkScope</c>,
/// <c>FindHome</c>, <c>SlewScopeToRaDec</c>) drive the live Alpaca mount instead of the
/// <c>HeadlessTelescopeMediator</c> stub.
///
/// The long-running ops (<see cref="SlewToCoordinatesAsync(Coordinates, CancellationToken)"/>,
/// <see cref="ParkTelescope"/>, <see cref="UnparkTelescope"/>,
/// <see cref="FindHome(IProgress{ApplicationStatus}, CancellationToken)"/>) drive the device and
/// block on a bounded wait for their terminal condition (settled-on-target, AtPark, !AtPark, AtHome),
/// returning <c>true</c> only when reached — reusing the cancellation+wall-clock-bounded launcher
/// from the focuser/rotator/dome mediators. The tracking writes are prompt synchronous calls.
/// The §28 centering loop also drives <see cref="Sync(Coordinates)"/> (a real
/// <c>SyncToCoordinates</c>, epoch-transformed + capability/parked-guarded), and the §58.4
/// <see cref="MeridianFlip"/> is the pier-side hint + goto the flip executor runs (#1238). Members no
/// registered consumer reaches (MoveAxis, PulseGuide, the topocentric slews, custom tracking rates,
/// snap port) stay no-op stubs — each is documented at its declaration.
/// </summary>
public sealed partial class TelescopeService : ITelescopeMediator {

    private const int MountOpSettleMaxPolls = 1800; // ~180s at the poll interval (park/unpark/findhome)
    // Slews get their own, larger ceiling: a slow-slew mount (1–2°/s and below) doing a long goto
    // can legitimately take 5–10 minutes, and a settled-too-early ceiling reports a SUCCESSFUL slew
    // as false to the instruction. ~600s covers a worst-case meridian-to-horizon goto on slow gear
    // without meaningfully widening the hung-driver window (cancellation still cuts it short).
    // ~600s at the poll interval. Instance-settable (#1265) so a bench fixture can prove the
    // settle-exhaustion fault in a few polls instead of ten minutes; the daemon never sets it.
    internal int SlewSettleMaxPolls { get; set; } = 6000;
    private static readonly TimeSpan MountOpPollInterval = TimeSpan.FromMilliseconds(100);
    // Wall-clock ceiling for a single blocking mount call (Park/Unpark/FindHome/the slew kickoff): a
    // silent device must not park a sequence thread until the OS TCP timeout. The terminal-condition
    // wait adds its own bound on top (~3min for park/unpark/findhome, ~10min for a slew), so the
    // worst-case total before RunMountOpAsync returns is ~8min (~15min for a slew); cancellation (ct)
    // cuts it short.
    private static readonly TimeSpan MountOpHardTimeout = TimeSpan.FromMinutes(5);
    // #1124 — bound on the on-demand EquatorialSystem read a slew/sync makes before the first
    // refresh has landed it: one property GET, so a few seconds is generous; Sync takes no token.
    private static readonly TimeSpan EquatorialSystemReadTimeout = TimeSpan.FromSeconds(10);
    // Sanity bound on the post-slew pointing, paired with the authoritative !Slewing signal. Generous
    // (5°, parity with the dome's azimuth window) on purpose: it only guards against a premature exit
    // where Slewing hasn't asserted yet and the mount is still at its old heading — a *completed*
    // slew (Slewing==false) settles well inside it.
    private const double SlewToleranceDeg = 5.0;

    /// <summary>
    /// Synchronous live snapshot for the Sequencer, served from the §32.4 cache (no blocking HTTP on
    /// the sequence thread). Never throws after Dispose — a running sequence may poll during shutdown,
    /// in which case it reports "not connected". Populates the fields the registered instructions'
    /// Validate/Execute read (Connected, AtPark, CanFindHome, TrackingModes), the cheap
    /// position/state fields already in the cache, and the meridian bookkeeping the
    /// <c>MeridianFlipTrigger</c> evaluates at every item boundary (#1229): <c>SideOfPier</c> from the
    /// mount, <c>SiderealTime</c> from the profile site longitude, <c>TimeToMeridianFlip</c> through
    /// the shared <see cref="OpenAstroAra.Astrometry.MeridianFlip.TimeToMeridianFlip"/> rule. The remaining TelescopeInfo
    /// members (guide rates, axis rates, …) stay at their defaults — no headless instruction consumes
    /// them and each would need additional per-poll device reads.
    /// </summary>
    public TelescopeInfo GetInfo() {
        lock (_gate) {
            var connected = !_disposed && _state == EquipmentConnectionState.Connected && _client is not null;
            var runtime = _runtime;
            var caps = _capabilities;
            var epoch = MapEpoch(_equatorialSystemRaw);
            var info = new TelescopeInfo {
                Connected = connected,
                Name = _device?.Name ?? string.Empty,
                DeviceId = _device?.UniqueId ?? string.Empty,
                RightAscension = connected ? runtime.RightAscensionHours ?? 0 : 0,
                Declination = connected ? runtime.DeclinationDegrees ?? 0 : 0,
                TrackingEnabled = connected && runtime.Tracking,
                AtPark = connected && runtime.Parked,
                AtHome = connected && runtime.AtHome,
                Slewing = connected && runtime.State == "slewing",
                EquatorialSystem = connected ? epoch : Epoch.J2000,
                CanFindHome = connected && caps?.CanFindHome == true,
                CanPark = connected && caps?.CanPark == true,
                CanSetTrackingEnabled = connected && caps?.CanSetTracking == true,
                SideOfPier = connected ? _sideOfPier : PierSide.pierUnknown,
                SiderealTime = double.NaN,
                TimeToMeridianFlip = double.NaN,
            };
            // #1124 — RA/Dec whose frame hasn't been read yet has no honest epoch label: leave the
            // pointing unset (frames record no RA/DEC/EQUINOX, as before the first position read)
            // rather than label it JNOW and have the capture path precess it into a wrong J2000.
            if (connected && _equatorialSystemKnown
                    && runtime.RightAscensionHours is double ra && runtime.DeclinationDegrees is double dec) {
                info.Coordinates = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec), epoch);
            }
            if (connected && caps is not null) {
                PopulateTrackingModes(info.TrackingModes, caps);
            }
            // #1229 — the meridian bookkeeping. Every "can't say" lands as NaN (the TelescopeInfo
            // default of 0 read as "the flip window is NOW" to the trigger's side-of-pier-disabled
            // branch, which fired a flip at the first boundary of every run on a tracking mount).
            if (connected) {
                var profile = _profileService?.ActiveProfile;
                (info.SiderealTime, info.TimeToMeridianFlip) = MeridianBookkeeping(
                    info.Coordinates, info.SideOfPier, profile?.MeridianFlipSettings,
                    profile?.AstrometrySettings.Longitude, UtcNow());
            }
            return info;
        }
    }

    // Injectable clock for the sidereal-time bookkeeping (tests pin the instant).
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// #1229 — the two trigger inputs, from the cached position + the profile site rather than the
    /// mount's own clock (the profile is the authority for every other sky computation: the §58.9
    /// altitude floor, Tonight's Sky, polar alignment — one sky model). Local sidereal time in hours
    /// from the site longitude; time to the flip in hours through the NINA rule the trigger's
    /// window math expects (<c>MaxMinutesAfterMeridian</c> shift + the side-of-pier 12 h deferrals).
    /// NaN for either when it cannot be computed: no site, no position, no flip settings, or the
    /// J2000→JNOW transform's natives missing — the trigger skips a NaN with a warning, which is
    /// the safe side (a flip never fires on a guess). Extracted (internal) for direct unit testing.
    /// </summary>
    internal static (double SiderealTimeHours, double TimeToMeridianFlipHours) MeridianBookkeeping(
            Coordinates? coordinates, PierSide sideOfPier, IMeridianFlipSettings? flipSettings,
            double? siteLongitudeDeg, DateTimeOffset atUtc) {
        if (siteLongitudeDeg is not double longitude || double.IsNaN(longitude)) {
            return (double.NaN, double.NaN);
        }
        var lstHours = SiteAstrometry.LocalSiderealTimeDeg(atUtc, longitude) / 15.0;
        if (coordinates is null || flipSettings is null) {
            return (lstHours, double.NaN);
        }
        try {
            var timeToFlip = OpenAstroAra.Astrometry.MeridianFlip.TimeToMeridianFlip(flipSettings, coordinates, Angle.ByHours(lstHours), sideOfPier);
            return (lstHours, timeToFlip.TotalHours);
        } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                or BadImageFormatException or TypeInitializationException) {
            // Same native-load failure set TransformBestEffort tolerates: a J2000 mount's position
            // needs the SOFA/NOVAS precession to JNOW, and a dev box without the natives must
            // degrade to "unknown" (NaN), never to a flip time computed in the wrong frame.
            return (lstHours, double.NaN);
        }
    }

    // TrackingModes drives SetTracking.Validate (it checks Contains(TrackingMode)). The cached
    // capability strings are our own ReadSiderealRates output (DriveRate.ToString()), so exact
    // enum-name matching is correct; Stopped is always reachable when the mount lets us stop tracking.
    private static void PopulateTrackingModes(IList<TrackingMode> modes, TelescopeCapabilitiesDto caps) {
        foreach (var rate in caps.SupportedSiderealRates) {
            if (MapDriveRateName(rate) is { } mode && !modes.Contains(mode)) {
                modes.Add(mode);
            }
        }
        if (caps.CanSetTracking && !modes.Contains(TrackingMode.Stopped)) {
            modes.Add(TrackingMode.Stopped);
        }
    }

    // Extracted (internal) for direct unit testing.
    internal static TrackingMode? MapDriveRateName(string driveRate) => driveRate switch {
        nameof(DriveRate.Sidereal) => TrackingMode.Sidereal,
        nameof(DriveRate.Lunar) => TrackingMode.Lunar,
        nameof(DriveRate.Solar) => TrackingMode.Solar,
        nameof(DriveRate.King) => TrackingMode.King,
        _ => null,
    };

    // ASCOM's mount-native coordinate system → NINA Epoch. Topocentric/Other map to JNOW: the mount
    // wants current-epoch coordinates. Only meaningful for a system actually READ from the device:
    // the "not yet read" sentinel (also Other) must never reach the slew/sync transform or the
    // capture pointing, so those paths gate on _equatorialSystemKnown (#1124). Extracted (internal)
    // for direct unit testing.
    internal static Epoch MapEpoch(EquatorialCoordinateType equatorialSystem) => equatorialSystem switch {
        EquatorialCoordinateType.J2000 => Epoch.J2000,
        EquatorialCoordinateType.J2050 => Epoch.J2050,
        EquatorialCoordinateType.B1950 => Epoch.B1950,
        _ => Epoch.JNOW,
    };

    // The epoch a slew target is transformed into. Coordinates.Transform only supports the
    // J2000/JNOW target frames (it throws NotSupportedException for the rest), so a mount reporting
    // J2050/B1950 (vanishingly rare) gets current-epoch coordinates — the nearest supported frame,
    // and far better than faulting the sequence. Extracted (internal) for direct unit testing.
    internal static Epoch MapSlewEpoch(EquatorialCoordinateType equatorialSystem) {
        var epoch = MapEpoch(equatorialSystem);
        return epoch is Epoch.J2000 or Epoch.JNOW ? epoch : Epoch.JNOW;
    }

    // Cross-epoch transforms P/Invoke the SOFA/NOVAS natives. The Pi package ships them (#1092)
    // and Program.cs logs their presence at boot, but a dev box or a hand-rolled install can still
    // lack them, so a missing/broken native must degrade to the untransformed target (≤ ~arcminutes of precession drift J2000↔JNOW today,
    // within a typical pointing model's slop) instead of failing the instruction. Only the
    // native-load failure modes are caught — a genuine astrometry error still surfaces (and is then
    // contained by RunMountOpAsync's op boundary). Internal for direct unit testing.
    internal Coordinates TransformBestEffort(Coordinates coords, Epoch targetEpoch) {
        try {
            return coords.Transform(targetEpoch);
        } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                or BadImageFormatException or TypeInitializationException) {
            LogTransformFallback(ex, targetEpoch);
            return coords;
        }
    }

    // #1124 — the mount's coordinate system for a slew/sync target. Known → the cached value.
    // Not read yet (the connect's first refresh is still in flight, or every read so far failed) →
    // one on-demand read, bounded by EquatorialSystemReadTimeout, committed to the cache on success
    // so the refresh stops retrying. Null means still unknown: the caller refuses the op rather than
    // guess JNOW (a J2000 target precessed into JNOW for a J2000 mount lands ~0.36° off). A read
    // against a client that was superseded or disconnected meanwhile is not trusted.
    private async Task<EquatorialCoordinateType?> ResolveEquatorialSystemAsync(AlpacaTelescope client, CancellationToken ct) {
        lock (_gate) {
            if (_equatorialSystemKnown && ReferenceEquals(_client, client)) {
                return _equatorialSystemRaw;
            }
        }
        // ReadEquatorialSystem never throws (a failed read is null), so the task abandoned on a
        // timeout needs no observer.
        var read = Task.Run(() => ReadEquatorialSystem(client), CancellationToken.None);
        EquatorialCoordinateType? value;
        try {
            value = await read.WaitAsync(EquatorialSystemReadTimeout, ct).ConfigureAwait(false); // a sequencer cancel propagates
        } catch (TimeoutException) {
            value = null;
        }
        lock (_gate) {
            if (_disposed || _state != EquipmentConnectionState.Connected || !ReferenceEquals(_client, client)) {
                return null;
            }
            if (_equatorialSystemKnown) {
                return _equatorialSystemRaw; // a refresh pass landed the read meanwhile
            }
            if (value is not null) {
                _equatorialSystemRaw = value.Value;
                _equatorialSystemKnown = true;
            }
            return value;
        }
    }

    public Task<bool> SlewToCoordinatesAsync(Coordinates coords, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(coords);
        AlpacaTelescope client;
        bool parked;
        lock (_gate) {
            // Connected check BEFORE the transform: Transform P/Invokes NOVAS, which must not run
            // (and on a dev box without the native lib, cannot run) for a slew that is going to be
            // reported as failed anyway.
            if (_disposed || _state != EquipmentConnectionState.Connected || _client is null) {
                return Task.FromResult(false);
            }
            client = _client;
            parked = _runtime.Parked;
        }
        if (parked) {
            // ASCOM mounts throw on slew-while-parked; pre-empt with a clean failure (the instruction
            // has already surfaced the parked warning to the user).
            LogMountOpRejectedParked("telescope.slew");
            return Task.FromResult(false);
        }
        return SlewCoreAsync();

        async Task<bool> SlewCoreAsync() {
            // #1124 — a slew right after connect can beat the first refresh: read the system now
            // rather than guess. Still unknown → fail the instruction without sending anything
            // (SlewScopeToRaDec ignores a false return, so a refusal must throw to be seen).
            var equatorialSystem = await ResolveEquatorialSystemAsync(client, token).ConfigureAwait(false);
            if (equatorialSystem is null) {
                LogMountOpRejectedUnknownSystem("telescope.slew");
                throw new SequenceEntityFailedException(UnknownSystemMessage("telescope.slew"));
            }
            // Transform to the mount's native coordinate system (a J2000 sequence target sent raw to
            // a JNOW mount would be off by the precession drift, ~arcminutes). Best-effort: the
            // cross-epoch path P/Invokes SOFA/NOVAS (shipped with the Pi package since #1092, but
            // absent on an unstaged dev box) — a missing native falls back to the untransformed
            // target rather than failing the run (same-epoch transforms are pure managed).
            var target = TransformBestEffort(coords, MapSlewEpoch(equatorialSystem.Value));
            var targetRa = target.RA;
            var targetDec = target.Dec;
            lock (_gate) {
                TargetLatch.NoteTargetCommand(); // §57.9 — a goto re-arms the target display
                SlewWatch.NoteSlewTarget(targetRa, targetDec); // §57.8 — slew_started carries the intent
            }
            try {
                return await RunMountOpAsync("telescope.slew",
                    c => {
                        TryEnableTracking(c);
                        // Async goto: returns immediately, Slewing goes true; the settle-wait below
                        // confirms arrival. Same call the REST SlewInBackground path uses.
                        c.SlewToCoordinatesAsync(targetRa, targetDec);
                    },
                    c => !ReadSlewing(c) && PointingNear(c, targetRa, targetDec),
                    token,
                    SlewSettleMaxPolls).ConfigureAwait(false);
            } finally {
                // Consume-or-clear (#836 r2/r5): when the op returns — success, failure, or throw —
                // this command's episode is over. Either the poll consumed the target (clear is a
                // no-op) or a fast slew settled between ticks and never opened an episode; in both
                // cases the target must not ride an unrelated later episode's slew_started.
                ClearPendingSlewTarget();
            }
        }
    }

    private void ClearPendingSlewTarget() {
        lock (_gate) {
            SlewWatch.ClearPendingTarget();
        }
    }

    public Task<bool> ParkTelescope(IProgress<ApplicationStatus> progress, CancellationToken token) =>
        // TryEnableTracking before Park here too (not just the REST path): some mounts (iOptron) won't
        // park from a stationary state, so an end-of-sequence auto-park would silently no-op otherwise.
        RunMountOpAsync("telescope.park", c => { TryEnableTracking(c); c.Park(); },
            c => ReadAtPark(c) && !ReadSlewing(c), token);

    public Task<bool> UnparkTelescope(IProgress<ApplicationStatus> progress, CancellationToken token) =>
        // !Slewing matters here too: a motorised unpark can clear AtPark the moment the command is
        // accepted while the mount is still moving to its unpark position — returning early would
        // let a back-to-back slew/tracking change land mid-motion.
        RunMountOpAsync("telescope.unpark", c => c.Unpark(),
            c => !ReadAtPark(c) && !ReadSlewing(c), token);

    public Task<bool> FindHome(IProgress<ApplicationStatus> progress, CancellationToken token) =>
        RunMountOpAsync("telescope.findhome", c => c.FindHome(),
            c => ReadAtHome(c) && !ReadSlewing(c), token);

    public bool SetTrackingEnabled(bool trackingEnabled) {
        var client = ConnectedClientOrNull();
        if (client is null) {
            return false;
        }
        NoteMountCommand(w => w.NoteTrackingCommanded(trackingEnabled));
        return TrySetTracking(client, trackingEnabled);
    }

    public bool SetTrackingMode(TrackingMode trackingMode) {
        if (trackingMode == TrackingMode.Custom) {
            // Custom needs the separate RA/Dec offset-rate surface (SiderealShiftTrackingRate) — not
            // wired headless; SetTracking.Validate already excludes it via TrackingModes.
            return false;
        }
        var client = ConnectedClientOrNull();
        if (client is null) {
            return false;
        }
        // A mode change asserts tracking on most drivers — expected-on with grace.
        NoteMountCommand(w => w.NoteTrackingCommanded(true));
        return TrySetTrackingMode(client, trackingMode);
    }

    public void StopSlew() {
        var client = ConnectedClientOrNull();
        if (client is null) {
            return;
        }
        // Fire-and-forget on purpose (the flip watchdog must never block or throw into the
        // flip-recovery path), but routed through the shared §57 abort core so an internal
        // abort gets the same lifecycle events as the panic button — tagged reason=watchdog,
        // so a watchdog-killed flip slew no longer reads as a normal slew_complete (the
        // #836 r4 follow-up from PORT_TODO).
        _ = Task.Run(
            () => AbortSlewCoreAsync(client, reason: "watchdog", rethrow: false),
            CancellationToken.None);
    }

    public async Task WaitForSlew(CancellationToken token) {
        var client = ConnectedClientOrNull();
        if (client is null) {
            return;
        }
        await WaitForMountConditionAsync(client, c => !ReadSlewing(c), token, SlewSettleMaxPolls).ConfigureAwait(false);
    }

    // #1222 — GetCurrentPosition tries the on-demand equatorial-system read at most once per
    // connection (reset on adopt): a mount whose read never answers must not add the 10 s bound to
    // every centering iteration and rotation frame; the refresh pass keeps retrying the read anyway.
    private bool _positionFrameReadAttempted;

    /// <summary>
    /// Current pointing from the §32.4 cache in the mount's native epoch; the headless-stub
    /// (0, 0, J2000) sentinel when not connected or the position hasn't been read yet.
    /// </summary>
    public Coordinates GetCurrentPosition() {
        AlpacaTelescope? unresolved = null;
        lock (_gate) {
            if (!_disposed && _state == EquipmentConnectionState.Connected && _client is not null
                    && !_equatorialSystemKnown && !_positionFrameReadAttempted) {
                _positionFrameReadAttempted = true;
                unresolved = _client;
            }
        }
        if (unresolved is not null) {
            // #1222 — the centering loop labels this position with the mount's frame to compute its
            // offset; in the window before the first refresh read the system, the label was a guess
            // (JNOW) and a J2000 mount's first iteration carried the ~0.36° precession error into the
            // offset. Read the system now, once, bounded like the slew/sync paths (#1124). This is a
            // sync API on the sequencer thread; RunMountOpAsync blocks it the same way.
            var resolved = ResolveEquatorialSystemAsync(unresolved, CancellationToken.None).GetAwaiter().GetResult();
            if (resolved is null) {
                LogPositionFrameGuessed(); // once per connection, by construction
            }
        }
        lock (_gate) {
            var connected = !_disposed && _state == EquipmentConnectionState.Connected && _client is not null;
            if (connected && _runtime.RightAscensionHours is double ra && _runtime.DeclinationDegrees is double dec) {
                return new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec), MapEpoch(_equatorialSystemRaw));
            }
        }
        return new Coordinates(Angle.ByDegree(0), Angle.ByDegree(0), Epoch.J2000);
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Telescope position requested while its equatorial system is still unknown (the read did not answer); labelling it JNOW — a J2000 mount's centering offset is off by the precession until the system reads (#1222)")]
    private partial void LogPositionFrameGuessed();

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Sequencer mount-op boundary: the blocking ASCOM call can throw arbitrary driver/HTTP exceptions and a concurrent Disconnect/Dispose can dispose the captured client mid-op; genuine sequencer cancellation is rethrown, and any other escape (including a device/HTTP-timeout OCE) is logged, published as a §42.4 fault, and rethrown as SequenceEntityFailedException so the instruction's retry/failure machinery engages (§42.2). CA1031's catch-classify-rethrow boundary applies.")]
    private async Task<bool> RunMountOpAsync(string op, Action<AlpacaTelescope> action, Func<AlpacaTelescope, bool> isDone, CancellationToken ct, int settleMaxPolls = MountOpSettleMaxPolls) {
        var client = ConnectedClientOrNull();
        if (client is null) {
            return false; // not connected: the instruction's Validate has already blocked this
        }
        try {
            // §42.2 — note the command before dispatch so the tracking watch's grace window is
            // armed by the time any refresh tick can observe the op's effect. Park/home end with
            // tracking legitimately off; everything else just suppresses without re-expecting.
            NoteMountCommand(op is "telescope.park" or "telescope.findhome"
                ? w => w.NoteParkCommanded()
                : w => w.NoteMotionCommanded());
            // Race the blocking ASCOM call against both the sequencer token and a wall-clock bound so
            // a hung HTTP call can't pin the sequence thread. The abandoned op is observed.
            var opTask = Task.Run(() => action(client), CancellationToken.None);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                linked.CancelAfter(MountOpHardTimeout);
                var completed = await Task.WhenAny(opTask, Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);
                if (completed != opTask) {
                    ObserveQuietly(opTask);
                    ct.ThrowIfCancellationRequested();      // sequencer cancel → propagate
                    throw new TimeoutException($"mount op {op} did not complete within {MountOpHardTimeout.TotalSeconds:0}s");
                }
                await linked.CancelAsync().ConfigureAwait(false);
            }
            await opTask.ConfigureAwait(false); // observe the op's result / surface its exception
            if (!await WaitForMountConditionAsync(client, isDone, ct, settleMaxPolls).ConfigureAwait(false)) {
                // Dispatched but never reached its terminal state (settle exhaustion, or the
                // connection dropped mid-wait — the publish gate suppresses the latter and the
                // §42.3 probe owns it). Either way the mount is not where the instruction needs
                // it — fail rather than report success.
                var msg = $"mount op {op} dispatched but did not reach its terminal state within the settle bound";
                PublishOpFault(client, EquipmentFaultKind.StallTimeout, msg);
                throw new SequenceEntityFailedException(msg);
            }
            return true;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw; // genuine sequencer cancellation — propagate so the run aborts
        } catch (SequenceEntityFailedException) {
            throw; // already classified + published above
        } catch (TimeoutException ex) {
            // The wall-clock bound above: the blocking call never returned — a stalled op (§42.4).
            // §42.2: publish the fault AND fail the instruction, so Attempts retries and
            // sequence.instruction_failed engage instead of the failure being silently swallowed.
            LogMountOpFailed(ex, op);
            PublishOpFault(client, EquipmentFaultKind.StallTimeout, ex.Message);
            throw new SequenceEntityFailedException(ex.Message, ex);
        } catch (Exception ex) {
            LogMountOpFailed(ex, op);
            PublishOpFault(client, EquipmentFaultKind.OpError, $"{op} failed: {ex.Message}");
            throw new SequenceEntityFailedException($"{op} failed: {ex.Message}", ex);
        }
    }

    // Polls the device directly until the terminal condition holds (refreshing the §32.4 cache each
    // tick so GetInfo stays current), or returns false on timeout / a dropped-or-superseded connection.
    // Delay-BEFORE-check (rotator-style), not check-then-delay: ASCOM does not contractually require
    // Slewing to be asserted before SlewToCoordinatesAsync returns, so an immediate first check could
    // read !Slewing + near-target and declare a slew settled before the mount ever moved (a target
    // within the tolerance of the current heading defeats the PointingNear guard). One unconditional
    // poll interval gives the driver that window; it costs 100ms on already-satisfied conditions.
    private async Task<bool> WaitForMountConditionAsync(AlpacaTelescope client, Func<AlpacaTelescope, bool> isDone, CancellationToken ct, int maxPolls = MountOpSettleMaxPolls) {
        var loggedReadFailure = false;
        for (var i = 0; i < maxPolls; i++) {
            await Task.Delay(MountOpPollInterval, ct).ConfigureAwait(false);
            bool stillOurClient;
            lock (_gate) {
                stillOurClient = !_disposed && _state == EquipmentConnectionState.Connected
                    && ReferenceEquals(_client, client);
            }
            if (!stillOurClient) {
                return false; // disconnected / superseded — the op can't be confirmed
            }
            var done = ReadCondition(client, isDone);
            if (done is null && !loggedReadFailure) {
                // A consistently-throwing condition read (e.g. an unsupported property) would
                // otherwise time out silently after the full bound — log it once so the timeout is
                // diagnosable, then keep polling (treat as not-yet-met).
                LogConditionReadFailed();
                loggedReadFailure = true;
            }
            RefreshCacheOnce();
            if (done == true) {
                return true;
            }
        }
        return false; // timed out without reaching the terminal state
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-poll predicate boundary: a transient/unsupported device read throws; report null ('unknown') so the wait keeps polling (and logs once) rather than faulting. CA1031's log-and-recover boundary applies.")]
    private static bool? ReadCondition(AlpacaTelescope client, Func<AlpacaTelescope, bool> isDone) {
        try {
            return isDone(client);
        } catch (Exception) {
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort pre-op aid: a goto needs Tracking, and some mounts (e.g. iOptron) won't park from a stationary/home state — both want Tracking enabled first. A mount that rejects the write may still accept the op (or fail it with its own clear error, which the op path logs); swallowing the write failure keeps the op attempt authoritative. CA1031's log-and-recover boundary applies.")]
    private void TryEnableTracking(AlpacaTelescope client) {
        try {
            if (!client.Tracking) {
                client.Tracking = true;
            }
        } catch (Exception ex) {
            LogTrackingWriteIgnored(ex);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Prompt tracking write boundary: the blocking ASCOM property write can throw arbitrary driver/HTTP exceptions; the failure is logged and reported as false to the instruction rather than faulting the sequence. CA1031's log-and-recover boundary applies.")]
    private bool TrySetTracking(AlpacaTelescope client, bool enabled) {
        try {
            client.Tracking = enabled;
            RefreshCacheOnce();
            return true;
        } catch (Exception ex) {
            LogTrackingWriteFailed(ex);
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Prompt tracking write boundary: the blocking ASCOM TrackingRate/Tracking writes can throw arbitrary driver/HTTP exceptions; the failure is logged and reported as false to the instruction rather than faulting the sequence. CA1031's log-and-recover boundary applies.")]
    private bool TrySetTrackingMode(AlpacaTelescope client, TrackingMode trackingMode) {
        try {
            if (trackingMode == TrackingMode.Stopped) {
                client.Tracking = false;
            } else {
                client.TrackingRate = MapDriveRate(trackingMode);
                client.Tracking = true;
            }
            RefreshCacheOnce();
            return true;
        } catch (Exception ex) {
            LogTrackingWriteFailed(ex);
            return false;
        }
    }

    // Extracted (internal) for direct unit testing. Custom is rejected before this map is consulted.
    internal static DriveRate MapDriveRate(TrackingMode trackingMode) => trackingMode switch {
        TrackingMode.Lunar => DriveRate.Lunar,
        TrackingMode.Solar => DriveRate.Solar,
        TrackingMode.King => DriveRate.King,
        _ => DriveRate.Sidereal,
    };

    private AlpacaTelescope? ConnectedClientOrNull() {
        lock (_gate) {
            return !_disposed && _state == EquipmentConnectionState.Connected ? _client : null;
        }
    }

    // Observes an abandoned (cancelled/timed-out) op task so a later fault can't surface as an
    // UnobservedTaskException, and logs it at Debug for post-mortem.
    private void ObserveQuietly(Task task) {
        _ = task.ContinueWith(
            t => LogAbandonedOpFaulted(t.Exception!),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // §42.4 — op-channel fault publish: snapshot the device under the gate, publish off-lock
    // (EquipmentFaultHub.Publish is non-blocking and never throws into the caller). A fault may
    // only be blamed on the LIVE client — an op whose client was superseded or disposed by a user
    // disconnect/reconnect mid-call must stay a log line (the §42.3 probe owns genuine disconnects),
    // so the liveness check and the device snapshot share one critical section.
    private void PublishOpFault(AlpacaTelescope client, EquipmentFaultKind kind, string details) {
        if (_faults is null) {
            return;
        }
        DiscoveredDeviceDto? device;
        lock (_gate) {
            if (!ReferenceEquals(_client, client)) {
                return;
            }
            device = _device;
        }
        _faults.Publish(new EquipmentFaultEvent(Contracts.DeviceType.Telescope, device?.UniqueId, device?.Name,
            kind, details, DateTimeOffset.UtcNow));
    }

    // Guarded per-field reads used by the terminal-condition predicates (each may throw on an
    // unsupported/transient property; the predicate treats a throw as not-yet-done via ReadCondition).
    private static bool ReadSlewing(AlpacaTelescope c) => c.Slewing;
    private static bool ReadAtPark(AlpacaTelescope c) => c.AtPark;
    private static bool ReadAtHome(AlpacaTelescope c) => c.AtHome;

    private static bool PointingNear(AlpacaTelescope c, double targetRaHours, double targetDecDeg) {
        var raDiffHours = Math.Abs(c.RightAscension - targetRaHours);
        var raDiffDeg = Math.Min(raDiffHours, 24.0 - raDiffHours) * 15.0; // wrap-aware, hours → degrees
        return raDiffDeg < SlewToleranceDeg && Math.Abs(c.Declination - targetDecDeg) < SlewToleranceDeg;
    }

    /// <summary>
    /// §28 — recalibrate the mount's pointing model to <paramref name="coordinates"/> (no motion). The
    /// centering loop (<see cref="OpenAstroAra.PlateSolving.CenteringSolver"/>) calls this after a solve so
    /// the follow-up slew lands accurately; it degrades to offset compensation when this returns false, so a
    /// mount without sync (returns false on <c>CanSync</c>), a parked/disconnected mount, or a driver fault
    /// is a clean "not synced", never a fault. Coordinates are transformed to the mount's native epoch first
    /// (a J2000 solve result synced raw to a JNOW mount would recalibrate to the wrong place). Unlike a slew,
    /// sync is instantaneous, so there's no settle-poll — a returning <c>SyncToCoordinates</c> is done.
    /// </summary>
    public Task<bool> Sync(Coordinates coordinates) => Sync(coordinates, CancellationToken.None);

    /// <summary>#1222 — <see cref="Sync(Coordinates)"/> with the caller's token: the equatorial-system
    /// read it may need (#1124) waits up to its 10 s bound, and a sequence Stop during a centering
    /// sync used to sit that out. The centering loop passes its token.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Mount sync boundary: the blocking ASCOM SyncToCoordinates/CanSync can throw arbitrary driver/HTTP exceptions and a concurrent Disconnect/Dispose can dispose the captured client mid-call; every escape is logged and reported as a failed sync (false) so the centering loop falls back to offset compensation rather than faulting the run. CA1031's log-and-recover boundary applies.")]
    public async Task<bool> Sync(Coordinates coordinates, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(coordinates);
        bool parked;
        lock (_gate) {
            // Connected check before the transform (which P/Invokes NOVAS): don't run native astrometry
            // for a sync that's going to be reported failed anyway.
            if (_disposed || _state != EquipmentConnectionState.Connected || _client is null) {
                return false;
            }
            parked = _runtime.Parked;
        }
        if (parked) {
            // Syncing a parked mount would recalibrate to the park position; ASCOM drivers throw. Pre-empt.
            LogMountOpRejectedParked("telescope.sync");
            return false;
        }
        var client = ConnectedClientOrNull();
        if (client is null) {
            return false;
        }
        // #1124 — as for the slew: read the system now if the first refresh hasn't. Still unknown →
        // a clean "not synced" (the centering loop offset-compensates, and its re-slew is refused in
        // turn) rather than recalibrate the pointing model in a guessed frame.
        var equatorialSystem = await ResolveEquatorialSystemAsync(client, token).ConfigureAwait(false);
        if (equatorialSystem is null) {
            LogMountOpRejectedUnknownSystem("telescope.sync");
            return false;
        }
        var target = TransformBestEffort(coordinates, MapSlewEpoch(equatorialSystem.Value));
        try {
            var opTask = Task.Run(() => {
                if (!client.CanSync) {
                    // Encoder/absolute mounts don't sync — a clean "not synced", the loop offset-compensates.
                    LogSyncUnsupported();
                    return false;
                }
                TryEnableTracking(client); // some mounts require tracking engaged to accept a sync
                client.SyncToCoordinates(target.RA, target.Dec);
                RefreshCacheOnce(); // reflect the recalibrated pointing into the §32.4 cache
                return true;
            }, CancellationToken.None);
            // Bound the blocking ASCOM round-trip against a wall clock so a hung driver can't pin the
            // sequencer thread — Sync takes no CancellationToken and the centering loop awaits it directly.
            // Same hard-timeout race as RunMountOpAsync, minus the settle-poll (sync is instantaneous, so a
            // returning SyncToCoordinates is done).
            using var timeout = new CancellationTokenSource(MountOpHardTimeout);
            var completed = await Task.WhenAny(opTask, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
            if (completed != opTask) {
                ObserveQuietly(opTask);
                throw new TimeoutException($"mount op telescope.sync did not complete within {MountOpHardTimeout.TotalSeconds:0}s");
            }
            return await opTask.ConfigureAwait(false);
        } catch (Exception ex) {
            LogMountOpFailed(ex, "telescope.sync");
            return false;
        }
    }

    // ── §58.4 — the flip slew (#1238) ─────────────────────────────────────────────────────────────
    // What NINA's AscomTelescope.MeridianFlip does: make sure the mount tracks, and when the profile
    // uses side of pier and the driver can set it, command the pier side the target should be on
    // after the flip (the mount's own flip, for drivers that need the hint), then an ordinary goto
    // to the same target — a GEM past the meridian lands on the other side on its own. The goto is
    // the shared slew core (epoch transform, parked/unknown-system guards, bounded settle), so the
    // MeridianFlipExecutor's watchdog, settle, recenter and VerifySideOfPier wrap a real flip. The
    // cache is refreshed before returning so the §58.5 verification reads the POST-flip pier side,
    // not the 2 s-old one.
    public async Task<bool> MeridianFlip(Coordinates targetCoordinates, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(targetCoordinates);
        var client = ConnectedClientOrNull();
        if (client is null) {
            return false;
        }
        if (_profileService?.ActiveProfile?.MeridianFlipSettings.UseSideOfPier == true) {
            await CommandDestinationPierSideAsync(client, targetCoordinates, token).ConfigureAwait(false);
        }
        var slewed = await SlewToCoordinatesAsync(targetCoordinates, token).ConfigureAwait(false);
        RefreshCacheOnce();
        return slewed;
    }

    // The pier-side hint: only when the driver advertises CanSetPierSide, the current side is
    // known, and it differs from the side the target is expected on at the current LST. A driver
    // that rejects the write (some only accept it from a specific state) just leaves the goto to
    // do the flip, as before. Best-effort by design — it never fails the flip on its own.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort pier-side hint ahead of the flip goto: any driver/HTTP failure here is logged and the goto still runs (the mount flips on its own past the meridian). CA1031's log-and-recover boundary applies.")]
    private async Task CommandDestinationPierSideAsync(AlpacaTelescope client, Coordinates target, CancellationToken token) {
        try {
            if (!client.CanSetPierSide) {
                return;
            }
            var info = GetInfo();
            if (info.SideOfPier == PierSide.pierUnknown || double.IsNaN(info.SiderealTime)) {
                return; // nothing to compare against — the goto alone flips
            }
            var expected = OpenAstroAra.Astrometry.MeridianFlip.ExpectedPierSide(target, Angle.ByHours(info.SiderealTime));
            if (expected == info.SideOfPier) {
                return;
            }
            LogFlipPierSideCommanded(expected);
            client.SideOfPier = expected == PierSide.pierWest ? PointingState.ThroughThePole : PointingState.Normal;
            // Some mounts start the flip slew on the write itself; let it settle before the goto.
            // A mount still moving at the bound gets the goto anyway (the executor's watchdog
            // owns that case), but say so in the log.
            if (!await WaitForMountConditionAsync(client, c => !ReadSlewing(c), token, SlewSettleMaxPolls).ConfigureAwait(false)) {
                LogFlipPierSideStillMoving();
            }
        } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            LogFlipPierSideFailed(ex);
        }
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Meridian flip: commanding pier side {PierSide} ahead of the flip goto (§58.4)")]
    private partial void LogFlipPierSideCommanded(PierSide pierSide);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Meridian flip: the pier-side hint was refused; the flip goto runs without it")]
    private partial void LogFlipPierSideFailed(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Meridian flip: the mount was still moving after the pier-side write when the settle bound expired; issuing the flip goto anyway")]
    private partial void LogFlipPierSideStillMoving();

    // The driver's own answer for which side a goto to these coordinates would land on, in the
    // mount's native epoch (best-effort: pierUnknown when the driver cannot say or the frame is
    // not known yet). The §28 centering path and the flip executor's callers may consult it.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Optional-property read boundary: DestinationSideOfPier is unimplemented on many drivers; the read falls back to pierUnknown rather than failing the caller. CA1031's log-and-recover boundary applies.")]
    public PierSide DestinationSideOfPier(Coordinates coordinates) {
        ArgumentNullException.ThrowIfNull(coordinates);
        AlpacaTelescope? client;
        EquatorialCoordinateType system;
        lock (_gate) {
            client = !_disposed && _state == EquipmentConnectionState.Connected && _equatorialSystemKnown ? _client : null;
            system = _equatorialSystemRaw;
        }
        if (client is null) {
            return PierSide.pierUnknown;
        }
        try {
            var target = TransformBestEffort(coordinates, MapSlewEpoch(system));
            return MapPointingState(client.DestinationSideOfPier(target.RA, target.Dec));
        } catch (Exception) {
            return PierSide.pierUnknown;
        }
    }

    // ── Unconsumed mediator surface — documented no-op stubs ─────────────────────────────────────
    // No registered headless instruction reaches these: MoveAxis/PulseGuide are interactive-GUI /
    // guider-calibration aids; the topocentric slews back SlewScopeToAltAz (not registered); custom
    // tracking rates and the snap port have no headless consumer. Each reports "didn't succeed"
    // like the stub.

    public void MoveAxis(TelescopeAxes axis, double rate) { }
    public void PulseGuide(GuideDirections direction, int duration) { }

    [Obsolete("Use SlewToTopocentricCoordinates instead.")]
    public Task<bool> SlewToCoordinatesAsync(TopocentricCoordinates coords, CancellationToken token) =>
        Task.FromResult(false);

    public Task<bool> SlewToTopocentricCoordinates(TopocentricCoordinates coords, CancellationToken token) =>
        Task.FromResult(false);

    public bool SetCustomTrackingRate(SiderealShiftTrackingRate rate) => false;
    public bool SendToSnapPort(bool start) => false;

    // Connection lifecycle is REST-driven; these mirror the headless stub. The instructions never
    // call them.
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());

    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(ITelescopeConsumer consumer) { }
    public void RemoveConsumer(ITelescopeConsumer consumer) { }
    public void Broadcast(TelescopeInfo deviceInfo) { }

    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }

    public IDevice GetDevice() =>
        throw new NotSupportedException(
            "TelescopeService does not expose a raw ASCOM IDevice; the Sequencer uses GetInfo() and the mount ops, and connection is driven through the REST surface.");

    public Task RaiseMeridianFlipping(BeforeMeridianFlipEventArgs e) => Task.CompletedTask;
    public Task RaiseMeridianFlipped(AfterMeridianFlipEventArgs e) => Task.CompletedTask;

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;
    public event Func<object, BeforeMeridianFlipEventArgs, Task>? MeridianFlipping;
    public event Func<object, AfterMeridianFlipEventArgs, Task>? MeridianFlipped;
    public event Func<object, EventArgs, Task>? Parked;
    public event Func<object, EventArgs, Task>? Homed;
    public event Func<object, EventArgs, Task>? Unparked;
    public event Func<object, MountSlewedEventArgs, Task>? Slewed;

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Mount mediator op {Op} failed")]
    private partial void LogMountOpFailed(Exception ex, string op);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Mount mediator op {Op} rejected: mount is parked")]
    private partial void LogMountOpRejectedParked(string op);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "Mount mediator op {Op} rejected: the mount's EquatorialSystem could not be read, so the target's epoch is unknown")]
    private partial void LogMountOpRejectedUnknownSystem(string op);

    private static string UnknownSystemMessage(string op) =>
        $"{op} refused: the mount has not reported its coordinate system (EquatorialSystem), so the target cannot be put in its epoch without guessing. Check the mount connection and retry.";

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Mount sync skipped: the mount reports CanSync=false; the centering loop will offset-compensate instead")]
    private partial void LogSyncUnsupported();

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Mount tracking write failed")]
    private partial void LogTrackingWriteFailed(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "pre-slew tracking enable ignored (slew attempt remains authoritative)")]
    private partial void LogTrackingWriteIgnored(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Mount StopSlew (AbortSlew) failed")]
    private partial void LogStopSlewFailed(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "abandoned mount op (cancelled/timed-out) later faulted")]
    private partial void LogAbandonedOpFaulted(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "mount terminal-condition read failed during settle-wait (will keep polling)")]
    private partial void LogConditionReadFailed();

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "epoch transform to {TargetEpoch} unavailable (SOFA/NOVAS native not packaged yet) — slewing with the untransformed target")]
    private partial void LogTransformFallback(Exception ex, Epoch targetEpoch);
}
