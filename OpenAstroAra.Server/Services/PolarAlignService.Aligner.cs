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
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>The Polar Aligner states and stop causes the routine reports (#1971).</summary>
    public static class PolarAlignCorrectionStates {
        public const string Manual = "manual";
        public const string Probing = "probing";
        public const string Moving = "moving";
        public const string Settling = "settling";
        public const string Measuring = "measuring";
        public const string Converged = "converged";
        public const string Stopped = "stopped";
    }

    public static class PolarAlignCorrectionCauses {
        public const string WrongDirection = "wrong_direction";
        public const string WrongScale = "wrong_scale";
        public const string DeviceFault = "device_fault";
        public const string NotCalibrated = "not_calibrated";
        public const string DeviceRefused = "device_refused";
        public const string Diverging = "diverging";
        public const string Timeout = "timeout";
        public const string LinkLost = "link_lost";
        public const string MountTracking = "mount_tracking";
        public const string MountSlewing = "mount_slewing";
        public const string StoppedByUser = "stopped_by_user";
        // #2069 (ARA-19)
        /// <summary>A Stop the device did not confirm (both axes still) within the verified stop's window: a Stop is
        /// owed, sent again at each measurement and at the end of the run, and nothing moves until one is confirmed.</summary>
        public const string StopNotConfirmed = "stop_not_confirmed";
        /// <summary>A Stop owed from before the run (the Stop route, Emergency Stop, an earlier run's end) was confirmed
        /// at a later measurement of this run: the device is still, and the run waits for the person's Resume before it
        /// moves it again.</summary>
        public const string StopConfirmed = "stop_confirmed";
        /// <summary>The next move would take an axis past the run's travel cap (<c>axis</c> names it).</summary>
        public const string TravelLimit = "travel_limit";
        /// <summary>The run has sent as many moves as one run may (<see cref="PolarAlignService.AlignerMoveLimit"/>).</summary>
        public const string MoveLimit = "move_limit";
    }

    // #1971 — the Polar Aligner in the live stages: after each measurement the routine moves the
    // motors (one probe move per axis, then a fraction of the error), waits for them, and lets the
    // next frame measure the result. Any way out stops the motors and leaves the routine running by
    // hand. The numbers (probe size, fraction, scale check) follow N.I.N.A.'s Polar Alignment
    // Correctors plugin, so one device behaves the same under both clients.
    public sealed partial class PolarAlignService : IPolarAlignerDriver {

        // The routine drives the Polar Aligner from its first measurement with one until it is stopped
        // or the run ends; "by hand" is not driving.
        bool IPolarAlignerDriver.DrivesPolarAligner {
            get {
                lock (_gate) {
                    return _active && _alignerRun is { Stopped: false }
                        && _aligner is { State: not PolarAlignCorrectionStates.Manual };
                }
            }
        }

        async Task IPolarAlignerDriver.StopPolarAlignerAsync() {
            try {
                await StopAlignerAsync(null, CancellationToken.None).ConfigureAwait(false);
            } catch (InvalidOperationException) {
                // The run ended between the question and the stop: its own exit stopped the motors.
            }
        }

        internal const double AlignerGain = 0.8;
        internal const double AlignerProbeArcmin = 2.0;
        private const double AlignerGrowthMarginArcmin = 0.1;
        private const int AlignerGrowthLimit = 2;

        internal TimeSpan AlignerPoll { get; set; } = TimeSpan.FromMilliseconds(250);
        internal TimeSpan AlignerMoveTimeout { get; set; } = TimeSpan.FromSeconds(60);
        /// <summary>#2069 — how long a verified stop waits for both axes to read still (<see cref="PolarAlignerVerifiedStop"/>).</summary>
        internal TimeSpan AlignerStopWindow { get; set; } = PolarAlignerVerifiedStop.DefaultWindow;

        // #2069 (ARA-19) — the run's bounds. Net travel per axis (the signed sum of the moves sent) stays within
        // max(3 × that axis's first measured offset + 30′, 10′); a run sends 40 moves at most; a device that cannot
        // stop a move once it is sent moves at most 10′ at a time.
        internal const int AlignerMoveLimit = 40;
        internal const double AlignerTravelErrorFactor = 3.0;
        internal const double AlignerTravelMarginArcmin = 30.0;
        internal const double AlignerTravelFloorArcmin = 10.0;
        internal const double AlignerNoStopMoveCapArcmin = 10.0;

        private readonly IPolarAlignerSource? _alignerSource;

        // One per run. Stopped, RearmRequested, StopOwed and UserStop are shared with the stop and resume
        // commands and are read and written under _gate. Everything else belongs to the run task.
        private sealed class AlignerRun(double latitudeDeg) : IDisposable {
            public double LatitudeDeg { get; } = latitudeDeg;
            public IPolarAligner? Device;
            public PolarAlignerInfo? Info;
            public bool Undrivable;
            public bool Engaged;      // a move went out: every exit owes the device a Stop
            public bool StopOwed;     // a Stop was not confirmed (link lost, or an axis still moving): retried each measurement
            public bool Stopped;
            public (string Cause, string? Axis, string? DeviceText)? LastStop; // why Stopped was set, for the report
            public bool RearmRequested; // a resume: the run task starts again from a new device read and probe
            public bool ProbeDone;
            public (double Az, double Alt)? BeforeProbe;
            public double? TotalBeforeMove;
            public int Growth;
            public CancellationTokenSource UserStop = new();
            // #2069 — run task. Kept across a resume: the axes do not forget where the run took them.
            public (double Az, double Alt)? FirstOffset; // each axis's offset from the pole at the first measurement (device arcmin)
            public double NetAz, NetAlt;                  // signed sum of the moves sent on each axis
            public int AzLastSign, AltLastSign;           // the sign of the axis's last move, 0 before its first (a first move,
                                                          // or one that reverses the last, may lose some to backlash)

            // Run task. A resume re-reads the device: it may be another one, or recalibrated.
            public void Rearm() {
                ProbeDone = false;
                BeforeProbe = null;
                TotalBeforeMove = null;
                Growth = 0;
                Device = null;
                Info = null;
                Undrivable = false;
            }

            public void Dispose() => UserStop.Dispose();
        }

        private sealed class AlignerStopException : Exception {
            public AlignerStopException() { }
            public AlignerStopException(string message) : base(message) { }
            public AlignerStopException(string message, Exception innerException) : base(message, innerException) { }
            public AlignerStopException(string cause, string? deviceText, string? axis = null) : base(cause) {
                Cause = cause;
                DeviceText = deviceText;
                Axis = axis;
            }
            public string Cause { get; } = PolarAlignCorrectionCauses.DeviceRefused;
            public string? DeviceText { get; }
            public string? Axis { get; }
        }

        private AlignerRun? _alignerRun;          // guarded by _gate
        private PolarAlignCorrectionDto? _aligner;   // guarded by _gate: what status and the event report
        private int _alignerMoves;                  // guarded by _gate

        /// <summary>The person stops the motors; the routine carries on by hand.</summary>
        public async Task<OperationAcceptedDto> StopAlignerAsync(string? idempotencyKey, CancellationToken ct) {
            AlignerRun run;
            CancellationTokenSource userStop;
            int gen;
            lock (_gate) {
                // A run reports an aligner state only once it has found one Ara can drive.
                run = _active && _aligner is not null && _alignerRun is { } r ? r
                    : throw new InvalidOperationException("polar alignment is not running with a Polar Aligner");
                gen = _generation;
                if (!run.Stopped) {
                    run.Stopped = true;
                    run.LastStop = (PolarAlignCorrectionCauses.StoppedByUser, null, null);
                    _aligner = new PolarAlignCorrectionDto(PolarAlignCorrectionStates.Stopped,
                        Cause: PolarAlignCorrectionCauses.StoppedByUser, Moves: _alignerMoves);
                }
                userStop = run.UserStop;
            }
            try {
                // Ends a wait on a move in flight; the run task then stops the device too.
                await userStop.CancelAsync().ConfigureAwait(false);
            } catch (ObjectDisposedException) {
                // A resume or the end of the run replaced it: nothing is waiting on it any more.
            }
            // #2069 — to the device that answers now, as the run's owed resend does: the run may not have read the device
            // yet (a Stop owed from before it is resent before that read), and after a lost link and a reconnect its own
            // object is the dead connection. Off the caller's thread: the emergency stop bounds a rung only once this call
            // has handed back a task, and a device on a bad link answers after its HTTP timeout.
            if ((_alignerSource?.Current() ?? run.Device) is { } device) {
                await Task.Run(() => StopDeviceVerifiedAsync(run, device), CancellationToken.None).ConfigureAwait(false);
            }
            // Read before _gate: the source takes the Switch, mount and aligner locks, never under the routine's.
            var sourceOwed = _alignerSource?.StopOwed == true;
            bool confirmedLate;
            lock (_gate) {
                confirmedLate = NoteOwedStopConfirmedLocked(run, sourceOwed);
            }
            LogAlignerStopped(confirmedLate ? PolarAlignCorrectionCauses.StopConfirmed : PolarAlignCorrectionCauses.StoppedByUser, null);
            // #2069 — an unconfirmed stop is reported as such (stop_not_confirmed), not as the person's stop.
            if (!await ReportStopAsync(gen, run).ConfigureAwait(false)) {
                await PublishAlignerAsync(gen).ConfigureAwait(false);
            }
            return Accepted("polar-align.aligner-stop", idempotencyKey);
        }

        /// <summary>After a stop: correct with the motors again, starting from a new probe.</summary>
        public async Task<OperationAcceptedDto> ResumeAlignerAsync(string? idempotencyKey, CancellationToken ct) {
            int gen;
            // Read before _gate: the source takes the Switch, mount and aligner locks, never under the routine's.
            var sourceOwed = _alignerSource?.StopOwed == true;
            lock (_gate) {
                if (!_active || _alignerRun is not { Stopped: true } run) {
                    throw new InvalidOperationException("the Polar Aligner is not stopped");
                }
                // #2069 — no move while a Stop is owed: the device may still be moving.
                if (run.StopOwed || sourceOwed) {
                    throw new InvalidOperationException(
                        "the Polar Aligner's Stop is not confirmed yet; Ara sends it again at each measurement. Resume once it reports still");
                }
                gen = _generation;
                run.Stopped = false;
                run.RearmRequested = true;
                run.UserStop.Dispose();
                run.UserStop = new CancellationTokenSource();
                _aligner = new PolarAlignCorrectionDto(PolarAlignCorrectionStates.Measuring, Moves: _alignerMoves);
            }
            await PublishAlignerAsync(gen).ConfigureAwait(false);
            return Accepted("polar-align.aligner-resume", idempotencyKey);
        }

        // Run task, at the start of a run.
        private AlignerRun? BeginAlignerRun(double latitudeDeg) {
            lock (_gate) {
                _alignerRun = _alignerSource is null ? null : new AlignerRun(latitudeDeg);
                _aligner = null;
                _alignerMoves = 0;
                return _alignerRun;
            }
        }

        // Run task, at each seed: the readout starts again from a new fit, so a probe still waiting
        // for its measurement and the before-and-after of the last move mean nothing any more.
        private void ResetAlignerBaseline() {
            lock (_gate) {
                if (_alignerRun is { } run) {
                    run.BeforeProbe = null;
                    run.TotalBeforeMove = null;
                    run.Growth = 0;
                }
            }
        }

        /// <summary>#2069 — the <c>polar_align.error</c> reason when the run's end could not confirm the Polar Aligner
        /// stopped (the mount's own is <see cref="MountDidNotStop"/>, which wins when both are unconfirmed).</summary>
        internal const string AlignerDidNotStop = "aligner_did_not_stop";

        internal const string AlignerCutPowerSentence =
            "Ara could not confirm that the Polar Aligner stopped. Cut the Polar Aligner's power if it still moves; Ara sends it Stop again before it moves it next.";

        // #2069 — the run (its RunEnd) whose end could not confirm the Polar Aligner stopped, for the stop path to report
        // once the run has unwound, and whether the run reported it itself. Guarded by _gate.
        private (RunEnd End, bool ToldByRun)? _alignerNotStoppedEnd;

        // Run task, at Done and on every exit: a run that moved the motors (or owes a Stop) stops them and verifies it (#2069). A
        // newer run's state is not touched. Started off the run task's thread so the mount's exit is not held behind it.
        // True when the device is confirmed still, or nothing was owed it.
        private Task<bool> EndAlignerRunAsync(AlignerRun? run) =>
            run is null ? Task.FromResult(true) : Task.Run(() => EndAlignerRunCoreAsync(run), CancellationToken.None);

        private async Task<bool> EndAlignerRunCoreAsync(AlignerRun run) {
            bool owed;
            lock (_gate) {
                owed = run.StopOwed;
            }
            // A Stop owed from before the run (the Stop route, Emergency Stop, an earlier run's end) is sent again here
            // too: a run does not go on to its verify measurement, or end stopped, on a device that may still move.
            owed |= _alignerSource?.StopOwed == true;
            // An owed Stop goes to the device that answers now: after a reconnect that is a new object.
            var device = owed ? _alignerSource?.Current() ?? run.Device : run.Device;
            var confirmed = true;
            // No device (none connected, and the run never read one; a run that moved one always has it): nothing to stop
            // and nothing that can move. The Stop stays owed by its device for when it comes back.
            if ((run.Engaged || owed) && device is not null) {
                confirmed = await StopDeviceVerifiedAsync(run, device).ConfigureAwait(false);
                if (!confirmed) {
                    LogAlignerStopNotConfirmedAtEnd();
                }
            }
            lock (_gate) {
                if (ReferenceEquals(_alignerRun, run)) {
                    _alignerRun = null;
                    _aligner = null;
                }
            }
            run.Dispose();
            return confirmed;
        }

        // Run task, after its end's aligner stop: an unconfirmed one is told. The stop path reports it when it has not yet
        // told the client the run ended; when it already has, it is this run's to report. Not when the run reported its own
        // end with the sentence (a failure), nor once a newer run owns the client's view.
        private async Task NoteAlignerEndAsync(RunEnd end, bool confirmed, bool reportedByRun) {
            if (confirmed) {
                return;
            }
            bool tell;
            lock (_gate) {
                _alignerNotStoppedEnd = (end, reportedByRun);
                // Read under the lock the stop path marks the end reported in (EndRoutineAsync): a run that outlived the
                // unwind grace and notes this between the stop path's read and its mark is told by the stop path, after
                // its mark by the run, never by neither.
                tell = !reportedByRun && end.Reported;
            }
            if (tell) {
                await PublishErrorAsync(AlignerDidNotStop, AlignerCutPowerSentence).ConfigureAwait(false);
            }
        }

        /// <summary>The stop path, once the run unwound: whether that run's end could not confirm the Polar Aligner stopped.</summary>
        private bool AlignerNotStoppedLocked(RunEnd? end) => end is not null && ReferenceEquals(_alignerNotStoppedEnd?.End, end);

        /// <summary>The stop path, as it marks the end reported: whether the run noted an unconfirmed aligner stop that the stop
        /// path's earlier read missed and the run did not report itself; the stop path tells it.</summary>
        private bool AlignerNotStoppedUntoldLocked(RunEnd? end, bool alreadyRead) =>
            !alreadyRead && AlignerNotStoppedLocked(end) && _alignerNotStoppedEnd is { ToldByRun: false };

        /// <summary>One measurement's turn of the motors. Returns when they are still (or were never
        /// asked): the next frame the loop takes is the measurement of what they did.</summary>
        private async Task CorrectAsync(int gen, double altErr, double azErr, PolarAlignSettingsDto settings, CancellationToken ct) {
            AlignerRun? run;
            CancellationToken userStop;
            bool stopped, stopOwed;
            lock (_gate) {
                run = _generation == gen ? _alignerRun : null;
                if (run is null) {
                    return;
                }
                if (run.RearmRequested) {
                    run.RearmRequested = false;
                    run.Rearm();
                }
                userStop = run.UserStop.Token;
                stopped = run.Stopped;
                stopOwed = run.StopOwed;
            }
            // An owed Stop goes to the device that answers now: after a reconnect that is a new object. #2069 — one the
            // Stop route, Emergency Stop or an earlier run left owed too; nothing moves until a Stop is confirmed. With no
            // device to send it to (none connected, none read yet) there is nothing to stop and nothing that moves: the run
            // goes on by hand, and the Stop stays owed by its device for when it comes back.
            if ((stopOwed || _alignerSource!.StopOwed) && (_alignerSource!.Current() ?? run.Device) is { } owed) {
                await StopDeviceVerifiedAsync(run, owed).ConfigureAwait(false);
                // Read before _gate: the source takes the Switch, mount and aligner locks, never under the routine's.
                var sourceOwed = _alignerSource.StopOwed;
                var confirmedLate = false;
                lock (_gate) {
                    stopOwed = run.StopOwed || sourceOwed;
                    if (stopOwed && !run.Stopped) {
                        // A Stop owed from before this run (the Stop route, Emergency Stop, an earlier run's end) that is
                        // still not confirmed: the run stops correcting and says why, so the person is not left with
                        // motors that never move and nothing on screen.
                        run.Stopped = true;
                        run.LastStop = (PolarAlignCorrectionCauses.StopNotConfirmed, null, null);
                    } else {
                        confirmedLate = NoteOwedStopConfirmedLocked(run, sourceOwed);
                    }
                }
                if (confirmedLate) {
                    LogAlignerStopped(PolarAlignCorrectionCauses.StopConfirmed, null);
                }
                await ReportStopAsync(gen, run).ConfigureAwait(false);
                if (stopOwed) {
                    return;
                }
            }
            if (stopped || run.Undrivable) {
                return;
            }
            var auto = settings.PolarAlignerAuto;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, userStop);
            try {
                if (run.Device is null) {
                    var device = _alignerSource!.Current();
                    if (device is null) {
                        return; // no Polar Aligner on the rig (yet): the person turns the bolts
                    }
                    var info = device.Info();
                    lock (_gate) {
                        run.Device = device;
                        run.Info = info;
                        run.Undrivable = !info.HostDriven;
                    }
                    if (!info.HostDriven) {
                        LogAlignerNotDrivable(info.DeviceName);
                        return;
                    }
                }
                if (!auto) {
                    await SetAlignerAsync(gen, new PolarAlignCorrectionDto(PolarAlignCorrectionStates.Manual)).ConfigureAwait(false);
                    ResetAlignerBaseline();
                    return;
                }
                await CorrectOnceAsync(gen, run, altErr, azErr, settings, linked.Token).ConfigureAwait(false);
            } catch (PolarAlignerException) when (!auto) {
                // By hand: a device that does not answer is not a stop. It is read again at the next measurement.
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                // The person's Stop: the command already set the state; the device gets its Stop here too,
                // in case a move went out after the command's own.
                if (run.Device is { } device) {
                    await StopDeviceVerifiedAsync(run, device).ConfigureAwait(false);
                    await ReportStopAsync(gen, run).ConfigureAwait(false);
                }
            } catch (AlignerStopException ex) {
                await StopCorrectingAsync(gen, run, ex.Cause, ex.DeviceText, ex.Axis).ConfigureAwait(false);
            } catch (PolarAlignerException ex) {
                await StopCorrectingAsync(gen, run, ex.Failure switch {
                    PolarAlignerFailure.NotCalibrated => PolarAlignCorrectionCauses.NotCalibrated,
                    PolarAlignerFailure.LinkLost => PolarAlignCorrectionCauses.LinkLost,
                    _ => PolarAlignCorrectionCauses.DeviceRefused,
                }, ex.Message).ConfigureAwait(false);
            }
        }

        private async Task CorrectOnceAsync(int gen, AlignerRun run, double altErr, double azErr,
                PolarAlignSettingsDto settings, CancellationToken ct) {
            var device = run.Device!;
            var info = run.Info!;
            var azInfo = info.Azimuth!;
            var altInfo = info.Altitude!;
            var (azMove, altMove) = PolarAlignGeometry.AlignerMove(altErr, azErr, run.LatitudeDeg);
            // Where the axis sits from the pole, in the device's own directions (the full move, undone).
            (double Az, double Alt) offset = (-azMove, -altMove);
            // #2069 — the travel cap is set from the first measurement the run drives the device from.
            run.FirstOffset ??= (Math.Abs(offset.Az), Math.Abs(offset.Alt));
            var total = Math.Sqrt(altErr * altErr + azErr * azErr);
            var hemisphere = run.LatitudeDeg > 0 ? "north" : "south";

            if (run.BeforeProbe is { } before) {
                run.BeforeProbe = null;
                var probe = ProbeSize(info);
                CheckProbe(PolarAlignerAxis.Azimuth, (offset.Az - before.Az) / probe, azInfo);
                CheckProbe(PolarAlignerAxis.Altitude, (offset.Alt - before.Alt) / probe, altInfo);
                run.ProbeDone = true;
            } else if (run.TotalBeforeMove is { } last) {
                run.Growth = total > last + AlignerGrowthMarginArcmin ? run.Growth + 1 : 0;
                LogAlignerResult(last, total);
                if (run.Growth >= AlignerGrowthLimit) {
                    throw new AlignerStopException(PolarAlignCorrectionCauses.Diverging, deviceText: null);
                }
            }
            run.TotalBeforeMove = null;

            var tolerance = settings.TargetToleranceArcmin;
            if (total <= tolerance) {
                await SetAlignerAsync(gen, new PolarAlignCorrectionDto(PolarAlignCorrectionStates.Converged)).ConfigureAwait(false);
                return;
            }

            var mount = _mount.GetInfo();
            if (!info.MovesWhileTracking && mount.TrackingEnabled) {
                throw new AlignerStopException(PolarAlignCorrectionCauses.MountTracking, deviceText: null);
            }
            if (!info.MovesWhileSlewing && mount.Slewing) {
                throw new AlignerStopException(PolarAlignCorrectionCauses.MountSlewing, deviceText: null);
            }

            List<(PolarAlignerAxis Axis, double Arcmin)> moves;
            string kind;
            if (!run.ProbeDone) {
                var probe = ProbeSize(info);
                moves = [(PolarAlignerAxis.Azimuth, probe), (PolarAlignerAxis.Altitude, probe)];
                kind = PolarAlignCorrectionStates.Probing;
                run.BeforeProbe = offset;
            } else {
                // An axis inside half the tolerance (on the sky, as the tolerance is) is left alone: with the
                // total outside it, the other one is not.
                moves = [];
                if (Math.Abs(azErr) >= tolerance / 2) {
                    moves.Add((PolarAlignerAxis.Azimuth, Math.Clamp(AlignerGain * azMove, -azInfo.MaxMoveArcmin, azInfo.MaxMoveArcmin)));
                }
                if (Math.Abs(altErr) >= tolerance / 2) {
                    moves.Add((PolarAlignerAxis.Altitude, Math.Clamp(AlignerGain * altMove, -altInfo.MaxMoveArcmin, altInfo.MaxMoveArcmin)));
                }
                kind = PolarAlignCorrectionStates.Moving;
                run.TotalBeforeMove = total;
            }
            if (!info.CanStop) {
                // #2069 — a move this device is sent cannot be cut short: each one is small.
                moves = [.. moves.Select(m => (m.Axis, Math.Clamp(m.Arcmin, -AlignerNoStopMoveCapArcmin, AlignerNoStopMoveCapArcmin)))];
            }
            CheckRunBounds(run, moves);

            foreach (var group in info.SimultaneousMoves ? [moves] : moves.Select(m => new List<(PolarAlignerAxis Axis, double Arcmin)> { m })) {
                // #2069 — where the device says each axis stands before the move, to check it follows (PositionArcmin).
                var standing = device.Status();
                foreach (var (axis, arcmin) in group) {
                    ct.ThrowIfCancellationRequested();
                    // #2066 — the Polar Aligner moves the mount's polar axis: a Stop Mount or Emergency Stop (its latch) that
                    // landed during the frame this move answers, or during the last move's wait, ends the run before it.
                    ThrowIfStoppedFromOutside(gen);
                    await SetAlignerAsync(gen, new PolarAlignCorrectionDto(kind, Axis: AxisToken(axis), Arcmin: Math.Round(arcmin, 2))).ConfigureAwait(false);
                    lock (_gate) {
                        if (run.Stopped) {
                            throw new OperationCanceledException(); // a Stop landed since the check above
                        }
                        run.Engaged = true;
                    }
                    device.Move(axis, arcmin);
                    lock (_gate) {
                        _alignerMoves++;
                    }
                    if (axis == PolarAlignerAxis.Azimuth) {
                        run.NetAz += arcmin;
                    } else {
                        run.NetAlt += arcmin;
                    }
                    LogAlignerMove(kind, hemisphere, altErr, azErr, axis, arcmin);
                }
                var after = await WaitForMoveAsync(device, group.Select(m => m.Axis).ToArray(), ct).ConfigureAwait(false);
                LogAlignerStatus(after.Azimuth.Moving, after.Azimuth.PositionArcmin, after.Altitude.Moving, after.Altitude.PositionArcmin);
                CheckFollowed(run, info, group, standing, after);
            }
            if (info.SettleSeconds > 0) {
                await SetAlignerAsync(gen, new PolarAlignCorrectionDto(PolarAlignCorrectionStates.Settling, Seconds: info.SettleSeconds)).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(info.SettleSeconds), ct).ConfigureAwait(false);
            }
            await SetAlignerAsync(gen, new PolarAlignCorrectionDto(PolarAlignCorrectionStates.Measuring)).ConfigureAwait(false);
        }

        // The device's own cap bounds the probe; its step size is the least it can do.
        private static double ProbeSize(PolarAlignerInfo info) =>
            Math.Max(
                Math.Min(AlignerProbeArcmin, Math.Min(info.Azimuth!.MaxMoveArcmin, info.Altitude!.MaxMoveArcmin) / 4),
                Math.Max(info.Azimuth.ResolutionArcmin, info.Altitude.ResolutionArcmin));

        // ratio = how far the axis went, over how far it was asked to go. A device that does not take up
        // its own backlash loses part of a first move to it, so its floor is lower.
        private void CheckProbe(PolarAlignerAxis axis, double ratio, PolarAlignerAxisInfo info) {
            LogAlignerProbe(axis, ratio);
            if (ratio < 0) {
                throw new AlignerStopException(PolarAlignCorrectionCauses.WrongDirection, null, AxisToken(axis));
            }
            if (ratio < (info.BacklashHandled ? 0.5 : 0.2) || ratio > 2) {
                throw new AlignerStopException(PolarAlignCorrectionCauses.WrongScale, null, AxisToken(axis));
            }
        }

        /// <summary>#2069 (ARA-19) — the axis's travel cap: max(3 × its offset at the run's first measurement + 30′, 10′).
        /// The device's own range is not known to Ara (its Info does not report one), so it does not bound the cap.</summary>
        internal static double AlignerTravelCap(double firstOffsetArcmin) =>
            Math.Max((AlignerTravelErrorFactor * firstOffsetArcmin) + AlignerTravelMarginArcmin, AlignerTravelFloorArcmin);

        // Before any move of the plan goes out: the whole plan fits the run's move count and each axis's travel cap,
        // or none of it is sent.
        private void CheckRunBounds(AlignerRun run, List<(PolarAlignerAxis Axis, double Arcmin)> moves) {
            int sent;
            lock (_gate) {
                sent = _alignerMoves;
            }
            if (sent + moves.Count > AlignerMoveLimit) {
                throw new AlignerStopException(PolarAlignCorrectionCauses.MoveLimit, deviceText: null);
            }
            var first = run.FirstOffset!.Value;
            var (netAz, netAlt) = (run.NetAz, run.NetAlt);
            foreach (var (axis, arcmin) in moves) {
                var azimuth = axis == PolarAlignerAxis.Azimuth;
                var net = (azimuth ? netAz : netAlt) + arcmin;
                if (Math.Abs(net) > AlignerTravelCap(azimuth ? first.Az : first.Alt)) {
                    LogAlignerTravelCap(axis, azimuth ? netAz : netAlt, arcmin, AlignerTravelCap(azimuth ? first.Az : first.Alt));
                    throw new AlignerStopException(PolarAlignCorrectionCauses.TravelLimit, null, AxisToken(axis));
                }
                if (azimuth) {
                    netAz = net;
                } else {
                    netAlt = net;
                }
            }
        }

        // #2069 (ARA-19) — with PositionArcmin, each finished move changes the axis's position by 0.5× to 2× the amount
        // sent, with its sign; on a device that does not take up its own backlash, an axis's first move and the first move
        // after it reverses direction may lose more (0.2×). One step of the device's resolution is allowed either side, so
        // a move of a step or two is not misread.
        private void CheckFollowed(AlignerRun run, PolarAlignerInfo info, List<(PolarAlignerAxis Axis, double Arcmin)> group,
                PolarAlignerStatus before, PolarAlignerStatus after) {
            foreach (var (axis, arcmin) in group) {
                var azimuth = axis == PolarAlignerAxis.Azimuth;
                var axisInfo = azimuth ? info.Azimuth! : info.Altitude!;
                // The axis's first move, or the first after it reverses: the backlash is taken up again.
                var sign = Math.Sign(arcmin);
                var takesUpBacklash = sign != 0 && sign != (azimuth ? run.AzLastSign : run.AltLastSign);
                if (sign != 0) {
                    if (azimuth) {
                        run.AzLastSign = sign;
                    } else {
                        run.AltLastSign = sign;
                    }
                }
                var (from, to) = azimuth
                    ? (before.Azimuth.PositionArcmin, after.Azimuth.PositionArcmin)
                    : (before.Altitude.PositionArcmin, after.Altitude.PositionArcmin);
                if (from is not { } p0 || to is not { } p1 || arcmin == 0) {
                    continue;
                }
                var moved = p1 - p0;
                var ratio = moved / arcmin;
                var slack = axisInfo.ResolutionArcmin / Math.Abs(arcmin);
                LogAlignerFollowed(axis, arcmin, moved, ratio);
                if (ratio < 0 && Math.Abs(moved) > axisInfo.ResolutionArcmin) {
                    throw new AlignerStopException(PolarAlignCorrectionCauses.WrongDirection, null, AxisToken(axis));
                }
                var floor = takesUpBacklash && !axisInfo.BacklashHandled ? 0.2 : 0.5;
                if (ratio < floor - slack || ratio > 2 + slack) {
                    throw new AlignerStopException(PolarAlignCorrectionCauses.WrongScale, null, AxisToken(axis));
                }
            }
        }

        // A fault string or a move still running at the deadline ends the wait as a stop. A Stop from
        // outside looks like a finished move here (not moving, no fault): the stop commands mark the run
        // stopped and cancel this wait first, so it is never taken for one.
        private async Task<PolarAlignerStatus> WaitForMoveAsync(IPolarAligner device, PolarAlignerAxis[] axes, CancellationToken ct) {
            var deadline = DateTimeOffset.UtcNow + AlignerMoveTimeout;
            while (true) {
                var status = device.Status();
                var moving = false;
                foreach (var axis in axes) {
                    var s = axis == PolarAlignerAxis.Azimuth ? status.Azimuth : status.Altitude;
                    if (s.Fault is { } fault) {
                        throw new AlignerStopException(PolarAlignCorrectionCauses.DeviceFault, fault, AxisToken(axis));
                    }
                    moving |= s.Moving;
                }
                if (!moving) {
                    ct.ThrowIfCancellationRequested();
                    return status;
                }
                if (DateTimeOffset.UtcNow >= deadline) {
                    throw new AlignerStopException(PolarAlignCorrectionCauses.Timeout, deviceText: null);
                }
                await Task.Delay(AlignerPoll, ct).ConfigureAwait(false);
            }
        }

        private async Task StopCorrectingAsync(int gen, AlignerRun run, string cause, string? deviceText, string? axis = null) {
            // Marked stopped first: a move must never follow, and a stop command that lands meanwhile keeps this cause.
            lock (_gate) {
                if (!run.Stopped) {
                    run.Stopped = true;
                    run.LastStop = (cause, axis, deviceText);
                }
            }
            if (run.Device is { } device) {
                await StopDeviceVerifiedAsync(run, device).ConfigureAwait(false);
            }
            LogAlignerStopped(cause, deviceText);
            await ReportStopAsync(gen, run).ConfigureAwait(false);
        }

        /// <summary>#2069 (ARA-19) — Stop, then poll until both axes read still (<see cref="PolarAlignerVerifiedStop"/>).
        /// Not confirmed: the Stop is owed (sent again at each measurement and at the end of the run) and recorded with
        /// the source, so the Stop route and the next run know it too. True when confirmed.</summary>
        private async Task<bool> StopDeviceVerifiedAsync(AlignerRun run, IPolarAligner device) {
            var outcome = await PolarAlignerVerifiedStop.StopAsync(device, AlignerStopWindow, AlignerPoll, _logger).ConfigureAwait(false);
            lock (_gate) {
                run.StopOwed = !outcome.Confirmed;
            }
            _alignerSource?.NoteStopOutcome(device, outcome.Confirmed);
            if (outcome.Confirmed) {
                LogAlignerStopConfirmed(outcome.Sends);
            } else {
                // Owed: sent again at each measurement, so it goes out as soon as the device answers.
                LogAlignerStopFailed(outcome.Sends, outcome.Error ?? PolarAlignerVerifiedStop.StillMoving);
            }
            return outcome.Confirmed;
        }

        /// <summary>#2069 — under <c>_gate</c>, after a verified stop (the run's resend or the correction Stop route): a run stopped
        /// because a Stop was owed (<c>stop_not_confirmed</c>) whose Stop is confirmed now stays stopped and waits for the
        /// person's Resume (<c>stop_confirmed</c>). A device that would not stop does not start moving again by itself;
        /// Resume drives it from a new device read and probe. True when it changed the cause.</summary>
        /// <param name="sourceOwed">The source's <c>StopOwed</c>, read by the caller before it took <c>_gate</c>.</param>
        private static bool NoteOwedStopConfirmedLocked(AlignerRun run, bool sourceOwed) {
            if (run.StopOwed || sourceOwed || !run.Stopped
                    || run.LastStop?.Cause != PolarAlignCorrectionCauses.StopNotConfirmed) {
                return false;
            }
            run.LastStop = (PolarAlignCorrectionCauses.StopConfirmed, null, null);
            return true;
        }

        /// <summary>The stopped state of a stopped run: its cause, or <c>stop_not_confirmed</c> while its Stop is owed (a lost
        /// link keeps <c>link_lost</c>, whose sentence already says the Stop goes out once the device answers). True when
        /// it published a change.</summary>
        private Task<bool> ReportStopAsync(int gen, AlignerRun run) {
            PolarAlignCorrectionDto state;
            lock (_gate) {
                if (!run.Stopped || run.LastStop is not { } last) {
                    return Task.FromResult(false);
                }
                state = run.StopOwed && last.Cause != PolarAlignCorrectionCauses.LinkLost
                    ? new PolarAlignCorrectionDto(PolarAlignCorrectionStates.Stopped, Cause: PolarAlignCorrectionCauses.StopNotConfirmed)
                    : new PolarAlignCorrectionDto(PolarAlignCorrectionStates.Stopped, Axis: last.Axis, Cause: last.Cause, DeviceText: last.DeviceText);
            }
            return SetAlignerAsync(gen, state);
        }

        private async Task<bool> SetAlignerAsync(int gen, PolarAlignCorrectionDto state) {
            lock (_gate) {
                // A superseded run; or the person's Stop landed first; or a resume landed before this stop was told.
                if (_generation != gen || (_alignerRun?.Stopped == true) != (state.State == PolarAlignCorrectionStates.Stopped)) {
                    return false;
                }
                state = state with { Moves = _alignerMoves };
                if (state == _aligner) {
                    return false;
                }
                _aligner = state;
            }
            await PublishAlignerAsync(gen).ConfigureAwait(false);
            return true;
        }

        private Task PublishAlignerAsync(int gen) {
            PolarAlignCorrectionDto? state;
            lock (_gate) {
                state = _generation == gen ? _aligner : null;
            }
            if (state is null) {
                return Task.CompletedTask;
            }
            var payload = new JsonObject { ["state"] = state.State, ["moves"] = state.Moves };
            if (state.Axis is not null) { payload["axis"] = state.Axis; }
            if (state.Arcmin is not null) { payload["arcmin"] = state.Arcmin; }
            if (state.Seconds is not null) { payload["seconds"] = state.Seconds; }
            if (state.Cause is not null) { payload["cause"] = state.Cause; }
            if (state.DeviceText is not null) { payload["device_text"] = state.DeviceText; }
            return PublishAsync(WsEventCatalog.PolarAlignCorrection, payload);
        }

        private static string AxisToken(PolarAlignerAxis axis) => axis == PolarAlignerAxis.Azimuth ? "azimuth" : "altitude";

        [LoggerMessage(EventId = 4543, Level = LogLevel.Information, Message = "§45 polar-align aligner {Kind}: {Hemisphere} site, error alt {AltArcmin:F2}′ az {AzArcmin:F2}′ (east-positive), sent {Axis} {Arcmin:F2}′")]
        private partial void LogAlignerMove(string kind, string hemisphere, double altArcmin, double azArcmin, PolarAlignerAxis axis, double arcmin);

        [LoggerMessage(EventId = 4548, Level = LogLevel.Information, Message = "§45 polar-align aligner status after the move: azimuth moving {AzMoving} at {AzArcmin}′, altitude moving {AltMoving} at {AltArcmin}′")]
        private partial void LogAlignerStatus(bool azMoving, double? azArcmin, bool altMoving, double? altArcmin);

        [LoggerMessage(EventId = 4549, Level = LogLevel.Information, Message = "§45 polar-align aligner test move on {Axis}: the axis went {Ratio:F2} times the amount asked")]
        private partial void LogAlignerProbe(PolarAlignerAxis axis, double ratio);

        [LoggerMessage(EventId = 4544, Level = LogLevel.Information, Message = "§45 polar-align aligner: error {BeforeArcmin:F2}′ before the move, {AfterArcmin:F2}′ after")]
        private partial void LogAlignerResult(double beforeArcmin, double afterArcmin);

        [LoggerMessage(EventId = 4545, Level = LogLevel.Warning, Message = "§45 polar-align aligner stopped: {Cause} ({DeviceText}); the routine continues by hand")]
        private partial void LogAlignerStopped(string cause, string? deviceText);

        [LoggerMessage(EventId = 4546, Level = LogLevel.Warning, Message = "§45 polar-align aligner: Stop not confirmed after {Sends} sends ({Reason}); it is sent again at the next measurement")]
        private partial void LogAlignerStopFailed(int sends, string reason);

        [LoggerMessage(EventId = 4640, Level = LogLevel.Information, Message = "§45 polar-align aligner: Stop confirmed (both axes still) after {Sends} sends")]
        private partial void LogAlignerStopConfirmed(int sends);

        [LoggerMessage(EventId = 4641, Level = LogLevel.Critical, Message = "§45 polar-align aligner: COULD NOT CONFIRM THE POLAR ALIGNER STOPPED at the end of the run — the Stop is owed; cut its power if it still moves")]
        private partial void LogAlignerStopNotConfirmedAtEnd();

        [LoggerMessage(EventId = 4642, Level = LogLevel.Information, Message = "§45 polar-align aligner {Axis}: sent {Arcmin:F2}′, its position moved {Moved:F2}′ ({Ratio:F2} times)")]
        private partial void LogAlignerFollowed(PolarAlignerAxis axis, double arcmin, double moved, double ratio);

        [LoggerMessage(EventId = 4643, Level = LogLevel.Warning, Message = "§45 polar-align aligner {Axis}: net travel {Net:F1}′ plus the next move {Arcmin:F1}′ would pass the run's cap of {Cap:F1}′")]
        private partial void LogAlignerTravelCap(PolarAlignerAxis axis, double net, double arcmin, double cap);

        [LoggerMessage(EventId = 4547, Level = LogLevel.Information, Message = "§45 polar-align aligner: {DeviceName} runs its own correction only; Ara does not drive it")]
        private partial void LogAlignerNotDrivable(string deviceName);
    }
}
