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

    // #1971 — the Polar Align Routine with a Polar Corrector. The sky is a fake that follows the
    // fake motors: a move turns the camera about the vertical (azimuth) or tilts it (altitude), in
    // the device's own directions, with no knowledge of the routine's east-positive numbers. A run
    // that converges therefore proves the sign and the scale end to end.
    public partial class PolarAlignServiceTest {

        private static PolarCorrectorInfo CorrectorInfo(double maxMove = 120, bool tier1 = true, bool movesWhileTracking = true,
                bool simultaneous = false, bool backlashHandled = true) =>
            new(tier1 ? [1, 2] : [2], "Fake base", "1.0", new PolarCorrectorAxisInfo(maxMove, 0.01, backlashHandled), new PolarCorrectorAxisInfo(maxMove, 0.01, backlashHandled),
                simultaneous, movesWhileTracking, MovesWhileSlewing: false, CanStop: true, SettleSeconds: 0);

        /// <summary>A Polar Corrector whose axis offset from the pole (arcminutes, device directions) the fake sky reads.</summary>
        private sealed class FakeCorrector : IPolarCorrector, IPolarCorrectorSource {
            private readonly object _gate = new();
            private double _az, _alt;
            private bool _moving;
            private readonly List<(PolarCorrectorAxis Axis, double Arcmin)> _moves = [];
            private int _stops;

            public FakeCorrector(double azOffset, double altOffset) {
                _az = azOffset;
                _alt = altOffset;
            }

            public bool Present { get; set; } = true;
            public PolarCorrectorInfo InfoReply { get; set; } = CorrectorInfo();
            /// <summary>How far, and which way, the axis really goes per arcminute asked.</summary>
            public double AzResponse { get; set; } = 1;
            public double AltResponse { get; set; } = 1;
            /// <summary>Called with the 1-based move number before the move is applied; an exception it returns is thrown.</summary>
            public Func<int, Exception?>? OnMove { get; set; }
            public string? FaultAfterMoves { get; set; }
            public int FaultFromMove { get; set; } = int.MaxValue;
            public bool NeverFinishes { get; set; }
            public bool StopFails { get; set; }
            /// <summary>While reset, Stop does not return: a device on a half-dropped link.</summary>
            public ManualResetEventSlim StopAnswers { get; } = new(initialState: true);

            public (double Az, double Alt) Offset { get { lock (_gate) { return (_az, _alt); } } }
            public (PolarCorrectorAxis Axis, double Arcmin)[] Moves { get { lock (_gate) { return [.. _moves]; } } }
            public int Stops => Volatile.Read(ref _stops);

            public IPolarCorrector? Current() => Present ? this : null;

            public PolarCorrectorInfo Info() => InfoReply;

            public PolarCorrectorStatus Status() {
                lock (_gate) {
                    var fault = _moves.Count >= FaultFromMove ? FaultAfterMoves : null;
                    return new PolarCorrectorStatus(new(_moving, null, fault), new(_moving, null, null));
                }
            }

            public void Move(PolarCorrectorAxis axis, double arcmin) {
                lock (_gate) {
                    if (OnMove?.Invoke(_moves.Count + 1) is { } ex) {
                        throw ex;
                    }
                    _moves.Add((axis, arcmin));
                    if (axis == PolarCorrectorAxis.Azimuth) {
                        _az += arcmin * AzResponse;
                    } else {
                        _alt += arcmin * AltResponse;
                    }
                    _moving = NeverFinishes;
                }
            }

            public void Stop() {
                StopAnswers.Wait();
                if (StopFails) {
                    throw new PolarCorrectorException(PolarCorrectorFailure.LinkLost, "no answer");
                }
                lock (_gate) {
                    _moving = false;
                }
                Interlocked.Increment(ref _stops);
            }

            public void Home() { }
        }

        /// <summary>A rig whose Polar Corrector connection can be replaced mid-run (a reconnect).</summary>
        private sealed class SwappableSource(IPolarCorrector first) : IPolarCorrectorSource {
            public volatile IPolarCorrector? Device = first;
            public IPolarCorrector? Current() => Device;
        }

        /// <summary>Seed frames A and B as the bench's fixed sky; every live frame is B's pointing moved by the
        /// corrector's offset: azimuth is a turn about the vertical, altitude a tilt.</summary>
        private sealed class CorrectedSky(FakeCorrector corrector, double latDeg = SiteLatDeg) : IPolarAlignFrameSolver {
            // The bench's two seed frames, mirrored to the southern sky for a southern site.
            private readonly PolarAlignSolveOutcome _a = new(true, SolveA.RaDegJnow, Math.Sign(latDeg) * SolveA.DecDegJnow);
            private readonly PolarAlignSolveOutcome _b = new(true, SolveB.RaDegJnow, Math.Sign(latDeg) * SolveB.DecDegJnow);
            private int _solves;
            public Task<double?> MeasureFocalLengthAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) =>
                Task.FromResult<double?>(null);
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                var n = Interlocked.Increment(ref _solves);
                if (n <= 2) {
                    return Task.FromResult(n == 1 ? _a : _b);
                }
                var now = DateTimeOffset.UtcNow;
                var p = PolarAlignGeometry.PointingAltAz(_b.RaDegJnow, _b.DecDegJnow, latDeg, SiteLonDeg, now);
                var (az, alt) = corrector.Offset;
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

        private static Task<JsonElement> CorrectorEventAsync(WsRecorder ws, string state, int skip = 0) =>
            CorrectorEventAsync(ws, e => e.GetProperty("state").GetString() == state, skip);

        private static async Task<JsonElement> CorrectorEventAsync(WsRecorder ws, Func<JsonElement, bool> match, int skip = 0) {
            bool Is((string Type, JsonElement Payload) e) => e.Type == WsEventCatalog.PolarAlignCorrector && match(e.Payload);
            await ws.WhenAsync(events => events.Skip(skip).Any(Is)).WaitAsync(SignalTimeout).ConfigureAwait(false);
            return ws.Events.Skip(skip).First(Is).Payload;
        }

        private static async Task<(PolarAlignService Svc, WsRecorder Ws)> StartWithCorrectorAsync(FakeCorrector corrector,
                Moq.Mock<OpenAstroAra.Equipment.Interfaces.Mediator.ITelescopeMediator>? mount = null, IProfileStore? store = null,
                IPolarAlignmentLog? log = null, Microsoft.Extensions.Logging.ILogger<PolarAlignService>? logger = null,
                TimeSpan? moveTimeout = null, double latDeg = SiteLatDeg) {
            var ws = new WsRecorder();
            var svc = NewService(NewFake(), new CorrectedSky(corrector, latDeg), mount, ws, store, log, logger: logger, corrector: corrector);
            svc.CorrectorPoll = TimeSpan.FromMilliseconds(5);
            if (moveTimeout is { } t) {
                svc.CorrectorMoveTimeout = t;
            }
            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            return (svc, ws);
        }

        private static async Task<PolarAlignCorrectorDto?> CorrectorStatusAsync(PolarAlignService svc) =>
            (await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).Corrector;

        private static void AssertStillAdjustingByHand(PolarAlignService svc, PolarAlignStateDto status) {
            Assert.That(svc.IsActive, Is.True, "the routine carries on by hand");
            Assert.That(status.State, Is.EqualTo("adjusting"));
        }

        [Test]
        public async Task The_motors_probe_each_axis_then_correct_until_the_error_is_inside_the_tolerance() {
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10);
            var logger = new CapturingLogger();
            var (svc, ws) = await StartWithCorrectorAsync(corrector, logger: logger).ConfigureAwait(false);
            using var _ = svc;

            var converged = await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Converged).ConfigureAwait(false);

            var moves = corrector.Moves;
            Assert.That(moves[0], Is.EqualTo((PolarCorrectorAxis.Azimuth, PolarAlignService.CorrectorProbeArcmin)), "a small test move on azimuth first");
            Assert.That(moves[1], Is.EqualTo((PolarCorrectorAxis.Altitude, PolarAlignService.CorrectorProbeArcmin)), "then on altitude");
            // After the probe the axis sits 22′ clockwise of the pole and 12′ above its geometric place.
            Assert.That(moves[2].Axis, Is.EqualTo(PolarCorrectorAxis.Azimuth));
            Assert.That(moves[2].Arcmin, Is.EqualTo(-PolarAlignService.CorrectorGain * 22).Within(0.5), "80 % of the azimuth offset, anticlockwise");
            Assert.That(moves[3].Axis, Is.EqualTo(PolarCorrectorAxis.Altitude));
            Assert.That(moves[3].Arcmin, Is.LessThan(-8), "80 % of the altitude error, downward");
            Assert.That(converged.GetProperty("moves").GetInt32(), Is.EqualTo(moves.Length).Or.EqualTo(corrector.Moves.Length));

            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.CurrentErrorArcmin, Is.LessThanOrEqualTo(1.0 + 0.05), "inside the 1′ tolerance");
            AssertStillAdjustingByHand(svc, status);

            var states = ws.Events.Where(e => e.Type == WsEventCatalog.PolarAlignCorrector).Select(e => e.Payload.GetProperty("state").GetString()).ToArray();
            Assert.That(states, Does.Contain("probing").And.Contain("moving").And.Contain("measuring"));
            var firstProbe = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignCorrector).Payload;
            Assert.That(firstProbe.GetProperty("axis").GetString(), Is.EqualTo("azimuth"));
            Assert.That(firstProbe.GetProperty("arcmin").GetDouble(), Is.EqualTo(2.0));
            Assert.That(logger.Entries.Count(e => e.EventId == 4543), Is.GreaterThanOrEqualTo(moves.Length), "one log line per move");
            Assert.That(logger.Entries.Count(e => e.EventId == 4548), Is.GreaterThanOrEqualTo(2), "the device's status after each wait");
            Assert.That(logger.Entries.Count(e => e.EventId == 4549), Is.EqualTo(2), "the test move's result, per axis");

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(corrector.Stops, Is.GreaterThanOrEqualTo(1), "stopping the routine stops the motors");
            Assert.That((await CorrectorStatusAsync(svc).ConfigureAwait(false)), Is.Null, "no corrector state once the run is over");
        }

        [Test]
        public async Task At_a_southern_site_the_motors_turn_the_right_way() {
            // The same misplaced axis in the device's directions (20′ clockwise of the pole, 10′ high): east-positive,
            // its azimuth error has the opposite sign from the north, and the move sent must not.
            var store = NewStore();
            store.PutSiteSettings(store.GetSiteSettings() with { LatitudeDeg = -SiteLatDeg });
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10);
            var (svc, ws) = await StartWithCorrectorAsync(corrector, store: store, latDeg: -SiteLatDeg).ConfigureAwait(false);
            using var _ = svc;

            var first = await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Probing).ConfigureAwait(false);
            var beforeProbe = ws.Events.Last(e => e.Type == WsEventCatalog.PolarAlignProgress).Payload;
            await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Converged).ConfigureAwait(false);

            Assert.That(first.GetProperty("arcmin").GetDouble(), Is.EqualTo(2.0));
            Assert.That(beforeProbe.GetProperty("azimuth_error_arcmin").GetDouble(), Is.LessThan(-10), "clockwise of the south pole is west: negative east-positive");
            var moves = corrector.Moves;
            Assert.That(moves[2].Axis, Is.EqualTo(PolarCorrectorAxis.Azimuth));
            Assert.That(moves[2].Arcmin, Is.EqualTo(-PolarAlignService.CorrectorGain * 22).Within(0.5), "anticlockwise, as in the north");
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.CurrentErrorArcmin, Is.LessThanOrEqualTo(1.0 + 0.05));
        }

        [Test]
        public async Task The_night_record_counts_the_moves_the_Polar_Corrector_made() {
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10);
            var log = new RecordingLog();
            var (svc, ws) = await StartWithCorrectorAsync(corrector, log: log).ConfigureAwait(false);
            using var _ = svc;
            await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Converged).ConfigureAwait(false);

            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);

            await log.FirstRow.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var row = log.Rows.First();
            Assert.That(row.CorrectorMoves, Is.EqualTo(corrector.Moves.Length).And.GreaterThan(2));
        }

        [Test]
        public async Task A_rig_without_a_Polar_Corrector_gets_no_corrector_state() {
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10) { Present = false };
            var (svc, ws) = await StartWithCorrectorAsync(corrector).ConfigureAwait(false);
            using var _ = svc;

            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 4).WaitAsync(SignalTimeout).ConfigureAwait(false);

            Assert.That(corrector.Moves, Is.Empty);
            Assert.That(ws.Count(WsEventCatalog.PolarAlignCorrector), Is.Zero);
            Assert.That(await CorrectorStatusAsync(svc).ConfigureAwait(false), Is.Null);
        }

        [Test]
        public async Task With_automatic_correction_off_the_person_turns_the_bolts() {
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10);
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { PolarCorrectorAuto = false });
            var (svc, ws) = await StartWithCorrectorAsync(corrector, store: store).ConfigureAwait(false);
            using var _ = svc;

            await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Manual).ConfigureAwait(false);
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 4).WaitAsync(SignalTimeout).ConfigureAwait(false);

            Assert.That(corrector.Moves, Is.Empty);
        }

        [Test]
        public async Task A_Polar_Corrector_that_only_runs_its_own_loop_is_never_driven() {
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10) { InfoReply = CorrectorInfo(tier1: false) };
            var (svc, ws) = await StartWithCorrectorAsync(corrector).ConfigureAwait(false);
            using var _ = svc;

            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 4).WaitAsync(SignalTimeout).ConfigureAwait(false);

            Assert.That(corrector.Moves, Is.Empty);
            Assert.That(ws.Count(WsEventCatalog.PolarAlignCorrector), Is.Zero);
        }

        [Test]
        public async Task No_move_is_larger_than_the_device_allows() {
            var corrector = new FakeCorrector(azOffset: 40, altOffset: 25) { InfoReply = CorrectorInfo(maxMove: 6) };
            var (svc, ws) = await StartWithCorrectorAsync(corrector).ConfigureAwait(false);
            using var _ = svc;

            await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Converged).ConfigureAwait(false);

            Assert.That(corrector.Moves.Select(m => Math.Abs(m.Arcmin)), Has.All.LessThanOrEqualTo(6.0));
            Assert.That(corrector.Moves[0].Arcmin, Is.EqualTo(1.5), "the test move is a quarter of the cap when the cap is small");
            Assert.That(corrector.Moves.Count(m => Math.Abs(m.Arcmin) == 6.0), Is.GreaterThan(2), "the large error is taken in capped steps");
        }

        private static async Task<(PolarAlignService Svc, JsonElement Stopped, PolarAlignStateDto Status)> RunUntilStoppedAsync(
                FakeCorrector corrector, Moq.Mock<OpenAstroAra.Equipment.Interfaces.Mediator.ITelescopeMediator>? mount = null,
                TimeSpan? moveTimeout = null) {
            var (svc, ws) = await StartWithCorrectorAsync(corrector, mount, moveTimeout: moveTimeout).ConfigureAwait(false);
            var stopped = await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Stopped).ConfigureAwait(false);
            // The frame after the stop: the routine still measures, and sends nothing more to the motors.
            var movesAtStop = corrector.Moves.Length;
            var progress = ws.Count(WsEventCatalog.PolarAlignProgress);
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, progress + 3).WaitAsync(SignalTimeout).ConfigureAwait(false);
            Assert.That(corrector.Moves, Has.Length.EqualTo(movesAtStop), "a stopped corrector gets no further move");
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.Corrector?.State, Is.EqualTo(PolarAlignCorrectorStates.Stopped));
            AssertStillAdjustingByHand(svc, status);
            return (svc, stopped, status);
        }

        [Test]
        public async Task A_test_move_that_goes_the_wrong_way_stops_the_motors() {
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10) { AzResponse = -1 };
            var (svc, stopped, _) = await RunUntilStoppedAsync(corrector).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectorCauses.WrongDirection));
            Assert.That(stopped.GetProperty("axis").GetString(), Is.EqualTo("azimuth"));
            Assert.That(corrector.Moves, Has.Length.EqualTo(2), "only the two test moves went out");
            Assert.That(corrector.Stops, Is.GreaterThanOrEqualTo(1));
        }

        [TestCase(3.0)]
        [TestCase(0.3)]
        public async Task A_test_move_of_the_wrong_size_stops_the_motors(double response) {
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10) { AltResponse = response };
            var (svc, stopped, _) = await RunUntilStoppedAsync(corrector).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectorCauses.WrongScale));
            Assert.That(stopped.GetProperty("axis").GetString(), Is.EqualTo("altitude"));
            Assert.That(corrector.Stops, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public async Task A_device_that_does_not_take_up_its_backlash_may_lose_most_of_the_test_move() {
            // 0.3 of the amount asked: a stop for a device that says it handles backlash, not for one that does not.
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10) { AltResponse = 0.3, InfoReply = CorrectorInfo(backlashHandled: false) };
            var (svc, ws) = await StartWithCorrectorAsync(corrector).ConfigureAwait(false);
            using var _ = svc;

            await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Converged).ConfigureAwait(false);

            Assert.That(ws.Events.Any(e => e.Type == WsEventCatalog.PolarAlignCorrector
                && e.Payload.GetProperty("state").GetString() == PolarAlignCorrectorStates.Stopped), Is.False);
        }

        [Test]
        public async Task A_fault_the_device_reports_after_a_move_stops_the_motors_with_its_text() {
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10) { FaultAfterMoves = "end of travel", FaultFromMove = 3 };
            var (svc, stopped, _) = await RunUntilStoppedAsync(corrector).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectorCauses.DeviceFault));
            Assert.That(stopped.GetProperty("device_text").GetString(), Is.EqualTo("end of travel"));
            Assert.That(corrector.Moves, Has.Length.EqualTo(3), "the failed move is the last: nothing is corrected from it");
            Assert.That(corrector.Stops, Is.GreaterThanOrEqualTo(1));
        }

        [TestCase(nameof(PolarCorrectorFailure.NotCalibrated), PolarAlignCorrectorCauses.NotCalibrated)]
        [TestCase(nameof(PolarCorrectorFailure.Refused), PolarAlignCorrectorCauses.DeviceRefused)]
        [TestCase(nameof(PolarCorrectorFailure.Protocol), PolarAlignCorrectorCauses.DeviceRefused)]
        public async Task A_move_the_device_refuses_stops_the_motors_with_its_text(string failure, string cause) {
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10) {
                OnMove = n => n == 1 ? new PolarCorrectorException(Enum.Parse<PolarCorrectorFailure>(failure), "the board has no factors") : null,
            };
            var (svc, stopped, _) = await RunUntilStoppedAsync(corrector).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(cause));
            Assert.That(stopped.GetProperty("device_text").GetString(), Is.EqualTo("the board has no factors"));
            Assert.That(corrector.Moves, Is.Empty);
            Assert.That(stopped.GetProperty("moves").GetInt32(), Is.Zero, "a refused move is not a move made");
        }

        [Test]
        public async Task An_error_that_grows_after_two_corrections_in_a_row_stops_the_motors() {
            // The probe passes; from the third move on the axes run backwards, so every correction makes it worse.
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10);
            corrector.OnMove = n => {
                if (n == 3) {
                    corrector.AzResponse = -1;
                    corrector.AltResponse = -1;
                }
                return null;
            };
            var (svc, stopped, _) = await RunUntilStoppedAsync(corrector).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectorCauses.Diverging));
            Assert.That(corrector.Moves, Has.Length.EqualTo(6), "two test moves and two corrections of both axes");
            Assert.That(corrector.Stops, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public async Task A_move_that_never_finishes_is_stopped() {
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10) { NeverFinishes = true };
            var (svc, stopped, _) = await RunUntilStoppedAsync(corrector, moveTimeout: TimeSpan.FromMilliseconds(60)).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectorCauses.Timeout));
            Assert.That(corrector.Moves, Has.Length.EqualTo(1));
            Assert.That(corrector.Stops, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public async Task A_lost_link_stops_the_motors_as_soon_as_the_device_answers_again() {
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10) { StopFails = true };
            corrector.OnMove = n => n == 2 ? new PolarCorrectorException(PolarCorrectorFailure.LinkLost, "connection refused") : null;
            var (svc, stopped, _) = await RunUntilStoppedAsync(corrector).ConfigureAwait(false);
            using var __ = svc;
            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectorCauses.LinkLost));
            Assert.That(corrector.Stops, Is.Zero, "the Stop could not be delivered yet");

            corrector.StopFails = false;
            var ws = Recorders.GetValue(svc, _ => throw new InvalidOperationException());
            var progress = ws.Count(WsEventCatalog.PolarAlignProgress);
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, progress + 2).WaitAsync(SignalTimeout).ConfigureAwait(false);

            Assert.That(corrector.Stops, Is.GreaterThanOrEqualTo(1), "the owed Stop goes out at the next measurement");
        }

        [Test]
        public async Task A_stop_owed_after_a_lost_link_goes_to_the_reconnected_device() {
            // The old connection never answers again; the reconnect is a new object from the source.
            var dead = new FakeCorrector(azOffset: 20, altOffset: 10) { StopFails = true };
            dead.OnMove = n => n == 2 ? new PolarCorrectorException(PolarCorrectorFailure.LinkLost, "connection refused") : null;
            var source = new SwappableSource(dead);
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), new CorrectedSky(dead), ws: ws, corrector: source);
            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Stopped).ConfigureAwait(false);

            var reconnected = new FakeCorrector(azOffset: 0, altOffset: 0);
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
            var corrector = new FakeCorrector(azOffset: -1.43, altOffset: 2);
            var (svc, ws) = await StartWithCorrectorAsync(corrector).ConfigureAwait(false);
            using var _ = svc;

            await CorrectorEventAsync(ws, e => e.GetProperty("state").GetString() == PolarAlignCorrectorStates.Moving).ConfigureAwait(false);
            await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Converged).ConfigureAwait(false);

            Assert.That(corrector.Moves.Skip(2).Select(m => m.Axis), Has.All.EqualTo(PolarCorrectorAxis.Altitude));
        }

        [Test]
        public async Task A_stop_that_comes_from_outside_the_routine_ends_its_motor_loop() {
            // The emergency stop and the device's own Stop route reach the routine through this door.
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10) { NeverFinishes = true };
            var (svc, ws) = await StartWithCorrectorAsync(corrector).ConfigureAwait(false);
            using var _ = svc;
            await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Probing).ConfigureAwait(false);
            IPolarCorrectorDriver driver = svc;
            Assert.That(driver.DrivesPolarCorrector, Is.True);

            await driver.StopPolarCorrectorAsync().ConfigureAwait(false);

            corrector.NeverFinishes = false;
            var movesAtStop = corrector.Moves.Length;
            var progress = ws.Count(WsEventCatalog.PolarAlignProgress);
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, progress + 3).WaitAsync(SignalTimeout).ConfigureAwait(false);
            Assert.That(corrector.Moves, Has.Length.EqualTo(movesAtStop), "no move follows the stop: the altitude test move never goes out");
            Assert.That(movesAtStop, Is.EqualTo(1));
            Assert.That(driver.DrivesPolarCorrector, Is.False);
            Assert.That(corrector.Stops, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public async Task A_stop_from_outside_returns_to_its_caller_before_a_slow_device_answers() {
            // The emergency stop bounds each rung only once the rung's call has returned a task: a device
            // Stop made on the caller's thread would hold the whole ladder for the HTTP timeout.
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10);
            var (svc, ws) = await StartWithCorrectorAsync(corrector).ConfigureAwait(false);
            using var _ = svc;
            await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Converged).ConfigureAwait(false);
            IPolarCorrectorDriver driver = svc;
            corrector.StopAnswers.Reset();
            try {
                var returned = Task.Factory.StartNew(() => driver.StopPolarCorrectorAsync(), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);

                var stopping = await returned.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                Assert.That(stopping.IsCompleted, Is.False, "the device has not answered yet");
                corrector.StopAnswers.Set();
                await stopping.WaitAsync(SignalTimeout).ConfigureAwait(false);
                Assert.That(corrector.Stops, Is.GreaterThanOrEqualTo(1));
            } finally {
                corrector.StopAnswers.Set();
            }
        }

        [Test]
        public async Task A_device_that_cannot_move_while_the_mount_tracks_is_not_sent_a_move() {
            // The bench mount keeps reporting tracking on.
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10) { InfoReply = CorrectorInfo(movesWhileTracking: false) };
            var (svc, stopped, _) = await RunUntilStoppedAsync(corrector, NewMount(tracking: true)).ConfigureAwait(false);
            using var __ = svc;

            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectorCauses.MountTracking));
            Assert.That(corrector.Moves, Is.Empty);
        }

        [Test]
        public async Task The_person_can_stop_the_motors_and_resume_them() {
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10) { NeverFinishes = true };
            var (svc, ws) = await StartWithCorrectorAsync(corrector).ConfigureAwait(false);
            using var _ = svc;
            await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Probing).ConfigureAwait(false);

            await svc.StopCorrectorAsync(null, CancellationToken.None).ConfigureAwait(false);

            var stopped = await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Stopped).ConfigureAwait(false);
            Assert.That(stopped.GetProperty("cause").GetString(), Is.EqualTo(PolarAlignCorrectorCauses.StoppedByUser));
            Assert.That(corrector.Stops, Is.GreaterThanOrEqualTo(1));
            AssertStillAdjustingByHand(svc, await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false));

            corrector.NeverFinishes = false;
            var mark = ws.Events.Count;
            var movesBefore = corrector.Moves.Length;
            await svc.ResumeCorrectorAsync(null, CancellationToken.None).ConfigureAwait(false);

            var probe = await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Probing, mark).ConfigureAwait(false);
            Assert.That(probe.GetProperty("axis").GetString(), Is.EqualTo("azimuth"), "a resume starts from a new test move");
            await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Converged, mark).ConfigureAwait(false);
            Assert.That(corrector.Moves, Has.Length.GreaterThan(movesBefore + 2));
        }

        [TestCase(false, true, true)]   // no Polar Corrector on the rig
        [TestCase(true, false, true)]   // one that only runs its own loop
        [TestCase(true, false, false)]  // the same, with automatic correction off
        public async Task A_running_routine_with_no_Polar_Corrector_to_drive_refuses_stop_and_reports_no_state(
                bool present, bool tier1, bool auto) {
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10) { Present = present, InfoReply = CorrectorInfo(tier1: tier1) };
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { PolarCorrectorAuto = auto });
            var (svc, ws) = await StartWithCorrectorAsync(corrector, store: store).ConfigureAwait(false);
            using var _ = svc;
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 4).WaitAsync(SignalTimeout).ConfigureAwait(false);

            await Assert.ThatAsync(() => svc.StopCorrectorAsync(null, CancellationToken.None), Throws.InvalidOperationException).ConfigureAwait(false);
            await Assert.ThatAsync(() => svc.ResumeCorrectorAsync(null, CancellationToken.None), Throws.InvalidOperationException).ConfigureAwait(false);

            Assert.That(await CorrectorStatusAsync(svc).ConfigureAwait(false), Is.Null);
            Assert.That(ws.Count(WsEventCatalog.PolarAlignCorrector), Is.Zero);
        }

        [Test]
        public async Task Stop_and_resume_are_refused_when_there_is_nothing_to_stop_or_resume() {
            var corrector = new FakeCorrector(azOffset: 20, altOffset: 10);
            using var idle = NewService(NewFake(), new CorrectedSky(corrector), corrector: corrector);
            await Assert.ThatAsync(() => idle.StopCorrectorAsync(null, CancellationToken.None), Throws.InvalidOperationException).ConfigureAwait(false);
            await Assert.ThatAsync(() => idle.ResumeCorrectorAsync(null, CancellationToken.None), Throws.InvalidOperationException).ConfigureAwait(false);

            var (svc, ws) = await StartWithCorrectorAsync(corrector).ConfigureAwait(false);
            using var _ = svc;
            await CorrectorEventAsync(ws, PolarAlignCorrectorStates.Converged).ConfigureAwait(false);
            await Assert.ThatAsync(() => svc.ResumeCorrectorAsync(null, CancellationToken.None), Throws.InvalidOperationException,
                "a corrector that is not stopped has nothing to resume").ConfigureAwait(false);
        }
    }
}
