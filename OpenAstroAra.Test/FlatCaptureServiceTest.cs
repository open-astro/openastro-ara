#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem.FlatDevice;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §48.3 — <see cref="FlatCaptureService"/> against a scriptable frame source (the scene's
    /// brightness is a linear ADU-per-second rate, so the probe loop's convergence is analytic),
    /// a fake flat-panel service, and a mocked imaging mediator: convergence + saved captures,
    /// first-probe tolerance exit, bounds-pinned and zero-light failures, panel light lifecycle
    /// (on at set brightness, off on every exit path incl. cancellation), and the not-wired guard.
    /// </summary>
    [TestFixture]
    public class FlatCaptureServiceTest {

        /// <summary>Frame source whose mean ADU is <c>rate × exposure</c>, clamped to 16-bit.</summary>
        private sealed class LinearSceneFrameSource : IAnalysisFrameSource {
            public double AduPerSecond { get; set; } = 10000;
            public List<double> ProbedExposures { get; } = new();

            public Task<AnalysisFrame> CaptureForAnalysisAsync(double exposureSec, int binning, CancellationToken ct) {
                ct.ThrowIfCancellationRequested();
                ProbedExposures.Add(exposureSec);
                var value = (ushort)Math.Clamp(AduPerSecond * exposureSec, 0, ushort.MaxValue);
                var pixels = new ushort[16];
                Array.Fill(pixels, value);
                return Task.FromResult(new AnalysisFrame(pixels, 4, 4, DateTimeOffset.UtcNow));
            }
        }

        private sealed class FakeFlatDeviceService : IFlatDeviceService {
            public bool LightOn { get; private set; }
            public int? RequestedBrightness { get; private set; }
            public List<bool> LightRequests { get; } = new();
            public bool ThrowOnApply { get; set; }

            public Task<FlatDeviceDto?> GetAsync(CancellationToken ct) =>
                Task.FromResult<FlatDeviceDto?>(new FlatDeviceDto(
                    "panel-1", "Fake Panel", EquipmentConnectionState.Connected,
                    new FlatDeviceStateDto(LightOn ? "light_on" : "cover_closed", CoverOpen: false, LightOn, LightOn ? (RequestedBrightness ?? 0) : 0)));

            public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) =>
                throw new NotSupportedException();

            public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) =>
                throw new NotSupportedException();

            public Task<OperationAcceptedDto> ApplyFlatPanelAsync(FlatPanelRequestDto request, string? idempotencyKey, CancellationToken ct) {
                if (ThrowOnApply) {
                    throw new InvalidOperationException("flat device is not connected");
                }
                if (request.LightOn is bool on) {
                    LightOn = on;
                    LightRequests.Add(on);
                }
                if (request.Brightness is int b) {
                    RequestedBrightness = b;
                }
                return Task.FromResult(new OperationAcceptedDto(Guid.NewGuid(), "flat-device.apply", DateTimeOffset.UtcNow, idempotencyKey));
            }
        }

        private LinearSceneFrameSource scene = null!;
        private FakeFlatDeviceService panel = null!;
        private Mock<IImagingMediator> imagingMock = null!;
        private List<CaptureSequence> captured = null!;

        [SetUp]
        public void Setup() {
            scene = new LinearSceneFrameSource();
            panel = new FakeFlatDeviceService();
            captured = new List<CaptureSequence>();
            imagingMock = new Mock<IImagingMediator>();
            imagingMock
                .Setup(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<string>()))
                .Callback<CaptureSequence, CancellationToken, IProgress<ApplicationStatus>?, string>((s, _, _, _) => captured.Add(s))
                .ReturnsAsync((IExposureData)null!);
        }

        private FlatCaptureService CreateSUT() =>
            new(frames: scene, imaging: imagingMock.Object, flatDevice: panel) {
                Delay = (_, _) => Task.CompletedTask,
            };

        private static FlatSetRequest Request(
            double targetAdu = 30000, double tolerancePct = 5, int frameCount = 3, int brightness = 60,
            double minSec = 0.01, double maxSec = 10, int gain = 101, int offset = 7) =>
            new(targetAdu, tolerancePct, frameCount, brightness, minSec, maxSec, gain, offset);

        [Test]
        public async Task Converges_then_captures_the_set_as_FLAT_at_the_found_exposure() {
            // 10000 ADU/s: 1s probe reads 10000, scaling lands 3s -> 30000 (exact target).
            var ok = await CreateSUT().CaptureFlatSetAsync(Request(), new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(scene.ProbedExposures, Has.Count.EqualTo(2), "probe at 1s, converge at 3s");
            Assert.That(scene.ProbedExposures[1], Is.EqualTo(3).Within(1e-6));
            Assert.That(captured, Has.Count.EqualTo(3));
            Assert.That(captured.All(s => s.ImageType == ImageTypes.FLAT), Is.True);
            Assert.That(captured[0].ExposureTime, Is.EqualTo(3).Within(1e-6));
            Assert.That(captured[0].Gain, Is.EqualTo(101), "flats must match the session gain");
            Assert.That(captured[0].Offset, Is.EqualTo(7));
        }

        [Test]
        public async Task A_first_probe_already_within_tolerance_skips_further_probing() {
            scene.AduPerSecond = 29500;   // 1s probe = 29500, inside 30000 ±5%
            var ok = await CreateSUT().CaptureFlatSetAsync(Request(), new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(scene.ProbedExposures, Has.Count.EqualTo(1));
            Assert.That(captured, Has.Count.EqualTo(3));
        }

        [Test]
        public async Task Lights_the_panel_at_the_requested_brightness_and_turns_it_off_after() {
            _ = await CreateSUT().CaptureFlatSetAsync(Request(brightness: 60), new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(panel.RequestedBrightness, Is.EqualTo(60));
            Assert.That(panel.LightRequests, Has.Count.EqualTo(2), "on for the set, off after");
            Assert.That(panel.LightRequests[0], Is.True);
            Assert.That(panel.LightRequests[1], Is.False);
            Assert.That(panel.LightOn, Is.False);
        }

        [Test]
        public async Task A_panel_too_dim_for_the_target_pins_the_max_bound_and_fails_without_capturing() {
            scene.AduPerSecond = 100;   // even 10s max = 1000 ADU, target 30000
            var ok = await CreateSUT().CaptureFlatSetAsync(Request(), new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(ok, Is.False);
            Assert.That(captured, Is.Empty, "a failed probe must never record bogus flats");
            Assert.That(panel.LightRequests.Last(), Is.False, "the panel light is restored off on failure");
        }

        [Test]
        public async Task A_zero_light_probe_fails_honestly() {
            scene.AduPerSecond = 0;   // pitch black — target/mean is meaningless
            var ok = await CreateSUT().CaptureFlatSetAsync(Request(), new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(ok, Is.False);
            Assert.That(captured, Is.Empty);
        }

        [Test]
        public void Cancellation_mid_set_propagates_and_still_turns_the_panel_off() {
            using var cts = new CancellationTokenSource();
            imagingMock
                .Setup(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<string>()))
                .Returns(async () => {
                    await cts.CancelAsync();
                    cts.Token.ThrowIfCancellationRequested();
                    return (IExposureData)null!;
                });

            Assert.CatchAsync<OperationCanceledException>(
                () => CreateSUT().CaptureFlatSetAsync(Request(), new Progress<ApplicationStatus>(), cts.Token));
            Assert.That(panel.LightRequests.Last(), Is.False, "the finally must restore the light even on cancel");
        }

        [Test]
        public async Task An_unavailable_panel_still_runs_the_probe_loop() {
            // Manual EL panels and sky sources are legitimate — the panel is optional.
            panel.ThrowOnApply = true;
            var ok = await CreateSUT().CaptureFlatSetAsync(Request(), new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(captured, Has.Count.EqualTo(3));
        }

        [Test]
        public async Task Missing_capture_wiring_fails_instead_of_pretending() {
            var sut = new FlatCaptureService(frames: null, imaging: null, flatDevice: panel);
            var ok = await sut.CaptureFlatSetAsync(Request(), new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(ok, Is.False);
        }

        [Test]
        public async Task A_misconfigured_request_is_rejected_before_touching_equipment() {
            var ok = await CreateSUT().CaptureFlatSetAsync(
                Request(frameCount: 0), new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(ok, Is.False);
            Assert.That(scene.ProbedExposures, Is.Empty);
            Assert.That(panel.LightRequests, Is.Empty);
        }

        private static readonly ushort[] MeanSample = { 100, 200, 300 };

        [Test]
        public void MeanAdu_is_the_plain_pixel_mean() {
            Assert.That(FlatCaptureService.MeanAdu(MeanSample), Is.EqualTo(200));
            Assert.That(FlatCaptureService.MeanAdu(ReadOnlySpan<ushort>.Empty), Is.EqualTo(0));
        }

        [Test]
        public void FlatPanelFlats_throws_when_no_executor_is_wired() {
            var item = new FlatPanelFlats(flatCaptureExecutor: null);
            Assert.ThrowsAsync<SequenceEntityFailedException>(
                () => item.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));
        }

        [Test]
        public void FlatPanelFlats_throws_when_the_executor_reports_failure() {
            var executor = new Mock<IFlatCaptureExecutor>();
            executor
                .Setup(x => x.CaptureFlatSetAsync(It.IsAny<FlatSetRequest>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var item = new FlatPanelFlats(executor.Object);
            Assert.ThrowsAsync<SequenceEntityFailedException>(
                () => item.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));
        }

        [Test]
        public async Task FlatPanelFlats_passes_its_fields_through_to_the_executor() {
            FlatSetRequest? seen = null;
            var executor = new Mock<IFlatCaptureExecutor>();
            executor
                .Setup(x => x.CaptureFlatSetAsync(It.IsAny<FlatSetRequest>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Callback<FlatSetRequest, IProgress<ApplicationStatus>, CancellationToken>((r, _, _) => seen = r)
                .ReturnsAsync(true);
            var item = new FlatPanelFlats(executor.Object) {
                TargetAdu = 25000, TargetAduTolerancePct = 3, FrameCount = 12,
                Brightness = 80, MinExposureSec = 0.5, MaxExposureSec = 6, Gain = 120, Offset = 30,
            };

            await item.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(seen, Is.EqualTo(new FlatSetRequest(25000, 3, 12, 80, 0.5, 6, 120, 30)));
        }

        [Test]
        public void FlatPanelFlats_rejects_a_misconfigured_plan_naming_the_fields() {
            var executor = new Mock<IFlatCaptureExecutor>();
            var item = new FlatPanelFlats(executor.Object) { MinExposureSec = 5, MaxExposureSec = 1 };
            var ex = Assert.ThrowsAsync<SequenceEntityFailedException>(
                () => item.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("misconfigured"));
            executor.VerifyNoOtherCalls();
        }

        [Test]
        public void FlatPanelFlats_clone_preserves_every_field() {
            var original = new FlatPanelFlats(flatCaptureExecutor: null) {
                TargetAdu = 25000, TargetAduTolerancePct = 3, FrameCount = 12,
                Brightness = 80, MinExposureSec = 0.5, MaxExposureSec = 6, Gain = 120, Offset = 30,
            };
            var clone = (FlatPanelFlats)original.Clone();
            Assert.That(clone, Is.Not.SameAs(original));
            Assert.That(clone.TargetAdu, Is.EqualTo(25000));
            Assert.That(clone.TargetAduTolerancePct, Is.EqualTo(3));
            Assert.That(clone.FrameCount, Is.EqualTo(12));
            Assert.That(clone.Brightness, Is.EqualTo(80));
            Assert.That(clone.MinExposureSec, Is.EqualTo(0.5));
            Assert.That(clone.MaxExposureSec, Is.EqualTo(6));
            Assert.That(clone.Gain, Is.EqualTo(120));
            Assert.That(clone.Offset, Is.EqualTo(30));
        }

        // ── §48.4 sky flats ──────────────────────────────────────────────────
        // No flat panel is involved; the sky itself is the light source, so these exercise the
        // frame source directly (twilight brightness = AduPerSecond) plus the stop-window bail-outs.

        private static SkyFlatSetRequest SkyRequest(
            double targetAdu = 25000, double tolerancePct = 5, int frameCount = 3,
            double minSec = 0.01, double maxSec = 10, double stopMax = 50000, double stopMin = 5000,
            int gain = 101, int offset = 7) =>
            new(targetAdu, tolerancePct, frameCount, minSec, maxSec, stopMax, stopMin, gain, offset);

        [Test]
        public async Task Sky_converges_and_captures_the_set_as_FLAT_reprobing_each_frame() {
            scene.AduPerSecond = 25000;   // 1s probe = 25000 = target, converges on the first probe
            var ok = await CreateSUT().CaptureSkyFlatSetAsync(SkyRequest(), new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(captured, Has.Count.EqualTo(3), "one saved frame per requested count");
            Assert.That(captured.All(s => s.ImageType == ImageTypes.FLAT), Is.True);
            Assert.That(captured.All(s => Math.Abs(s.ExposureTime - 1) < 1e-6), Is.True);
            Assert.That(captured[0].Gain, Is.EqualTo(101));
            Assert.That(captured[0].Offset, Is.EqualTo(7));
            Assert.That(scene.ProbedExposures, Has.Count.EqualTo(3), "exactly one probe per frame — no panel, no wasted twilight");
        }

        [Test]
        public async Task Sky_too_bright_at_the_minimum_exposure_bails_without_capturing() {
            // 10M ADU/s saturates every frame to full-well (well over the 50000 ceiling); a narrow
            // exposure window lets the probe walk to the 0.01s floor and hit the bright bail there.
            scene.AduPerSecond = 10_000_000;
            var ok = await CreateSUT().CaptureSkyFlatSetAsync(
                SkyRequest(minSec: 0.01, maxSec: 0.02), new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(ok, Is.False, "dawn is too bright — the set must stop, not save blown frames");
            Assert.That(captured, Is.Empty);
        }

        [Test]
        public async Task Sky_too_dark_at_the_maximum_exposure_bails_without_capturing() {
            scene.AduPerSecond = 100;   // even a 10s frame reads 1000 ADU, under the 5000 floor
            var ok = await CreateSUT().CaptureSkyFlatSetAsync(SkyRequest(), new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(ok, Is.False, "the sky is too dark — stop rather than save under-exposed frames");
            Assert.That(captured, Is.Empty);
        }

        [Test]
        public async Task Sky_pinned_but_inside_the_stop_window_captures_anyway() {
            // 4,000,000 ADU/s pins the exposure at the 0.01s floor, where a 0.01s frame reads 40000
            // ADU — off-target (target 25000) but comfortably inside [5000, 50000]. An
            // off-target-but-usable frame beats losing the twilight window, so it captures anyway.
            scene.AduPerSecond = 4_000_000;
            var ok = await CreateSUT().CaptureSkyFlatSetAsync(
                SkyRequest(frameCount: 2, minSec: 0.01, maxSec: 0.02), new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(captured, Has.Count.EqualTo(2));
            Assert.That(captured.All(s => Math.Abs(s.ExposureTime - 0.01) < 1e-6), Is.True, "captured at the pinned min exposure");
        }

        [Test]
        public async Task Sky_zero_light_fails_honestly() {
            scene.AduPerSecond = 0;
            var ok = await CreateSUT().CaptureSkyFlatSetAsync(SkyRequest(), new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(ok, Is.False);
            Assert.That(captured, Is.Empty);
        }

        [Test]
        public async Task Sky_rejects_a_target_outside_its_stop_bounds_before_probing() {
            var ok = await CreateSUT().CaptureSkyFlatSetAsync(
                SkyRequest(targetAdu: 60000), new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(ok, Is.False, "target ADU above the stop-max ceiling is nonsensical");
            Assert.That(scene.ProbedExposures, Is.Empty);
        }

        [Test]
        public async Task Sky_missing_capture_wiring_fails_instead_of_pretending() {
            var sut = new FlatCaptureService(frames: null, imaging: null, flatDevice: panel);
            var ok = await sut.CaptureSkyFlatSetAsync(SkyRequest(), new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(ok, Is.False);
        }

        [Test]
        public void Sky_cancellation_mid_set_propagates() {
            using var cts = new CancellationTokenSource();
            scene.AduPerSecond = 25000;
            imagingMock
                .Setup(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<string>()))
                .Returns(async () => {
                    await cts.CancelAsync();
                    cts.Token.ThrowIfCancellationRequested();
                    return (IExposureData)null!;
                });

            Assert.CatchAsync<OperationCanceledException>(
                () => CreateSUT().CaptureSkyFlatSetAsync(SkyRequest(), new Progress<ApplicationStatus>(), cts.Token));
        }

        [Test]
        public void SkyFlats_throws_when_no_executor_is_wired() {
            var item = new SkyFlats(flatCaptureExecutor: null);
            Assert.ThrowsAsync<SequenceEntityFailedException>(
                () => item.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));
        }

        [Test]
        public void SkyFlats_throws_when_the_executor_reports_failure() {
            var executor = new Mock<IFlatCaptureExecutor>();
            executor
                .Setup(x => x.CaptureSkyFlatSetAsync(It.IsAny<SkyFlatSetRequest>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var item = new SkyFlats(executor.Object);
            Assert.ThrowsAsync<SequenceEntityFailedException>(
                () => item.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));
        }

        [Test]
        public async Task SkyFlats_passes_its_fields_through_to_the_executor() {
            SkyFlatSetRequest? seen = null;
            var executor = new Mock<IFlatCaptureExecutor>();
            executor
                .Setup(x => x.CaptureSkyFlatSetAsync(It.IsAny<SkyFlatSetRequest>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Callback<SkyFlatSetRequest, IProgress<ApplicationStatus>, CancellationToken>((r, _, _) => seen = r)
                .ReturnsAsync(true);
            var item = new SkyFlats(executor.Object) {
                TargetAdu = 20000, TargetAduTolerancePct = 4, FrameCount = 15,
                MinExposureSec = 0.02, MaxExposureSec = 8, StopAtMaxAdu = 45000, StopAtMinAdu = 4000,
                Gain = 120, Offset = 30,
            };

            await item.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(seen, Is.EqualTo(new SkyFlatSetRequest(20000, 4, 15, 0.02, 8, 45000, 4000, 120, 30)));
        }

        [Test]
        public void SkyFlats_rejects_a_target_outside_the_stop_window_naming_the_fields() {
            var executor = new Mock<IFlatCaptureExecutor>();
            var item = new SkyFlats(executor.Object) { TargetAdu = 25000, StopAtMaxAdu = 20000 };
            var ex = Assert.ThrowsAsync<SequenceEntityFailedException>(
                () => item.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("misconfigured"));
            executor.VerifyNoOtherCalls();
        }

        [Test]
        public void SkyFlats_clone_preserves_every_field() {
            var original = new SkyFlats(flatCaptureExecutor: null) {
                TargetAdu = 20000, TargetAduTolerancePct = 4, FrameCount = 15,
                MinExposureSec = 0.02, MaxExposureSec = 8, StopAtMaxAdu = 45000, StopAtMinAdu = 4000,
                Gain = 120, Offset = 30,
            };
            var clone = (SkyFlats)original.Clone();
            Assert.That(clone, Is.Not.SameAs(original));
            Assert.That(clone.TargetAdu, Is.EqualTo(20000));
            Assert.That(clone.TargetAduTolerancePct, Is.EqualTo(4));
            Assert.That(clone.FrameCount, Is.EqualTo(15));
            Assert.That(clone.MinExposureSec, Is.EqualTo(0.02));
            Assert.That(clone.MaxExposureSec, Is.EqualTo(8));
            Assert.That(clone.StopAtMaxAdu, Is.EqualTo(45000));
            Assert.That(clone.StopAtMinAdu, Is.EqualTo(4000));
            Assert.That(clone.Gain, Is.EqualTo(120));
            Assert.That(clone.Offset, Is.EqualTo(30));
        }
    }
}
