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
    /// more than <see cref="TurnDecLimitDeg"/> (<see cref="RaMoved"/>, the mirror of <c>dec_moved</c>). ASCOM leaves the
    /// Dec motor's sign to the driver, so a <see cref="MoveToStartProbeDeg"/> probe goes first; a probe that took Dec
    /// toward the pole is stopped and settled, and a new move then goes the other way (never a reversal mid-move). Then
    /// the exit's confirmed stop, the tracking the mount had handed back, and a fresh pre-flight list.
    /// </summary>
    public sealed partial class PolarAlignService {

        /// <summary>How far from the site's pole the move leaves the scope (inside the 2–5° the start asks for).</summary>
        internal const double MoveToStartOffPoleDeg = 4.0;

        /// <summary>The first, small Dec move that learns which way the Dec motor turns.</summary>
        internal const double MoveToStartProbeDeg = 0.3;

        /// <summary>The most the move takes the Dec axis from where it starts: the target plus the probe. The mount's
        /// clearance must be confirmed for at least this.</summary>
        internal const double MoveToStartReachDeg = MoveToStartOffPoleDeg + MoveToStartProbeDeg;

        /// <summary>The band, from the pole, the start asks for: the move must end inside it.</summary>
        internal const double StartBandNearDeg = 2.0;
        internal const double StartBandFarDeg = 5.0;

        /// <summary>The reason of a Dec move during which the RA reading moved (the mirror of <see cref="DecMoved"/>).</summary>
        internal const string RaMoved = "ra_moved";

        private const string MoveToStartWhy = "move_to_start";

        private const string DecWrongWayMessage =
            "the mount's Dec went toward the pole, the other way from the probe's lesson, so Polar Align stopped it and never turned it back — check the mount, then point it 2–5° from the pole with its own controls";

        // A move to the start position is under way (it holds _opLock and the motion owner). Guarded by _gate.
        private bool _movingToStart;

        /// <inheritdoc />
        public async Task<PolarAlignMoveToStartResultDto> MoveToStartAsync(CancellationToken ct) {
            // #2066 — the stop count at the call, as a start reads it: a Stop Mount from here on ends the move before it moves.
            var abortEpochAtCall = _motionControl.AbortEpoch;
            var ownAbortsAtCall = Interlocked.Read(ref _ownAborts);
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
                var preflight = await EvaluatePreflightAsync(PolarAlignStages.Fine, handTurn: false, ct).ConfigureAwait(false);
                if (preflight.Result.MoveToStartRefusal is { } refused) {
                    ExceptionDispatchInfo.Capture(refused).Throw();
                }
                if (holdRefusal is not null) {
                    throw holdRefusal;
                }
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

        /// <summary>The move itself, under <c>_opLock</c>, from the guards' fresh reading: a run of its own generation (so
        /// Stop, Emergency Stop and the run-wide watch reach it), the move, the exit, then the fresh list.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "The move's boundary: any failure must still stop the mount and come back as the move's outcome, never escape with the motor marked.")]
        private async Task<PolarAlignMoveToStartResultDto> RunMoveToStartAsync(
                IDisposable hold, double poleSign, long session, long abortEpochAtCall, long ownAbortsAtCall, CancellationToken ct) {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); // a client that goes away stops the move
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
            var priorTracking = _mount.GetInfo().TrackingEnabled;
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
            var fresh = await EvaluatePreflightAsync(PolarAlignStages.Fine, handTurn: false, CancellationToken.None).ConfigureAwait(false);
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

        /// <summary>The probe, then the move off the pole, then the check that the scope points 2–5° from it. Tracking is on
        /// first, so the RA reading stands still while the Dec axis turns (the RA guard reads it).</summary>
        private async Task MoveDecToStartAsync(double poleSign, int gen, CancellationToken ct) {
            ThrowIfStoppedFromOutside(gen);
            await EnsureTrackingOnAsync(gen, ct).ConfigureAwait(false);
            (double RaDeg, double DecDeg)? from = null;
            var probed = await MoveDecAxisUntilAsync(1.0, MoveToStartProbeDeg,
                (start, now) => Math.Abs(now.DecDeg - start.DecDeg), checkDirection: false, gen, ct,
                beforeMove: start => from = start).ConfigureAwait(false);
            var towardPole = poleSign * (probed.DecDeg - from!.Value.DecDeg);
            if (Math.Abs(towardPole) < MoveToStartProbeDeg / 2) {
                throw new RoutineFailedException(TurnMismatch, string.Create(CultureInfo.InvariantCulture,
                    $"the mount's Dec reading moved only {Math.Abs(towardPole):F2}° in the {MoveToStartProbeDeg:F1}° probe, so its Dec motor's direction is not known — check the mount, then try again"));
            }
            // A probe toward the pole has stopped and settled: the next move is a new one, the other way.
            var awaySign = towardPole > 0 ? -1.0 : 1.0;
            LogMoveToStartProbed(towardPole, awaySign);
            var remaining = MoveToStartOffPoleDeg - (90.0 - (poleSign * probed.DecDeg));
            if (remaining > StopStillDeg) {
                await MoveDecAxisUntilAsync(awaySign, remaining,
                    (start, now) => poleSign * (start.DecDeg - now.DecDeg), checkDirection: true, gen, ct).ConfigureAwait(false);
            }
            var final = await RunPointingJnowAsync(gen, ct).ConfigureAwait(false);
            var offPole = 90.0 - (poleSign * final.DecDeg);
            if (offPole < StartBandNearDeg || offPole > StartBandFarDeg) {
                throw new RoutineFailedException(TurnMismatch, string.Create(CultureInfo.InvariantCulture,
                    $"the move ended with the scope {offPole:F1}° from the pole, not 2–5° — check the mount, then point it with its own controls"));
            }
            LogMoveToStartArrived(offPole);
        }

        /// <summary>
        /// The Dec twin of <see cref="MoveAxisUntilAsync"/>: run the Dec motor until <paramref name="progress"/> reaches
        /// <paramref name="degrees"/> by the mount's own reading, polled every <see cref="AxisMovePoll"/>, fast until the
        /// last <see cref="AxisSlowZoneDeg"/> and slow for the rest, at the rates the mount offers at or below Polar Align's.
        /// The same guards: steady first, the stall, twice the planned time plus slack, a stop from outside, a reading that
        /// jumps, the pier side, and (mirroring <c>dec_moved</c>) an RA reading that moves more than
        /// <see cref="TurnDecLimitDeg"/>. With <paramref name="checkDirection"/>, a move that goes the wrong way is stopped
        /// and fails, never reversed. Every exit stops the motor; then the settle on fresh readings.
        /// </summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A refused motor command is this move's slew_failed with the mount's message, never internal_error.")]
        private async Task<(double RaDeg, double DecDeg)> MoveDecAxisUntilAsync(
                double sign, double degrees, Func<(double RaDeg, double DecDeg), (double RaDeg, double DecDeg), double> progress,
                bool checkDirection, int gen, CancellationToken ct, Action<(double RaDeg, double DecDeg)>? beforeMove = null) {
            const TelescopeAxes axis = TelescopeAxes.Secondary;
            var (startRa, startDec, startSide) = await EnsureMountSteadyAsync(gen, ct).ConfigureAwait(false);
            var start = (startRa, startDec);
            var (fastRate, slowRate) = MatchTurnRates(axis);
            beforeMove?.Invoke(start);
            var direction = sign >= 0 ? 1.0 : -1.0;
            var abortEpoch = RunAbortEpoch(gen);
            var fast = degrees > AxisSlowZoneDeg;
            double Rate() => direction * (fast ? fastRate : slowRate);
            var bound = TurnTimeBound(degrees, fastRate, slowRate);
            var best = 0.0;
            var settled = false;
            var startRate = Rate();
            LogAxisMove(axis, degrees, startRate);
            (double RaDeg, double DecDeg, PierSide Side) after;
            BeginMotion(MountMotion.AxisMove, gen, ct);
            try {
                await RunAxisAsync(axis, Rate(), abortEpoch).ConfigureAwait(false);
                var began = Clock.GetTimestamp();
                var bestAt = began;
                var last = (Dec: startDec, At: began);
                while (true) {
                    await Task.Delay(AxisMovePoll, ct).ConfigureAwait(false);
                    if (!IsCurrent(gen)) {
                        throw new OperationCanceledException("the move was stopped while the mount moved");
                    }
                    ThrowIfStoppedFromOutside(gen);
                    var (nowRa, nowDec, nowSide) = await RunPoseJnowAsync(gen, ct, MountReadKind.Pose).ConfigureAwait(false);
                    var at = Clock.GetTimestamp();
                    var now = (nowRa, nowDec);
                    NoteMountLeft(now, nowSide is PierSide.pierUnknown ? startSide : nowSide);
                    var change = nowDec - last.Dec;
                    if (change != 0) {
                        ThrowIfReadingJumped(change, fastRate, Clock.GetElapsedTime(last.At, at), "Dec");
                        last = (nowDec, at);
                    }
                    ThrowIfDecMoveLeftItsAxis(startRa, startSide, nowRa, nowSide);
                    var p = progress(start, now);
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
                    if (Clock.GetElapsedTime(began, at) > bound) {
                        LogAxisMoveTimedOut(axis, Math.Max(best, 0), degrees, bound.TotalSeconds);
                        throw new RoutineFailedException(SlewTimeout, string.Create(CultureInfo.InvariantCulture,
                            $"the mount moved Dec only {Math.Max(best, 0):F1}° of {degrees:F1}° in {bound.TotalSeconds:F0} s, twice the time the move takes at its speed — check it, then try again"));
                    }
                    if (fast && degrees - p <= AxisSlowZoneDeg) {
                        fast = false;
                    }
                    if (!IsCurrent(gen)) {
                        throw new OperationCanceledException("the move was stopped while the mount moved");
                    }
                    await RunAxisAsync(axis, Rate(), abortEpoch).ConfigureAwait(false);
                }
                if (RunStopsFenced(gen)) {
                    throw new OperationCanceledException("the move was fenced as it arrived");
                }
                await StopAxisBestEffortAsync(axis).ConfigureAwait(false);
                settled = true;
                after = await SettleAfterTurnAsync(start, startSide, degrees, progress, gen, ct,
                    checkPose: (ra, _, side) => ThrowIfDecMoveLeftItsAxis(startRa, startSide, ra, side)).ConfigureAwait(false);
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
            var moved = progress(start, (after.RaDeg, after.DecDeg));
            LogAxisMoveDone(axis, moved);
            return (after.RaDeg, after.DecDeg);
        }

        /// <summary>One poll's checks on a Dec move against its start: the RA reading moved more than
        /// <see cref="TurnDecLimitDeg"/> (<see cref="RaMoved"/>; tracking holds it still), or the pier side changed.</summary>
        private void ThrowIfDecMoveLeftItsAxis(double startRaDeg, PierSide startSide, double nowRaDeg, PierSide nowSide) {
            var raMoved = Math.Abs(Wrap180(nowRaDeg - startRaDeg));
            if (raMoved > TurnDecLimitDeg) {
                LogDecMoveRaMoved(raMoved);
                throw new RoutineFailedException(RaMoved, string.Create(CultureInfo.InvariantCulture,
                    $"the mount's RA reading moved {raMoved:F2}° during a Dec move (a Dec move should not move it) — check the RA clutch is tight and the mount moves only in Dec, then try again"));
            }
            if (startSide is not PierSide.pierUnknown && nowSide is not PierSide.pierUnknown && nowSide != startSide) {
                LogTurnPierChanged(startSide, nowSide);
                throw new RoutineFailedException(PierChanged,
                    "the mount reported a different side of the pier during the Dec move — Polar Align never flips the mount; check where it points, then try again");
            }
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: moving the scope to the start position, {OffPoleDeg:F1}° from the {Pole} pole, with the Dec motor alone")]
        private partial void LogMoveToStartBegins(double offPoleDeg, string pole);

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: the Dec probe moved {TowardPoleDeg:+0.00;-0.00}° toward the pole; the move off it runs at sign {Sign:+0;-0}")]
        private partial void LogMoveToStartProbed(double towardPoleDeg, double sign);

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: the scope points {OffPoleDeg:F2}° from the pole — at the start position")]
        private partial void LogMoveToStartArrived(double offPoleDeg);

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: the move to the start position ended: {Outcome} ({Reason})")]
        private partial void LogMoveToStartEnded(string outcome, string reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "§45 polar-align: the RA reading moved {MovedDeg:F3}° during a Dec move — stopped (ra_moved)")]
        private partial void LogDecMoveRaMoved(double movedDeg);
    }
}
