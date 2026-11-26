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
using OpenAstroAra.Server.Contracts.Refusals;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TelescopeAxes = OpenAstroAra.Core.Enums.TelescopeAxes;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// The RA turns of a measurement, NINA's Three Point Polar Alignment way: the RA motor alone (MoveAxis on the primary
    /// axis, never a GoTo), so the firmware picks no pier side and the Dec axis cannot move. A turn is timed, not read:
    /// the motor runs at the fastest rate the mount offers at or below <see cref="AxisRateDegPerSec"/> for the time the
    /// turn takes at that rate on <see cref="Clock"/>, then it is stopped and the mount is given <see cref="AxisSettle"/>
    /// to stand still. No mount reading is taken during or after a turn. Every measurement starts from the mount's Home
    /// position (tube at the pole, counterweights down), where the two turns are safe on any equatorial mount, and the
    /// plate solves say what the mount did (<see cref="FitThreePointOrFail"/>).
    /// </summary>
    public sealed partial class PolarAlignService {

        /// <summary>The motor speed a turn asks for (°/s, NINA's 3°/s), matched to the fastest rate the mount offers at or
        /// below it (<see cref="MatchTurnRate"/>), never rounded up.</summary>
        internal double AxisRateDegPerSec { get; set; } = 3.0;

        /// <summary>The motor runs on a lease: a daemon that stops renewing it (hung, crashed run) stops the axis.</summary>
        private static readonly TimeSpan AxisMoveLease = TimeSpan.FromSeconds(3);

        /// <summary>How often a turn renews the motor's lease.</summary>
        internal TimeSpan AxisLeaseRenew { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>A renewal is left out once the lease already granted outlasts the turn's end by this much: the stop is
        /// then never queued behind a renewal that stalls, and the lease does not lapse as the stop goes out.</summary>
        private static readonly TimeSpan AxisLeaseMargin = TimeSpan.FromMilliseconds(500);

        /// <summary>How long the mount is given to stand still after a turn's stop, before the next frame.</summary>
        internal TimeSpan AxisSettle { get; set; } = TimeSpan.FromSeconds(2.5);

        /// <summary>The clock a turn is timed on (its monotonic timestamp and its timers), the one the wait for the mount's
        /// Find Home is paced on, and the one a stop's send is stamped on, against the sample times of the readings that
        /// confirm it (<see cref="UntrustedForStillness"/>). A test sets its own.</summary>
        internal TimeProvider Clock { get; set; } = TimeProvider.System;

        /// <summary>The rate a turn runs the RA motor at (°/s, unsigned): the fastest the mount offers at or below
        /// <see cref="AxisRateDegPerSec"/>, never above it. A mount that offers none fails the run with <c>slew_failed</c>
        /// before the motor starts.</summary>
        private double MatchTurnRate() {
            var requested = AxisRateDegPerSec;
            var matched = _motionControl.MatchMoveAxisRate((int)TelescopeAxes.Primary, requested);
            if (matched is not double rate || !double.IsFinite(rate) || Math.Abs(rate) <= 0) {
                LogNoRateAtOrBelow(requested);
                throw new RoutineFailedException("slew_failed", string.Create(CultureInfo.InvariantCulture,
                    $"the mount offers no motor speed at or below {requested:0.##}°/s for the turn (Polar Align never runs a motor faster than it asks) — check the mount's connection and its rates, then start again"));
            }
            var magnitude = Math.Min(Math.Abs(rate), Math.Abs(requested)); // never above the request, whatever the seam says
            if (magnitude < Math.Abs(requested)) {
                LogRateMatched(requested, magnitude);
            }
            return magnitude;
        }

        /// <summary>Turn the RA axis by <paramref name="signedDeltaDeg"/> (+ a positive motor rate, east by ASCOM's
        /// convention): run the RA motor at the matched rate for degrees / rate seconds, renewing its lease every
        /// <see cref="AxisLeaseRenew"/> until the lease granted covers the rest of the turn, send the stop, and wait
        /// <see cref="AxisSettle"/>. The motion is marked from before
        /// the motor starts until the settle is over, so a Stop meanwhile sends AbortSlew. A stop from outside (Stop Mount,
        /// Emergency Stop, a park) ends the run and gets no renewal; every exit stops the motor, unless a takeover fenced
        /// the run's stops; a motor command the mount refuses fails the run with <c>slew_failed</c>.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A refused motor command is this routine's slew_failed with a recovery sentence, never internal_error.")]
        private async Task TurnRaAsync(double signedDeltaDeg, int gen, CancellationToken ct) {
            // A mount fault's recovery may have dropped tracking since the measurement turned it on.
            await KeepTrackingOnForTurnAsync(gen, ct).ConfigureAwait(false);
            var degrees = Math.Abs(signedDeltaDeg);
            var rate = Math.CopySign(MatchTurnRate(), signedDeltaDeg);
            var runFor = TimeSpan.FromSeconds(degrees / Math.Abs(rate));
            // The run's stop count from its Start, not a fresh one per turn: a stop between two turns (during a solve) is
            // a stop too, and the telescope service refuses a motor command that would start under a count read before it.
            var abortEpoch = RunAbortEpoch(gen);
            var stopped = false;
            LogTurn(degrees, rate, runFor.TotalSeconds);
            BeginMotion(MountMotion.AxisMove, gen, ct);
            try {
                // A lease lasts at least AxisMoveLease from before its command was sent.
                var leased = Clock.GetTimestamp();
                await RunAxisAsync(TelescopeAxes.Primary, rate, abortEpoch).ConfigureAwait(false);
                // Timed from when the mount took the motor command, on the monotonic clock: a wall clock that steps (chrony,
                // a UTCDate write) neither stretches nor cuts the turn.
                var began = Clock.GetTimestamp();
                while (true) {
                    var left = runFor - Clock.GetElapsedTime(began);
                    if (left <= TimeSpan.Zero) {
                        break;
                    }
                    await Task.Delay(TurnWait(left), Clock, ct).ConfigureAwait(false);
                    ThrowIfRunEnded(gen);
                    left = runFor - Clock.GetElapsedTime(began);
                    if (left <= TimeSpan.Zero) {
                        break;
                    }
                    if (AxisMoveLease - Clock.GetElapsedTime(leased) >= left + AxisLeaseMargin) {
                        continue; // the lease already granted outlasts the turn: no renewal for the stop to wait behind
                    }
                    leased = Clock.GetTimestamp();
                    await RunAxisAsync(TelescopeAxes.Primary, rate, abortEpoch).ConfigureAwait(false); // the lease renewed
                }
                if (RunStopsFenced(gen)) {
                    // A takeover gave up waiting for this run and fenced it: the mount is its own, no stop from here.
                    throw new OperationCanceledException("the run was fenced as the turn ended");
                }
                await RunAxisAsync(TelescopeAxes.Primary, 0).ConfigureAwait(false);
                stopped = true;
                await Task.Delay(AxisSettle, Clock, ct).ConfigureAwait(false);
                ThrowIfRunEnded(gen);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception) when (!IsCurrent(gen)) {
                // A Stop (its AbortSlew refuses the next renewal, or ends the move) is a stop, not a failure.
                throw new OperationCanceledException("the run was stopped while the mount turned");
            } catch (RoutineFailedException) {
                throw;
            } catch (Exception) when (AbortedSince(abortEpoch)) {
                // A refused motor command after a stop: from outside, the run-wide watch's failure. Only Polar Align's own
                // stop moved the count (a superseded run's late resend): the mount still refused the move.
                ThrowIfStoppedFromOutside(gen);
                throw new RoutineFailedException("slew_failed",
                    "the mount refused the motor move right after a stop — check it is still, then start again");
            } catch (RefusalException ex) {
                // The mount layer's own refusal (an unverified position): its sentence says what to do.
                LogTurnRefused(ex);
                throw new RoutineFailedException("slew_failed", ex.Message);
            } catch (Exception ex) {
                // The user gets the recovery; the driver's own text goes to the log.
                LogTurnRefused(ex);
                throw new RoutineFailedException("slew_failed",
                    "the mount refused to turn its motor for the measurement — check it is unparked and free to move, then start again");
            } finally {
                // Not once a takeover or an end gave up on this run and fenced it: a late MoveAxis 0 would land on the park
                // or the move that came after.
                if (!stopped && !RunStopsFenced(gen)) {
                    await StopAxisBestEffortAsync(TelescopeAxes.Primary).ConfigureAwait(false);
                }
            }
            // Ended normally. A turn that threw keeps the motion marked: the run's exit stops the mount again.
            EndMotion();
            LogTurnDone(degrees);
        }

        /// <summary>The next wait of a turn with <paramref name="left"/> to run: to its end or to the next lease renewal,
        /// whichever comes first, in whole milliseconds rounded up (a timer waits whole milliseconds: a wait rounded down
        /// to none would never end the turn).</summary>
        private TimeSpan TurnWait(TimeSpan left) {
            var wait = left < AxisLeaseRenew ? left : AxisLeaseRenew;
            return TimeSpan.FromMilliseconds(Math.Max(1.0, Math.Ceiling(wait.TotalMilliseconds)));
        }

        /// <summary>A turn goes on only for the run that started it, and only while nothing outside stopped the mount.</summary>
        private void ThrowIfRunEnded(int gen) {
            if (!IsCurrent(gen)) {
                throw new OperationCanceledException("the run was superseded while the mount turned");
            }
            ThrowIfStoppedFromOutside(gen);
        }

        /// <summary>The daemon's leased motor command (the same one the app's direction pad uses), through the seam. Never
        /// the mediator's <c>MoveAxis</c>: <c>TelescopeService</c>'s is a no-op stub.</summary>
        private Task RunAxisAsync(TelescopeAxes axis, double rate, long? abortEpoch = null) =>
            _motionControl.MoveAxisAsync((int)axis, rate, rate == 0 ? null : (int)AxisMoveLease.TotalMilliseconds, rate == 0 ? null : abortEpoch, CancellationToken.None);

        private bool AbortedSince(long? abortEpoch) =>
            abortEpoch is long since && _motionControl.AbortEpoch != since;

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "The stop is best-effort on a failed turn's exit; its fault must not mask the turn's outcome, and the lease stops the axis anyway.")]
        private async Task StopAxisBestEffortAsync(TelescopeAxes axis) {
            try {
                await RunAxisAsync(axis, 0).ConfigureAwait(false);
            } catch (Exception ex) {
                LogAxisStopFailed(axis, ex);
            }
        }

        [LoggerMessage(EventId = 4550, Level = LogLevel.Information, Message = "§45 polar-align: RA motor turn of {Degrees:F1}° at {Rate:F3}°/s for {Seconds:F2} s (timed, no GoTo)")]
        private partial void LogTurn(double degrees, double rate, double seconds);

        [LoggerMessage(EventId = 4552, Level = LogLevel.Information, Message = "§45 polar-align: RA motor turn of {Degrees:F1}° stopped and settled")]
        private partial void LogTurnDone(double degrees);

        [LoggerMessage(EventId = 4554, Level = LogLevel.Warning, Message = "§45 polar-align: the mount refused the RA motor turn")]
        private partial void LogTurnRefused(Exception exception);

        [LoggerMessage(EventId = 4553, Level = LogLevel.Warning, Message = "§45 polar-align: stopping the {Axis} motor failed (its lease stops it)")]
        private partial void LogAxisStopFailed(TelescopeAxes axis, Exception exception);

        [LoggerMessage(EventId = 4620, Level = LogLevel.Warning, Message = "§45 polar-align: the mount offers no RA motor rate at or below {Requested:F2}°/s — no turn (slew_failed)")]
        private partial void LogNoRateAtOrBelow(double requested);

        [LoggerMessage(EventId = 4621, Level = LogLevel.Information, Message = "§45 polar-align: RA turn rate {Requested:F2}°/s matched to the mount's {Matched:F3}°/s (at or below, never above)")]
        private partial void LogRateMatched(double requested, double matched);
    }
}
