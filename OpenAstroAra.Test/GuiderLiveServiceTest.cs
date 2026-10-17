#region "copyright"
/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1364 — the live guide-camera view without a guider: the disconnected contract, which
    /// is what the Live tab shows before Connect, and the action surface's refusals.</summary>
    [TestFixture]
    public class GuiderLiveServiceTest {

        private static GuiderService NewGuiderService() =>
            new(new HeadlessProfileService(),
                new GuiderRecoveryCoordinator(Mock.Of<IGuiderProcessSupervisor>(), Mock.Of<INotificationService>(),
                    Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

        private sealed class NoFrames : IPolarAlignFrameFetcher {
            public Task FetchAsync(string host, int rpcPort, string filename, string destinationPath, CancellationToken ct) => Task.CompletedTask;
            public Task<byte[]> FetchLiveFrameJpegAsync(string host, int rpcPort, CancellationToken ct) => throw new System.Net.Http.HttpRequestException("404");
        }

        [Test]
        public async Task Without_a_guider_the_view_is_disconnected_and_has_no_frame() {
            using var guider = NewGuiderService();
            var live = new GuiderLiveService(guider, new NoFrames());
            var view = await live.GetAsync(CancellationToken.None);
            Assert.That(view.Connected, Is.False);
            Assert.That(view.HasFrame, Is.False);
            Assert.That(view.Stars, Is.Empty);
            Assert.That(view.LockX, Is.Null);
            Assert.That(await live.GetFrameJpegAsync(CancellationToken.None), Is.Null, "204, never an error");
        }

        [Test]
        public void Actions_without_a_guider_are_refused_as_not_connected() {
            using var guider = NewGuiderService();
            var live = new GuiderLiveService(guider, new NoFrames());
            Assert.That(() => live.LoopAsync(CancellationToken.None), Throws.InvalidOperationException);
            Assert.That(() => live.StopAsync(CancellationToken.None), Throws.InvalidOperationException);
            Assert.That(() => live.DeselectStarAsync(CancellationToken.None), Throws.InvalidOperationException);
            Assert.That(() => live.SelectStarAsync(100, 200, CancellationToken.None), Throws.InvalidOperationException);
        }

        [Test]
        public void A_tap_outside_the_frame_is_a_bad_request_before_any_guider_is_asked() {
            using var guider = NewGuiderService();
            var live = new GuiderLiveService(guider, new NoFrames());
            Assert.That(() => live.SelectStarAsync(-1, 10, CancellationToken.None), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => live.SelectStarAsync(double.NaN, 10, CancellationToken.None), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public async Task The_endpoint_helper_maps_the_three_refusals() {
            var notConnected = await OpenAstroAra.Server.Endpoints.EquipmentEndpoints.GuiderLiveActionAsync(() => throw new InvalidOperationException("guider is not connected"));
            var badPosition = await OpenAstroAra.Server.Endpoints.EquipmentEndpoints.GuiderLiveActionAsync(() => throw new ArgumentOutOfRangeException("x"));
            var refused = await OpenAstroAra.Server.Endpoints.EquipmentEndpoints.GuiderLiveActionAsync(
                () => throw new OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.GuiderRpcException("set_lock_position", 1, "no image"));
            var ok = await OpenAstroAra.Server.Endpoints.EquipmentEndpoints.GuiderLiveActionAsync(() => Task.CompletedTask);
            Assert.That(StatusOf(notConnected), Is.EqualTo(409));
            Assert.That(StatusOf(badPosition), Is.EqualTo(400));
            Assert.That(StatusOf(refused), Is.EqualTo(422));
            Assert.That(StatusOf(ok), Is.EqualTo(204));
        }

        private static OpenAstroAra.Server.Contracts.GuiderCalibrationDataDto Cal(double xAngle, double yAngle, double xRate, double yRate, double dec = 0, bool calibrated = true) =>
            GuiderLiveService.ToDto(new OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.GuiderCalibrationData(calibrated, xAngle, yAngle, xRate, yRate, "+", "-", dec));

        [Test]
        public void The_orthogonality_error_is_how_far_the_axes_are_from_square() {
            Assert.That(Cal(10, 100, 10, 10).OrthogonalityErrorDeg, Is.EqualTo(0));
            Assert.That(Cal(10, 85, 10, 10).OrthogonalityErrorDeg, Is.EqualTo(15));
            Assert.That(Cal(170, 80, 10, 10).OrthogonalityErrorDeg, Is.EqualTo(0), "angles wrap at 180°");
            Assert.That(Cal(-5, 100, 10, 10).OrthogonalityErrorDeg, Is.EqualTo(15));
        }

        [Test]
        public void The_verdict_reads_the_run_and_the_stored_calibration() {
            Assert.That(GuiderLiveService.Judge("running", null, null).Verdict, Is.EqualTo("check"));
            Assert.That(GuiderLiveService.Judge("failed", "star lost", null), Is.EqualTo(("redo", "The last calibration failed: star lost.")));
            var failedOverStored = GuiderLiveService.Judge("failed", "star lost", Cal(10, 100, 12, 11));
            Assert.That(failedOverStored.Verdict, Is.EqualTo("redo"));
            Assert.That(failedOverStored.Reason, Does.Contain("earlier calibration"), "a stored calibration from before the run is still in use");
            Assert.That(GuiderLiveService.Judge("none", null, null).Verdict, Is.EqualTo("redo"), "nothing stored");
            Assert.That(GuiderLiveService.Judge("complete", null, Cal(10, 100, 10, 10, calibrated: false)).Verdict, Is.EqualTo("redo"));
            Assert.That(GuiderLiveService.Judge("complete", null, Cal(10, 100, 12, 11)).Verdict, Is.EqualTo("good"));
            var skew = GuiderLiveService.Judge("complete", null, Cal(10, 85, 12, 11));
            Assert.That(skew.Verdict, Is.EqualTo("check"));
            Assert.That(skew.Reason, Does.Contain("15°"));
            var stuck = GuiderLiveService.Judge("none", null, Cal(10, 100, 12, 3));
            Assert.That(stuck.Verdict, Is.EqualTo("check"));
            Assert.That(stuck.Reason, Does.Contain("Dec rate"));
            // At dec 60° the camera's RA rate is half the sky rate, so the Dec rate is compared with
            // twice the camera RA rate: 12 vs 24 (0.5×) is at the edge and still good; 5 vs 24 is not.
            Assert.That(GuiderLiveService.Judge("complete", null, Cal(10, 100, 12, 12, dec: 60)).Verdict, Is.EqualTo("good"));
            Assert.That(GuiderLiveService.Judge("complete", null, Cal(10, 100, 12, 5, dec: 60)).Verdict, Is.EqualTo("check"));
            Assert.That(GuiderLiveService.Judge("complete", null, Cal(10, 100, 12, 5, dec: 0)).Verdict, Is.EqualTo("check"), "and at the equator too (0.4×)");
            // Near the pole the cos(dec) correction is meaningless: no rate verdict either way.
            Assert.That(GuiderLiveService.Judge("complete", null, Cal(10, 100, 12, 5, dec: 85)).Verdict, Is.EqualTo("good"));
            Assert.That(GuiderLiveService.Judge("complete", null, Cal(10, 100, 12, 5, dec: double.NaN)).Verdict, Is.EqualTo("good"), "an unknown declination skips the rate check");
        }

        [Test]
        public async Task Without_a_guider_the_calibration_view_says_redo() {
            using var guider = NewGuiderService();
            var live = new GuiderLiveService(guider, new NoFrames());
            var view = await live.GetCalibrationAsync(CancellationToken.None);
            Assert.That(view.Connected, Is.False);
            Assert.That(view.Verdict, Is.EqualTo("redo"));
            Assert.That(view.Steps, Is.Empty);
            await Assert.ThatAsync(() => live.ClearCalibrationAsync(CancellationToken.None), Throws.InvalidOperationException);
        }

        private static readonly double[] SpacedAges = [4.0, 2.0, 0.0];

        [Test]
        public void History_maps_to_guide_steps_with_ages_and_signed_pulses() {
            const long now = 1_760_000_000_000;
            var raw = Newtonsoft.Json.Linq.JObject.Parse(@"{
                ""pixel_scale"": 4.98,
                ""points"": [
                    { ""t"": 1759999996000, ""ra"": 0.2, ""dec"": -0.1, ""ra_dur"": 120, ""ra_dir"": ""E"", ""dec_dur"": 0, ""dec_dir"": """", ""snr"": 41.5, ""mass"": 12000 },
                    { ""t"": 1759999998000, ""ra"": -0.3, ""dec"": 0.4, ""ra_dur"": 90, ""ra_dir"": ""W"", ""dec_dur"": 200, ""dec_dir"": ""S"", ""snr"": 40.1, ""mass"": 11800 }
                ]
            }");
            var steps = GuiderLiveService.MapHistory(raw, now);
            Assert.That(steps, Has.Count.EqualTo(2));
            Assert.That(steps[0].AgeSec, Is.EqualTo(4), "oldest first");
            Assert.That(steps[1].AgeSec, Is.EqualTo(2));
            Assert.That(steps[0].RaDurationMs, Is.EqualTo(-120), "East is negative, like guider.step");
            Assert.That(steps[1].RaDurationMs, Is.EqualTo(90));
            Assert.That(steps[1].DecDurationMs, Is.EqualTo(-200), "South is negative");
            Assert.That(steps[1].RaArcsec, Is.EqualTo(Math.Round(-0.3 * 4.98, 3)));
            Assert.That(steps[0].Snr, Is.EqualTo(41.5));
            Assert.That(steps[0].PixelScaleArcsec, Is.EqualTo(4.98));

            // Without plausible timestamps the points are spaced back from the newest.
            var undated = Newtonsoft.Json.Linq.JObject.Parse(@"{ ""points"": [ { ""t"": 5, ""ra"": 0 }, { ""t"": 6, ""ra"": 0 }, { ""ra"": 0 } ] }");
            var spaced = GuiderLiveService.MapHistory(undated, now);
            Assert.That(spaced.Select(x => x.AgeSec), Is.EqualTo(SpacedAges));
            Assert.That(spaced[0].RaArcsec, Is.Null, "no pixel scale, no arcsec");
            Assert.That(GuiderLiveService.MapHistory(Newtonsoft.Json.Linq.JObject.Parse("{}"), now), Is.Empty);
        }

        [Test]
        public async Task Without_a_guider_the_history_is_empty() {
            using var guider = NewGuiderService();
            var live = new GuiderLiveService(guider, new NoFrames());
            var history = await live.GetHistoryAsync(400, CancellationToken.None);
            Assert.That(history.Connected, Is.False);
            Assert.That(history.Steps, Is.Empty);
        }

        private static int? StatusOf(Microsoft.AspNetCore.Http.IResult result) =>
            (result as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode;

        // ── #1364 item 4 — the Guiding Assistant's status mapping ──

        [Test]
        public void Guiding_assistant_idle_and_missing_fields_map_to_nulls() {
            var idle = GuiderLiveService.MapGuidingAssistant(Newtonsoft.Json.Linq.JObject.Parse("{\"state\":\"idle\"}"), connected: true);
            Assert.That(idle.Connected, Is.True);
            Assert.That(idle.State, Is.EqualTo("idle"));
            Assert.That(idle.ElapsedSec, Is.Null);
            Assert.That(idle.RaDriftPxMin, Is.Null, "no statistics before two samples: null, never 0");
            Assert.That(idle.Recommendations, Is.Null);

            var none = GuiderLiveService.AssistantDisconnected;
            Assert.That(none.Connected, Is.False);
            Assert.That(none.State, Is.EqualTo("idle"));
        }

        [Test]
        public void Guiding_assistant_done_maps_every_figure_and_the_recommendations() {
            var raw = Newtonsoft.Json.Linq.JObject.Parse(@"{
                ""state"": ""done"", ""duration_s"": 120, ""measure_backlash"": true, ""elapsed_s"": 121.3, ""samples"": 60,
                ""pixel_scale"": 3.93, ""ra_drift_px_min"": 0.42, ""dec_drift_px_min"": -1.1, ""ra_rms_px"": 0.31,
                ""dec_rms_px"": 0.22, ""high_freq_rms_px"": 0.18, ""ra_peak_px"": 0.9, ""dec_peak_px"": 0.7,
                ""ra_peak_peak_px"": 1.6, ""ra_max_drift_px_s"": 0.02, ""snr"": 41.5, ""star_mass"": 12000,
                ""polar_align_error_arcmin"": 4.2, ""polar_align_error_is_lower_bound"": false,
                ""backlash_status"": ""done"", ""backlash_result"": ""Backlash is 350 ms"", ""backlash_ms"": 350, ""backlash_sigma_ms"": 40, ""backlash_px"": 2.1,
                ""recommendations"": { ""min_move_ra_px"": 0.15, ""min_move_dec_px"": 0.2, ""min_move_seeing_based"": true,
                    ""exposure_s"": 2, ""exposure_max_s"": 4, ""dec_backlash_ms"": 350, ""dec_guide_mode"": ""North"",
                    ""messages"": [ { ""id"": ""dec_backlash"", ""text"": ""Dec backlash is large"" }, { ""id"": ""no_text"" } ] }
            }");
            var dto = GuiderLiveService.MapGuidingAssistant(raw, connected: true);
            Assert.That(dto.State, Is.EqualTo("done"));
            Assert.That((dto.DurationSec, dto.MeasureBacklash, dto.ElapsedSec, dto.Samples), Is.EqualTo((120.0, true, 121.3, 60)));
            Assert.That((dto.RaDriftPxMin, dto.DecDriftPxMin, dto.HighFreqRmsPx), Is.EqualTo((0.42, -1.1, 0.18)));
            Assert.That((dto.PolarAlignErrorArcmin, dto.PolarAlignErrorIsLowerBound), Is.EqualTo((4.2, false)));
            Assert.That((dto.BacklashMs, dto.BacklashSigmaMs, dto.BacklashPx), Is.EqualTo((350.0, 40.0, 2.1)));
            Assert.That(dto.BacklashResult, Is.EqualTo("Backlash is 350 ms"));
            var r = dto.Recommendations!;
            Assert.That((r.MinMoveRaPx, r.MinMoveDecPx, r.MinMoveSeeingBased), Is.EqualTo((0.15, 0.2, true)));
            Assert.That((r.ExposureSec, r.ExposureMaxSec, r.DriftLimitingExposureSec), Is.EqualTo((2.0, 4.0, (double?)null)));
            Assert.That((r.DecBacklashMs, r.DecGuideMode, r.DecAlgorithm), Is.EqualTo((350.0, "North", (string?)null)));
            Assert.That(r.Messages.Select(m => (m.Id, m.Text)), Is.EqualTo(new[] { ("dec_backlash", "Dec backlash is large") }), "a message without text is dropped");
            Assert.That(dto.Error, Is.Null);
        }

        [Test]
        public async Task Guiding_assistant_without_a_guider_refuses_start_stop_apply_and_reads_disconnected() {
            using var svc = NewGuiderService();
            var live = new GuiderLiveService(svc, new NoFrames());
            Assert.That((await live.GetGuidingAssistantAsync(CancellationToken.None).ConfigureAwait(false)).Connected, Is.False);
            await Assert.ThatAsync(() => live.StartGuidingAssistantAsync(null, false, CancellationToken.None), Throws.InvalidOperationException);
            await Assert.ThatAsync(() => live.StopGuidingAssistantAsync(CancellationToken.None), Throws.InvalidOperationException);
            await Assert.ThatAsync(() => live.ApplyGuidingAssistantAsync(JustExposure, CancellationToken.None), Throws.InvalidOperationException);
        }

        private static readonly string[] JustExposure = ["exposure"];
    }
}
