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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// What a run does by itself once the user taps Done in the adjust loop: the adjust readout is recorded, the mount is
    /// stopped and confirmed, it goes Home (state <c>homing</c>), the verify measurement is made (<c>checking</c>), and it
    /// goes Home again (<c>homing</c>) before the run ends <c>checked</c>. Home is the mount's own Find Home, sent through
    /// the mediator like every other Find Home of the daemon. A mount that cannot find Home makes no Home trip: its
    /// verify measurement turns the opposite way, which brings the tube back to where the run started. A Stop works at
    /// every moment of the chain; a Find Home that fails ends the run <c>failed</c> with <see cref="HomeFailed"/>.
    /// </summary>
    public sealed partial class PolarAlignService {

        /// <summary>The <c>polar_align.error</c> reason when the mount refused Find Home or did not finish it.</summary>
        internal const string HomeFailed = "home_failed";

        internal const string HomeFailedMessage =
            "the mount did not go Home: it refused Find Home, or did not finish it. Ara stopped the mount where it is — " +
            "check the mount (unparked, free to turn, Home works from the mount controls), send it Home, then start again";

        /// <summary>How often the wait for the mount's Find Home looks for a stop from outside and renews the camera's
        /// lease.</summary>
        internal TimeSpan HomeWatchPoll { get; set; } = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Done: the user is satisfied with the adjust loop's readout. Answers at once; the run ends its loop and goes on
        /// by itself (<see cref="BeginVerifyAsync"/>, <see cref="VerifyAfterDoneAsync"/>). Acts only while the run adjusts
        /// (<c>adjusting</c> or <c>paused</c>): at any other moment, and with no run active, it is a no-op accept.
        /// </summary>
        public Task<OperationAcceptedDto> CompleteAsync(string? idempotencyKey, CancellationToken ct) {
            lock (_gate) {
                if (_active && _state is "adjusting" or "paused") {
                    _liveRun?.End.RequestDone();
                }
            }
            return Task.FromResult(Accepted("polar-align.complete", idempotencyKey));
        }

        /// <summary>What the chain after Done goes by: whether the mount finds Home by itself, and the adjust loop's last
        /// readout, which the verdict is compared with.</summary>
        private readonly record struct DoneAt(bool FindsHome, (double AltArcmin, double AzArcmin)? AdjustReadout);

        /// <summary>
        /// Done, in one step: the adjust readout becomes the last result and the §45.13 row with outcome <c>complete</c>,
        /// and the run enters its verify stage — <c>homing</c> on a mount that finds Home by itself, <c>checking</c> on
        /// one that does not. A run that a Stop ended meanwhile records nothing.
        /// </summary>
        private async Task<DoneAt> BeginVerifyAsync(int gen) {
            var findsHome = _mount.GetInfo().CanFindHome;
            DoneAt done;
            PolarAlignmentRecord row;
            lock (_gate) {
                if (_generation != gen) {
                    throw new OperationCanceledException("the run was stopped as Done landed");
                }
                RecordLastResultLocked();
                row = SessionRowLocked("complete");
                done = new DoneAt(findsHome,
                    _altErrorArcmin is double alt && _azErrorArcmin is double az ? (alt, az) : null);
                BeginMeasurementLocked(PolarAlignStages.Verify, findsHome ? "homing" : "checking");
            }
            LogDone(findsHome);
            await WriteSessionRowBestEffortAsync(row, CancellationToken.None).ConfigureAwait(false);
            await PublishStageAsync(gen).ConfigureAwait(false);
            return done;
        }

        /// <summary>
        /// The chain after Done, once the mount is stopped: Home, the verify measurement, Home. On a mount that cannot find
        /// Home: the verify measurement alone, turned the opposite way to the align measurement. The verdict is on the
        /// status, and on the <c>polar_align.stage</c> event of the last <c>homing</c>, before the mount is back Home.
        /// </summary>
        private async Task<PolarAlignCheckResultDto> VerifyAfterDoneAsync(
                IGuideCaptureLink guiderClient, IPolarAlignMainCameraSolver? mainCamera, string workDir, SiteSettingsDto site,
                PolarAlignSettingsDto settings, DoneAt done, Func<Task> renewLeaseIfDue, RunEnd end, int gen, CancellationToken ct) {
            if (done.FindsHome) {
                await GoHomeAsync(renewLeaseIfDue, end, gen, ct).ConfigureAwait(false);
                await SetChainStateAsync(gen, "checking").ConfigureAwait(false);
            }
            // Done's stop may have sent AbortSlew: the verify turns start under a reading taken after it.
            RebaselineAbortWatch(gen);
            if (mainCamera is null) {
                // The adjust loop's last frame was abandoned where Done found it.
                await WaitForAbandonedCaptureAsync(guiderClient, ct).ConfigureAwait(false);
            }
            await renewLeaseIfDue().ConfigureAwait(false);
            var turn = SignedTurnDeg(settings, site);
            var verdict = await RunVerifyStageAsync(guiderClient, mainCamera, workDir, site, settings,
                done.FindsHome ? turn : -turn, done.AdjustReadout, gen, ct).ConfigureAwait(false);
            if (done.FindsHome) {
                await SetChainStateAsync(gen, "homing").ConfigureAwait(false);
                await GoHomeAsync(renewLeaseIfDue, end, gen, ct).ConfigureAwait(false);
            }
            return verdict;
        }

        /// <summary>
        /// Send the mount Home with its own Find Home and wait until it is there. The motion is marked from before the
        /// command goes out, so a Stop meanwhile sends AbortSlew and the telescope endpoints refuse other moves
        /// (<see cref="IsMovingMount"/>). The run lends its hold of the mount's motion owner to the Find Home, which the
        /// telescope service refuses while a routine holds the mount, and takes it again once the mount is Home. The run's
        /// token ends the wait (Stop); a stop from outside (Stop Mount, Emergency Stop) ends it too, and the run with it.
        /// A Find Home the mount refuses or does not finish fails the run with <see cref="HomeFailed"/>.
        /// </summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Any fault of the mount's Find Home is this routine's home_failed with a recovery sentence, never internal_error.")]
        private async Task GoHomeAsync(Func<Task> renewLeaseIfDue, RunEnd end, int gen, CancellationToken ct) {
            await renewLeaseIfDue().ConfigureAwait(false);
            BeginMotion(MountMotion.Home, gen, ct);
            LogGoingHome();
            end.ReleaseHold();
            bool atHome;
            using (var homing = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                try {
                    var find = _mount.FindHome(new Progress<ApplicationStatus>(), homing.Token);
                    // Left behind by a stop from outside: its own end is not this run's to report.
                    _ = find.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    while (!find.IsCompleted) {
                        await Task.WhenAny(find, Task.Delay(HomeWatchPoll, Clock, homing.Token)).ConfigureAwait(false);
                        ThrowIfRunEnded(gen);
                        await renewLeaseIfDue().ConfigureAwait(false);
                    }
                    atHome = await find.ConfigureAwait(false);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested || !IsCurrent(gen)) {
                    throw;
                } catch (RoutineFailedException) {
                    throw;
                } catch (Exception ex) {
                    LogHomeFailed(ex);
                    atHome = false;
                } finally {
                    await homing.CancelAsync().ConfigureAwait(false);
                }
            }
            if (!atHome) {
                throw new RoutineFailedException(HomeFailed, HomeFailedMessage);
            }
            ThrowIfRunEnded(gen);
            lock (_gate) {
                // With the generation: a run that was stopped meanwhile takes nothing, and never a newer run's hold.
                if (_generation != gen) {
                    throw new OperationCanceledException("the run was stopped as the mount reached Home");
                }
                try {
                    end.Hold(AcquireMountForRun());
                } catch (RefusalException ex) {
                    throw new RoutineFailedException(HomeFailed, ex.Message);
                }
            }
            EndMotion();
            LogAtHome();
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: Done — the adjust readout is recorded; the mount is stopped, then verified (finds Home by itself: {FindsHome})")]
        private partial void LogDone(bool findsHome);

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: sending the mount Home (its own Find Home)")]
        private partial void LogGoingHome();

        [LoggerMessage(Level = LogLevel.Information, Message = "§45 polar-align: the mount is Home")]
        private partial void LogAtHome();

        [LoggerMessage(Level = LogLevel.Warning, Message = "§45 polar-align: the mount's Find Home failed — the run ends (home_failed)")]
        private partial void LogHomeFailed(Exception exception);
    }
}
