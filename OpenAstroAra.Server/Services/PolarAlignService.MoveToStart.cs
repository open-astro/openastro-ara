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
using OpenAstroAra.Server.Contracts.Refusals;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using PierSide = OpenAstroAra.Core.Enums.PierSide;
using TelescopeAxes = OpenAstroAra.Core.Enums.TelescopeAxes;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// #2166 — "Move to start position": when the start pointing fails because the scope points within 3° of the pole,
    /// the user may ask Polar Align (one tap, then the app's motion confirm) to move it out. It is the opt-in #2089 asks
    /// for: nothing moves before the call. The move turns the Dec axis alone with MoveAxis(Secondary), never a GoTo, so the
    /// RA axis and the counterweights stay where they are, to <see cref="MoveToStartOffPoleDeg"/> from the site's pole.
    /// It is a Polar Align move under the mount's motion owner with the guards of every turn (#2067): a fresh reading
    /// every <see cref="AxisMovePoll"/>, the stall bound, twice the planned time plus slack, Stop, Stop Mount and Emergency
    /// Stop obeyed, MoveAxis 0 on every exit, a pier side that changes stops and fails it, and the RA reading may not move
    /// more than <see cref="DecMoveRaSkyLimitDeg"/> on the sky (<see cref="RaMoved"/>, the mirror of <c>dec_moved</c>).
    /// A start within <see cref="LeavePoleDeg"/> of the pole has no side (#2189: AlpacaBridge iOptron's Dec rate is
    /// sky-relative, so at the pole either sign moves away onto the side the mount picks): its first leg only leaves the
    /// pole, and the side the mount then reports is the start's side. ASCOM leaves the Dec motor's sign to the driver, so
    /// a direction saved for this mount, driver and side is used, or a <see cref="MoveToStartProbeDeg"/> probe goes first;
    /// a probe that took Dec toward the pole is stopped and settled, and a new move then goes the other way (never a
    /// reversal mid-move). From the start's side the move never crosses the pole and must end on that side. Then the
    /// exit's confirmed stop, the tracking the mount had handed back, and a fresh pre-flight list.
    /// </summary>
    public sealed partial class PolarAlignService {

        /// <summary>How far from the site's pole the move leaves the scope (inside the 2–5° the start asks for).</summary>
        internal const double MoveToStartOffPoleDeg = 4.0;

        /// <summary>The first, small Dec move that learns which way the Dec motor turns.</summary>
        internal const double MoveToStartProbeDeg = 0.3;

        /// <summary>The most the move takes the pointing from where it starts: the target plus the larger of the probe and
        /// <see cref="LeavePoleDeg"/> (a start just under it may leave the pole onto the other side, #2189: 0.5° + 4°). The
        /// mount's clearance must be confirmed for at least this.</summary>
        internal const double MoveToStartReachDeg = MoveToStartOffPoleDeg + LeavePoleDeg;

        /// <summary>The band, from the pole, the start asks for: the move must end inside it.</summary>
        internal const double StartBandNearDeg = 2.0;
        internal const double StartBandFarDeg = 5.0;

        /// <summary>Within this of the pole a reading whose RA and pier side flip is the Dec axis going over the pole.</summary>
        internal const double PoleCrossingDeg = 1.0;

        /// <summary>#2166 rig check — a probe that starts this close to the pole (the probe plus a margin) may go over it,
        /// so its way is read only once the pier side and RA reading stand still (<see cref="WaitForReadingAtThePoleAsync"/>).</summary>
        internal const double PoleCrossingRiskDeg = MoveToStartProbeDeg + 0.5;

        /// <summary>How long the pier side and RA reading must stand still after such a probe: longer than the ~6 s the
        /// HAE29C's showed a pole crossing late.</summary>
        internal TimeSpan PoleReadingQuietFor { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>The most the move waits for them to stand still before it fails.</summary>
        internal TimeSpan PoleReadingWaitMax { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>#2189 rig check — a start closer to the pole than this has no side (a sky-relative Dec rate moves away from
        /// the pole whatever its sign), so its first leg only leaves the pole, to at least this far from it (aiming
        /// <see cref="LeavePoleMarginDeg"/> farther). From this far a 0.3° probe toward the pole, even one that runs to 0.4°,
        /// ends short of it, so it cannot cross; the mount's pier side is read off the pole.</summary>
        internal const double LeavePoleDeg = 0.5;

        /// <summary>How far past <see cref="LeavePoleDeg"/> the leg aims, so a short clock stop still ends at least
        /// <see cref="LeavePoleDeg"/> off the pole: the rig's clock-stopped 0.3° probe moved 0.37–0.40°, so a probe toward the
        /// pole from 0.5° off ends at least 0.1° short of it (#2189 review).</summary>
        internal const double LeavePoleMarginDeg = 0.2;

        /// <summary>An RA reading change under this is still (the flip of a crossing is 180°).</summary>
        internal const double PoleReadingQuietRaDeg = 1.0;

        /// <summary>The most the RA reading may move east or west on the sky during a Dec move (degrees, ΔRA × cos Dec).</summary>
        internal const double DecMoveRaSkyLimitDeg = 0.1;

        /// <summary>The reason of a Dec move during which the RA reading moved (the mirror of <see cref="DecMoved"/>).</summary>
        internal const string RaMoved = "ra_moved";

        private const string MoveToStartWhy = "move_to_start";

        private const string DecSavedWrongWayMessage =
            "the mount's Dec went toward the pole, the other way from its saved Dec direction, so Polar Align stopped it and never turned it back; the saved direction is forgotten, so the next move probes again — check the mount, then try again";

        private const string DecWrongWayMessage =
            "the mount's Dec went toward the pole, the other way from the probe's lesson, so Polar Align stopped it and never turned it back — check the mount, then point it 2–5° from the pole with its own controls";

        // A move to the start position is under way (it holds _opLock and the motion owner). Guarded by _gate.
        private bool _movingToStart;

        // Calls of MoveToStartAsync under way, from before they wait on _opLock to their end: a Start during the move's
        // pre-checks is refused too, not queued behind the whole move. Guarded by _gate.
        private int _moveToStartCalls;

        /// <inheritdoc />
        public async Task<PolarAlignMoveToStartResultDto> MoveToStartAsync(PolarAlignMoveToStartConfirmation? confirmation, CancellationToken ct) {
            // #2066 — the stop count at the call, as a start reads it: a Stop Mount from here on ends the move before it moves.
            var abortEpochAtCall = _motionControl.AbortEpoch;
            var ownAbortsAtCall = Interlocked.Read(ref _ownAborts);
            lock (_gate) {
                _moveToStartCalls++;
            }
            try {
                return await MoveToStartLockedAsync(confirmation, abortEpochAtCall, ownAbortsAtCall, ct).ConfigureAwait(false);
            } finally {
                lock (_gate) {
                    _moveToStartCalls--;
                }
            }
        }

        private async Task<PolarAlignMoveToStartResultDto> MoveToStartLockedAsync(
                PolarAlignMoveToStartConfirmation? confirmation, long abortEpochAtCall, long ownAbortsAtCall, CancellationToken ct) {
            await _opLock.WaitAsync(ct).ConfigureAwait(false);
            IDisposable? hold = null;
            try {
                lock (_gate) {
                    if (_active || _exitsInFlight > 0 || _movingToStart || _runTask is { IsCompleted: false }) {
                        throw new RefusalException(RefusalCatalog.PolarAlignRunActive,
                            "a Polar Align run is going (or still stopping the mount) — stop it first, then move to the start position");
                    }
                }
                RefusalException? holdRefusal = null;
                try {
                    hold = AcquireMountForRun();
                } catch (RefusalException ex) {
                    holdRefusal = ex;
                }
                // The pre-flight list's offer is the guard: the start pointing failed on the pole, nothing blocks the move.
                var preflight = await EvaluatePreflightAsync(PolarAlignStages.Fine, ct).ConfigureAwait(false);
                if (preflight.Result.MoveToStartRefusal is { } refused) {
                    ExceptionDispatchInfo.Capture(refused).Throw();
                }
                if (holdRefusal is not null) {
                    throw holdRefusal;
                }
                ThrowIfMoveNotConfirmed(confirmation, preflight.Result.Dto.MoveToStart);
                var poleSign = _profileStore.GetSiteSettings().LatitudeDeg < 0 ? -1.0 : 1.0;
                // A fresh reading: within 3° of the site's own pole (the plan refuses either pole), steady, not slewing.
                var (_, decDeg, session, _, slewing) = await StartPointingJnowAsync(ct).ConfigureAwait(false);
                ThrowIfBusyAtStart(slewing);
                if (poleSign * decDeg <= OnAxisDecDeg) {
                    throw new RefusalException(RefusalCatalog.PolarAlignMoveToStartNotNeeded,
                        "the scope does not point within 3° of the site's pole, so it needs no move to the start position — start Polar Align");
                }
                return await RunMoveToStartAsync(hold!, poleSign, session, abortEpochAtCall, ownAbortsAtCall, ct).ConfigureAwait(false);
            } catch {
                hold?.Dispose();
                throw;
            } finally {
                _opLock.Release();
            }
        }

        /// <summary>#2166 — a Start, a Check or a start plan while the move to the start position runs: refused at once
        /// (<c>polar-align-run-active</c>), never left waiting for the move to end.</summary>
        private void ThrowIfMovingToStart() {
            lock (_gate) {
                if (!_movingToStart && _moveToStartCalls == 0) {
                    return;
                }
            }
            throw new RefusalException(RefusalCatalog.PolarAlignRunActive,
                "Polar Align is moving the scope to the start position — wait for the move to end (or stop it), then start Polar Align");
        }

        /// <summary>ADR 0024 rule 1 — nothing moves without the user's confirmation of this move: the offer's target and
        /// reach the app's confirm showed, equal to the offer now (a changed offer is asked again).</summary>
        private void ThrowIfMoveNotConfirmed(PolarAlignMoveToStartConfirmation? confirmation, PolarAlignMoveToStartOfferDto? offer) {
            if (confirmation is not null && offer is not null && Math.Abs(confirmation.TargetOffPoleDeg - offer.TargetOffPoleDeg) < 1e-6
                    && Math.Abs(confirmation.ReachDeg - offer.ReachDeg) < 1e-6) {
                return;
            }
            LogMoveToStartNotConfirmed(confirmation is null ? "none" : "stale");
            throw new RefusalException(RefusalCatalog.PolarAlignMoveNotConfirmed,
                confirmation is null
                    ? "confirm the move first: Polar Align turns the Dec axis alone, up to " + MoveToStartReachDeg.ToString("0.#", CultureInfo.InvariantCulture) + "°, to the start position"
                    : "the move to the start position changed since you confirmed it — confirm it again");
        }

        /// <summary>The move itself, under <c>_opLock</c>, from the guards' fresh reading: a run of its own generation (so
        /// Stop, Emergency Stop and the run-wide watch reach it), the move, the exit, then the fresh list.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "The move's boundary: any failure must still stop the mount and come back as the move's outcome, never escape with the motor marked.")]
        private async Task<PolarAlignMoveToStartResultDto> RunMoveToStartAsync(
                IDisposable hold, double poleSign, long session, long abortEpochAtCall, long ownAbortsAtCall, CancellationToken ct) {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); // a client that goes away stops the move
            // Read before the move is marked: a read that throws leaves nothing marked running (the caller frees the hold).
            var priorTracking = _mount.GetInfo().TrackingEnabled;
            var end = new RunEnd();
            end.Hold(hold);
            int gen;
            lock (_gate) {
                gen = ++_generation;
                _liveRun = (cts, end);
                _movingToStart = true;
                end.Fence = StopFence;
                _runFence = (gen, end.Fence);
                end.MountSession = session;
                // As a start's baseline: only Polar Align's own stops since the call are skipped.
                var epochNow = _motionControl.AbortEpoch;
                var ownNow = Interlocked.Read(ref _ownAborts);
                var (epoch, own) = epochNow - abortEpochAtCall <= ownNow - ownAbortsAtCall ? (epochNow, ownNow) : (abortEpochAtCall, ownAbortsAtCall);
                _abortWatch = (gen, epoch, own, end);
                _runSession = (gen, session);
                _trackingTouched = false;
                EndMotion();
            }
            end.PriorTracking = priorTracking;
            LogMoveToStartBegins(MoveToStartOffPoleDeg, poleSign > 0 ? "north" : "south");
            string outcome;
            string? reason = null;
            string? message = null;
            var takenOver = false;
            try {
                await MoveDecToStartAsync(poleSign, gen, cts.Token).ConfigureAwait(false);
                outcome = PolarAlignMoveToStartOutcomes.Arrived;
            } catch (OperationCanceledException) {
                outcome = PolarAlignMoveToStartOutcomes.Stopped;
                message = "the move to the start position was stopped — the mount stands where it stopped";
            } catch (RoutineFailedException ex) when (ex.Reason == SlewAbortedExternally) {
                outcome = PolarAlignMoveToStartOutcomes.Stopped;
                (reason, message, takenOver) = (ex.Reason, ex.Message, ex.MountTakenOver);
            } catch (RoutineFailedException ex) {
                outcome = PolarAlignMoveToStartOutcomes.Failed;
                (reason, message, takenOver) = (ex.Reason, ex.Message, ex.MountTakenOver);
            } catch (Exception ex) {
                LogRoutineFailed("move_to_start", ex);
                outcome = PolarAlignMoveToStartOutcomes.Failed;
                (reason, message) = ("internal_error", ex.Message);
            }
            bool? stopped;
            try {
                stopped = await ExitMoveToStartAsync(end, outcome == PolarAlignMoveToStartOutcomes.Arrived, takenOver, priorTracking).ConfigureAwait(false);
            } finally {
                end.ReleaseHold();
                lock (_gate) {
                    _movingToStart = false;
                    if (_liveRun is { } live && ReferenceEquals(live.End, end)) {
                        _liveRun = null;
                    }
                }
            }
            if (stopped == false) {
                outcome = PolarAlignMoveToStartOutcomes.Failed;
                (reason, message) = (MountDidNotStop, CutPowerSentence);
            }
            LogMoveToStartEnded(outcome, reason ?? "none");
            // The pointing read again: the fresh list the app shows (its start pointing passes once the move arrived).
            var fresh = await EvaluatePreflightAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            return new PolarAlignMoveToStartResultDto(outcome, reason, message, fresh.Result.Dto);
        }

        /// <summary>The move's exit, as a run's: the confirmed stop (AbortSlew when a move may run, MoveAxis 0 on both axes,
        /// fresh reads), unless Emergency Stop, a park or Find Home took the mount over or the move's stops were fenced;
        /// then tracking: an arrived move hands back the tracking the mount had, any other end turns it off. Null when the
        /// mount was left to a takeover.</summary>
        private async Task<bool?> ExitMoveToStartAsync(RunEnd end, bool arrived, bool takenOver, bool priorTracking) {
            if (takenOver || end.Requested == TakenOver || StopsFenced(end.Fence)) {
                LogExitLeftToTakeover(MoveToStartWhy);
                return null;
            }
            var stopped = await StopMountMotionAsync(MoveToStartWhy, end.Fence).ConfigureAwait(false);
            if (stopped is null) {
                LogExitLeftToTakeover(MoveToStartWhy);
                return null;
            }
            NotePoseUnverifiedAfterRun(end, stopped);
            end.MountStopped = stopped;
            await NoteMountLeftAfterStopAsync().ConfigureAwait(false);
            if (StopsFenced(end.Fence)) {
                return stopped;
            }
            if (arrived && stopped == true) {
                if (_trackingTouched) {
                    RestoreTrackingBestEffort(priorTracking);
                }
            } else {
                TrackingOffBestEffort(MoveToStartWhy);
            }
            return stopped;
        }

        /// <summary>Next to the pole, the leg that leaves it (<see cref="LeaveThePoleAsync"/>); then the saved direction's
        /// move or the probe and the move off the pole; then the check that the scope points 2–5° from it, on the start's
        /// side. Tracking is on first, so the RA reading stands still while the Dec axis turns (the RA guard reads it).
        /// Distances are signed from the pole on the start's side (<see cref="DecMoveTrack"/>); from at least
        /// <see cref="LeavePoleDeg"/> off a probe cannot cross the pole, and one that reads as having crossed fails.</summary>
        private async Task MoveDecToStartAsync(double poleSign, int gen, CancellationToken ct) {
            ThrowIfStoppedFromOutside(gen);
            ThrowIfMoveToStartTooSlow();
            await EnsureTrackingOnAsync(gen, ct).ConfigureAwait(false);
            var track = new DecMoveTrack(poleSign);
            // #2166 rig check — which way the Dec motor goes, saved for this mount and driver by an earlier move: the move
            // goes straight off the pole, with no probe. A probe from Home went over the pole on the HAE29C, and its pier
            // side and RA reading showed it only ~6 s later, so the probe read as "away" and the move went on over the pole.
            var driver = ReadMountDriver();
            var startReading = await RunReadingAsync(gen, MountReadKind.Full, ct, foldPastPole: true).ConfigureAwait(false);
            if (90.0 - (poleSign * startReading.DecDeg) < LeavePoleDeg) {
                // #2189 rig check — at the pole the start has no side: a sky-relative Dec rate (AlpacaBridge iOptron: positive
                // is north on either side) moves away from the pole whatever its sign, onto the side the mount picks. The
                // first leg only leaves the pole; the side the mount then reports is the start's side, and the move goes on
                // as a start from there.
                startReading = await LeaveThePoleAsync(poleSign, startReading, gen, ct).ConfigureAwait(false);
            }
            var startSide = startReading.Side;
            if (SavedDecTowardPoleSign(driver, startSide) is double savedToward) {
                var savedAway = -savedToward;
                var startOffPole = 90.0 - (poleSign * startReading.DecDeg);
                LogMoveToStartSavedDirection(savedAway, driver.Identity);
                try {
                    if (startOffPole < PoleCrossingRiskDeg) {
                        // From next to the pole the Dec reading falls whichever way the axis goes, and a pass over the pole
                        // shows late: a short first leg, then the wait, proves the saved direction before the rest. A
                        // wrong one ends this leg 0.3° over the pole, never 4° (#2189 review).
                        await MoveDecAxisUntilAsync(savedAway, MoveToStartProbeDeg, track,
                            (start, now) => now - start, checkDirection: true, gen, ct).ConfigureAwait(false);
                        await WaitForReadingAtThePoleAsync(track, gen, ct).ConfigureAwait(false);
                        // The leg must have gone away from the pole on the start's side: over it, or toward it (a 0.3° leg
                        // toward the pole stays under the 0.5° wrong-way stop), the saved direction is wrong (#2189 review).
                        var leg = track.SignedOffPoleDeg - startOffPole;
                        if (track.OverThePole || leg < MoveToStartProbeDeg / 2) {
                            LogDecSavedDirectionWentOver(leg);
                            throw new RoutineFailedException(TurnWrongWay, DecSavedWrongWayMessage);
                        }
                    }
                    var toGo = MoveToStartOffPoleDeg - (track.Started ? track.SignedOffPoleDeg : startOffPole);
                    if (toGo > StopStillDeg) {
                        await MoveDecAxisUntilAsync(savedAway, toGo, track,
                            (start, now) => now - start, checkDirection: true, gen, ct).ConfigureAwait(false);
                    }
                } catch (RoutineFailedException ex) when (ex.Reason == TurnWrongWay) {
                    // The saved direction does not hold (another driver build, a re-wired motor): forget it, so the next
                    // move probes again. Never reversed here.
                    _profileStore.PutPolarAlignDecMotorDirection(null);
                    throw new RoutineFailedException(TurnWrongWay, DecSavedWrongWayMessage);
                }
                EndMoveToStartOnItsSide(track, await RunReadingAsync(gen, MountReadKind.Full, ct, foldPastPole: true).ConfigureAwait(false));
                return;
            }
            var (from, probed) = await MoveDecAxisUntilAsync(1.0, MoveToStartProbeDeg, track,
                (start, now) => Math.Abs(now - start), checkDirection: false, gen, ct).ConfigureAwait(false);
            if (from < PoleCrossingRiskDeg) {
                // A probe this close to the pole may have gone over it, and the mount may show that late: wait until its
                // pier side and RA reading stand still long enough before reading which way the probe went.
                await WaitForReadingAtThePoleAsync(track, gen, ct).ConfigureAwait(false);
                probed = track.SignedOffPoleDeg;
            }
            var towardPole = from - probed;
            if (Math.Abs(towardPole) < MoveToStartProbeDeg / 2) {
                throw new RoutineFailedException(TurnMismatch, string.Create(CultureInfo.InvariantCulture,
                    $"the mount's Dec reading moved only {Math.Abs(towardPole):F2}° in the {MoveToStartProbeDeg:F1}° probe, so its Dec motor's direction is not known — check the mount, then try again"));
            }
            // A probe toward (or over) the pole has stopped and settled: the next move is a new one, the other way.
            var awaySign = towardPole > 0 ? -1.0 : 1.0;
            LogMoveToStartProbed(towardPole, awaySign);
            if (track.OverThePole) {
                // From at least LeavePoleDeg off the pole a 0.3° probe cannot cross it: a crossing here is a reading or a mount
                // that does not do what it is told. Polar Align never flips the mount.
                throw new RoutineFailedException(PierChanged,
                    "the Dec probe went over the pole — Polar Align never flips the mount; check where it points, then point it 2–5° from the pole with its own controls");
            }
            var remaining = MoveToStartOffPoleDeg - probed;
            if (remaining > StopStillDeg) {
                await MoveDecAxisUntilAsync(awaySign, remaining, track,
                    (start, now) => now - start, checkDirection: true, gen, ct).ConfigureAwait(false);
            }
            EndMoveToStartOnItsSide(track, await RunReadingAsync(gen, MountReadKind.Full, ct, foldPastPole: true).ConfigureAwait(false));
            // Arrived on its side the way the probe said: the probe's lesson holds, so it is saved for this mount and driver.
            SaveDecTowardPoleSign(driver, track.OriginSide, towardPole > 0 ? 1.0 : -1.0);
        }

        /// <summary>
        /// #2189 rig check — a start within <see cref="LeavePoleDeg"/> of the pole has no side: on the HAE29C a positive Dec
        /// rate from Home stayed on the west side once and went over to the east the next time, as the mount picked. One leg
        /// at a positive rate takes the scope off the pole: <paramref name="start"/>'s distance plus <see cref="LeavePoleDeg"/>
        /// and <see cref="LeavePoleMarginDeg"/> of the Dec reading's path (|Δ| summed, so a pass over the pole counts right), stopped by
        /// the clock, never judged wrong-way, over the pole or a pier-side change, since every side is right here; the RA
        /// reading's own moves still count against <see cref="DecMoveRaSkyLimitDeg"/>. Then the wait for a still pier side and
        /// RA reading (<see cref="WaitForReadingAtThePoleAsync"/>). Returns the reading the move goes on from: at least
        /// <see cref="LeavePoleDeg"/> from the pole (a 0.3° probe from there cannot cross it) on the side the mount reports.
        /// </summary>
        private async Task<FreshPointing> LeaveThePoleAsync(double poleSign, FreshPointing start, int gen, CancellationToken ct) {
            var startOffPole = 90.0 - (poleSign * start.DecDeg);
            var path = startOffPole + LeavePoleDeg + LeavePoleMarginDeg;
            LogMoveToStartLeavesThePole(startOffPole, path);
            var leaving = new DecMoveTrack(poleSign) { Leaving = true };
            var lastOffPole = startOffPole;
            var travelled = 0.0;
            await MoveDecAxisUntilAsync(1.0, path, leaving,
                (_, _) => {
                    travelled += Math.Abs(leaving.LastOffPoleDeg - lastOffPole);
                    lastOffPole = leaving.LastOffPoleDeg;
                    return travelled;
                }, checkDirection: false, gen, ct).ConfigureAwait(false);
            await WaitForReadingAtThePoleAsync(leaving, gen, ct).ConfigureAwait(false);
            var off = await RunReadingAsync(gen, MountReadKind.Full, ct, foldPastPole: true).ConfigureAwait(false);
            var offPole = 90.0 - (poleSign * off.DecDeg);
            if (offPole < LeavePoleDeg) {
                throw new RoutineFailedException(TurnMismatch, string.Create(CultureInfo.InvariantCulture,
                    $"the first Dec move left the scope {offPole:F2}° from the pole, not {LeavePoleDeg:F1}° — check the mount, then point it 2–5° from the pole with its own controls"));
            }
            LogMoveToStartLeftThePole(offPole, off.Side);
            return off;
        }

        /// <summary>The final reading: the move must end on the side of the pier it started on, 2–5° from the pole.</summary>
        private void EndMoveToStartOnItsSide(DecMoveTrack track, FreshPointing final) {
            ObserveDecMove(track, final);
            if (track.OverThePole) {
                var (origin, ended) = (track.OriginSide, track.ExpectedSide(overThePole: true));
                LogDecMovePierChanged(origin, ended);
                throw new RoutineFailedException(PierChanged,
                    "the move to the start position ended over the pole, on the other side of the pier — Polar Align never flips the mount; check where it points, then point it 2–5° from the pole with its own controls");
            }
            var offPole = track.SignedOffPoleDeg;
            if (offPole < StartBandNearDeg || offPole > StartBandFarDeg) {
                throw new RoutineFailedException(TurnMismatch, string.Create(CultureInfo.InvariantCulture,
                    $"the move ended with the scope {offPole:F1}° from the pole, not 2–5° — check the mount, then point it with its own controls"));
            }
            LogMoveToStartArrived(offPole);
        }

        /// <summary>#2166 rig check — the saved Dec direction for <paramref name="driver"/> on <paramref name="side"/>: the
        /// sign of a positive rate toward the pole there. Null with none saved for this mount, driver and side, or a side the
        /// mount does not report.</summary>
        private double? SavedDecTowardPoleSign(MountDriverKey driver, PierSide side) {
            if (SideName(side) is not { } name
                    || _profileStore.GetPolarAlignDecMotorDirection() is not { } saved
                    || !saved.IsFor(driver.Mount, driver.Info, driver.Version, name)) {
                return null;
            }
            return saved.TowardPoleSign;
        }

        /// <summary>Save the probe's lesson (a positive rate went toward the pole on <paramref name="side"/> when
        /// <paramref name="towardOnSide"/> is +1) for <paramref name="driver"/> and that side. Nothing is saved with no
        /// mount key or a side the mount did not report.</summary>
        private void SaveDecTowardPoleSign(MountDriverKey driver, PierSide side, double towardOnSide) {
            if (driver.Mount.Length == 0 || SideName(side) is not { } name) {
                return;
            }
            var sign = towardOnSide > 0 ? 1 : -1;
            _profileStore.PutPolarAlignDecMotorDirection(
                new PolarAlignDecMotorDirectionDto(sign, name, driver.Mount, driver.Info, driver.Version, DateTimeOffset.UtcNow));
            LogDecDirectionSavedText(sign > 0 ? "toward" : "away from", name, driver.Identity);
        }

        private static string? SideName(PierSide side) => side switch {
            PierSide.pierWest => "west",
            PierSide.pierEast => "east",
            _ => null,
        };

        /// <summary>
        /// #2166 rig check — after a probe that started within <see cref="PoleCrossingRiskDeg"/> of the pole: fresh full
        /// readings every <see cref="AxisMovePoll"/> (each through <see cref="ObserveDecMove"/>, so a late flip of the RA
        /// reading and the pier side is read as the crossing it is) until the pier side and the RA reading have stood still
        /// for <see cref="PoleReadingQuietFor"/>. On the HAE29C (AlpacaBridge 6.0.0-beta.4) they showed a crossing ~6 s after
        /// the Dec reading did. Not still within <see cref="PoleReadingWaitMax"/>: the probe's way is not known, and the move
        /// fails before anything else moves.
        /// </summary>
        private async Task WaitForReadingAtThePoleAsync(DecMoveTrack track, int gen, CancellationToken ct) {
            var began = Clock.GetTimestamp();
            var quietSince = began;
            (double Ra, PierSide Side)? last = null;
            LogMoveToStartWaitsAtThePole(PoleReadingQuietFor.TotalSeconds);
            while (true) {
                await Task.Delay(AxisMovePoll, ct).ConfigureAwait(false);
                if (!IsCurrent(gen)) {
                    throw new OperationCanceledException("the move was stopped while it waited at the pole");
                }
                ThrowIfStoppedFromOutside(gen);
                var r = await RunReadingAsync(gen, MountReadKind.Full, ct, foldPastPole: true).ConfigureAwait(false);
                var at = Clock.GetTimestamp();
                ObserveDecMove(track, r);
                if (last is not { } l || l.Side != r.Side || Math.Abs(Wrap180(r.RawRaDeg - l.Ra)) > PoleReadingQuietRaDeg) {
                    last = (r.RawRaDeg, r.Side);
                    quietSince = at;
                } else if (Clock.GetElapsedTime(quietSince, at) >= PoleReadingQuietFor) {
                    return;
                }
                if (Clock.GetElapsedTime(began, at) > PoleReadingWaitMax) {
                    throw new RoutineFailedException(TurnMismatch, string.Create(CultureInfo.InvariantCulture,
                        $"the mount's pier side and RA reading did not stand still within {PoleReadingWaitMax.TotalSeconds:F0} s of the Dec probe next to the pole, so which way it went is not known — check the mount, then try again"));
                }
            }
        }

        /// <summary>The longest the probe and the move may take together by their own time bounds (twice the planned time
        /// at the Dec rates the mount offers, plus slack). The app waits a fixed time for the move's answer
        /// (<c>moveToStartTimeout</c>, 10 min) and going away stops the move, so a mount too slow to finish inside this
        /// fails before anything moves, never part way.</summary>
        internal TimeSpan MoveToStartMotorBudget { get; set; } = TimeSpan.FromMinutes(5);

        private void ThrowIfMoveToStartTooSlow() {
            var (fastRate, slowRate) = MatchTurnRates(TelescopeAxes.Secondary);
            var worst = TurnTimeBound(MoveToStartProbeDeg, fastRate, slowRate) + TurnTimeBound(MoveToStartOffPoleDeg, fastRate, slowRate);
            if (worst > MoveToStartMotorBudget) {
                throw new RoutineFailedException("slew_failed", string.Create(CultureInfo.InvariantCulture,
                    $"the mount turns its Dec axis at most {fastRate:0.###}°/s, so the move could take {worst.TotalSeconds:F0} s, more than the {MoveToStartMotorBudget.TotalSeconds:F0} s Polar Align allows — nothing moved; point it 2–5° from the pole with its own controls"));
            }
        }

        /// <summary>
        /// The Dec twin of <see cref="MoveAxisUntilAsync"/>: run the Dec motor until <paramref name="progress"/> (from the
        /// move's start to now, on <paramref name="track"/>'s signed distance from the pole) reaches
        /// <paramref name="degrees"/> by the mount's own reading, polled every <see cref="AxisMovePoll"/>, fast until the
        /// last <see cref="AxisSlowZoneDeg"/> and slow for the rest, at the rates the mount offers at or below Polar Align's.
        /// The same guards: steady first, the stall, twice the planned time plus slack, a stop from outside, a reading that
        /// jumps, and <see cref="ObserveDecMove"/> on every reading (the pier side, and the RA reading that moves). With
        /// <paramref name="checkDirection"/>, a move that goes the wrong way is stopped and fails, never reversed. Every exit
        /// stops the motor; then the settle on fresh readings. Returns the signed distance at the start and at the end.
        /// </summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A refused motor command is this move's slew_failed with the mount's message, never internal_error.")]
        private async Task<(double From, double To)> MoveDecAxisUntilAsync(
                double sign, double degrees, DecMoveTrack track, Func<double, double, double> progress,
                bool checkDirection, int gen, CancellationToken ct) {
            const TelescopeAxes axis = TelescopeAxes.Secondary;
            var steady = await EnsureMountSteadyReadingAsync(gen, ct, foldPastPole: true).ConfigureAwait(false);
            ObserveDecMove(track, steady);
            var from = track.SignedOffPoleDeg;
            var startSide = steady.Side;
            var start = (steady.RaDeg, steady.DecDeg);
            var (fastRate, slowRate) = MatchTurnRates(axis);
            var direction = sign >= 0 ? 1.0 : -1.0;
            var abortEpoch = RunAbortEpoch(gen);
            var fast = degrees > AxisSlowZoneDeg;
            double Rate() => direction * (fast ? fastRate : slowRate);
            var bound = TurnTimeBound(degrees, fastRate, slowRate);
            var best = 0.0;
            var settled = false;
            var startRate = Rate();
            LogAxisMove(axis, degrees, startRate);
            BeginMotion(MountMotion.AxisMove, gen, ct);
            try {
                await RunAxisAsync(axis, Rate(), abortEpoch).ConfigureAwait(false);
                var began = Clock.GetTimestamp();
                var bestAt = began;
                var last = (OffPole: from, At: began);
                // #2166 rig check — the motor's commanded travel: the move also stops once it reaches its distance, however
                // far the reading trails the motor (the HAE29C's 0.3° probe ran 1.9 s, 1°, on a reading 2 s behind).
                var travel = new CommandedTravel(Clock);
                travel.Reset(0.0, Rate(), began);
                var segmentBegan = began;
                // A top-up's settle is not motor time: the bound counts only the moves (#2188 review).
                var settledFor = TimeSpan.Zero;
                // Where the current segment started (the settled progress before a top-up): the follow check is per segment.
                var segmentBase = 0.0;
                while (true) {
                    var byClock = false;
                    var read = 0.0;
                    var lastStep = 0.0;
                    var prevP = double.NaN;
                    Task? pendingRead = null;
                    while (true) {
                        // #2189 rig check — the clock also ends the fast phase (at the slow zone), not the reading alone.
                        // A fast phase with no more than a top-up's worth left is no phase: the rest runs to the distance.
                        var target = fast && degrees - AxisSlowZoneDeg > segmentBase + ClockStopTopUpDeg ? degrees - AxisSlowZoneDeg : degrees;
                        await Task.Delay(NextAxisPoll(travel, target), ct).ConfigureAwait(false);
                        if (!IsCurrent(gen)) {
                            throw new OperationCanceledException("the move was stopped while the mount moved");
                        }
                        ThrowIfStoppedFromOutside(gen);
                        // #2189 rig check — the clock stop never waits on a read that blocks past the due time.
                        var dueFrom = Clock.GetTimestamp();
                        if (travel.DegreesAt(dueFrom) >= target - 1e-9) {
                            byClock = true;
                            break;
                        }
                        var readTask = RunReadingAsync(gen, MountReadKind.Pose, ct, foldPastPole: true);
                        if (await ClockDueBeforeReadAsync(readTask, travel.TimeTo(target, dueFrom)).ConfigureAwait(false)) {
                            byClock = true;
                            pendingRead = readTask;
                            break;
                        }
                        var r = await readTask.ConfigureAwait(false);
                        var at = Clock.GetTimestamp();
                        NoteMountLeft((r.RaDeg, r.DecDeg), r.Side is PierSide.pierUnknown ? startSide : r.Side);
                        ObserveDecMove(track, r);
                        var now = track.SignedOffPoleDeg;
                        var change = now - last.OffPole;
                        if (change != 0) {
                            ThrowIfReadingJumped(change, fastRate, Clock.GetElapsedTime(last.At, at), "Dec");
                            last = (now, at);
                        }
                        var p = progress(from, now);
                        read = p;
                        if (!double.IsNaN(prevP)) {
                            lastStep = p - prevP;
                        }
                        prevP = p;
                        if (p >= degrees - 1e-9) {
                            break;
                        }
                        if (checkDirection && p <= -WrongWayDeg) {
                            var wrongRate = Rate();
                            LogAxisMoveWrongWay(axis, -p, wrongRate, direction);
                            throw new RoutineFailedException(TurnWrongWay, DecWrongWayMessage);
                        }
                        if (p > best + 0.05) {
                            best = p;
                            bestAt = at;
                        } else if (Clock.GetElapsedTime(bestAt, at) > AxisMoveStall) {
                            LogAxisMoveStalled(axis, Math.Max(best, 0), degrees, AxisMoveStall.TotalSeconds);
                            throw new RoutineFailedException(SlewStalled, string.Create(CultureInfo.InvariantCulture,
                                $"the mount's Dec reading stopped changing after {Math.Max(best, 0):F1}° of the {degrees:F1}° move (no progress for {AxisMoveStall.TotalSeconds:F0} s) — check it can move freely and its connection, then try again"));
                        }
                        if (Clock.GetElapsedTime(began, at) - settledFor > bound) {
                            LogAxisMoveTimedOut(axis, Math.Max(best, 0), degrees, bound.TotalSeconds);
                            throw new RoutineFailedException(SlewTimeout, string.Create(CultureInfo.InvariantCulture,
                                $"the mount moved Dec only {Math.Max(best, 0):F1}° of {degrees:F1}° in {bound.TotalSeconds:F0} s, twice the time the move takes at its speed — check it, then try again"));
                        }
                        // #2166 rig check — the motor has been told the whole distance: stop it now, whatever the reading says.
                        // The reading reached the slow zone itself (within a still reading's 0.05°): its speed change below holds, no stop by the clock.
                        if (p < target - StopStillDeg && travel.DegreesAt(at) >= target - 1e-9) {
                            byClock = true;
                            break;
                        }
                        if (fast && degrees - p <= AxisSlowZoneDeg + 1e-9) {
                            fast = false;
                        }
                        if (!IsCurrent(gen)) {
                            throw new OperationCanceledException("the move was stopped while the mount moved");
                        }
                        await RunAxisAsync(axis, Rate(), abortEpoch).ConfigureAwait(false);
                        // Timed once the mount took it: the old rate ran until then, so a slowdown never under-counts (#2188 review).
                        travel.Run(Rate(), Clock.GetTimestamp());
                    }
                    if (RunStopsFenced(gen)) {
                        throw new OperationCanceledException("the move was fenced as it arrived");
                    }
                    var stoppedAt = Clock.GetTimestamp();
                    var commanded = travel.DegreesAt(stoppedAt);
                    await StopAxisBestEffortAsync(axis).ConfigureAwait(false);
                    if (pendingRead is not null) {
                        await DrainReadAsync(pendingRead).ConfigureAwait(false);
                    }
                    settled = true;
                    // The settle's progress reads the track, which checkPose has just moved on to the settle's reading.
                    await SettleAfterTurnAsync(start, startSide, degrees, (_, _) => progress(from, track.SignedOffPoleDeg), gen, ct,
                        checkPose: r => ObserveDecMove(track, r), foldPastPole: true).ConfigureAwait(false);
                    settledFor += Clock.GetElapsedTime(stoppedAt, Clock.GetTimestamp());
                    if (!byClock) {
                        break;
                    }
                    // Stopped by the clock: the settled reading says where the move really ended.
                    LogClockStop(axis, commanded, read, degrees);
                    var settledDeg = progress(from, track.SignedOffPoleDeg);
                    if (checkDirection && settledDeg <= -SettledWrongWayDeg(commanded - segmentBase)) {
                        LogAxisMoveWrongWay(axis, -settledDeg, Rate(), direction);
                        throw new RoutineFailedException(TurnWrongWay, DecWrongWayMessage);
                    }
                    ThrowIfReadingDidNotFollow(axis, settledDeg - segmentBase, commanded - segmentBase, degrees, lastStep > ClockStopAdvancingDeg,
                        sawReading: !double.IsNaN(prevP), "move");
                    var rest = degrees - settledDeg;
                    if (rest <= ClockStopTopUpDeg) {
                        break;
                    }
                    // Short of the distance once the reading caught up: a top-up for the rest, under the same guards and bound.
                    LogClockStopTopUp(axis, settledDeg, degrees, rest);
                    ThrowIfStoppedFromOutside(gen);
                    if (!IsCurrent(gen)) {
                        throw new OperationCanceledException("the move was stopped while the mount moved");
                    }
                    fast = rest > AxisSlowZoneDeg + ClockStopTopUpDeg; // a rest at the slow zone (3° and a rounding hair) runs slow
                    best = Math.Max(best, settledDeg);
                    settled = false;
                    await RunAxisAsync(axis, Rate(), abortEpoch).ConfigureAwait(false);
                    segmentBegan = Clock.GetTimestamp();
                    bestAt = segmentBegan;
                    last = (track.SignedOffPoleDeg, segmentBegan);
                    travel.Reset(settledDeg, Rate(), segmentBegan);
                    segmentBase = settledDeg;
                }
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception) when (!IsCurrent(gen)) {
                throw new OperationCanceledException("the move was stopped while the mount moved");
            } catch (RoutineFailedException) {
                throw;
            } catch (Exception) when (AbortedSince(abortEpoch)) {
                ThrowIfStoppedFromOutside(gen);
                throw new RoutineFailedException("slew_failed",
                    "the mount refused the Dec move right after a stop — check it is still, then try again");
            } catch (Exception ex) {
                LogAxisMoveRefused(axis, ex);
                throw new RoutineFailedException("slew_failed",
                    "the mount refused to turn its Dec motor — check it is unparked and free to move, then try again");
            } finally {
                if (!settled && !RunStopsFenced(gen)) {
                    await StopAxisBestEffortAsync(axis).ConfigureAwait(false);
                }
            }
            EndMotion();
            var to = track.SignedOffPoleDeg;
            var moved = progress(from, to);
            LogAxisMoveDone(axis, moved);
            return (from, to);
        }

        /// <summary>
        /// #2166 — the move to the start position's readings, one after another: where the scope points from the site's
        /// pole, signed for the side of the pier the move started on (negative once the Dec axis went over the pole), and
        /// the guards every reading passes.
        /// </summary>
        internal sealed class DecMoveTrack(double poleSign) {
            public double PoleSign { get; } = poleSign;
            public bool Started { get; set; }
            /// <summary>#2189 — the leg that only leaves the pole: no pier-side or over-the-pole judgement, and an RA flip of a
            /// pass over the pole is not counted as RA motion (<see cref="LeaveThePoleAsync"/>).</summary>
            public bool Leaving { get; init; }
            /// <summary>The pier side the move started on, from the first reading that says (<c>pierUnknown</c> until then).</summary>
            public PierSide OriginSide { get; set; } = PierSide.pierUnknown;
            /// <summary>The Dec axis is over the pole from where the move started (the reading's RA flipped by 12 h there).</summary>
            public bool OverThePole { get; set; }
            public double LastRawRaDeg { get; set; }
            public double LastJnowRaDeg { get; set; }
            /// <summary>The last reading's distance from the pole (degrees, unsigned).</summary>
            public double LastOffPoleDeg { get; set; }
            /// <summary>How far the RA reading went east or west on the sky (degrees, summed reading to reading), in the
            /// mount's own frame and in JNOW.</summary>
            public double RaSkyRawDeg { get; set; }
            public double RaSkyJnowDeg { get; set; }
            /// <summary>The distance from the pole on the start's side: negative over the pole.</summary>
            public double SignedOffPoleDeg => OverThePole ? -LastOffPoleDeg : LastOffPoleDeg;

            /// <summary>The side a reading must report: the start's, or the other one over the pole (unknown while the
            /// start's is).</summary>
            public PierSide ExpectedSide(bool overThePole) => (OriginSide, overThePole) switch {
                (PierSide.pierUnknown, _) => PierSide.pierUnknown,
                (var side, false) => side,
                (PierSide.pierEast, true) => PierSide.pierWest,
                _ => PierSide.pierEast,
            };
        }

        /// <summary>
        /// One reading of the move to the start position, against the one before it on <paramref name="track"/>:
        /// <list type="bullet">
        /// <item>The RA reading flipping by about 12 h between two readings within <see cref="PoleCrossingDeg"/> of the pole
        /// is the Dec axis going over it (the same pointing, read from the other side): allowed, and the readings after it
        /// are measured from the far side.</item>
        /// <item>The reading's pier side must be the start's, or the other one while over the pole; any other fails it
        /// (<see cref="PierChanged"/>). A reading that does not say is not checked.</item>
        /// <item>The RA reading must not move (tracking holds it): its moves east or west are summed as sky angles
        /// (ΔRA × cos Dec, so the 1/cos Dec spread of a pointing model near the pole does not count), in the mount's own
        /// frame and in JNOW. Past <see cref="DecMoveRaSkyLimitDeg"/> in both, the RA axis turned
        /// (<see cref="RaMoved"/>). Both, because a mount that reports J2000 may hold either one still.</item>
        /// </list>
        /// </summary>
        private void ObserveDecMove(DecMoveTrack track, FreshPointing r) {
            var offPole = 90.0 - (track.PoleSign * r.DecDeg);
            if (!track.Started) {
                track.Started = true;
                (track.LastRawRaDeg, track.LastJnowRaDeg, track.LastOffPoleDeg) = (r.RawRaDeg, r.RaDeg, offPole);
                track.OriginSide = r.Side;
                return;
            }
            var rawStep = Wrap180(r.RawRaDeg - track.LastRawRaDeg);
            var jnowStep = Wrap180(r.RaDeg - track.LastJnowRaDeg);
            if (track.Leaving) {
                // Leaving the pole: any side is right, and a pass over it flips the RA reading by 12 h. Smaller RA moves still
                // count: the RA axis must not turn.
                if (Math.Abs(rawStep) <= 90.0) {
                    var cosLeaving = Math.Sin(Math.Max(offPole, track.LastOffPoleDeg) * Math.PI / 180.0);
                    track.RaSkyRawDeg += Math.Abs(rawStep) * cosLeaving;
                    track.RaSkyJnowDeg += Math.Abs(jnowStep) * cosLeaving;
                    var leavingRa = Math.Min(track.RaSkyRawDeg, track.RaSkyJnowDeg);
                    if (leavingRa > DecMoveRaSkyLimitDeg) {
                        LogDecMoveRaMoved(leavingRa);
                        throw new RoutineFailedException(RaMoved, string.Create(CultureInfo.InvariantCulture,
                            $"the mount's RA reading moved {leavingRa:F2}° on the sky during a Dec move (a Dec move should not move it) — check the RA clutch is tight and the mount moves only in Dec, then try again"));
                    }
                }
                (track.LastRawRaDeg, track.LastJnowRaDeg, track.LastOffPoleDeg) = (r.RawRaDeg, r.RaDeg, offPole);
                return;
            }
            var over = track.OverThePole;
            var crossed = Math.Abs(rawStep) > 90.0 && offPole <= PoleCrossingDeg && track.LastOffPoleDeg <= PoleCrossingDeg;
            if (crossed) {
                over = !over;
                rawStep = Wrap180(rawStep - 180.0);
                jnowStep = Wrap180(jnowStep - 180.0);
            }
            if (r.Side is not PierSide.pierUnknown) {
                if (track.OriginSide is PierSide.pierUnknown) {
                    track.OriginSide = over ? (r.Side == PierSide.pierEast ? PierSide.pierWest : PierSide.pierEast) : r.Side;
                }
                var expected = track.ExpectedSide(over);
                if (r.Side != expected) {
                    var reported = r.Side;
                    LogDecMovePierChanged(expected, reported);
                    throw new RoutineFailedException(PierChanged,
                        "the mount reported a different side of the pier during the Dec move — Polar Align never flips the mount; check where it points, then try again");
                }
            }
            if (crossed) {
                LogDecMoveCrossedPole(over ? "over" : "back over");
            }
            // cos Dec of the reading farther from the pole: the stricter of the two.
            var cosDec = Math.Sin(Math.Max(offPole, track.LastOffPoleDeg) * Math.PI / 180.0);
            track.RaSkyRawDeg += Math.Abs(rawStep) * cosDec;
            track.RaSkyJnowDeg += Math.Abs(jnowStep) * cosDec;
            var raMoved = Math.Min(track.RaSkyRawDeg, track.RaSkyJnowDeg);
            if (raMoved > DecMoveRaSkyLimitDeg) {
                LogDecMoveRaMoved(raMoved);
                throw new RoutineFailedException(RaMoved, string.Create(CultureInfo.InvariantCulture,
                    $"the mount's RA reading moved {raMoved:F2}° on the sky during a Dec move (a Dec move should not move it) — check the RA clutch is tight and the mount moves only in Dec, then try again"));
            }
            track.OverThePole = over;
            (track.LastRawRaDeg, track.LastJnowRaDeg, track.LastOffPoleDeg) = (r.RawRaDeg, r.RaDeg, offPole);
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "§45 polar-align: the move to the start position refused: not confirmed ({Confirmation} confirmation)")]
        private partial void LogMoveToStartNotConfirmed(string confirmation);

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: moving the scope to the start position, {OffPoleDeg:F1}° from the {Pole} pole, with the Dec motor alone")]
        private partial void LogMoveToStartBegins(double offPoleDeg, string pole);

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: the Dec probe moved {TowardPoleDeg:+0.00;-0.00}° toward the pole; the move off it runs at sign {Sign:+0;-0}")]
        private partial void LogMoveToStartProbed(double towardPoleDeg, double sign);

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: the scope points {OffPoleDeg:F2}° from the pole — at the start position")]
        private partial void LogMoveToStartArrived(double offPoleDeg);

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: the move to the start position ended: {Outcome} ({Reason})")]
        private partial void LogMoveToStartEnded(string outcome, string reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "§45 polar-align: the RA reading moved {MovedDeg:F3}° on the sky during a Dec move — stopped (ra_moved)")]
        private partial void LogDecMoveRaMoved(double movedDeg);

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: the Dec axis went {Which} the pole during the move to the start position")]
        private partial void LogDecMoveCrossedPole(string which);

        [LoggerMessage(Level = LogLevel.Warning, Message = "§45 polar-align: pier side changed from {From} to {To} during a Dec move — stopped (pier_changed)")]
        private partial void LogDecMovePierChanged(PierSide from, PierSide to);

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: the move to the start position goes at Dec sign {Sign:+0;-0}, the direction saved for {Mount} (no probe)")]
        private partial void LogMoveToStartSavedDirection(double sign, string mount);

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: the Dec motor direction (a positive rate goes {Way} the pole on the {Side} side) is saved for {Mount}")]
        private partial void LogDecDirectionSavedText(string way, string side, string mount);

        [LoggerMessage(Level = LogLevel.Warning, Message = "§45 polar-align: the saved Dec direction's first leg moved {LegDeg:+0.00;-0.00}° away from the pole (toward it, or over it) — stopped, the saved direction is forgotten (turn_wrong_way)")]
        private partial void LogDecSavedDirectionWentOver(double legDeg);

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: the scope points {OffPoleDeg:F2}° from the pole, where a start has no side: a first Dec move of {PathDeg:F2}° only leaves the pole")]
        private partial void LogMoveToStartLeavesThePole(double offPoleDeg, double pathDeg);

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: the scope left the pole: {OffPoleDeg:F2}° from it on the {Side} side, the move's start side from here")]
        private partial void LogMoveToStartLeftThePole(double offPoleDeg, PierSide side);


        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: the Dec probe started next to the pole: waiting until the pier side and RA reading stand still for {Seconds:F0} s")]
        private partial void LogMoveToStartWaitsAtThePole(double seconds);
    }
}
