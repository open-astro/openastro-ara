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

        private readonly IPolarAlignerSource? _alignerSource;

        // One per run. Stopped, RearmRequested, StopOwed and UserStop are shared with the stop and resume
        // commands and are read and written under _gate. Everything else belongs to the run task.
        private sealed class AlignerRun(double latitudeDeg) : IDisposable {
            public double LatitudeDeg { get; } = latitudeDeg;
            public IPolarAligner? Device;
            public PolarAlignerInfo? Info;
            public bool Undrivable;
            public bool Engaged;      // a move went out: every exit owes the device a Stop
            public bool StopOwed;     // a Stop could not be delivered (link lost): retried each measurement
            public bool Stopped;
            public bool RearmRequested; // a resume: the run task starts again from a new device read and probe
            public bool ProbeDone;
            public (double Az, double Alt)? BeforeProbe;
            public double? TotalBeforeMove;
            public int Growth;
            public CancellationTokenSource UserStop = new();

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
            IPolarAligner? device;
            CancellationTokenSource userStop;
            int gen;
            lock (_gate) {
                // A run reports an aligner state only once it has found one Ara can drive.
                run = _active && _aligner is not null && _alignerRun is { } r ? r
                    : throw new InvalidOperationException("polar alignment is not running with a Polar Aligner");
                gen = _generation;
                device = run.Device;
                if (!run.Stopped) {
                    run.Stopped = true;
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
            if (device is not null) {
                // Off the caller's thread: the emergency stop bounds a rung only once this call has
                // handed back a task, and a device on a bad link answers after its HTTP timeout.
                await Task.Run(() => StopDeviceBestEffort(run, device), CancellationToken.None).ConfigureAwait(false);
            }
            LogAlignerStopped(PolarAlignCorrectionCauses.StoppedByUser, null);
            await PublishAlignerAsync(gen).ConfigureAwait(false);
            return Accepted("polar-align.aligner-stop", idempotencyKey);
        }

        /// <summary>After a stop: correct with the motors again, starting from a new probe.</summary>
        public async Task<OperationAcceptedDto> ResumeAlignerAsync(string? idempotencyKey, CancellationToken ct) {
            int gen;
            lock (_gate) {
                if (!_active || _alignerRun is not { Stopped: true } run) {
                    throw new InvalidOperationException("the Polar Aligner is not stopped");
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

        // Run task, on every exit: a run that moved the motors stops them. A newer run's state is not touched.
        private void EndAlignerRun(AlignerRun? run) {
            if (run is null) {
                return;
            }
            if (run is { Engaged: true, Device: { } device }) {
                StopDeviceBestEffort(run, device);
            }
            lock (_gate) {
                if (ReferenceEquals(_alignerRun, run)) {
                    _alignerRun = null;
                    _aligner = null;
                }
            }
            run.Dispose();
        }

        /// <summary>One measurement's turn of the motors. Returns when they are still (or were never
        /// asked): the next frame the loop takes is the measurement of what they did.</summary>
        private async Task CorrectAsync(int gen, double altErr, double azErr, PolarAlignSettingsDto settings, CancellationToken ct) {
            AlignerRun? run;
            CancellationToken userStop;
            bool stopped, stopOwed, handTurn;
            lock (_gate) {
                run = _generation == gen ? _alignerRun : null;
                if (run is null) {
                    return;
                }
                handTurn = _handTurn;
                if (run.RearmRequested) {
                    run.RearmRequested = false;
                    run.Rearm();
                }
                userStop = run.UserStop.Token;
                stopped = run.Stopped;
                stopOwed = run.StopOwed;
            }
            // An owed Stop goes to the device that answers now: after a reconnect that is a new object.
            if (stopOwed && (_alignerSource!.Current() ?? run.Device) is { } owed) {
                StopDeviceBestEffort(run, owed);
            }
            if (stopped || run.Undrivable) {
                return;
            }
            // #2083 — a hand-turn run moves nothing, the Polar Aligner's motors included: the person turns its bolts, as
            // with auto off.
            var auto = settings.PolarAlignerAuto && !handTurn;
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
                    StopDeviceBestEffort(run, device);
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

            foreach (var group in info.SimultaneousMoves ? [moves] : moves.Select(m => new List<(PolarAlignerAxis Axis, double Arcmin)> { m })) {
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
                    LogAlignerMove(kind, hemisphere, altErr, azErr, axis, arcmin);
                }
                var after = await WaitForMoveAsync(device, group.Select(m => m.Axis).ToArray(), ct).ConfigureAwait(false);
                LogAlignerStatus(after.Azimuth.Moving, after.Azimuth.PositionArcmin, after.Altitude.Moving, after.Altitude.PositionArcmin);
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
            if (run.Device is { } device) {
                StopDeviceBestEffort(run, device);
            }
            lock (_gate) {
                run.Stopped = true;
            }
            LogAlignerStopped(cause, deviceText);
            await SetAlignerAsync(gen, new PolarAlignCorrectionDto(PolarAlignCorrectionStates.Stopped, Axis: axis, Cause: cause, DeviceText: deviceText)).ConfigureAwait(false);
        }

        private void StopDeviceBestEffort(AlignerRun run, IPolarAligner device) {
            try {
                device.Stop();
                lock (_gate) {
                    run.StopOwed = false;
                }
            } catch (PolarAlignerException ex) {
                // Owed: sent again at each measurement, so it goes out as soon as the device answers.
                lock (_gate) {
                    run.StopOwed = true;
                }
                LogAlignerStopFailed(ex.Message);
            }
        }

        private async Task SetAlignerAsync(int gen, PolarAlignCorrectionDto state) {
            lock (_gate) {
                // A superseded run; or the person's Stop landed first; or a resume landed before this stop was told.
                if (_generation != gen || (_alignerRun?.Stopped == true) != (state.State == PolarAlignCorrectionStates.Stopped)) {
                    return;
                }
                state = state with { Moves = _alignerMoves };
                if (state == _aligner) {
                    return;
                }
                _aligner = state;
            }
            await PublishAlignerAsync(gen).ConfigureAwait(false);
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

        [LoggerMessage(EventId = 4546, Level = LogLevel.Warning, Message = "§45 polar-align aligner: Stop not delivered ({Reason}); it is sent again at the next measurement")]
        private partial void LogAlignerStopFailed(string reason);

        [LoggerMessage(EventId = 4547, Level = LogLevel.Information, Message = "§45 polar-align aligner: {DeviceName} runs its own correction only; Ara does not drive it")]
        private partial void LogAlignerNotDrivable(string deviceName);
    }
}
