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

    /// <summary>The Polar Corrector states and stop causes the routine reports (#1971).</summary>
    public static class PolarAlignCorrectorStates {
        public const string Manual = "manual";
        public const string Probing = "probing";
        public const string Moving = "moving";
        public const string Settling = "settling";
        public const string Measuring = "measuring";
        public const string Converged = "converged";
        public const string Stopped = "stopped";
    }

    public static class PolarAlignCorrectorCauses {
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

    // #1971 — the Polar Corrector in the live stages: after each measurement the routine moves the
    // motors (one probe move per axis, then a fraction of the error), waits for them, and lets the
    // next frame measure the result. Any way out stops the motors and leaves the routine running by
    // hand. The numbers (probe size, fraction, scale check) follow N.I.N.A.'s Polar Alignment
    // Correctors plugin, so one device behaves the same under both clients.
    public sealed partial class PolarAlignService : IPolarCorrectorDriver {

        // The routine drives the Polar Corrector from its first measurement with one until it is stopped
        // or the run ends; "by hand" is not driving.
        bool IPolarCorrectorDriver.DrivesPolarCorrector {
            get {
                lock (_gate) {
                    return _active && _correctorRun is { Stopped: false }
                        && _corrector is { State: not PolarAlignCorrectorStates.Manual };
                }
            }
        }

        async Task IPolarCorrectorDriver.StopPolarCorrectorAsync() {
            try {
                await StopCorrectorAsync(null, CancellationToken.None).ConfigureAwait(false);
            } catch (InvalidOperationException) {
                // The run ended between the question and the stop: its own exit stopped the motors.
            }
        }

        internal const double CorrectorGain = 0.8;
        internal const double CorrectorProbeArcmin = 2.0;
        private const double CorrectorGrowthMarginArcmin = 0.1;
        private const int CorrectorGrowthLimit = 2;

        internal TimeSpan CorrectorPoll { get; set; } = TimeSpan.FromMilliseconds(250);
        internal TimeSpan CorrectorMoveTimeout { get; set; } = TimeSpan.FromSeconds(60);

        private readonly IPolarCorrectorSource? _correctorSource;

        // One per run. Stopped, RearmRequested, StopOwed and UserStop are shared with the stop and resume
        // commands and are read and written under _gate. Everything else belongs to the run task.
        private sealed class CorrectorRun(double latitudeDeg) : IDisposable {
            public double LatitudeDeg { get; } = latitudeDeg;
            public IPolarCorrector? Device;
            public PolarCorrectorInfo? Info;
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

        private sealed class CorrectorStopException : Exception {
            public CorrectorStopException() { }
            public CorrectorStopException(string message) : base(message) { }
            public CorrectorStopException(string message, Exception innerException) : base(message, innerException) { }
            public CorrectorStopException(string cause, string? deviceText, string? axis = null) : base(cause) {
                Cause = cause;
                DeviceText = deviceText;
                Axis = axis;
            }
            public string Cause { get; } = PolarAlignCorrectorCauses.DeviceRefused;
            public string? DeviceText { get; }
            public string? Axis { get; }
        }

        private CorrectorRun? _correctorRun;          // guarded by _gate
        private PolarAlignCorrectorDto? _corrector;   // guarded by _gate: what status and the event report
        private int _correctorMoves;                  // guarded by _gate

        /// <summary>The person stops the motors; the routine carries on by hand.</summary>
        public async Task<OperationAcceptedDto> StopCorrectorAsync(string? idempotencyKey, CancellationToken ct) {
            CorrectorRun run;
            IPolarCorrector? device;
            CancellationTokenSource userStop;
            int gen;
            lock (_gate) {
                run = _active && _correctorRun is { } r ? r
                    : throw new InvalidOperationException("polar alignment is not running with a Polar Corrector");
                gen = _generation;
                device = run.Device;
                if (!run.Stopped) {
                    run.Stopped = true;
                    _corrector = new PolarAlignCorrectorDto(PolarAlignCorrectorStates.Stopped,
                        Cause: PolarAlignCorrectorCauses.StoppedByUser, Moves: _correctorMoves);
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
                StopDeviceBestEffort(run, device);
            }
            LogCorrectorStopped(PolarAlignCorrectorCauses.StoppedByUser, null);
            await PublishCorrectorAsync(gen).ConfigureAwait(false);
            return Accepted("polar-align.corrector-stop", idempotencyKey);
        }

        /// <summary>After a stop: correct with the motors again, starting from a new probe.</summary>
        public async Task<OperationAcceptedDto> ResumeCorrectorAsync(string? idempotencyKey, CancellationToken ct) {
            int gen;
            lock (_gate) {
                if (!_active || _correctorRun is not { Stopped: true } run) {
                    throw new InvalidOperationException("the Polar Corrector is not stopped");
                }
                gen = _generation;
                run.Stopped = false;
                run.RearmRequested = true;
                run.UserStop.Dispose();
                run.UserStop = new CancellationTokenSource();
                _corrector = new PolarAlignCorrectorDto(PolarAlignCorrectorStates.Measuring, Moves: _correctorMoves);
            }
            await PublishCorrectorAsync(gen).ConfigureAwait(false);
            return Accepted("polar-align.corrector-resume", idempotencyKey);
        }

        // Run task, at the start of a run.
        private CorrectorRun? BeginCorrectorRun(double latitudeDeg) {
            lock (_gate) {
                _correctorRun = _correctorSource is null ? null : new CorrectorRun(latitudeDeg);
                _corrector = null;
                _correctorMoves = 0;
                return _correctorRun;
            }
        }

        // Run task, at each seed: the readout starts again from a new fit, so a probe still waiting
        // for its measurement and the before-and-after of the last move mean nothing any more.
        private void ResetCorrectorBaseline() {
            lock (_gate) {
                if (_correctorRun is { } run) {
                    run.BeforeProbe = null;
                    run.TotalBeforeMove = null;
                    run.Growth = 0;
                }
            }
        }

        // Run task, on every exit: a run that moved the motors stops them. A newer run's state is not touched.
        private void EndCorrectorRun(CorrectorRun? run) {
            if (run is null) {
                return;
            }
            if (run is { Engaged: true, Device: { } device }) {
                StopDeviceBestEffort(run, device);
            }
            lock (_gate) {
                if (ReferenceEquals(_correctorRun, run)) {
                    _correctorRun = null;
                    _corrector = null;
                }
            }
            run.Dispose();
        }

        /// <summary>One measurement's turn of the motors. Returns when they are still (or were never
        /// asked): the next frame the loop takes is the measurement of what they did.</summary>
        private async Task CorrectAsync(int gen, double altErr, double azErr, PolarAlignSettingsDto settings, CancellationToken ct) {
            CorrectorRun? run;
            CancellationToken userStop;
            bool stopped, stopOwed;
            lock (_gate) {
                run = _generation == gen ? _correctorRun : null;
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
            // An owed Stop goes to the device that answers now: after a reconnect that is a new object.
            if (stopOwed && (_correctorSource!.Current() ?? run.Device) is { } owed) {
                StopDeviceBestEffort(run, owed);
            }
            if (stopped || run.Undrivable) {
                return;
            }
            if (!settings.PolarCorrectorAuto) {
                if (run.Device is not null || _correctorSource!.Current() is not null) {
                    await SetCorrectorAsync(gen, new PolarAlignCorrectorDto(PolarAlignCorrectorStates.Manual)).ConfigureAwait(false);
                    ResetCorrectorBaseline();
                }
                return;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, userStop);
            try {
                if (run.Device is null) {
                    var device = _correctorSource!.Current();
                    if (device is null) {
                        return; // no Polar Corrector on the rig (yet): the person turns the bolts
                    }
                    var info = device.Info();
                    lock (_gate) {
                        run.Device = device;
                        run.Info = info;
                        run.Undrivable = !info.HostDriven;
                    }
                    if (!info.HostDriven) {
                        LogCorrectorNotDrivable(info.DeviceName);
                        return;
                    }
                }
                await CorrectOnceAsync(gen, run, altErr, azErr, settings, linked.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                // The person's Stop: the command already set the state; the device gets its Stop here too,
                // in case a move went out after the command's own.
                if (run.Device is { } device) {
                    StopDeviceBestEffort(run, device);
                }
            } catch (CorrectorStopException ex) {
                await StopCorrectingAsync(gen, run, ex.Cause, ex.DeviceText, ex.Axis).ConfigureAwait(false);
            } catch (PolarCorrectorException ex) {
                await StopCorrectingAsync(gen, run, ex.Failure switch {
                    PolarCorrectorFailure.NotCalibrated => PolarAlignCorrectorCauses.NotCalibrated,
                    PolarCorrectorFailure.LinkLost => PolarAlignCorrectorCauses.LinkLost,
                    _ => PolarAlignCorrectorCauses.DeviceRefused,
                }, ex.Message).ConfigureAwait(false);
            }
        }

        private async Task CorrectOnceAsync(int gen, CorrectorRun run, double altErr, double azErr,
                PolarAlignSettingsDto settings, CancellationToken ct) {
            var device = run.Device!;
            var info = run.Info!;
            var azInfo = info.Azimuth!;
            var altInfo = info.Altitude!;
            var (azMove, altMove) = PolarAlignGeometry.CorrectorMove(altErr, azErr, run.LatitudeDeg);
            // Where the axis sits from the pole, in the device's own directions (the full move, undone).
            (double Az, double Alt) offset = (-azMove, -altMove);
            var total = Math.Sqrt(altErr * altErr + azErr * azErr);
            var hemisphere = run.LatitudeDeg > 0 ? "north" : "south";

            if (run.BeforeProbe is { } before) {
                run.BeforeProbe = null;
                var probe = ProbeSize(info);
                CheckProbe(PolarCorrectorAxis.Azimuth, (offset.Az - before.Az) / probe, azInfo);
                CheckProbe(PolarCorrectorAxis.Altitude, (offset.Alt - before.Alt) / probe, altInfo);
                run.ProbeDone = true;
            } else if (run.TotalBeforeMove is { } last) {
                run.Growth = total > last + CorrectorGrowthMarginArcmin ? run.Growth + 1 : 0;
                LogCorrectorResult(last, total);
                if (run.Growth >= CorrectorGrowthLimit) {
                    throw new CorrectorStopException(PolarAlignCorrectorCauses.Diverging, deviceText: null);
                }
            }
            run.TotalBeforeMove = null;

            var tolerance = settings.TargetToleranceArcmin;
            if (total <= tolerance) {
                await SetCorrectorAsync(gen, new PolarAlignCorrectorDto(PolarAlignCorrectorStates.Converged)).ConfigureAwait(false);
                return;
            }

            var mount = _mount.GetInfo();
            if (!info.MovesWhileTracking && mount.TrackingEnabled) {
                throw new CorrectorStopException(PolarAlignCorrectorCauses.MountTracking, deviceText: null);
            }
            if (!info.MovesWhileSlewing && mount.Slewing) {
                throw new CorrectorStopException(PolarAlignCorrectorCauses.MountSlewing, deviceText: null);
            }

            List<(PolarCorrectorAxis Axis, double Arcmin)> moves;
            string kind;
            if (!run.ProbeDone) {
                var probe = ProbeSize(info);
                moves = [(PolarCorrectorAxis.Azimuth, probe), (PolarCorrectorAxis.Altitude, probe)];
                kind = PolarAlignCorrectorStates.Probing;
                run.BeforeProbe = offset;
            } else {
                // An axis inside half the tolerance (on the sky, as the tolerance is) is left alone: with the
                // total outside it, the other one is not.
                moves = [];
                if (Math.Abs(azErr) >= tolerance / 2) {
                    moves.Add((PolarCorrectorAxis.Azimuth, Math.Clamp(CorrectorGain * azMove, -azInfo.MaxMoveArcmin, azInfo.MaxMoveArcmin)));
                }
                if (Math.Abs(altErr) >= tolerance / 2) {
                    moves.Add((PolarCorrectorAxis.Altitude, Math.Clamp(CorrectorGain * altMove, -altInfo.MaxMoveArcmin, altInfo.MaxMoveArcmin)));
                }
                kind = PolarAlignCorrectorStates.Moving;
                run.TotalBeforeMove = total;
            }

            foreach (var group in info.SimultaneousMoves ? [moves] : moves.Select(m => new List<(PolarCorrectorAxis Axis, double Arcmin)> { m })) {
                foreach (var (axis, arcmin) in group) {
                    ct.ThrowIfCancellationRequested();
                    await SetCorrectorAsync(gen, new PolarAlignCorrectorDto(kind, Axis: AxisToken(axis), Arcmin: Math.Round(arcmin, 2))).ConfigureAwait(false);
                    lock (_gate) {
                        if (run.Stopped) {
                            throw new OperationCanceledException(); // a Stop landed since the check above
                        }
                        run.Engaged = true;
                    }
                    device.Move(axis, arcmin);
                    lock (_gate) {
                        _correctorMoves++;
                    }
                    LogCorrectorMove(kind, hemisphere, altErr, azErr, axis, arcmin);
                }
                var after = await WaitForMoveAsync(device, group.Select(m => m.Axis).ToArray(), ct).ConfigureAwait(false);
                LogCorrectorStatus(after.Azimuth.Moving, after.Azimuth.PositionArcmin, after.Altitude.Moving, after.Altitude.PositionArcmin);
            }
            if (info.SettleSeconds > 0) {
                await SetCorrectorAsync(gen, new PolarAlignCorrectorDto(PolarAlignCorrectorStates.Settling, Seconds: info.SettleSeconds)).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(info.SettleSeconds), ct).ConfigureAwait(false);
            }
            await SetCorrectorAsync(gen, new PolarAlignCorrectorDto(PolarAlignCorrectorStates.Measuring)).ConfigureAwait(false);
        }

        // The device's own cap bounds the probe; its step size is the least it can do.
        private static double ProbeSize(PolarCorrectorInfo info) =>
            Math.Max(
                Math.Min(CorrectorProbeArcmin, Math.Min(info.Azimuth!.MaxMoveArcmin, info.Altitude!.MaxMoveArcmin) / 4),
                Math.Max(info.Azimuth.ResolutionArcmin, info.Altitude.ResolutionArcmin));

        // ratio = how far the axis went, over how far it was asked to go. A device that does not take up
        // its own backlash loses part of a first move to it, so its floor is lower.
        private void CheckProbe(PolarCorrectorAxis axis, double ratio, PolarCorrectorAxisInfo info) {
            LogCorrectorProbe(axis, ratio);
            if (ratio < 0) {
                throw new CorrectorStopException(PolarAlignCorrectorCauses.WrongDirection, null, AxisToken(axis));
            }
            if (ratio < (info.BacklashHandled ? 0.5 : 0.2) || ratio > 2) {
                throw new CorrectorStopException(PolarAlignCorrectorCauses.WrongScale, null, AxisToken(axis));
            }
        }

        // A fault string or a move still running at the deadline ends the wait as a stop. A Stop from
        // outside looks like a finished move here (not moving, no fault): the stop commands mark the run
        // stopped and cancel this wait first, so it is never taken for one.
        private async Task<PolarCorrectorStatus> WaitForMoveAsync(IPolarCorrector device, PolarCorrectorAxis[] axes, CancellationToken ct) {
            var deadline = DateTimeOffset.UtcNow + CorrectorMoveTimeout;
            while (true) {
                var status = device.Status();
                var moving = false;
                foreach (var axis in axes) {
                    var s = axis == PolarCorrectorAxis.Azimuth ? status.Azimuth : status.Altitude;
                    if (s.Fault is { } fault) {
                        throw new CorrectorStopException(PolarAlignCorrectorCauses.DeviceFault, fault, AxisToken(axis));
                    }
                    moving |= s.Moving;
                }
                if (!moving) {
                    ct.ThrowIfCancellationRequested();
                    return status;
                }
                if (DateTimeOffset.UtcNow >= deadline) {
                    throw new CorrectorStopException(PolarAlignCorrectorCauses.Timeout, deviceText: null);
                }
                await Task.Delay(CorrectorPoll, ct).ConfigureAwait(false);
            }
        }

        private async Task StopCorrectingAsync(int gen, CorrectorRun run, string cause, string? deviceText, string? axis = null) {
            if (run.Device is { } device) {
                StopDeviceBestEffort(run, device);
            }
            lock (_gate) {
                run.Stopped = true;
            }
            LogCorrectorStopped(cause, deviceText);
            await SetCorrectorAsync(gen, new PolarAlignCorrectorDto(PolarAlignCorrectorStates.Stopped, Axis: axis, Cause: cause, DeviceText: deviceText)).ConfigureAwait(false);
        }

        private void StopDeviceBestEffort(CorrectorRun run, IPolarCorrector device) {
            try {
                device.Stop();
                lock (_gate) {
                    run.StopOwed = false;
                }
            } catch (PolarCorrectorException ex) {
                // Owed: sent again at each measurement, so it goes out as soon as the device answers.
                lock (_gate) {
                    run.StopOwed = true;
                }
                LogCorrectorStopFailed(ex.Message);
            }
        }

        private async Task SetCorrectorAsync(int gen, PolarAlignCorrectorDto state) {
            lock (_gate) {
                // A superseded run; or the person's Stop landed first; or a resume landed before this stop was told.
                if (_generation != gen || (_correctorRun?.Stopped == true) != (state.State == PolarAlignCorrectorStates.Stopped)) {
                    return;
                }
                state = state with { Moves = _correctorMoves };
                if (state == _corrector) {
                    return;
                }
                _corrector = state;
            }
            await PublishCorrectorAsync(gen).ConfigureAwait(false);
        }

        private Task PublishCorrectorAsync(int gen) {
            PolarAlignCorrectorDto? state;
            lock (_gate) {
                state = _generation == gen ? _corrector : null;
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
            return PublishAsync(WsEventCatalog.PolarAlignCorrector, payload);
        }

        private static string AxisToken(PolarCorrectorAxis axis) => axis == PolarCorrectorAxis.Azimuth ? "azimuth" : "altitude";

        [LoggerMessage(EventId = 4543, Level = LogLevel.Information, Message = "§45 polar-align corrector {Kind}: {Hemisphere} site, error alt {AltArcmin:F2}′ az {AzArcmin:F2}′ (east-positive), sent {Axis} {Arcmin:F2}′")]
        private partial void LogCorrectorMove(string kind, string hemisphere, double altArcmin, double azArcmin, PolarCorrectorAxis axis, double arcmin);

        [LoggerMessage(EventId = 4548, Level = LogLevel.Information, Message = "§45 polar-align corrector status after the move: azimuth moving {AzMoving} at {AzArcmin}′, altitude moving {AltMoving} at {AltArcmin}′")]
        private partial void LogCorrectorStatus(bool azMoving, double? azArcmin, bool altMoving, double? altArcmin);

        [LoggerMessage(EventId = 4549, Level = LogLevel.Information, Message = "§45 polar-align corrector test move on {Axis}: the axis went {Ratio:F2} times the amount asked")]
        private partial void LogCorrectorProbe(PolarCorrectorAxis axis, double ratio);

        [LoggerMessage(EventId = 4544, Level = LogLevel.Information, Message = "§45 polar-align corrector: error {BeforeArcmin:F2}′ before the move, {AfterArcmin:F2}′ after")]
        private partial void LogCorrectorResult(double beforeArcmin, double afterArcmin);

        [LoggerMessage(EventId = 4545, Level = LogLevel.Warning, Message = "§45 polar-align corrector stopped: {Cause} ({DeviceText}); the routine continues by hand")]
        private partial void LogCorrectorStopped(string cause, string? deviceText);

        [LoggerMessage(EventId = 4546, Level = LogLevel.Warning, Message = "§45 polar-align corrector: Stop not delivered ({Reason}); it is sent again at the next measurement")]
        private partial void LogCorrectorStopFailed(string reason);

        [LoggerMessage(EventId = 4547, Level = LogLevel.Information, Message = "§45 polar-align corrector: {DeviceName} runs its own correction only; Ara does not drive it")]
        private partial void LogCorrectorNotDrivable(string deviceName);
    }
}
