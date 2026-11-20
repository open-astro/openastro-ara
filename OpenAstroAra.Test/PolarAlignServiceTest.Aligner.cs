#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    // #1971 — the Polar Align Routine with a Polar Aligner. The sky is a fake that follows the
    // fake motors: a move turns the camera about the vertical (azimuth) or tilts it (altitude), in
    // the device's own directions, with no knowledge of the routine's east-positive numbers. A run
    // that converges therefore proves the sign and the scale end to end.
    public partial class PolarAlignServiceTest {

        private static PolarAlignerInfo AlignerInfo(double maxMove = 120, bool tier1 = true, bool movesWhileTracking = true,
                bool simultaneous = false, bool backlashHandled = true, bool canStop = true) =>
            new(tier1 ? [1, 2] : [2], "Fake base", "1.0", new PolarAlignerAxisInfo(maxMove, 0.01, backlashHandled), new PolarAlignerAxisInfo(maxMove, 0.01, backlashHandled),
                simultaneous, movesWhileTracking, MovesWhileSlewing: false, CanStop: canStop, SettleSeconds: 0);

        /// <summary>A Polar Aligner whose axis offset from the pole (arcminutes, device directions) the fake sky reads.</summary>
        private sealed class FakeAligner : IPolarAligner, IPolarAlignerSource {
            private readonly object _gate = new();
            private double _az, _alt;
            private bool _moving;
            private readonly List<(PolarAlignerAxis Axis, double Arcmin)> _moves = [];
            private int _stops;

            public FakeAligner(double azOffset, double altOffset) {
                _az = azOffset;
                _alt = altOffset;
            }

            public bool Present { get; set; } = true;
            public PolarAlignerInfo InfoReply { get; set; } = AlignerInfo();
            /// <summary>How far, and which way, the axis really goes per arcminute asked.</summary>
            public double AzResponse { get; set; } = 1;
            public double AltResponse { get; set; } = 1;
            /// <summary>Called with the 1-based move number before the move is applied; an exception it returns is thrown.</summary>
            public Func<int, Exception?>? OnMove { get; set; }
            public string? FaultAfterMoves { get; set; }
            public int FaultFromMove { get; set; } = int.MaxValue;
            public bool NeverFinishes { get; set; }
            public bool StopFails { get; set; }
            /// <summary>#2069 — Stop is answered with the device's own error (a driver that does not implement it).</summary>
            public bool StopRefused { get; set; }
            /// <summary>#2069 — Stop throws this, not a <see cref="PolarAlignerException"/> (a driver bug, a disposed client).</summary>
            public Exception? StopThrows { get; set; }
            /// <summary>#2069 — the source's record that a Stop is owed, as the daemon's Polar Aligner service keeps it.</summary>
            public bool SourceStopOwed { get; set; }
            bool IPolarAlignerSource.StopOwed => SourceStopOwed;
            void IPolarAlignerSource.NoteStopOutcome(IPolarAligner device, bool confirmed) => SourceStopOwed = !confirmed;
            /// <summary>Status throws as on a lost link.</summary>
            public bool StatusFails { get; set; }
            /// <summary>#2069 — Status reports each axis's position (its offset, as the axis really moved).</summary>
            public bool ReportsPosition { get; set; }
            /// <summary>#2069 — this many Stops (from now) are answered but leave the motors turning.</summary>
            public int StopsIgnored { get; set; }
            /// <summary>Called on each Info read.</summary>
            public Action? OnInfo { get; set; }
            private int _statusAfterStop;
            /// <summary>Status reads since the last Stop the device answered.</summary>
            public int StatusReadsAfterLastStop => Volatile.Read(ref _statusAfterStop);
            public bool Moving { get { lock (_gate) { return _moving; } } }
            /// <summary>While reset, Stop does not return: a device on a half-dropped link.</summary>
            public ManualResetEventSlim StopAnswers { get; } = new(initialState: true);

            public (double Az, double Alt) Offset { get { lock (_gate) { return (_az, _alt); } } }
            public (PolarAlignerAxis Axis, double Arcmin)[] Moves { get { lock (_gate) { return [.. _moves]; } } }
            public int Stops => Volatile.Read(ref _stops);

            public IPolarAligner? Current() => Present ? this : null;

            public PolarAlignerInfo Info() {
                OnInfo?.Invoke();
                return InfoReply;
            }

            public PolarAlignerStatus Status() {
                if (StatusFails) {
                    throw new PolarAlignerException(PolarAlignerFailure.LinkLost, "no answer");
                }
                Interlocked.Increment(ref _statusAfterStop);
                lock (_gate) {
                    var fault = _moves.Count >= FaultFromMove ? FaultAfterMoves : null;
                    return new PolarAlignerStatus(new(_moving, ReportsPosition ? _az : null, fault), new(_moving, ReportsPosition ? _alt : null, null));
                }
            }

            public void Move(PolarAlignerAxis axis, double arcmin) {
                lock (_gate) {
                    if (OnMove?.Invoke(_moves.Count + 1) is { } ex) {
                        throw ex;
                    }
                    _moves.Add((axis, arcmin));
                    if (axis == PolarAlignerAxis.Azimuth) {
                        _az += arcmin * AzResponse;
                    } else {
                        _alt += arcmin * AltResponse;
                    }
                    _moving = NeverFinishes;
                }
            }

            public void Stop() {
                StopAnswers.Wait();
                if (StopThrows is { } thrown) {
                    throw thrown;
                }
                if (StopFails) {
                    throw new PolarAlignerException(PolarAlignerFailure.LinkLost, "no answer");
                }
                if (StopRefused) {
                    throw new PolarAlignerException(PolarAlignerFailure.Refused, "PolarAlign:Stop is not implemented");
                }
                lock (_gate) {
                    if (StopsIgnored > 0) {
                        StopsIgnored--;
                    } else {
                        _moving = false;
                    }
                }
                Volatile.Write(ref _statusAfterStop, 0);
                Interlocked.Increment(ref _stops);
            }

            public void Home() { }
        }

        /// <summary>A rig whose Polar Aligner connection can be replaced mid-run (a reconnect).</summary>
        private sealed class SwappableSource(IPolarAligner first) : IPolarAlignerSource {
            public volatile IPolarAligner? Device = first;
            public IPolarAligner? Current() => Device;
        }

        /// <summary>Seed frames A, B and C as the bench's fixed sky; every live frame is C's pointing moved by the
        /// aligner's offset: azimuth is a turn about the vertical, altitude a tilt.</summary>
        private sealed class CorrectedSky(FakeAligner aligner, double latDeg = SiteLatDeg) : IPolarAlignFrameSolver {
            // The bench's three seed frames, mirrored to the southern sky for a southern site.
            private readonly PolarAlignSolveOutcome _a = new(true, SolveA.RaDegJnow, Math.Sign(latDeg) * SolveA.DecDegJnow);
            private readonly PolarAlignSolveOutcome _b = new(true, SolveB.RaDegJnow, Math.Sign(latDeg) * SolveB.DecDegJnow);
            private readonly PolarAlignSolveOutcome _c = new(true, SolveC.RaDegJnow, Math.Sign(latDeg) * SolveC.DecDegJnow);
            private int _solves;
            public Task<double?> MeasureFocalLengthAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) =>
                Task.FromResult<double?>(null);
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                var n = Interlocked.Increment(ref _solves);
                if (n <= 3) {
                    return Task.FromResult(n == 1 ? _a : n == 2 ? _b : _c);
                }
                var now = DateTimeOffset.UtcNow;
                var p = PolarAlignGeometry.PointingAltAz(_c.RaDegJnow, _c.DecDegJnow, latDeg, SiteLonDeg, now);
                var (az, alt) = aligner.Offset;
                var (newAlt, newAz) = MovedWithTheMount(p.AltDeg, p.AzDeg, alt / 60.0, az / 60.0, latDeg);
                var (ra, dec) = SiteAstrometry.EquatorialFromAltAz(newAlt, newAz, latDeg,
                    SiteAstrometry.LocalSiderealTimeDeg(now, SiteLonDeg));
                return Task.FromResult(new PolarAlignSolveOutcome(true, ra, dec));
            }
        }

        /// <summary>Where a camera fixed to the mount points after the pole is raised by <paramref name="raiseDeg"/> and the
        /// mount turned <paramref name="turnDeg"/> clockwise about the vertical: a rigid rotation, so far from the pole the
        /// camera's own altitude moves by less than the pole's (cos of its azimuth from the pole).</summary>
        private static (double AltDeg, double AzDeg) MovedWithTheMount(double altDeg, double azDeg, double raiseDeg, double turnDeg, double latDeg) {
            var poleAz = latDeg > 0 ? 0.0 : 180.0;
            double Rad(double d) => d * Math.PI / 180.0;
            // x toward the pole's azimuth, y toward increasing azimuth, z up.
            var x = Math.Cos(Rad(altDeg)) * Math.Cos(Rad(azDeg - poleAz));
            var y = Math.Cos(Rad(altDeg)) * Math.Sin(Rad(azDeg - poleAz));
            var z = Math.Sin(Rad(altDeg));
            var (cr, sr) = (Math.Cos(Rad(raiseDeg)), Math.Sin(Rad(raiseDeg)));
            (x, z) = (x * cr - z * sr, z * cr + x * sr);
            var (ct, st) = (Math.Cos(Rad(turnDeg)), Math.Sin(Rad(turnDeg)));
            (x, y) = (x * ct - y * st, x * st + y * ct);
            return (Math.Asin(Math.Clamp(z, -1, 1)) * 180.0 / Math.PI, poleAz + Math.Atan2(y, x) * 180.0 / Math.PI);
        }

        private static Task<JsonElement> AlignerEventAsync(WsRecorder ws, string state, int skip = 0) =>
            AlignerEventAsync(ws, e => e.GetProperty("state").GetString() == state, skip);

        private static async Task<JsonElement> AlignerEventAsync(WsRecorder ws, Func<JsonElement, bool> match, int skip = 0) {
            bool Is((string Type, JsonElement Payload) e) => e.Type == WsEventCatalog.PolarAlignCorrection && match(e.Payload);
            await ws.WhenAsync(events => events.Skip(skip).Any(Is)).WaitAsync(SignalTimeout).ConfigureAwait(false);
            return ws.Events.Skip(skip).First(Is).Payload;
        }

        private static async Task<(PolarAlignService Svc, WsRecorder Ws)> StartWithAlignerAsync(FakeAligner aligner,
                Moq.Mock<OpenAstroAra.Equipment.Interfaces.Mediator.ITelescopeMediator>? mount = null, IProfileStore? store = null,
                IPolarAlignmentLog? log = null, Microsoft.Extensions.Logging.ILogger<PolarAlignService>? logger = null,
                TimeSpan? moveTimeout = null, double latDeg = SiteLatDeg) {
            var ws = new WsRecorder();
            var svc = NewService(NewFake(), new CorrectedSky(aligner, latDeg), mount, ws, store, log, logger: logger, aligner: aligner);
            svc.AlignerPoll = TimeSpan.FromMilliseconds(5);
            svc.AlignerStopWindow = TimeSpan.FromMilliseconds(60);
            if (moveTimeout is { } t) {
                svc.AlignerMoveTimeout = t;
            }
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            return (svc, ws);
        }

        private static async Task<PolarAlignCorrectionDto?> AlignerStatusAsync(PolarAlignService svc) =>
            (await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).Correction;

        private static void AssertStillAdjustingByHand(PolarAlignService svc, PolarAlignStateDto status) {
            Assert.That(svc.IsActive, Is.True, "the routine carries on by hand");
            Assert.That(status.State, Is.EqualTo("adjusting"));
        }

        [Test]
        public async Task The_motors_probe_each_axis_then_correct_until_the_error_is_inside_the_tolerance() {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10);
            var logger = new CapturingLogger();
            var (svc, ws) = await StartWithAlignerAsync(aligner, logger: logger).ConfigureAwait(false);
            using var _ = svc;

            var converged = await AlignerEventAsync(ws, PolarAlignCorrectionStates.Converged).ConfigureAwait(false);

            var moves = aligner.Moves;
            Assert.That(moves[0], Is.EqualTo((PolarAlignerAxis.Azimuth, PolarAlignService.AlignerProbeArcmin)), "a small test move on azimuth first");
            Assert.That(moves[1], Is.EqualTo((PolarAlignerAxis.Altitude, PolarAlignService.AlignerProbeArcmin)), "then on altitude");
            // After the probe the axis sits 22′ clockwise of the pole and 12′ above its geometric place.
            Assert.That(moves[2].Axis, Is.EqualTo(PolarAlignerAxis.Azimuth));
            Assert.That(moves[2].Arcmin, Is.EqualTo(-PolarAlignService.AlignerGain * 22).Within(0.5), "80 % of the azimuth offset, anticlockwise");
            Assert.That(moves[3].Axis, Is.EqualTo(PolarAlignerAxis.Altitude));
            Assert.That(moves[3].Arcmin, Is.LessThan(-8), "80 % of the altitude error, downward");
            Assert.That(converged.GetProperty("moves").GetInt32(), Is.EqualTo(moves.Length).Or.EqualTo(aligner.Moves.Length));

            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.CurrentErrorArcmin, Is.LessThanOrEqualTo(1.0 + 0.05), "inside the 1′ tolerance");
            AssertStillAdjustingByHand(svc, status);

            var states = ws.Events.Where(e => e.Type == WsEventCatalog.PolarAlignCorrection).Select(e => e.Payload.GetProperty("state").GetString()).ToArray();
            Assert.That(states, Does.Contain("probing").And.Contain("moving").And.Contain("measuring"));
            var firstProbe = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignCorrection).Payload;
            Assert.That(firstProbe.GetProperty("axis").GetString(), Is.EqualTo("azimuth"));
            Assert.That(firstProbe.GetProperty("arcmin").GetDouble(), Is.EqualTo(2.0));
            Assert.That(logger.Entries.Count(e => e.EventId == 4543), Is.GreaterThanOrEqualTo(moves.Length), "one log line per move");
            Assert.That(logger.Entries.Count(e => e.EventId == 4548), Is.GreaterThanOrEqualTo(2), "the device's status after each wait");
            Assert.That(logger.Entries.Count(e => e.EventId == 4549), Is.EqualTo(2), "the test move's result, per axis");

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(aligner.Stops, Is.GreaterThanOrEqualTo(1), "stopping the routine stops the motors");
            Assert.That((await AlignerStatusAsync(svc).ConfigureAwait(false)), Is.Null, "no aligner state once the run is over");
        }

        [Test]
        public async Task At_a_southern_site_the_motors_turn_the_right_way() {
            // The same misplaced axis in the device's directions (20′ clockwise of the pole, 10′ high): east-positive,
            // its azimuth error has the opposite sign from the north, and the move sent must not.
            var store = NewStore();
            store.PutSiteSettings(store.GetSiteSettings() with { LatitudeDeg = -SiteLatDeg });
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10);
            var (svc, ws) = await StartWithAlignerAsync(aligner, store: store, latDeg: -SiteLatDeg).ConfigureAwait(false);
            using var _ = svc;

            var first = await AlignerEventAsync(ws, PolarAlignCorrectionStates.Probing).ConfigureAwait(false);
            var beforeProbe = ws.Events.Last(e => e.Type == WsEventCatalog.PolarAlignProgress).Payload;
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Converged).ConfigureAwait(false);

            Assert.That(first.GetProperty("arcmin").GetDouble(), Is.EqualTo(2.0));
            Assert.That(beforeProbe.GetProperty("azimuth_error_arcmin").GetDouble(), Is.LessThan(-10), "clockwise of the south pole is west: negative east-positive");
            var moves = aligner.Moves;
            Assert.That(moves[2].Axis, Is.EqualTo(PolarAlignerAxis.Azimuth));
            Assert.That(moves[2].Arcmin, Is.EqualTo(-PolarAlignService.AlignerGain * 22).Within(0.5), "anticlockwise, as in the north");
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.CurrentErrorArcmin, Is.LessThanOrEqualTo(1.0 + 0.05));
        }

        [Test]
        public async Task The_night_record_counts_the_moves_the_Polar_Aligner_made() {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10);
            var log = new RecordingLog();
            var (svc, ws) = await StartWithAlignerAsync(aligner, log: log).ConfigureAwait(false);
            using var _ = svc;
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Converged).ConfigureAwait(false);

            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);

            await log.FirstRow.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var row = log.Rows.First();
            Assert.That(row.AlignerMoves, Is.EqualTo(aligner.Moves.Length).And.GreaterThan(2));
        }

        [Test]
        public async Task A_rig_without_a_Polar_Aligner_gets_no_aligner_state() {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { Present = false };
            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;

            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 4).WaitAsync(SignalTimeout).ConfigureAwait(false);

            Assert.That(aligner.Moves, Is.Empty);
            Assert.That(ws.Count(WsEventCatalog.PolarAlignCorrection), Is.Zero);
            Assert.That(await AlignerStatusAsync(svc).ConfigureAwait(false), Is.Null);
        }

        [Test]
        public async Task With_automatic_correction_off_the_person_turns_the_bolts() {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10);
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { PolarAlignerAuto = false });
            var (svc, ws) = await StartWithAlignerAsync(aligner, store: store).ConfigureAwait(false);
            using var _ = svc;

            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Manual).ConfigureAwait(false);
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 4).WaitAsync(SignalTimeout).ConfigureAwait(false);

            Assert.That(aligner.Moves, Is.Empty);
        }

        [Test]
        public async Task A_Polar_Aligner_that_only_runs_its_own_loop_is_never_driven() {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { InfoReply = AlignerInfo(tier1: false) };
            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;

            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 4).WaitAsync(SignalTimeout).ConfigureAwait(false);

            Assert.That(aligner.Moves, Is.Empty);
            Assert.That(ws.Count(WsEventCatalog.PolarAlignCorrection), Is.Zero);
        }

        [Test]
        public async Task No_move_is_larger_than_the_device_allows() {
            var aligner = new FakeAligner(azOffset: 40, altOffset: 25) { InfoReply = AlignerInfo(maxMove: 6) };
            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;

            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Converged).ConfigureAwait(false);

            Assert.That(aligner.Moves.Select(m => Math.Abs(m.Arcmin)), Has.All.LessThanOrEqualTo(6.0));
            Assert.That(aligner.Moves[0].Arcmin, Is.EqualTo(1.5), "the test move is a quarter of the cap when the cap is small");
            Assert.That(aligner.Moves.Count(m => Math.Abs(m.Arcmin) == 6.0), Is.GreaterThan(2), "the large error is taken in capped steps");
        }

        private static async Task<(PolarAlignService Svc, JsonElement Stopped, PolarAlignStateDto Status)> RunUntilStoppedAsync(
                FakeAligner aligner, Moq.Mock<OpenAstroAra.Equipment.Interfaces.Mediator.ITelescopeMediator>? mount = null,
                TimeSpan? moveTimeout = null) {
            var (svc, ws) = await StartWithAlignerAsync(aligner, mount, moveTimeout: moveTimeout).ConfigureAwait(false);
            var stopped = await AlignerEventAsync(ws, PolarAlignCorrectionStates.Stopped).ConfigureAwait(false);
            // The frame after the stop: the routine still measures, and sends nothing more to the motors.
            var movesAtStop = aligner.Moves.Length;
            var progress = ws.Count(WsEventCatalog.PolarAlignProgress);
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, progress + 3).WaitAsync(SignalTimeout).ConfigureAwait(false);
            Assert.That(aligner.Moves, Has.Length.EqualTo(movesAtStop), "a stopped aligner gets no further move");
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.Correction?.State, Is.EqualTo(PolarAlignCorrectionStates.Stopped));
            AssertStillAdjustingByHand(svc, status);
            return (svc, stopped, status);
        }

        [Test]
        public async Task A_test_move_that_goes_the_wrong_way_stops_the_motors() {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { AzResponse = -1 };
            var (svc, stopped, _) = await RunUntilStoppedAsync(aligner).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectionCauses.WrongDirection));
            Assert.That(stopped.GetProperty("axis").GetString(), Is.EqualTo("azimuth"));
            Assert.That(aligner.Moves, Has.Length.EqualTo(2), "only the two test moves went out");
            Assert.That(aligner.Stops, Is.GreaterThanOrEqualTo(1));
        }

        [TestCase(3.0)]
        [TestCase(0.3)]
        public async Task A_test_move_of_the_wrong_size_stops_the_motors(double response) {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { AltResponse = response };
            var (svc, stopped, _) = await RunUntilStoppedAsync(aligner).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectionCauses.WrongScale));
            Assert.That(stopped.GetProperty("axis").GetString(), Is.EqualTo("altitude"));
            Assert.That(aligner.Stops, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public async Task A_device_that_does_not_take_up_its_backlash_may_lose_most_of_the_test_move() {
            // 0.3 of the amount asked: a stop for a device that says it handles backlash, not for one that does not.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { AltResponse = 0.3, InfoReply = AlignerInfo(backlashHandled: false) };
            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;

            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Converged).ConfigureAwait(false);

            Assert.That(ws.Events.Any(e => e.Type == WsEventCatalog.PolarAlignCorrection
                && e.Payload.GetProperty("state").GetString() == PolarAlignCorrectionStates.Stopped), Is.False);
        }

        [Test]
        public async Task A_fault_the_device_reports_after_a_move_stops_the_motors_with_its_text() {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { FaultAfterMoves = "end of travel", FaultFromMove = 3 };
            var (svc, stopped, _) = await RunUntilStoppedAsync(aligner).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectionCauses.DeviceFault));
            Assert.That(stopped.GetProperty("device_text").GetString(), Is.EqualTo("end of travel"));
            Assert.That(aligner.Moves, Has.Length.EqualTo(3), "the failed move is the last: nothing is corrected from it");
            Assert.That(aligner.Stops, Is.GreaterThanOrEqualTo(1));
        }

        [TestCase(nameof(PolarAlignerFailure.NotCalibrated), PolarAlignCorrectionCauses.NotCalibrated)]
        [TestCase(nameof(PolarAlignerFailure.Refused), PolarAlignCorrectionCauses.DeviceRefused)]
        [TestCase(nameof(PolarAlignerFailure.Protocol), PolarAlignCorrectionCauses.DeviceRefused)]
        public async Task A_move_the_device_refuses_stops_the_motors_with_its_text(string failure, string cause) {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) {
                OnMove = n => n == 1 ? new PolarAlignerException(Enum.Parse<PolarAlignerFailure>(failure), "the board has no factors") : null,
            };
            var (svc, stopped, _) = await RunUntilStoppedAsync(aligner).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(cause));
            Assert.That(stopped.GetProperty("device_text").GetString(), Is.EqualTo("the board has no factors"));
            Assert.That(aligner.Moves, Is.Empty);
            Assert.That(stopped.GetProperty("moves").GetInt32(), Is.Zero, "a refused move is not a move made");
        }

        [Test]
        public async Task An_error_that_grows_after_two_corrections_in_a_row_stops_the_motors() {
            // The probe passes; from the third move on the axes run backwards, so every correction makes it worse.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10);
            aligner.OnMove = n => {
                if (n == 3) {
                    aligner.AzResponse = -1;
                    aligner.AltResponse = -1;
                }
                return null;
            };
            var (svc, stopped, _) = await RunUntilStoppedAsync(aligner).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectionCauses.Diverging));
            Assert.That(aligner.Moves, Has.Length.EqualTo(6), "two test moves and two corrections of both axes");
            Assert.That(aligner.Stops, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public async Task A_move_that_never_finishes_is_stopped() {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { NeverFinishes = true };
            var (svc, stopped, _) = await RunUntilStoppedAsync(aligner, moveTimeout: TimeSpan.FromMilliseconds(60)).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectionCauses.Timeout));
            Assert.That(aligner.Moves, Has.Length.EqualTo(1));
            Assert.That(aligner.Stops, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public async Task A_lost_link_stops_the_motors_as_soon_as_the_device_answers_again() {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { StopFails = true };
            aligner.OnMove = n => {
                if (n != 2) {
                    return null;
                }
                aligner.StatusFails = true; // the whole link is down: Stop and Status both fail
                return new PolarAlignerException(PolarAlignerFailure.LinkLost, "connection refused");
            };
            var (svc, stopped, _) = await RunUntilStoppedAsync(aligner).ConfigureAwait(false);
            using var __ = svc;
            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectionCauses.LinkLost));
            Assert.That(aligner.Stops, Is.Zero, "the Stop could not be delivered yet");

            aligner.StopFails = false;
            aligner.StatusFails = false;
            var ws = Recorders.GetValue(svc, _ => throw new InvalidOperationException());
            var progress = ws.Count(WsEventCatalog.PolarAlignProgress);
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, progress + 2).WaitAsync(SignalTimeout).ConfigureAwait(false);

            Assert.That(aligner.Stops, Is.GreaterThanOrEqualTo(1), "the owed Stop goes out at the next measurement");
        }

        [Test]
        public async Task A_stop_owed_after_a_lost_link_goes_to_the_reconnected_device() {
            // The old connection never answers again; the reconnect is a new object from the source.
            var dead = new FakeAligner(azOffset: 20, altOffset: 10) { StopFails = true };
            dead.OnMove = n => {
                if (n != 2) {
                    return null;
                }
                dead.StatusFails = true;
                return new PolarAlignerException(PolarAlignerFailure.LinkLost, "connection refused");
            };
            var source = new SwappableSource(dead);
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), new CorrectedSky(dead), ws: ws, aligner: source);
            svc.AlignerPoll = TimeSpan.FromMilliseconds(5);
            svc.AlignerStopWindow = TimeSpan.FromMilliseconds(60);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Stopped).ConfigureAwait(false);

            var reconnected = new FakeAligner(azOffset: 0, altOffset: 0);
            source.Device = reconnected;
            var progress = ws.Count(WsEventCatalog.PolarAlignProgress);
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, progress + 2).WaitAsync(SignalTimeout).ConfigureAwait(false);

            Assert.That(reconnected.Stops, Is.GreaterThanOrEqualTo(1), "the Stop reaches the device that answers now");
            Assert.That(reconnected.Moves, Is.Empty);
        }

        [Test]
        public async Task An_axis_inside_half_the_tolerance_on_the_sky_is_left_alone() {
            // After the +2′ test moves the axis sits 0.57′ of rotation clockwise (0.4′ on the sky at 45°, inside
            // half of the 1′ tolerance) and 4′ high: only altitude is corrected.
            var aligner = new FakeAligner(azOffset: -1.43, altOffset: 2);
            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;

            await AlignerEventAsync(ws, e => e.GetProperty("state").GetString() == PolarAlignCorrectionStates.Moving).ConfigureAwait(false);
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Converged).ConfigureAwait(false);

            Assert.That(aligner.Moves.Skip(2).Select(m => m.Axis), Has.All.EqualTo(PolarAlignerAxis.Altitude));
        }

        [Test]
        public async Task A_stop_that_comes_from_outside_the_routine_ends_its_motor_loop() {
            // The emergency stop and the device's own Stop route reach the routine through this door.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { NeverFinishes = true };
            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Probing).ConfigureAwait(false);
            IPolarAlignerDriver driver = svc;
            Assert.That(driver.DrivesPolarAligner, Is.True);

            await driver.StopPolarAlignerAsync().ConfigureAwait(false);

            aligner.NeverFinishes = false;
            var movesAtStop = aligner.Moves.Length;
            var progress = ws.Count(WsEventCatalog.PolarAlignProgress);
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, progress + 3).WaitAsync(SignalTimeout).ConfigureAwait(false);
            Assert.That(aligner.Moves, Has.Length.EqualTo(movesAtStop), "no move follows the stop: the altitude test move never goes out");
            Assert.That(movesAtStop, Is.EqualTo(1));
            Assert.That(driver.DrivesPolarAligner, Is.False);
            Assert.That(aligner.Stops, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public async Task A_stop_from_outside_returns_to_its_caller_before_a_slow_device_answers() {
            // The emergency stop bounds each rung only once the rung's call has returned a task: a device
            // Stop made on the caller's thread would hold the whole ladder for the HTTP timeout.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10);
            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Converged).ConfigureAwait(false);
            IPolarAlignerDriver driver = svc;
            // #2069 — a window long enough that the verified stop still waits on the held Stop when the test releases it.
            svc.AlignerStopWindow = TimeSpan.FromSeconds(10);
            aligner.StopAnswers.Reset();
            try {
                var returned = Task.Factory.StartNew(() => driver.StopPolarAlignerAsync(), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);

                var stopping = await returned.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                Assert.That(stopping.IsCompleted, Is.False, "the device has not answered yet");
                aligner.StopAnswers.Set();
                await stopping.WaitAsync(SignalTimeout).ConfigureAwait(false);
                Assert.That(aligner.Stops, Is.GreaterThanOrEqualTo(1));
            } finally {
                aligner.StopAnswers.Set();
            }
        }

        [Test]
        public async Task A_device_that_cannot_move_while_the_mount_tracks_is_not_sent_a_move() {
            // The bench mount keeps reporting tracking on: since #2072 the live readout ends the run before the
            // device is asked for anything (the aligner's own MountTracking check stays as a second guard).
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { InfoReply = AlignerInfo(movesWhileTracking: false) };
            var mount = NewMount(tracking: true);
            mount.Setup(m => m.SetTrackingEnabled(Moq.It.IsAny<bool>())).Returns(true); // accepted, never applied
            var (svc, ws) = await StartWithAlignerAsync(aligner, mount).ConfigureAwait(false);
            using var __ = svc;

            var status = await PollStateAsync(svc, "failed").ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("failed"));
            Assert.That(ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload.GetProperty("reason").GetString(),
                Is.EqualTo(PolarAlignService.TrackingNotOff));
            Assert.That(aligner.Moves, Is.Empty);
        }

        [Test]
        public async Task The_person_can_stop_the_motors_and_resume_them() {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { NeverFinishes = true };
            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Probing).ConfigureAwait(false);

            await svc.StopAlignerAsync(null, CancellationToken.None).ConfigureAwait(false);

            var stopped = await AlignerEventAsync(ws, PolarAlignCorrectionStates.Stopped).ConfigureAwait(false);
            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectionCauses.StoppedByUser));
            Assert.That(aligner.Stops, Is.GreaterThanOrEqualTo(1));
            AssertStillAdjustingByHand(svc, await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false));

            aligner.NeverFinishes = false;
            var mark = ws.Events.Count;
            var movesBefore = aligner.Moves.Length;
            await svc.ResumeAlignerAsync(null, CancellationToken.None).ConfigureAwait(false);

            var probe = await AlignerEventAsync(ws, PolarAlignCorrectionStates.Probing, mark).ConfigureAwait(false);
            Assert.That(probe.GetProperty("axis").GetString(), Is.EqualTo("azimuth"), "a resume starts from a new test move");
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Converged, mark).ConfigureAwait(false);
            Assert.That(aligner.Moves, Has.Length.GreaterThan(movesBefore + 2));
        }

        [TestCase(false, true, true)]   // no Polar Aligner on the rig
        [TestCase(true, false, true)]   // one that only runs its own loop
        [TestCase(true, false, false)]  // the same, with automatic correction off
        public async Task A_running_routine_with_no_Polar_Aligner_to_drive_refuses_stop_and_reports_no_state(
                bool present, bool tier1, bool auto) {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { Present = present, InfoReply = AlignerInfo(tier1: tier1) };
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { PolarAlignerAuto = auto });
            var (svc, ws) = await StartWithAlignerAsync(aligner, store: store).ConfigureAwait(false);
            using var _ = svc;
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 4).WaitAsync(SignalTimeout).ConfigureAwait(false);

            await Assert.ThatAsync(() => svc.StopAlignerAsync(null, CancellationToken.None), Throws.InvalidOperationException).ConfigureAwait(false);
            await Assert.ThatAsync(() => svc.ResumeAlignerAsync(null, CancellationToken.None), Throws.InvalidOperationException).ConfigureAwait(false);

            Assert.That(await AlignerStatusAsync(svc).ConfigureAwait(false), Is.Null);
            Assert.That(ws.Count(WsEventCatalog.PolarAlignCorrection), Is.Zero);
        }

        [Test]
        public async Task Stop_and_resume_are_refused_when_there_is_nothing_to_stop_or_resume() {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10);
            using var idle = NewService(NewFake(), new CorrectedSky(aligner), aligner: aligner);
            await Assert.ThatAsync(() => idle.StopAlignerAsync(null, CancellationToken.None), Throws.InvalidOperationException).ConfigureAwait(false);
            await Assert.ThatAsync(() => idle.ResumeAlignerAsync(null, CancellationToken.None), Throws.InvalidOperationException).ConfigureAwait(false);

            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Converged).ConfigureAwait(false);
            await Assert.ThatAsync(() => svc.ResumeAlignerAsync(null, CancellationToken.None), Throws.InvalidOperationException,
                "an aligner that is not stopped has nothing to resume").ConfigureAwait(false);
        }

        // ── #2069 (ARA-19): verified stop, the follow check and the run's caps; #2102: the MountTracking guard ──

        private static bool IsStoppedWith(JsonElement e, string cause) =>
            e.GetProperty("state").GetString() == PolarAlignCorrectionStates.Stopped
            && e.TryGetProperty("cause", out var c) && c.GetString() == cause;

        [Test]
        public async Task Aligner_stop_not_confirmed_is_reported_and_owed() {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { NeverFinishes = true };
            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Probing).ConfigureAwait(false);
            aligner.StopsIgnored = int.MaxValue; // the motors keep turning through every Stop

            await svc.StopAlignerAsync(null, CancellationToken.None).ConfigureAwait(false);

            await AlignerEventAsync(ws, e => IsStoppedWith(e, PolarAlignCorrectionCauses.StopNotConfirmed)).ConfigureAwait(false);
            Assert.That(aligner.Stops, Is.GreaterThanOrEqualTo(PolarAlignerVerifiedStop.Sends), "the Stop was sent again before giving up");
            await Assert.ThatAsync(() => svc.ResumeAlignerAsync(null, CancellationToken.None), Throws.InvalidOperationException,
                "no move while a Stop is owed").ConfigureAwait(false);

            // The device obeys again: the owed Stop goes out at the next measurement and is confirmed.
            var stopsBefore = aligner.Stops;
            var mark = ws.Events.Count;
            aligner.StopsIgnored = 0;
            await AlignerEventAsync(ws, e => IsStoppedWith(e, PolarAlignCorrectionCauses.StoppedByUser), mark).ConfigureAwait(false);
            Assert.That(aligner.Stops, Is.GreaterThan(stopsBefore), "the owed Stop went out again");
            Assert.That(aligner.Moving, Is.False);
            Assert.That(aligner.Moves, Has.Length.EqualTo(1), "nothing moved while the Stop was owed");
        }

        [Test]
        public async Task Polar_align_stop_mid_aligner_move_stops_and_verifies_the_device() {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { NeverFinishes = true };
            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Probing).ConfigureAwait(false);
            aligner.StopsIgnored = 1; // the first Stop is lost on the way

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(aligner.Moving, Is.False, "Stop was sent again until the device read still");
            Assert.That(aligner.Stops, Is.GreaterThanOrEqualTo(2));
            Assert.That(aligner.StatusReadsAfterLastStop, Is.GreaterThanOrEqualTo(1), "the stop was checked on a Status read after it");
            Assert.That(aligner.Moves, Has.Length.EqualTo(1));
        }

        [Test]
        public async Task A_device_whose_position_moves_the_wrong_way_is_stopped_after_one_move() {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { AzResponse = -1, ReportsPosition = true };
            var (svc, stopped, _) = await RunUntilStoppedAsync(aligner).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectionCauses.WrongDirection));
            Assert.That(stopped.GetProperty("axis").GetString(), Is.EqualTo("azimuth"));
            Assert.That(aligner.Moves, Has.Length.EqualTo(1), "stopped on the device's own position, before the next move");
            Assert.That(aligner.Stops, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public async Task Aligner_stops_at_the_run_travel_cap() {
            // The test moves land; then each axis turns a twentieth of the amount sent, so the error shrinks a little
            // each time (never grows) and the moves add up on one side.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10);
            aligner.OnMove = n => {
                if (n == 3) {
                    aligner.AzResponse = 0.05;
                    aligner.AltResponse = 0.05;
                }
                return null;
            };
            var (svc, stopped, _) = await RunUntilStoppedAsync(aligner).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectionCauses.TravelLimit));
            Assert.That(stopped.GetProperty("axis").GetString(), Is.EqualTo("azimuth"));
            var cap = PolarAlignService.AlignerTravelCap(20);
            var netAz = aligner.Moves.Where(m => m.Axis == PolarAlignerAxis.Azimuth).Sum(m => m.Arcmin);
            Assert.That(Math.Abs(netAz), Is.LessThanOrEqualTo(cap + 1), "no move took the axis past its cap");
            Assert.That(Math.Abs(netAz), Is.GreaterThan(cap - 20), "it stopped at the cap, not before");
            Assert.That(aligner.Moves, Has.Length.LessThan(PolarAlignService.AlignerMoveLimit));
            Assert.That(aligner.Stops, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public async Task Aligner_stops_after_the_move_count_cap() {
            // After the test moves each axis overshoots (2.4 times the amount sent): the error changes side and shrinks by
            // 8 % a correction, so it never grows and is still outside the tolerance after 40 moves.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10);
            aligner.OnMove = n => {
                if (n == 3) {
                    aligner.AzResponse = 2.4;
                    aligner.AltResponse = 2.4;
                }
                return null;
            };
            var (svc, stopped, _) = await RunUntilStoppedAsync(aligner).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectionCauses.MoveLimit));
            Assert.That(aligner.Moves, Has.Length.EqualTo(PolarAlignService.AlignerMoveLimit));
            Assert.That(stopped.GetProperty("moves").GetInt32(), Is.EqualTo(PolarAlignService.AlignerMoveLimit));
        }

        [Test]
        public async Task A_device_that_cannot_stop_is_sent_at_most_ten_arcminutes_a_move() {
            var aligner = new FakeAligner(azOffset: 40, altOffset: 25) { InfoReply = AlignerInfo(canStop: false) };
            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;

            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Converged).ConfigureAwait(false);

            Assert.That(aligner.Moves.Select(m => Math.Abs(m.Arcmin)), Has.All.LessThanOrEqualTo(PolarAlignService.AlignerNoStopMoveCapArcmin));
            Assert.That(aligner.Moves.Count(m => Math.Abs(m.Arcmin) == PolarAlignService.AlignerNoStopMoveCapArcmin), Is.GreaterThan(2),
                "the large error is taken in 10′ steps");
        }

        [Test]
        public async Task A_mount_that_reads_tracking_on_at_the_aligner_step_stops_a_device_that_cannot_move_while_tracking() {
            // #2102 — tracking that comes on between the live loop's own check and the aligner's read (here: as the
            // aligner is first read) meets the aligner's MountTracking guard, the second line of defence.
            var tracking = false;
            var mount = NewMount(tracking: false);
            var info = new OpenAstroAra.Equipment.Equipment.MyTelescope.TelescopeInfo {
                Connected = true, SiderealTime = 20.0, SideOfPier = OpenAstroAra.Core.Enums.PierSide.pierWest,
            };
            mount.Setup(m => m.GetInfo()).Returns(() => {
                info.TrackingEnabled = tracking;
                return info;
            });
            mount.Setup(m => m.SetTrackingEnabled(Moq.It.IsAny<bool>())).Returns((bool on) => {
                tracking = on;
                return true;
            });
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) {
                InfoReply = AlignerInfo(movesWhileTracking: false),
                OnInfo = () => tracking = true,
            };
            var (svc, ws) = await StartWithAlignerAsync(aligner, mount).ConfigureAwait(false);
            using var _ = svc;

            var stopped = await AlignerEventAsync(ws, PolarAlignCorrectionStates.Stopped).ConfigureAwait(false);

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectionCauses.MountTracking));
            Assert.That(aligner.Moves, Is.Empty, "no move was sent");
        }

        [Test]
        public async Task A_polar_align_stop_the_aligner_ignores_ends_failed_with_aligner_did_not_stop() {
            // ARA-19: the Stop at the run's end is verified, and one the device never confirms is told, never "stopped".
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { NeverFinishes = true };
            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Probing).ConfigureAwait(false);
            aligner.StopsIgnored = int.MaxValue;

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var error = ws.Events.Single(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.AlignerDidNotStop));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("Cut the Polar Aligner's power"));
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStopped), Is.Zero, "never \"stopped\" while the aligner may still move");
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("failed"));
            Assert.That(aligner.SourceStopOwed, Is.True, "the Stop is owed to the next one that drives it");
        }

        [Test]
        public async Task A_stop_owed_from_before_the_run_is_reported_and_nothing_moves_until_it_is_confirmed() {
            // The Stop route or Emergency Stop left a Stop owed; at this run's first measurement the resend fails too.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { SourceStopOwed = true, StopFails = true };
            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;

            await AlignerEventAsync(ws, e => IsStoppedWith(e, PolarAlignCorrectionCauses.StopNotConfirmed)).ConfigureAwait(false);
            Assert.That(aligner.Moves, Is.Empty, "nothing moves while the Stop is owed");
            Assert.That((await AlignerStatusAsync(svc).ConfigureAwait(false))?.Cause, Is.EqualTo(PolarAlignCorrectionCauses.StopNotConfirmed));
            await Assert.ThatAsync(() => svc.ResumeAlignerAsync(null, CancellationToken.None), Throws.InvalidOperationException,
                "no Resume while the Stop is owed").ConfigureAwait(false);

            // The device answers again: the owed Stop is confirmed at a later measurement. The run waits for Resume, which
            // drives it from a fresh test move.
            var mark = ws.Events.Count;
            aligner.StopFails = false;
            await AlignerEventAsync(ws, e => IsStoppedWith(e, PolarAlignCorrectionCauses.StopConfirmed), mark).ConfigureAwait(false);
            Assert.That(aligner.SourceStopOwed, Is.False);
            Assert.That(aligner.Moves, Is.Empty, "the run waits for Resume");

            mark = ws.Events.Count;
            await svc.ResumeAlignerAsync(null, CancellationToken.None).ConfigureAwait(false);
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Probing, mark).ConfigureAwait(false);
            // The measurement after the probing state: its move has gone out by then.
            var progress = ws.Count(WsEventCatalog.PolarAlignProgress);
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, progress + 1).WaitAsync(SignalTimeout).ConfigureAwait(false);
            Assert.That(aligner.Moves[0], Is.EqualTo((PolarAlignerAxis.Azimuth, PolarAlignService.AlignerProbeArcmin)),
                "Resume starts from a fresh test move");
        }

        [Test]
        public async Task An_owed_stop_confirmed_later_in_the_run_sends_no_move_until_Resume() {
            // #2069: a device whose Stop from before the run was confirmed only at a later measurement does not
            // start moving again by itself; the run says so and waits for the person's Resume.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { SourceStopOwed = true, StopFails = true };
            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;
            await AlignerEventAsync(ws, e => IsStoppedWith(e, PolarAlignCorrectionCauses.StopNotConfirmed)).ConfigureAwait(false);

            var mark = ws.Events.Count;
            aligner.StopFails = false;
            await AlignerEventAsync(ws, e => IsStoppedWith(e, PolarAlignCorrectionCauses.StopConfirmed), mark).ConfigureAwait(false);
            var progress = ws.Count(WsEventCatalog.PolarAlignProgress);
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, progress + 3).WaitAsync(SignalTimeout).ConfigureAwait(false);

            Assert.That(aligner.Moves, Is.Empty, "no move after the Stop is confirmed, until Resume");
            Assert.That(ws.Events.Skip(mark).Where(e => e.Type == WsEventCatalog.PolarAlignCorrection)
                    .Select(e => e.Payload.GetProperty("state").GetString()),
                Has.All.EqualTo(PolarAlignCorrectionStates.Stopped), "the run never left stopped by itself");
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.Correction?.Cause, Is.EqualTo(PolarAlignCorrectionCauses.StopConfirmed));
            AssertStillAdjustingByHand(svc, status);
        }

        [Test]
        public async Task A_device_that_refuses_Stop_but_reads_still_is_confirmed_and_the_run_moves() {
            // A device whose driver answers PolarAlign:Stop with an error (it has no Stop): the answer shows the link works,
            // so a still read confirms the stop, and an owed Stop does not keep every later run from moving it.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { SourceStopOwed = true, StopRefused = true };
            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;

            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Probing).ConfigureAwait(false);
            Assert.That(aligner.SourceStopOwed, Is.False, "the stop was confirmed");
            Assert.That(aligner.Moves, Is.Not.Empty);
        }

        /// <summary>The fake sky, with a hold the test can put on the next solve: the run waits there, between two
        /// measurements, so nothing of its own (an owed resend) runs while the test drives a route.</summary>
        private sealed class HoldableSky(IPolarAlignFrameSolver inner) : IPolarAlignFrameSolver {
            private volatile TaskCompletionSource? _hold;
            private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Completes once a solve waits on the hold.</summary>
            public Task Held => _held.Task;
            public void Hold() => _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Release() => _hold?.TrySetResult();
            public Task<double?> MeasureFocalLengthAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) =>
                inner.MeasureFocalLengthAsync(fitsPath, binning, hintRa, hintDec, ct);
            public async Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                if (_hold is { } hold) {
                    _held.TrySetResult();
                    await hold.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                return await inner.SolveAsync(fitsPath, binning, hintRa, hintDec, ct).ConfigureAwait(false);
            }
        }

        [Test]
        public async Task The_correction_Stop_route_sends_an_owed_Stop_to_the_device_before_the_run_has_read_it() {
            // #2069 — a Stop owed from before the run is resent before the run reads its device, so the run's own device is
            // still unknown when it reports stop_not_confirmed. The app's Stop button (POST …/correction/stop) must still
            // reach the device, and its confirmation turns the run to stop_confirmed: it waits for Resume.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { SourceStopOwed = true, StopFails = true };
            var sky = new HoldableSky(new CorrectedSky(aligner));
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), sky, ws: ws, aligner: aligner);
            svc.AlignerPoll = TimeSpan.FromMilliseconds(5);
            svc.AlignerStopWindow = TimeSpan.FromMilliseconds(60);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await AlignerEventAsync(ws, e => IsStoppedWith(e, PolarAlignCorrectionCauses.StopNotConfirmed)).ConfigureAwait(false);
            sky.Hold();
            await sky.Held.WaitAsync(SignalTimeout).ConfigureAwait(false);
            aligner.StopFails = false;

            await svc.StopAlignerAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(aligner.Stops, Is.GreaterThanOrEqualTo(1), "the route's Stop reached the device");
            Assert.That(aligner.SourceStopOwed, Is.False, "and its confirmation clears the owed Stop");
            Assert.That((await AlignerStatusAsync(svc).ConfigureAwait(false))?.Cause, Is.EqualTo(PolarAlignCorrectionCauses.StopConfirmed));

            // The run measures on and sends nothing by itself; Resume is accepted now.
            sky.Release();
            var progress = ws.Count(WsEventCatalog.PolarAlignProgress);
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, progress + 3).WaitAsync(SignalTimeout).ConfigureAwait(false);
            Assert.That(aligner.Moves, Is.Empty, "no move until Resume");
            Assert.That((await AlignerStatusAsync(svc).ConfigureAwait(false))?.Cause, Is.EqualTo(PolarAlignCorrectionCauses.StopConfirmed));
            await svc.ResumeAlignerAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task The_correction_Stop_route_sends_an_owed_Stop_to_the_reconnected_device() {
            // #2069 — after a lost link and a reconnect, the run's own device object is the dead connection: the app's Stop
            // button goes to the device that answers now, as the run's owed resend does.
            var dead = new FakeAligner(azOffset: 20, altOffset: 10) { StopFails = true };
            dead.OnMove = n => {
                if (n != 2) {
                    return null;
                }
                dead.StatusFails = true;
                return new PolarAlignerException(PolarAlignerFailure.LinkLost, "connection refused");
            };
            var source = new SwappableSource(dead);
            var sky = new HoldableSky(new CorrectedSky(dead));
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), sky, ws: ws, aligner: source);
            svc.AlignerPoll = TimeSpan.FromMilliseconds(5);
            svc.AlignerStopWindow = TimeSpan.FromMilliseconds(60);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await AlignerEventAsync(ws, e => IsStoppedWith(e, PolarAlignCorrectionCauses.LinkLost)).ConfigureAwait(false);
            sky.Hold();
            await sky.Held.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var reconnected = new FakeAligner(azOffset: 0, altOffset: 0);
            source.Device = reconnected;

            await svc.StopAlignerAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(reconnected.Stops, Is.GreaterThanOrEqualTo(1), "the Stop reached the device that answers now");
            Assert.That(reconnected.Moves, Is.Empty);
            await svc.ResumeAlignerAsync(null, CancellationToken.None).ConfigureAwait(false); // confirmed: no Stop is owed
            sky.Release();
        }

        [Test]
        public async Task A_Status_read_that_loses_the_link_before_a_move_stops_the_run_with_link_lost() {
            // #2069 — the follow check reads where each axis stands before the move: a lost link there is a stop, and no
            // move goes out.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { StatusFails = true };
            var (svc, stopped, _) = await RunUntilStoppedAsync(aligner).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectionCauses.LinkLost));
            Assert.That(aligner.Moves, Is.Empty, "stopped before any move");
        }

        /// <summary>Logs nothing; completes <see cref="Logged"/> on the first entry with the event id.</summary>
        private sealed class EventSignalLogger(int eventId) : Microsoft.Extensions.Logging.ILogger<PolarAlignService> {
            private readonly TaskCompletionSource _logged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task Logged => _logged.Task;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId id,
                    TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                if (id.Id == eventId) {
                    _logged.TrySetResult();
                }
            }
        }

        /// <summary>A night log whose row write waits on <see cref="Gate"/>: it holds the stop path between its read of the
        /// run's findings and its last event.</summary>
        private sealed class GatedLog : IPolarAlignmentLog {
            public Func<Task>? Gate { get; set; }
            public async Task InsertAsync(PolarAlignmentRecord record, CancellationToken ct) {
                if (Gate is { } gate) {
                    await gate().ConfigureAwait(false);
                }
            }
            public Task<PolarAlignmentRecord?> GetLatestMeasuredAsync(DateTimeOffset since, CancellationToken ct) =>
                Task.FromResult<PolarAlignmentRecord?>(null);
        }

        [Test]
        public async Task An_unconfirmed_aligner_stop_noted_after_the_stop_path_read_it_is_still_told_once() {
            // #2069 — a run that outlives the unwind grace notes its unconfirmed end stop after the stop path read the run's
            // findings (and found none) but before it marked its end reported. The stop path tells it as it marks.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { NeverFinishes = true };
            var logger = new EventSignalLogger(4641); // the run's end could not confirm the stop
            var log = new GatedLog();
            var (svc, ws) = await StartWithAlignerAsync(aligner, log: log, logger: logger).ConfigureAwait(false);
            using var _ = svc;
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Probing).ConfigureAwait(false);
            aligner.StopsIgnored = int.MaxValue;
            svc.RunUnwindGrace = TimeSpan.FromMilliseconds(20);   // the stop path goes on while the run's end stop polls
            svc.AlignerStopWindow = TimeSpan.FromMilliseconds(400);
            // The stop path's row write waits until the run has noted its end (just after the log line).
            log.Gate = async () => {
                await logger.Logged.WaitAsync(SignalTimeout).ConfigureAwait(false);
                await Task.Delay(200).ConfigureAwait(false);
            };

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            static bool IsAlignerDidNotStop((string Type, JsonElement Payload) e) => e.Type == WsEventCatalog.PolarAlignError
                && e.Payload.GetProperty("reason").GetString() == PolarAlignService.AlignerDidNotStop;
            await ws.WhenAsync(events => events.Any(IsAlignerDidNotStop)).WaitAsync(SignalTimeout).ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);
            Assert.That(ws.Events.Count(IsAlignerDidNotStop), Is.EqualTo(1), "told once");
        }

        /// <summary>The fake sky, with a solver-setup fault the test can switch on, and the run's work dir (where each frame
        /// it solves lies).</summary>
        private sealed class FaultableSky(IPolarAlignFrameSolver inner) : IPolarAlignFrameSolver {
            private volatile bool _fail;
            private volatile string? _workDir;
            public void Fail() => _fail = true;
            public string? WorkDir => _workDir;
            public Task<double?> MeasureFocalLengthAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) =>
                inner.MeasureFocalLengthAsync(fitsPath, binning, hintRa, hintDec, ct);
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                _workDir = System.IO.Path.GetDirectoryName(fitsPath);
                return _fail
                    ? throw new OpenAstroAra.PlateSolving.PlateSolverConfigurationException("the guide scope's focal length is not set")
                    : inner.SolveAsync(fitsPath, binning, hintRa, hintDec, ct);
            }
        }

        [Test]
        public async Task A_failure_after_the_aligner_moved_keeps_its_reason_and_adds_the_aligner_cut_power_sentence() {
            // #2069 — the run moved the motors; the device ignores every Stop (the timed-out test move's, the resends, the
            // end's). A failure then keeps its own reason, and says the Polar Aligner may still move.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { NeverFinishes = true, StopsIgnored = int.MaxValue };
            var sky = new FaultableSky(new CorrectedSky(aligner));
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), sky, ws: ws, aligner: aligner);
            svc.AlignerPoll = TimeSpan.FromMilliseconds(5);
            svc.AlignerStopWindow = TimeSpan.FromMilliseconds(60);
            svc.AlignerMoveTimeout = TimeSpan.FromMilliseconds(100);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await AlignerEventAsync(ws, e => IsStoppedWith(e, PolarAlignCorrectionCauses.StopNotConfirmed)).ConfigureAwait(false);
            Assert.That(aligner.Moves, Is.Not.Empty, "the run moved the motors");

            sky.Fail();

            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("solver_configuration"), "the failure keeps its own reason");
            var message = error.GetProperty("message").GetString();
            Assert.That(message, Does.Contain("focal length is not set"));
            Assert.That(message, Does.Contain("Cut the Polar Aligner's power"));
            Assert.That(message, Does.Not.Contain("Cut the mount's power"), "the mount's stop was confirmed");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.EqualTo(1), "one error, no separate aligner_did_not_stop");
        }

        [Test]
        public async Task A_stop_neither_the_mount_nor_the_aligner_confirms_tells_one_mount_did_not_stop_with_both_sentences() {
            // #2066 + #2069 — both stops at the run's end are unconfirmed: one mount_did_not_stop (it wins), whose message
            // asks to cut the mount's power and the Polar Aligner's.
            var mount = new FakeStopMount(tracking: true);
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { NeverFinishes = true };
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            using var svc = NewService(NewFake(), new CorrectedSky(aligner), mount.Mock, ws, aligner: aligner);
            svc.AlignerPoll = TimeSpan.FromMilliseconds(5);
            svc.AlignerStopWindow = TimeSpan.FromMilliseconds(60);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Probing).ConfigureAwait(false);
            aligner.StopsIgnored = int.MaxValue;
            mount.IgnoresStops = true;

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.MountDidNotStop));
            var message = error.GetProperty("message").GetString();
            Assert.That(message, Does.Contain("Cut the mount's power"));
            Assert.That(message, Does.Contain("Cut the Polar Aligner's power"));
            await Task.Delay(100).ConfigureAwait(false);
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.EqualTo(1), "told once, not a second aligner_did_not_stop");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStopped), Is.Zero);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("failed"));
        }

        [Test]
        public async Task A_check_whose_end_cannot_confirm_a_stop_owed_from_before_fails_with_aligner_did_not_stop() {
            // #2069 — the check never drives the Polar Aligner, but a Stop owed from before the run (Emergency Stop, the Stop
            // route) is sent again at its end; when that is not confirmed, the verdict is not given: the run ends failed.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { SourceStopOwed = true, StopFails = true };
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), new HintFollowingSolver(), NewPoleMount(), ws, store: NewStore(), aligner: aligner);
            svc.AlignerPoll = TimeSpan.FromMilliseconds(5);
            svc.AlignerStopWindow = TimeSpan.FromMilliseconds(60);

            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);

            Assert.That((await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false)).State, Is.EqualTo("failed"));
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.AlignerDidNotStop));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("Cut the Polar Aligner's power"));
            Assert.That(aligner.Moves, Is.Empty, "the check moves nothing");
            Assert.That(aligner.SourceStopOwed, Is.True, "still owed");
        }

        [Test]
        public async Task A_check_whose_end_confirms_a_stop_owed_from_before_gives_its_verdict() {
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { SourceStopOwed = true };
            using var svc = NewService(NewFake(), new HintFollowingSolver(), NewPoleMount(), store: NewStore(), aligner: aligner);
            svc.AlignerPoll = TimeSpan.FromMilliseconds(5);
            svc.AlignerStopWindow = TimeSpan.FromMilliseconds(60);

            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);

            Assert.That((await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false)).State, Is.EqualTo("checked"));
            Assert.That(aligner.Stops, Is.GreaterThanOrEqualTo(1), "the owed Stop went out at the check's end");
            Assert.That(aligner.SourceStopOwed, Is.False, "and was confirmed");
        }

        [Test]
        public async Task A_check_with_no_Polar_Aligner_connected_is_not_failed_by_a_stop_its_absent_device_owes() {
            // #2069 — the Polar Aligner owed a Stop, then its carrier was disconnected (the person cut its power). With no
            // device connected there is nothing to stop and nothing that can move: the check gives its verdict.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { SourceStopOwed = true, Present = false };
            var logger = new EventSignalLogger(4641); // the run's end could not confirm the stop
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), new HintFollowingSolver(), NewPoleMount(), ws, store: NewStore(), logger: logger, aligner: aligner);
            svc.AlignerPoll = TimeSpan.FromMilliseconds(5);
            svc.AlignerStopWindow = TimeSpan.FromMilliseconds(60);

            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);

            Assert.That((await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false)).State, Is.EqualTo("checked"));
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.Zero, "no aligner_did_not_stop");
            Assert.That(logger.Logged.IsCompleted, Is.False, "no Critical could-not-confirm line");
            Assert.That(aligner.SourceStopOwed, Is.True, "still owed by its device, for when it comes back");
        }

        [Test]
        public async Task A_run_with_no_Polar_Aligner_connected_is_not_stopped_by_a_stop_its_absent_device_owes() {
            // #2069 — as above in the live stages: no stop_not_confirmed state, and the person's Stop ends the run stopped.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { SourceStopOwed = true, Present = false };
            var logger = new EventSignalLogger(4641);
            var (svc, ws) = await StartWithAlignerAsync(aligner, logger: logger).ConfigureAwait(false);
            using var _ = svc;
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 4).WaitAsync(SignalTimeout).ConfigureAwait(false);

            Assert.That(ws.Count(WsEventCatalog.PolarAlignCorrection), Is.Zero, "no stop_not_confirmed: nothing to stop");

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("stopped"));
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.Zero, "no aligner_did_not_stop");
            Assert.That(logger.Logged.IsCompleted, Is.False, "no Critical could-not-confirm line");
            Assert.That(aligner.SourceStopOwed, Is.True, "still owed by its device");
        }

        [Test]
        public async Task A_device_without_backlash_handling_may_lose_most_of_the_first_move_after_a_reversal() {
            // #2069 — the test moves go out (+2′); the first correction on each axis turns the other way, and the device takes
            // up its backlash again: it goes 0.3 of the amount sent. Its position shows that; it is not a wrong_scale.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { ReportsPosition = true, InfoReply = AlignerInfo(backlashHandled: false) };
            aligner.OnMove = n => {
                (aligner.AzResponse, aligner.AltResponse) = n is 3 or 4 ? (0.3, 0.3) : (1.0, 1.0);
                return null;
            };
            var (svc, ws) = await StartWithAlignerAsync(aligner).ConfigureAwait(false);
            using var _ = svc;

            var end = await AlignerEventAsync(ws, e => e.GetProperty("state").GetString()
                is PolarAlignCorrectionStates.Converged or PolarAlignCorrectionStates.Stopped).ConfigureAwait(false);

            Assert.That(end.GetProperty("state").GetString(), Is.EqualTo(PolarAlignCorrectionStates.Converged), end.ToString());
            Assert.That(aligner.Moves[2].Arcmin, Is.Negative, "the first correction reversed the test move");
        }

        [Test]
        public async Task A_driver_exception_from_Stop_is_an_unconfirmed_stop_and_the_run_still_cleans_up() {
            // #2069 — a Stop that throws something other than the aligner's own error (a driver bug, a disposed client) is
            // no answer: the run's end owes the Stop and says so, and still deletes its work dir.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10) { NeverFinishes = true };
            var sky = new FaultableSky(new CorrectedSky(aligner));
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), sky, ws: ws, aligner: aligner);
            svc.AlignerPoll = TimeSpan.FromMilliseconds(5);
            svc.AlignerStopWindow = TimeSpan.FromMilliseconds(60);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Probing).ConfigureAwait(false);
            var workDir = sky.WorkDir;
            Assert.That(workDir, Is.Not.Null.And.Not.Empty);
            Assert.That(System.IO.Directory.Exists(workDir), Is.True);
            aligner.StopThrows = new ObjectDisposedException("HttpClient");

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.AlignerDidNotStop));
            Assert.That(aligner.SourceStopOwed, Is.True, "the Stop is owed");
            var deadline = DateTime.UtcNow + SignalTimeout;
            while (System.IO.Directory.Exists(workDir) && DateTime.UtcNow < deadline) {
                await Task.Delay(10).ConfigureAwait(false);
            }
            Assert.That(System.IO.Directory.Exists(workDir), Is.False, "the run's work dir is deleted");
        }
    }
}
