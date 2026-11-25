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
using TelescopeAxes = OpenAstroAra.Core.Enums.TelescopeAxes;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// #2048 — every end of a run leaves the mount stopped, as NINA's Three Point Polar Alignment does: its catch around
    /// the RA move sends MoveAxis 0 on any error or cancel, and its finally turns tracking off (NINA PolarAlignment.cs,
    /// the MoveAxis catch at 1037-1041 and the finally at 791-805). Here every exit — Stop, a sequence start, a failure,
    /// an exception, a time bound, a lost guider, daemon shutdown and the verdict — and Done, before the mount goes Home,
    /// sends AbortSlew when a motor move or the run's own Find Home may be running, then MoveAxis 0 on both axes, then
    /// confirms on fresh reads that the mount does not slew and its position stands still. Nothing on that path waits on
    /// a lock, a lease or the operation it stops, and no call on it takes the run's token.
    /// </summary>
    public sealed partial class PolarAlignService {

        // The motions the routine commands: a timed motor move, and the mount's own Find Home after Done. It never slews
        // or unparks.
        private enum MountMotion { None = 0, AxisMove = 1, Home = 2 }

        // The motion this routine commanded and has not seen end. Written by the run task (a superseded one too: the
        // mount knows nothing of generations), read by the stop path. Cleared only when the motion ended normally or a
        // stop was confirmed: a motor move or a Find Home whose run was cancelled may still run until its stop lands.
        private int _motion;

        private MountMotion CurrentMotion => (MountMotion)Volatile.Read(ref _motion);

        /// <summary>The run's gate before it commands motion: a run that was stopped or superseded starts none. The
        /// motion is marked BEFORE the run is checked: a Stop that fences the run after the check then reads the mark and
        /// sends AbortSlew in its first round (a mark left by a failed check only adds one harmless AbortSlew).</summary>
        private void BeginMotion(MountMotion kind, int gen, CancellationToken ct) {
            Volatile.Write(ref _motion, (int)kind);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrent(gen)) {
                throw new OperationCanceledException("the run was stopped before the mount moved");
            }
            ThrowIfStoppedFromOutside(gen);
        }

        private void EndMotion() => Volatile.Write(ref _motion, (int)MountMotion.None);

        /// <summary>#2066 — a motor move or the Find Home of a live run, or its stop not confirmed yet: the telescope
        /// endpoints refuse the user's GoTo, pad move, Home and Park meanwhile (<c>polar-align-moving-mount</c>). A run that
        /// ended with its stop unconfirmed no longer counts: the user must be able to move the mount away by hand.</summary>
        public bool IsMovingMount => CurrentMotion != MountMotion.None && IsActive;

        // ── the run-wide watch for a stop from outside (#2066, #2067) ─────────────────────────

        // Polar Align's own AbortSlew sends: the run tells its own stop from one it did not raise.
        private long _ownAborts;

        // The mount's stop count up to which _ownAborts has looked at the reasons. Guarded by _ownAbortsGate.
        private long _ownAbortsCountedThrough;
        private readonly object _ownAbortsGate = new();

        /// <summary>The farthest the stop count may move during one AbortSlew send and still be read count by count: the
        /// telescope service keeps the last 32 reasons. Past it, none is counted as Polar Align's own (a stop from outside
        /// then ends the run, never the other way round).</summary>
        private const long OwnAbortsReadable = 16;

        /// <summary>
        /// #2066 — after Polar Align's AbortSlew send, count as its own only the stops the telescope service tagged
        /// <see cref="AbortReason"/> since <paramref name="before"/> (the count when the send began). The count moving is
        /// not enough: a send refused before it counts (a mount not connected) while Stop Mount lands would hide the Stop
        /// Mount from the run-wide watch, and a Start's baseline would skip past it. Each count is read once, whichever of
        /// two sends that overlap reads it first.
        /// </summary>
        private void CountOwnAborts(IMountMotionControl service, long before) {
            lock (_ownAbortsGate) {
                var after = service.AbortEpoch;
                var from = Math.Max(before, _ownAbortsCountedThrough);
                if (after - from <= OwnAbortsReadable) {
                    for (var count = from; count < after; count++) {
                        // The first reason after `count`: the one the count after it was raised with.
                        if (string.Equals(service.AbortReasonSince(count, except: null), AbortReason, StringComparison.Ordinal)) {
                            Interlocked.Increment(ref _ownAborts);
                        }
                    }
                }
                _ownAbortsCountedThrough = Math.Max(_ownAbortsCountedThrough, after);
            }
        }

        // The telescope's AbortEpoch and _ownAborts when the run's Start was called (re-read once the run has stopped the
        // mount itself, RebaselineAbortWatch), and the run's end (its mount session), for the run of this generation.
        // Guarded by _gate.
        private (int Gen, long Epoch, long OwnAborts, RunEnd End)? _abortWatch;

        /// <summary>The reading of the mount's stop count a run moves under, from the reading taken at
        /// (<paramref name="epoch"/>, <paramref name="ownAborts"/>): the count now when only Polar Align's own stops moved it
        /// since, so the telescope service does not refuse the run's next motor command over them; the old reading when
        /// another stop came, which then ends the run (<see cref="ThrowIfStoppedFromOutside"/>).</summary>
        private (long Epoch, long OwnAborts) AbortBaselineSince(long epoch, long ownAborts) {
            var epochNow = _motionControl.AbortEpoch;
            var ownNow = Interlocked.Read(ref _ownAborts);
            return epochNow - epoch <= ownNow - ownAborts ? (epochNow, ownNow) : (epoch, ownAborts);
        }

        /// <summary>The run stopped the mount itself (Done, before the verify measurement): its next turns start under a
        /// reading taken after its own stops (<see cref="AbortBaselineSince"/>). A stop from outside ends the run here.</summary>
        private void RebaselineAbortWatch(int gen) {
            ThrowIfStoppedFromOutside(gen);
            lock (_gate) {
                if (_abortWatch is { } w && w.Gen == gen) {
                    var (epoch, ownAborts) = AbortBaselineSince(w.Epoch, w.OwnAborts);
                    _abortWatch = w with { Epoch = epoch, OwnAborts = ownAborts };
                }
            }
        }

        // #2066 — the stop fence a run was launched under (RunEnd.Fence), by generation, for its turns' own clean-up stops.
        // Guarded by _gate.
        private (int Gen, long Fence)? _runFence;

        /// <summary>#2066 — whether the stops of the run launched as generation <paramref name="gen"/> are fenced. A run
        /// that a newer launch replaced keeps no record: its turns' stops go out as before.</summary>
        private bool RunStopsFenced(int gen) {
            long fence;
            lock (_gate) {
                if (_runFence is not { } f || f.Gen != gen) {
                    return false;
                }
                fence = f.Fence;
            }
            return StopsFenced(fence);
        }

        /// <summary>The run's AbortEpoch reading (<c>_abortWatch</c>), for the leased motor commands; null for a superseded
        /// run or a mount that keeps no count.</summary>
        private long? RunAbortEpoch(int gen) {
            lock (_gate) {
                return _abortWatch is { } w && w.Gen == gen ? w.Epoch : null;
            }
        }

        /// <summary>
        /// The run-wide watch: any stop the run did not raise since its Start — Stop Mount, Emergency Stop, the meridian
        /// watchdog, a park, a Find Home — fails it with <see cref="SlewAbortedExternally"/>, so it starts no further turn
        /// and turns tracking on no more. Checked before every motor move, before every tracking switch-on, after every seed
        /// frame and each live frame, through every turn (before each renewal of the motor's lease) and through the run's
        /// own Find Home.
        /// </summary>
        private void ThrowIfStoppedFromOutside(int gen) {
            var service = _motionControl;
            (long Epoch, long OwnAborts, RunEnd End) watch;
            lock (_gate) {
                if (_abortWatch is not { } w || w.Gen != gen) {
                    return;
                }
                watch = (w.Epoch, w.OwnAborts, w.End);
            }
            // #2066 — a mount that reconnected since the Start (a Wi-Fi drop, a power cycle) may have moved meanwhile: its
            // position is flagged at once, so nothing moves it by the model until the user confirms Home.
            NotePoseUnverifiedAfterRun(watch.End, mountStopped: null);
            var own = Interlocked.Read(ref _ownAborts) - watch.OwnAborts;
            if (service.AbortEpoch - watch.Epoch <= own) {
                return;
            }
            var by = service.AbortReasonSince(watch.Epoch, except: AbortReason);
            LogStoppedFromOutside(by ?? "unknown");
            throw new RoutineFailedException(SlewAbortedExternally, StoppedFromOutsideMessage(by), mountTakenOver: TakesTheMountOver(by), stoppedBy: by);
        }

        /// <summary>
        /// #2066, #2065 (ARA-10) — the pose flag: a run whose stop was not confirmed (<paramref name="mountStopped"/> false),
        /// or whose mount session changed since its launch (a reconnect or a power cycle while it ran), leaves the mount's
        /// position unverified (<see cref="MountPoseTrust.StopUnconfirmed"/>). GoTo, Park and the next polar alignment
        /// are refused until a Home search ends at Home (#2173) or the user confirms the mount is at Home. Once per run; never throws.
        /// </summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "The flag is set on the run's exit and its stop path, which must always finish: a fault is logged as Critical.")]
        private void NotePoseUnverifiedAfterRun(RunEnd end, bool? mountStopped) {
            if (_mount is not ITelescopeService service) {
                return;
            }
            var notStopped = mountStopped == false;
            if (!notStopped && _motionControl.SessionEpoch == end.MountSession) {
                return;
            }
            if (!end.TryFlagPose()) {
                return;
            }
            LogPoseLeftUnverified(notStopped ? "its stop was not confirmed" : "the mount reconnected while it ran");
            try {
                service.MarkPositionUnverified(MountPoseTrust.StopUnconfirmed);
            } catch (Exception ex) {
                LogPoseFlagFailed(ex);
            }
        }

        /// <summary>The <c>polar_align.error</c> reason when a stop Polar Align did not raise ended the run.</summary>
        internal const string SlewAbortedExternally = "slew_aborted_externally";

        /// <summary>A stop that hands the mount to its sender: Emergency Stop stops and checks it itself, a park or Find Home
        /// moves it on purpose. The run's exit then sends no stop of its own (it would abort the park) and confirms none.</summary>
        internal static bool TakesTheMountOver(string? by) => by is MountStopReasons.EmergencyStop or MountStopReasons.Park or MountStopReasons.FindHome;

        /// <summary>#2066 — the reason a pad stop counts with: a MoveAxis 0 from the app's direction pad (or any client of
        /// <c>/moveaxis</c>) that lands while Polar Align turns the mount. The run ends and does not restart the motor.</summary>
        public const string PadStop = MountStopReasons.PadStop;

        /// <summary>The <c>polar_align.error</c> message for <see cref="SlewAbortedExternally"/>, by what stopped the mount
        /// (the event's <c>stopped_by</c>; the app words it in its own language, #2107). English, for the log and an app that
        /// does not know the reason.</summary>
        internal static string StoppedFromOutsideMessage(string? by) => by switch {
            MountStopReasons.EmergencyStop => "Emergency Stop stopped the mount — polar alignment ended. Start again when ready",
            MountStopReasons.Watchdog => "the meridian-flip watchdog stopped the mount — polar alignment ended. Check the mount, then start again",
            MountStopReasons.Park => "the mount was parked from outside polar alignment — polar alignment ended. Unpark it and start again when ready",
            MountStopReasons.FindHome => "the mount was sent Home from outside polar alignment — polar alignment ended. Start again when ready",
            MountStopReasons.PadStop => "the mount was stopped from the direction pad — polar alignment ended. Start again when ready",
            MountStopReasons.FaultReaction => "the equipment-fault reaction stopped the mount (its park was refused: the mount's position is unverified) — polar alignment ended. Check the rig before you start again",
            MountStopReasons.PoseUnverified => "the safety reaction stopped the mount (conditions turned unsafe, and the park was refused: the mount's position is unverified) — polar alignment ended. Check the rig before you start again",
            _ => "the mount was stopped from outside polar alignment (Stop Mount) — polar alignment ended. Start again when ready",
        };

        /// <summary>The end request when Emergency Stop or the safety park takes the mount over (<see cref="StopForTakeoverAsync"/>).</summary>
        internal const string TakenOver = "taken_over";

        /// <summary>
        /// #2066 — how long a takeover waits for the routine to end. Inside the 10 s each caller gives it (Emergency Stop's
        /// rung, the safety and the fault park's first step), so past it this call fences the run's stops and answers
        /// before the caller gives up and parks: a run stuck in a call that ignores cancellation, or an earlier Stop still
        /// confirming its stop, sends nothing once the park may be under way (an AbortSlew or a MoveAxis 0 would abort it).
        /// </summary>
        internal TimeSpan TakeoverStopBound { get; set; } = TimeSpan.FromSeconds(9);

        /// <inheritdoc />
        public async Task<OperationAcceptedDto> StopForTakeoverAsync(string by, CancellationToken ct) {
            LogTakenOver(by);
            var ending = EndRoutineAsync(outcome: "aborted", null, ct, endRequest: TakenOver);
            try {
                return await ending.WaitAsync(TakeoverStopBound, CancellationToken.None).ConfigureAwait(false);
            } catch (TimeoutException) {
                FenceStops();
                LogTakeoverFenced(by, TakeoverStopBound.TotalSeconds);
                // The abandoned end still holds _opLock until the stuck call returns (it is fenced, so it sends nothing): a
                // Start after the park waits on it, within its own bounds, as it would behind any end still running.
                _ = ending.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                return Accepted("polar-align.stop", null);
            }
        }

        // ── the stop fence (#2066) ───────────────────────────────────────────────────────────

        // Moved when a takeover gives up waiting for the routine, and when an end abandons a run stuck past its grace. A run
        // keeps the value from its launch (RunEnd.Fence); once it moves, none of that run's stop commands goes out any more.
        private long _stopFence;

        private long StopFence => Interlocked.Read(ref _stopFence);

        /// <summary>Whether the stops of a run launched under <paramref name="fence"/> are fenced: the mount is someone
        /// else's now (a takeover's park, or the user after an end that abandoned the run).</summary>
        private bool StopsFenced(long fence) => StopFence != fence;

        /// <summary>No stop of any run launched so far goes out from here on. The motion mark goes too: the telescope
        /// endpoints must not keep refusing a Park or a GoTo for a run that no longer answers for the mount.</summary>
        private void FenceStops() {
            Interlocked.Increment(ref _stopFence);
            EndMotion();
        }

        // Instance (not const) so tests shrink them. The defaults follow the abort contract of #2048: a stop is never
        // confirmed within 2 s of the last command (AlpacaBridge iOptron caches status and position for 1 s), needs two
        // fresh reads 1.5 s apart, is sent three times at most, 2 s apart, and is given up after 8 s.
        /// <summary>The longest one stop command or read may take; a hung PUT must not hold back the next command.</summary>
        internal TimeSpan StopCommandTimeout { get; set; } = TimeSpan.FromSeconds(3);
        internal TimeSpan StopVerifyPoll { get; set; } = TimeSpan.FromMilliseconds(250);
        internal TimeSpan StopVerifyStillFor { get; set; } = TimeSpan.FromSeconds(1.5);
        internal TimeSpan StopVerifyNotBefore { get; set; } = TimeSpan.FromSeconds(2);
        internal TimeSpan StopResendAfter { get; set; } = TimeSpan.FromSeconds(2);
        internal TimeSpan StopVerifyWindow { get; set; } = TimeSpan.FromSeconds(8);
        /// <summary>Daemon shutdown waits this long for a running routine to stop the mount and unwind.</summary>
        internal TimeSpan ShutdownGrace { get; set; } = TimeSpan.FromSeconds(15);
        internal const int StopSends = 3;

        /// <summary>
        /// The longest Stop takes on the daemon before it answers, from the bounds above: the stuck run. The first
        /// round of stop commands (AbortSlew and MoveAxis 0 on both axes, each up to <see cref="StopCommandTimeout"/>), then
        /// the <see cref="RunUnwindGrace"/> for a run that never unwinds, then the stop the end confirms itself: its whole
        /// <see cref="StopVerifyWindow"/>, plus the poll, the two reads and the resent round that its last pass may start.
        /// The client's end timeout (<c>PolarAlignApi.endTimeout</c> in polar_align_api.dart) must outlast it with margin
        /// for tracking off, the session row and the camera release; PolarAlignServiceTest checks that it does.
        /// </summary>
        internal TimeSpan EndWorstCase {
            get {
                var round = StopCommandTimeout * 3;
                var confirmedStop = StopVerifyWindow + StopVerifyPoll + (StopCommandTimeout * 2) + round;
                return round + RunUnwindGrace + confirmedStop;
            }
        }

        /// <summary>Two reads closer than this in RA and in Dec (degrees) count as standing still.</summary>
        internal const double StopStillDeg = 0.05;

        /// <summary>The reason PA's AbortSlew carries in the telescope service's log and <c>slew_aborted</c> event.</summary>
        internal const string AbortReason = MountStopReasons.PolarAlign;

        /// <summary>The <c>polar_align.error</c> reason when a stop could not be confirmed.</summary>
        internal const string MountDidNotStop = "mount_did_not_stop";

        /// <summary>The end request when Guiding takes the guide camera from an open run: the mount still stops, and
        /// the tracking the run found is handed back, since the guiding that follows needs it (a GoTo turns tracking on by
        /// itself, guiding does not).</summary>
        internal const string HandedToGuiding = "handed_to_guiding";

        internal const string CutPowerSentence =
            "Ara could not confirm that the mount stopped. Cut the mount's power now, and check the mount before you start again.";

        /// <summary>How a run was asked to end, and what its exit found. One per run, shared by the run task and the
        /// stop path, so the stop path never needs the run's lock.</summary>
        private sealed class RunEnd {
            private string? _requested;
            private int _mountStopped = -1; // -1 not known yet, 0 not confirmed, 1 confirmed
            private int _reported;
            private int _poseFlagged;

            /// <summary>#2066 — the mount's session (<see cref="ITelescopeService.SessionEpoch"/>) when the run was launched: a
            /// change while it runs leaves the pose unverified (<see cref="NotePoseUnverifiedAfterRun"/>).</summary>
            public long MountSession { get; set; }

            /// <summary>True the first time only: the run's pose flag is sent once.</summary>
            public bool TryFlagPose() => Interlocked.Exchange(ref _poseFlagged, 1) == 0;

            /// <summary>#2066 — the run's own failure report (<see cref="FailRoutineAsync"/>) claimed the run's last event, and
            /// tells an unconfirmed stop itself (with the cut-power sentence). Guarded by _gate.</summary>
            public bool ReportsItself { get; set; }

            /// <summary>#2066 — <c>mount_did_not_stop</c> was told for this run (by the stop path or by the run's exit), so it
            /// is told once. Guarded by _gate.</summary>
            public bool NotStoppedTold { get; set; }

            /// <summary>How the run was asked to end: <c>aborted</c>, <c>ended_by_sequence</c>, <c>shutdown</c>,
            /// <see cref="TakenOver"/> or <see cref="HandedToGuiding"/>; null until one asks.</summary>
            public string? Requested => Volatile.Read(ref _requested);

            /// <summary>The first request wins: a Stop that lands after a takeover does not turn it into a Stop.</summary>
            public void Request(string outcome) => Interlocked.CompareExchange(ref _requested, outcome, null);

            private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Completes when the user taps Done in the adjust loop: the run ends the loop and goes on by itself
            /// (PolarAlignService.Chain.cs). Not an end request.</summary>
            public Task Done => _done.Task;

            public void RequestDone() => _done.TrySetResult();

            public bool? MountStopped {
                get => Volatile.Read(ref _mountStopped) switch { 1 => true, 0 => false, _ => null };
                set => Volatile.Write(ref _mountStopped, value switch { true => 1, false => 0, null => -1 });
            }

            /// <summary>Set once the stop path published the run's last event: a later finding is the run's own to report.</summary>
            public bool Reported => Volatile.Read(ref _reported) == 1;

            public void MarkReported() => Volatile.Write(ref _reported, 1);

            private IDisposable? _hold;

            /// <summary>#2066 (ARA-2) — the run's hold of the mount's motion owner, taken before it launched.</summary>
            public void Hold(IDisposable hold) => Volatile.Write(ref _hold, hold);

            /// <summary>Give the run's motion owner back, once per hold: when its exit has stopped the mount, when an end
            /// gives up waiting for a stuck run, when the next Start drains it, and while the run's own Find Home runs
            /// (<see cref="GoHomeAsync"/>, which takes it again with <see cref="Hold"/>).</summary>
            public void ReleaseHold() => Interlocked.Exchange(ref _hold, null)?.Dispose();

            /// <summary>#2066 — the stop fence when the run was launched (set before the run is published): once the fence
            /// moves, no stop of this run goes out (<see cref="StopsFenced"/>).</summary>
            public long Fence { get; set; }

            private int _priorTracking = -1; // -1 not read yet, 0 off, 1 on

            /// <summary>#2066 — the tracking the run found, for an end that abandons the run and hands it back itself.</summary>
            public bool? PriorTracking {
                get => Volatile.Read(ref _priorTracking) switch { 1 => true, 0 => false, _ => null };
                set => Volatile.Write(ref _priorTracking, value switch { true => 1, false => 0, null => -1 });
            }
        }

        // The current run's cancellation and end request, for the stop path, which must not wait on _opLock. Guarded by _gate.
        private (CancellationTokenSource Cts, RunEnd End)? _liveRun;

        // Every end request (Stop, a sequence start, a lost camera, shutdown) counts here before it does anything
        // else. A Start notes the count when it is called and launches no run if it changed: a Stop that lands while a
        // Start still holds _opLock (taking the camera, stopping the guider's loop) finds no run to stop yet, and must not
        // let that Start move the mount after it. Guarded by _gate.
        private long _endRequests;

        /// <summary>
        /// The first step of Stop, a sequence start and daemon shutdown, before any lock or wait: fence the run (its
        /// late writes become no-ops, and it starts no new motion), send the mount its stop, and cancel the run. The run's
        /// own exit then sends the stop again and confirms it. A no-op when no run is live.
        /// </summary>
        private async Task StopRunFirstAsync(string outcome) {
            (CancellationTokenSource Cts, RunEnd End)? live;
            bool active;
            lock (_gate) {
                _endRequests++;
                live = _liveRun;
                active = _active;
                if (live is not null) {
                    _generation++;
                }
            }
            if (live is not { } run) {
                return;
            }
            run.End.Request(outcome);
            // Started before the cancel and awaited after it: the cancel runs the run's continuations, and a hung mount
            // command must not hold the cancel back. A mount that is not connected gets no stop here: the run's exit keeps
            // trying while the client reconnects (StopMountMotionAsync).
            var stopping = active || CurrentMotion != MountMotion.None
                ? SendMountStopAsync(outcome, forceAbort: false, run.End.Fence)
                : Task.CompletedTask;
            try {
                await run.Cts.CancelAsync().ConfigureAwait(false);
            } catch (ObjectDisposedException) {
                // A concurrent end already disposed it, after it cancelled it.
            }
            await stopping.ConfigureAwait(false);
        }

        /// <summary>
        /// Stop the mount and confirm it stopped: AbortSlew when a motor move or the Find Home of this routine may be
        /// running (or the mount reads as slewing), MoveAxis 0 on the primary and the secondary axis, then fresh reads
        /// until two of them, at least <see cref="StopVerifyStillFor"/> apart and <see cref="StopVerifyNotBefore"/> after the
        /// last command, both say not slewing and differ by under <see cref="StopStillDeg"/> in RA and in Dec. The stop is
        /// sent again every <see cref="StopResendAfter"/>, <see cref="StopSends"/> times in all. A failed, missing or
        /// slewing read, or one the mount sampled before the last stop went out, never counts as stopped; a read that is
        /// slow to arrive does (<see cref="UntrustedForStillness"/>). #2066 — a mount that is not connected gets its stop once the client
        /// reconnects: a round that cannot reach it is not a send, and it is tried again on every poll inside the window.
        /// False, logged as Critical, when the stop is not confirmed within <see cref="StopVerifyWindow"/>. Null when the
        /// run's stops were fenced (<paramref name="fence"/>): the mount is a takeover's, or the user's after an end
        /// abandoned the run, and nothing more is sent.
        /// </summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "The exit must always finish: a fault here is 'not confirmed', logged as Critical, never an escape from the run's exit.")]
        private async Task<bool?> StopMountMotionAsync(string why, long fence) {
            try {
                return await StopMountMotionCoreAsync(why, fence).ConfigureAwait(false);
            } catch (Exception ex) {
                LogMountStopFailed(why, ex);
                return false;
            }
        }

        private async Task<bool?> StopMountMotionCoreAsync(string why, long fence) {
            var began = Stopwatch.GetTimestamp();
            var lastSent = began;
            // When the last stop round had gone out, on the clock the mount's readings are stamped on.
            var lastSentUtc = Clock.GetUtcNow();
            var sends = 0;
            var waitingLogged = false;
            (double RaDeg, double DecDeg, long At)? anchor = null;
            while (Stopwatch.GetElapsedTime(began) < StopVerifyWindow) {
                if (StopsFenced(fence)) {
                    LogMountStopFenced(why);
                    return null;
                }
                if (sends == 0) {
                    if (!await SendMountStopAsync(why, forceAbort: false, fence).ConfigureAwait(false)) {
                        if (!waitingLogged && !StopsFenced(fence)) {
                            waitingLogged = true;
                            LogMountStopWaitsForReconnect(why, StopVerifyWindow.TotalSeconds);
                        }
                        await Task.Delay(StopVerifyPoll, CancellationToken.None).ConfigureAwait(false);
                        continue;
                    }
                    sends = 1;
                    lastSent = Stopwatch.GetTimestamp();
                    lastSentUtc = Clock.GetUtcNow();
                    continue; // the window is checked again before the first read: a late reconnect adds no pass of its own
                }
                await Task.Delay(StopVerifyPoll, CancellationToken.None).ConfigureAwait(false);
                var reading = await ReadStillnessAsync(lastSentUtc).ConfigureAwait(false);
                var at = Stopwatch.GetTimestamp();
                if (reading is { Slewing: false } r) {
                    if (anchor is { } a && Math.Abs(Wrap180(r.RaDeg - a.RaDeg)) < StopStillDeg && Math.Abs(r.DecDeg - a.DecDeg) < StopStillDeg) {
                        if (Stopwatch.GetElapsedTime(a.At, at) >= StopVerifyStillFor && Stopwatch.GetElapsedTime(lastSent, at) >= StopVerifyNotBefore) {
                            EndMotion();
                            var seconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
                            LogMountStopConfirmed(why, sends, seconds);
                            return true;
                        }
                    } else {
                        anchor = (r.RaDeg, r.DecDeg, at); // moved (or the first reading): the still time starts again here
                    }
                } else {
                    anchor = null;
                }
                if (sends < StopSends && Stopwatch.GetElapsedTime(lastSent) >= StopResendAfter) {
                    // A mount that still reads as slewing, or gives no reading, gets the AbortSlew too. A round that could
                    // not reach the mount (it dropped off since) is not counted and goes out again on the next poll.
                    if (await SendMountStopAsync(why, forceAbort: reading is not { Slewing: false }, fence).ConfigureAwait(false)) {
                        sends++;
                        LogMountStopResent(why, sends);
                        lastSent = Stopwatch.GetTimestamp();
                        lastSentUtc = Clock.GetUtcNow();
                    }
                }
            }
            if (StopsFenced(fence)) {
                LogMountStopFenced(why);
                return null;
            }
            if (sends == 0) {
                LogMountStopNotConnected(why, StopVerifyWindow.TotalSeconds);
            } else {
                LogMountStopNotConfirmed(why, sends, StopVerifyWindow.TotalSeconds);
            }
            return false;
        }

        /// <summary>One round of stop commands, each bounded by <see cref="StopCommandTimeout"/> and none taking a token
        /// that could already be cancelled. True when the round reached a connected mount (a command that failed there
        /// still counts: the confirmation decides); false when the mount is not connected, or dropped off during the round
        /// (#2066: the caller tries again while the client reconnects), or the run's stops are fenced (nothing more goes
        /// out, not even the rest of this round).</summary>
        private async Task<bool> SendMountStopAsync(string why, bool forceAbort, long fence) {
            if (StopsFenced(fence) || !MountConnected()) {
                return false;
            }
            var motion = CurrentMotion;
            var abort = forceAbort || motion != MountMotion.None || SlewingByTheCache();
            LogMountStopSent(why, abort, motion);
            var reached = true;
            if (abort) {
                // Polar Align's own stops are counted from the reason the telescope service tagged each count with, so the
                // run-wide watch never reads them as a stop from outside, and never reads a stop from outside that landed
                // during this send as one of them (CountOwnAborts). A watch that reads between the count and the tally sees
                // one stop more than Polar Align's own; only a run that is already stopping (its generation moved) or a
                // Start's baseline (re-read at the launch, RunAbortBaseline) can read then.
                var countBefore = _motionControl.AbortEpoch;
                reached &= await StopCommandAsync("AbortSlew",
                    () => _motionControl.AbortSlewAsync(AbortReason, CancellationToken.None), fence).ConfigureAwait(false);
                CountOwnAborts(_motionControl, countBefore);
            }
            reached &= await StopCommandAsync("MoveAxis primary 0", () => RunAxisAsync(TelescopeAxes.Primary, 0), fence).ConfigureAwait(false);
            reached &= await StopCommandAsync("MoveAxis secondary 0", () => RunAxisAsync(TelescopeAxes.Secondary, 0), fence).ConfigureAwait(false);
            return reached;
        }

        /// <summary>One stop command. False when it was not sent because the run's stops are fenced, or it failed on a mount
        /// that is not connected (now); true otherwise.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Each stop command is best-effort: its fault is logged and the next command still goes out; the confirmation decides.")]
        private async Task<bool> StopCommandAsync(string command, Func<Task> send, long fence) {
            if (StopsFenced(fence)) {
                LogMountStopCommandFenced(command);
                return false;
            }
            try {
                await send().WaitAsync(StopCommandTimeout).ConfigureAwait(false);
            } catch (TimeoutException) {
                LogMountStopCommandTimedOut(command, StopCommandTimeout.TotalSeconds);
            } catch (Exception ex) {
                LogMountStopCommandFailed(command, ex);
                return MountConnected();
            }
            return true;
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A fault reading the cached connection is 'not connected': the stop is tried again on the next poll.")]
        private bool MountConnected() {
            try {
                return _mount.GetInfo() is { Connected: true };
            } catch (Exception) {
                return false;
            }
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "The cached reading only adds an AbortSlew; a fault reading it must not stop the stop.")]
        private bool SlewingByTheCache() {
            try {
                return _mount.GetInfo()?.Slewing == true;
            } catch (Exception) {
                return true;
            }
        }

        /// <summary>A fresh reading for the stop's confirmation: the driver's position now (never the cached one) and its
        /// Slewing now (the cache when the driver gives no answer). Null when there is no reading, or one that cannot be
        /// trusted (<see cref="UntrustedForStillness"/>: "not confirmed").</summary>
        /// <param name="stopSentUtc">When the stop the reading would confirm had gone out, on <see cref="Clock"/>.</param>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A failed read is 'not confirmed' by contract: the confirmation loop reads again or gives up.")]
        private async Task<(double RaDeg, double DecDeg, bool Slewing)?> ReadStillnessAsync(DateTimeOffset stopSentUtc) {
            try {
                var r = await _motionControl.ReadNowAsync(CancellationToken.None).WaitAsync(StopCommandTimeout).ConfigureAwait(false);
                if (UntrustedForStillness(r, stopSentUtc)) {
                    return null;
                }
                return (r.RaHours * 15.0, r.DecDegrees, r.Slewing ?? SlewingByTheCache());
            } catch (Exception) {
                return null;
            }
        }

        /// <summary>A reading the stop's confirmation must not count (a disconnected, failed or earlier read is "not
        /// confirmed", never "stopped"): none, the (0, 0) placeholder the daemon answers after a failed status poll, or one
        /// the mount sampled (<see cref="MountReading.ReadAtUtc"/>) before the stop it would confirm had gone out
        /// (<paramref name="stopSentUtc"/>). How long a reading took to arrive plays no part: on a link whose replies stall
        /// for seconds, a late reading of the stopped mount is as good as a prompt one.</summary>
        internal static bool UntrustedForStillness([NotNullWhen(false)] MountReading? r, DateTimeOffset stopSentUtc) =>
            r is null || (r.RaHours == 0.0 && r.DecDegrees == 0.0) || r.ReadAtUtc < stopSentUtc;

        /// <summary>Tracking at the end of a run: off, whatever ended it (the flow ends with the mount at Home, tracking
        /// off; a Stop, a sequence start, a failure or a shutdown never turns tracking back on). The one hand-back is
        /// Guiding taking the camera from an open run (<see cref="HandedToGuiding"/>): guiding needs the tracking the run
        /// found, unless another caller set tracking after the run's last write. A mount whose stop was not confirmed
        /// (<paramref name="mountStopped"/> not true) never gets tracking back: off.</summary>
        private void ApplyExitTracking(bool? priorTracking, bool handedToGuiding, bool? mountStopped, string why) {
            if (handedToGuiding && mountStopped == true && priorTracking is bool prior) {
                if (_trackingTouched) {
                    RestoreTrackingBestEffort(prior);
                }
                return;
            }
            TrackingOffBestEffort(why);
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "The exit's tracking write is best-effort on every exit path; a mount fault here must not mask the routine's outcome.")]
        private void TrackingOffBestEffort(string why) {
            try {
                _mount.SetTrackingEnabled(false);
                LogTrackingOffAtExit(why);
            } catch (Exception ex) {
                LogTrackingRestoreFailed(ex);
            } finally {
                _ownTrackingWrites = _mount.TrackingWriteCount;
            }
        }

        /// <summary>Daemon shutdown: a live routine stops the mount and turns tracking off before the telescope service
        /// (created before this service, so disposed after it) goes away. Bounded by <see cref="ShutdownGrace"/>.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Dispose must not throw; the failure is logged loudly.")]
        private void StopRunForShutdown() {
            bool live;
            lock (_gate) {
                _endRequests++; // a Start still taking the camera launches no run into the shutdown
                live = _liveRun is not null;
            }
            if (!live) {
                return;
            }
            var run = _runTask;
            try {
                var shutdown = Task.Run(async () => {
                    await StopRunFirstAsync("shutdown").ConfigureAwait(false);
                    if (run is not null) {
                        await AwaitRunUnwindAsync(run).ConfigureAwait(false);
                    }
                }, CancellationToken.None);
                if (!shutdown.Wait(ShutdownGrace)) {
                    LogShutdownStopTimedOut(ShutdownGrace.TotalSeconds);
                }
            } catch (Exception ex) {
                LogShutdownStopFailed(ex);
            }
        }

        [LoggerMessage(EventId = 4555, Level = LogLevel.Information, Message = "§45 polar-align ({Why}): stopping the mount — AbortSlew {Abort}, motion {Motion}, then MoveAxis 0 on both axes")]
        private partial void LogMountStopSent(string why, bool abort, MountMotion motion);

        [LoggerMessage(EventId = 4556, Level = LogLevel.Information, Message = "§45 polar-align ({Why}): the mount stopped (confirmed on fresh reads after {Sends} stop sends, {Seconds:F1} s)")]
        private partial void LogMountStopConfirmed(string why, int sends, double seconds);

        [LoggerMessage(EventId = 4557, Level = LogLevel.Warning, Message = "§45 polar-align ({Why}): the mount is not confirmed stopped yet — stop sent again ({Sends} of 3)")]
        private partial void LogMountStopResent(string why, int sends);

        [LoggerMessage(EventId = 4558, Level = LogLevel.Critical, Message = "§45 polar-align ({Why}): COULD NOT CONFIRM THE MOUNT STOPPED after {Sends} stop sends in {Seconds:F0} s — cut the mount's power and check it")]
        private partial void LogMountStopNotConfirmed(string why, int sends, double seconds);

        [LoggerMessage(EventId = 4559, Level = LogLevel.Critical, Message = "§45 polar-align ({Why}): the mount did not connect again within {Seconds:F0} s — Ara could not stop it or confirm it stopped; cut the mount's power and check it")]
        private partial void LogMountStopNotConnected(string why, double seconds);

        [LoggerMessage(Level = LogLevel.Warning, Message = "§45 polar-align ({Why}): the mount is not connected — its stop goes out as soon as it connects again (for up to {Seconds:F0} s)")]
        private partial void LogMountStopWaitsForReconnect(string why, double seconds);

        [LoggerMessage(Level = LogLevel.Warning, Message = "§45 polar-align ({Why}): the run's stops are fenced (a takeover owns the mount, or the run was abandoned) — no further stop, no confirmation")]
        private partial void LogMountStopFenced(string why);

        [LoggerMessage(Level = LogLevel.Warning, Message = "§45 polar-align: the stop command {Command} was not sent — the run's stops are fenced")]
        private partial void LogMountStopCommandFenced(string command);

        [LoggerMessage(Level = LogLevel.Warning, Message = "§45 polar-align: {By} waited {Seconds:F0} s for polar alignment to end — its stops are fenced so none can land on what {By} does next")]
        private partial void LogTakeoverFenced(string by, double seconds);

        [LoggerMessage(Level = LogLevel.Warning, Message = "§45 polar-align: the stuck run was abandoned — its stops are fenced so a late one cannot land on what the mount does next")]
        private partial void LogAbandonedRunFenced();

        [LoggerMessage(EventId = 4560, Level = LogLevel.Warning, Message = "§45 polar-align: the stop command {Command} failed")]
        private partial void LogMountStopCommandFailed(string command, Exception exception);

        [LoggerMessage(EventId = 4561, Level = LogLevel.Warning, Message = "§45 polar-align: the stop command {Command} did not answer within {Seconds:F0} s (the next command goes out)")]
        private partial void LogMountStopCommandTimedOut(string command, double seconds);

        [LoggerMessage(EventId = 4562, Level = LogLevel.Information, Message = "§45 polar-align ({Why}): tracking off at the end of the run")]
        private partial void LogTrackingOffAtExit(string why);

        [LoggerMessage(EventId = 4563, Level = LogLevel.Critical, Message = "§45 polar-align: the routine did not stop the mount within {Seconds:F0} s of daemon shutdown")]
        private partial void LogShutdownStopTimedOut(double seconds);

        [LoggerMessage(EventId = 4564, Level = LogLevel.Critical, Message = "§45 polar-align: stopping the mount at daemon shutdown failed")]
        private partial void LogShutdownStopFailed(Exception exception);

        [LoggerMessage(EventId = 4566, Level = LogLevel.Critical, Message = "§45 polar-align ({Why}): stopping the mount failed — cut the mount's power and check it")]
        private partial void LogMountStopFailed(string why, Exception exception);

        [LoggerMessage(EventId = 4567, Level = LogLevel.Information, Message = "§45 polar-align: an end request landed while the routine was starting — the run was not launched and the mount was not moved")]
        private partial void LogStartStoppedBeforeRun();

        [LoggerMessage(EventId = 4565, Level = LogLevel.Information, Message = "§45 polar-align: a newer run owns the mount — this superseded run leaves the stop and tracking to it")]
        private partial void LogExitLeftToSuccessor();

        [LoggerMessage(Level = LogLevel.Warning, Message = "§45 polar-align: the mount was stopped from outside the routine ({By}) — the run ends and starts no further move")]
        private partial void LogStoppedFromOutside(string by);

        [LoggerMessage(Level = LogLevel.Warning, Message = "§45 polar-align: {By} takes the mount over — the run ends; its exit leaves the stop and its check to {By}")]
        private partial void LogTakenOver(string by);

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align ({Why}): the mount was taken over — no stop of the routine's own, no tracking write")]
        private partial void LogExitLeftToTakeover(string why);

        [LoggerMessage(Level = LogLevel.Critical, Message = "§45 polar-align: the mount's position is now unverified ({Why}) — GoTo, Park and polar alignment are refused until a Home search ends at Home or Home is confirmed (#2066)")]
        private partial void LogPoseLeftUnverified(string why);

        [LoggerMessage(Level = LogLevel.Critical, Message = "§45 polar-align: could not mark the mount's position unverified")]
        private partial void LogPoseFlagFailed(Exception exception);
    }
}
