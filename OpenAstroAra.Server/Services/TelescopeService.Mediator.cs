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
/// Members no registered headless instruction consumes (MoveAxis, PulseGuide, Sync, the topocentric
/// slews, MeridianFlip, custom tracking rates, snap port, DestinationSideOfPier) stay no-op stubs —
/// each is documented at its declaration.
/// </summary>
public sealed partial class TelescopeService : ITelescopeMediator {

    private const int MountOpSettleMaxPolls = 1800; // ~180s at the poll interval (park/unpark/findhome)
    // Slews get their own, larger ceiling: a slow-slew mount (1–2°/s and below) doing a long goto
    // can legitimately take 5–10 minutes, and a settled-too-early ceiling reports a SUCCESSFUL slew
    // as false to the instruction. ~600s covers a worst-case meridian-to-horizon goto on slow gear
    // without meaningfully widening the hung-driver window (cancellation still cuts it short).
    private const int SlewSettleMaxPolls = 6000; // ~600s at the poll interval
    private static readonly TimeSpan MountOpPollInterval = TimeSpan.FromMilliseconds(100);
    // Wall-clock ceiling for a single blocking mount call (Park/Unpark/FindHome/the slew kickoff): a
    // silent device must not park a sequence thread until the OS TCP timeout. The terminal-condition
    // wait adds its own bound on top (~3min for park/unpark/findhome, ~10min for a slew), so the
    // worst-case total before RunMountOpAsync returns is ~8min (~15min for a slew); cancellation (ct)
    // cuts it short.
    private static readonly TimeSpan MountOpHardTimeout = TimeSpan.FromMinutes(5);
    // Sanity bound on the post-slew pointing, paired with the authoritative !Slewing signal. Generous
    // (5°, parity with the dome's azimuth window) on purpose: it only guards against a premature exit
    // where Slewing hasn't asserted yet and the mount is still at its old heading — a *completed*
    // slew (Slewing==false) settles well inside it.
    private const double SlewToleranceDeg = 5.0;

    /// <summary>
    /// Synchronous live snapshot for the Sequencer, served from the §32.4 cache (no blocking HTTP on
    /// the sequence thread). Never throws after Dispose — a running sequence may poll during shutdown,
    /// in which case it reports "not connected". Populates the fields the registered instructions'
    /// Validate/Execute read (Connected, AtPark, CanFindHome, TrackingModes) plus the cheap
    /// position/state fields already in the cache; the remaining ~30 TelescopeInfo members (sidereal
    /// time, meridian bookkeeping, guide rates, axis rates, …) stay at their defaults — no headless
    /// instruction consumes them and each would need additional per-poll device reads.
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
            };
            if (connected && runtime.RightAscensionHours is double ra && runtime.DeclinationDegrees is double dec) {
                info.Coordinates = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec), epoch);
            }
            if (connected && caps is not null) {
                PopulateTrackingModes(info.TrackingModes, caps);
            }
            return info;
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

    // ASCOM's mount-native coordinate system → NINA Epoch. Topocentric/Other (and the "not yet read"
    // sentinel) map to JNOW: the mount wants current-epoch coordinates. Extracted (internal) for
    // direct unit testing.
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

    // Cross-epoch transforms P/Invoke the SOFA/NOVAS natives, which are not yet packaged for
    // linux/macOS (PORT_TODO §14e: libnovas.so/libsofa.so). Until they ship, a missing/broken native
    // must degrade to the untransformed target (≤ ~arcminutes of precession drift J2000↔JNOW today,
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

    public Task<bool> SlewToCoordinatesAsync(Coordinates coords, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(coords);
        EquatorialCoordinateType equatorialSystem;
        bool parked;
        lock (_gate) {
            // Connected check BEFORE the transform: Transform P/Invokes NOVAS, which must not run
            // (and on a dev box without the native lib, cannot run) for a slew that is going to be
            // reported as failed anyway.
            if (_disposed || _state != EquipmentConnectionState.Connected || _client is null) {
                return Task.FromResult(false);
            }
            equatorialSystem = _equatorialSystemRaw;
            parked = _runtime.Parked;
        }
        if (parked) {
            // ASCOM mounts throw on slew-while-parked; pre-empt with a clean failure (the instruction
            // has already surfaced the parked warning to the user).
            LogMountOpRejectedParked("telescope.slew");
            return Task.FromResult(false);
        }
        // Transform to the mount's native coordinate system (a J2000 sequence target sent raw to a
        // JNOW mount would be off by the precession drift, ~arcminutes). Best-effort: the cross-epoch
        // path P/Invokes SOFA/NOVAS, whose non-Windows natives are a known packaging gap (PORT_TODO:
        // libnovas.so/libsofa.so is a §14e follow-up) — a missing native falls back to the
        // untransformed target rather than failing the run (same-epoch transforms are pure managed).
        var target = TransformBestEffort(coords, MapSlewEpoch(equatorialSystem));
        var targetRa = target.RA;
        var targetDec = target.Dec;
        return RunMountOpAsync("telescope.slew",
            c => {
                TryEnableTracking(c);
                // Async goto: returns immediately, Slewing goes true; the settle-wait below confirms
                // arrival. Same call the REST SlewInBackground path uses.
                c.SlewToCoordinatesAsync(targetRa, targetDec);
            },
            c => !ReadSlewing(c) && PointingNear(c, targetRa, targetDec),
            token,
            SlewSettleMaxPolls);
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
        return client is not null && TrySetTracking(client, trackingEnabled);
    }

    public bool SetTrackingMode(TrackingMode trackingMode) {
        if (trackingMode == TrackingMode.Custom) {
            // Custom needs the separate RA/Dec offset-rate surface (SiderealShiftTrackingRate) — not
            // wired headless; SetTracking.Validate already excludes it via TrackingModes.
            return false;
        }
        var client = ConnectedClientOrNull();
        return client is not null && TrySetTrackingMode(client, trackingMode);
    }

    public void StopSlew() {
        var client = ConnectedClientOrNull();
        if (client is not null) {
            TryAbortSlew(client);
        }
    }

    public async Task WaitForSlew(CancellationToken token) {
        var client = ConnectedClientOrNull();
        if (client is null) {
            return;
        }
        await WaitForMountConditionAsync(client, c => !ReadSlewing(c), token, SlewSettleMaxPolls).ConfigureAwait(false);
    }

    /// <summary>
    /// Current pointing from the §32.4 cache in the mount's native epoch; the headless-stub
    /// (0, 0, J2000) sentinel when not connected or the position hasn't been read yet.
    /// </summary>
    public Coordinates GetCurrentPosition() {
        lock (_gate) {
            var connected = !_disposed && _state == EquipmentConnectionState.Connected && _client is not null;
            if (connected && _runtime.RightAscensionHours is double ra && _runtime.DeclinationDegrees is double dec) {
                return new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec), MapEpoch(_equatorialSystemRaw));
            }
        }
        return new Coordinates(Angle.ByDegree(0), Angle.ByDegree(0), Epoch.J2000);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Sequencer mount-op boundary: the blocking ASCOM call can throw arbitrary driver/HTTP exceptions and a concurrent Disconnect/Dispose can dispose the captured client mid-op; genuine sequencer cancellation is rethrown but any other escape (including a device/HTTP-timeout OCE) is logged and reported as a failed op (false) rather than faulting the autonomous run. CA1031's log-and-recover boundary applies.")]
    private async Task<bool> RunMountOpAsync(string op, Action<AlpacaTelescope> action, Func<AlpacaTelescope, bool> isDone, CancellationToken ct, int settleMaxPolls = MountOpSettleMaxPolls) {
        var client = ConnectedClientOrNull();
        if (client is null) {
            return false; // not connected: the instruction's Validate has already blocked this
        }
        try {
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
            return await WaitForMountConditionAsync(client, isDone, ct, settleMaxPolls).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw; // genuine sequencer cancellation — propagate so the run aborts
        } catch (Exception ex) {
            LogMountOpFailed(ex, op);
            return false;
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

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "§57 panic-stop boundary: AbortSlew can throw driver/HTTP exceptions; StopSlew is fire-and-forget for the sequencer, so the failure is logged rather than thrown into the sequence. CA1031's log-and-recover boundary applies.")]
    private void TryAbortSlew(AlpacaTelescope client) {
        try {
            client.AbortSlew();
            RefreshCacheOnce();
        } catch (Exception ex) {
            LogStopSlewFailed(ex);
        }
    }

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
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Mount sync boundary: the blocking ASCOM SyncToCoordinates/CanSync can throw arbitrary driver/HTTP exceptions and a concurrent Disconnect/Dispose can dispose the captured client mid-call; every escape is logged and reported as a failed sync (false) so the centering loop falls back to offset compensation rather than faulting the run. CA1031's log-and-recover boundary applies.")]
    public async Task<bool> Sync(Coordinates coordinates) {
        ArgumentNullException.ThrowIfNull(coordinates);
        EquatorialCoordinateType equatorialSystem;
        bool parked;
        lock (_gate) {
            // Connected check before the transform (which P/Invokes NOVAS): don't run native astrometry
            // for a sync that's going to be reported failed anyway.
            if (_disposed || _state != EquipmentConnectionState.Connected || _client is null) {
                return false;
            }
            equatorialSystem = _equatorialSystemRaw;
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
        var target = TransformBestEffort(coordinates, MapSlewEpoch(equatorialSystem));
        try {
            return await Task.Run(() => {
                if (!client.CanSync) {
                    // Encoder/absolute mounts don't sync — a clean "not synced", the loop offset-compensates.
                    LogSyncUnsupported();
                    return false;
                }
                TryEnableTracking(client); // some mounts require tracking engaged to accept a sync
                client.SyncToCoordinates(target.RA, target.Dec);
                RefreshCacheOnce(); // reflect the recalibrated pointing into the §32.4 cache
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogMountOpFailed(ex, "telescope.sync");
            return false;
        }
    }

    // ── Unconsumed mediator surface — documented no-op stubs ─────────────────────────────────────
    // No registered headless instruction reaches these: MoveAxis/PulseGuide are interactive-GUI /
    // guider-calibration aids; DestinationSideOfPier belongs to the plate-solve/Center path (not ported
    // yet); the topocentric slews back SlewScopeToAltAz (not registered); MeridianFlip is the imaging-loop
    // trigger orchestration (lands with the capture path); custom tracking rates and the snap port have no
    // headless consumer. Each reports "didn't succeed" like the stub.

    public void MoveAxis(TelescopeAxes axis, double rate) { }
    public void PulseGuide(GuideDirections direction, int duration) { }

    [Obsolete("Use SlewToTopocentricCoordinates instead.")]
    public Task<bool> SlewToCoordinatesAsync(TopocentricCoordinates coords, CancellationToken token) =>
        Task.FromResult(false);

    public Task<bool> SlewToTopocentricCoordinates(TopocentricCoordinates coords, CancellationToken token) =>
        Task.FromResult(false);

    public Task<bool> MeridianFlip(Coordinates targetCoordinates, CancellationToken token) =>
        Task.FromResult(false);

    public bool SetCustomTrackingRate(SiderealShiftTrackingRate rate) => false;
    public bool SendToSnapPort(bool start) => false;
    public PierSide DestinationSideOfPier(Coordinates coordinates) => PierSide.pierUnknown;

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
