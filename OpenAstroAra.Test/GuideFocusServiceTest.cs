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
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Setup → Smart Focus, guide-camera card: the focus loop's guards and its frame measurement (sim-free — the
    /// capture path through the guider daemon is exercised against the fake guider in integration), and the
    /// main-camera connect guard that keeps the guider's camera from being opened twice.
    /// </summary>
    [TestFixture]
    public class GuideFocusServiceTest {

        private const ushort Background = 1000;

        private static void AddStar(ushort[] frame, int width, int height, int cx, int cy, double amplitude, double sigma = 1.5) {
            int r = (int)Math.Ceiling(sigma * 3);
            for (int dy = -r; dy <= r; dy++) {
                int y = cy + dy;
                if (y < 0 || y >= height) continue;
                for (int dx = -r; dx <= r; dx++) {
                    int x = cx + dx;
                    if (x < 0 || x >= width) continue;
                    double v = amplitude * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
                    int idx = y * width + x;
                    frame[idx] = (ushort)Math.Min(ushort.MaxValue, frame[idx] + v);
                }
            }
        }

        private static ushort[] FlatField(int width, int height) {
            var f = new ushort[width * height];
            Array.Fill(f, Background);
            return f;
        }

        private static Phd2SettingsDto Phd2(string guiderCamera) => new(
            Host: "localhost", Port: 4400, Phd2Profile: "Default", DitherEnabled: true, DitherEveryNFrames: 1,
            DitherPixels: 5, SettlePixels: 1.5, SettleTimeSec: 10, SettleTimeoutSec: 60, ForceCalibrationEachSession: false,
            GuiderCamera: guiderCamera);

        private static DiscoveredDeviceDto Device(string host, string ip, int port, int number) =>
            new("id", "ASI220MM Mini", DeviceType.Camera, host, ip, port, number, false);

        private static GuiderRecoveryCoordinator NewRecovery() =>
            new(Mock.Of<IGuiderProcessSupervisor>(), Mock.Of<INotificationService>(), Mock.Of<IDiagnosticsService>(),
                NullLogger<GuiderRecoveryCoordinator>.Instance);

        [Test]
        public void Validate_bounds_the_exposure_and_binning() {
            Assert.DoesNotThrow(() => GuideFocusService.Validate(new GuideFocusStartRequestDto(2.0)));
            Assert.DoesNotThrow(() => GuideFocusService.Validate(new GuideFocusStartRequestDto(GuideFocusService.MaxExposureSec, 2)));
            Assert.Throws<ArgumentOutOfRangeException>(() => GuideFocusService.Validate(new GuideFocusStartRequestDto(0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => GuideFocusService.Validate(new GuideFocusStartRequestDto(31)));
            Assert.Throws<ArgumentOutOfRangeException>(() => GuideFocusService.Validate(new GuideFocusStartRequestDto(double.NaN)));
            Assert.Throws<ArgumentOutOfRangeException>(() => GuideFocusService.Validate(new GuideFocusStartRequestDto(1, 0)));
        }

        [Test]
        public void The_daemon_owns_its_camera_while_guiding_or_calibrating() {
            Assert.That(GuideFocusService.GuiderBusy("Guiding"), Is.True);
            Assert.That(GuideFocusService.GuiderBusy("Calibrating"), Is.True);
            Assert.That(GuideFocusService.GuiderBusy("LostLock"), Is.True);
            Assert.That(GuideFocusService.GuiderBusy("Paused"), Is.True);
            Assert.That(GuideFocusService.GuiderBusy("Looping"), Is.False, "looping is just exposing — the lease stops it");
            Assert.That(GuideFocusService.GuiderBusy("Stopped"), Is.False);
            Assert.That(GuideFocusService.GuiderBusy(""), Is.False);
            Assert.That(GuideFocusService.GuiderBusy(null), Is.False);
        }

        [Test]
        public void Measure_reads_stars_hfr_peak_and_fwhm_from_a_guide_frame() {
            int w = 200, h = 200;
            var frame = FlatField(w, h);
            AddStar(frame, w, h, 40, 40, 20_000);
            AddStar(frame, w, h, 120, 60, 12_000);
            AddStar(frame, w, h, 80, 150, 9_000);
            AddStar(frame, w, h, 160, 160, 15_000, sigma: 2.0);
            var at = new DateTimeOffset(2026, 10, 3, 4, 0, 0, TimeSpan.Zero);

            var sample = GuideFocusService.Measure(frame, w, h, 7, at);

            Assert.That(sample.Seq, Is.EqualTo(7));
            Assert.That(sample.CapturedUtc, Is.EqualTo(at));
            Assert.That(sample.Stars, Is.EqualTo(4));
            Assert.That(sample.Hfr, Is.GreaterThan(0.5).And.LessThan(5));
            Assert.That(sample.PeakAdu, Is.EqualTo(Background + 20_000).Within(500), "the brightest star's peak");
            Assert.That(sample.Fwhm, Is.GreaterThan(0));
        }

        [Test]
        public void Measure_on_a_starless_frame_reads_zero_not_NaN() {
            var sample = GuideFocusService.Measure(FlatField(120, 120), 120, 120, 1, DateTimeOffset.UnixEpoch);
            Assert.That(sample.Stars, Is.EqualTo(0));
            Assert.That(sample.Hfr, Is.EqualTo(0));
            Assert.That(sample.PeakAdu, Is.EqualTo(0));
            Assert.That(sample.Fwhm, Is.EqualTo(0));
        }

        [Test]
        public async Task Status_is_idle_before_a_start_and_start_refuses_a_disconnected_guider() {
            using var guider = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            using var svc = new GuideFocusService(guider, Mock.Of<IPolarAlignFrameFetcher>(), decoder: Mock.Of<IGuideFrameDecoder>());

            var status = svc.GetStatus();
            Assert.That(status.Active, Is.False);
            Assert.That(status.State, Is.EqualTo("idle"));
            Assert.That(status.Recent, Is.Empty);
            Assert.That(status.HasFrame, Is.False);
            Assert.That(svc.GetFrame(), Is.Null);
            Assert.That(svc.IsActive, Is.False);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(new GuideFocusStartRequestDto(2), CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("not connected"));
            Assert.That(svc.GetStatus().State, Is.EqualTo("idle"), "a refused start leaves no trace");
        }

        [Test]
        public async Task Start_refuses_while_polar_alignment_holds_the_guide_camera() {
            using var guider = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            using var svc = new GuideFocusService(guider, Mock.Of<IPolarAlignFrameFetcher>(), polarAlignActive: () => true);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(new GuideFocusStartRequestDto(2), CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("polar alignment"));
        }

        [Test]
        public async Task Stop_when_nothing_runs_is_a_no_op() {
            using var guider = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            using var svc = new GuideFocusService(guider, Mock.Of<IPolarAlignFrameFetcher>());
            await svc.StopAsync();
            Assert.That(svc.GetStatus().State, Is.EqualTo("idle"));
        }

        // ─── CameraConnectGuard ───

        [Test]
        public void The_guider_camera_is_recognised_by_host_ip_port_and_number() {
            var phd2 = Phd2("Alpaca Camera [rc91.lan:6800/1]");
            Assert.That(CameraConnectGuard.IsGuiderCamera(Device("rc91.lan", "192.168.1.235", 6800, 1), phd2), Is.True);
            Assert.That(CameraConnectGuard.IsGuiderCamera(Device("RC91", "192.168.1.235", 6800, 1), phd2), Is.True, "unqualified vs .lan");
            Assert.That(CameraConnectGuard.IsGuiderCamera(Device("other", "rc91.lan", 6800, 1), phd2), Is.True, "matched on the ip field");
            Assert.That(CameraConnectGuard.IsGuiderCamera(Device("rc91.lan", "192.168.1.235", 6800, 0), phd2), Is.False, "the main camera on the same bridge");
            Assert.That(CameraConnectGuard.IsGuiderCamera(Device("rc91.lan", "192.168.1.235", 11111, 1), phd2), Is.False, "another Alpaca server");
            Assert.That(CameraConnectGuard.IsGuiderCamera(Device("asiair.lan", "192.168.1.118", 6800, 1), phd2), Is.False);
        }

        // ─── the loop itself, driven through the synthetic-frames seam (no guider daemon) ───

        private static GuideFocusService NewLoopService(GuiderService guider, Func<(ushort[] Pixels, int Width, int Height)> frames) =>
            new(guider, Mock.Of<IPolarAlignFrameFetcher>(), decoder: Mock.Of<IGuideFrameDecoder>(), syntheticFrames: frames);

        private static GuiderService NewGuider() =>
            new(new HeadlessProfileService(), NewRecovery(), NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

        private static (ushort[] Pixels, int Width, int Height) StarFrame() {
            const int w = 128, h = 128;
            var f = FlatField(w, h);
            foreach (var (x, y) in new[] { (30, 30), (90, 40), (60, 95), (100, 100) }) {
                AddStar(f, w, h, x, y, 20000, sigma: 1.8);
            }
            return (f, w, h);
        }

        private static async Task<GuideFocusStatusDto> PollStatusAsync(GuideFocusService svc, Func<GuideFocusStatusDto, bool> done) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var status = svc.GetStatus();
            while (!done(status)) {
                await Task.Delay(20, cts.Token);
                status = svc.GetStatus();
            }
            return status;
        }

        private static GuideFocusSampleDto Sample(long seq, double hfr, int stars) =>
            new(seq, DateTimeOffset.UnixEpoch, hfr, stars, 20000, hfr * 2);

        [Test]
        public void Record_keeps_the_best_two_star_frame_caps_the_window_and_updates_the_frame() {
            using var guider = NewGuider();
            using var svc = NewLoopService(guider, StarFrame);

            svc.Record(Sample(1, 0.4, 1), jpeg: null);          // one "star" (a hot pixel) never counts as best
            svc.Record(Sample(2, 2.0, 5), jpeg: new byte[] { 1 });
            svc.Record(Sample(3, 1.5, 3), jpeg: null);          // better, no new picture
            svc.Record(Sample(4, 1.8, 4), jpeg: null);          // worse — best stays at seq 3
            var status = svc.GetStatus();
            Assert.That(status.BestHfr, Is.EqualTo(1.5));
            Assert.That(status.BestSeq, Is.EqualTo(3));
            Assert.That(status.Latest!.Seq, Is.EqualTo(4));
            Assert.That(svc.GetFrame()!.Value.Seq, Is.EqualTo(2), "the frame is the last one that came with a picture");

            for (var seq = 5; seq < 5 + GuideFocusService.RecentWindow; seq++) {
                svc.Record(Sample(seq, 2.5, 4), jpeg: null);
            }
            status = svc.GetStatus();
            Assert.That(status.Recent, Has.Count.EqualTo(GuideFocusService.RecentWindow));
            Assert.That(status.Recent[^1].Seq, Is.EqualTo(4 + GuideFocusService.RecentWindow));
        }

        [Test]
        public async Task The_loop_measures_frames_and_a_stop_ends_it() {
            using var guider = NewGuider();
            using var svc = NewLoopService(guider, StarFrame);

            await svc.StartAsync(new GuideFocusStartRequestDto(GuideFocusService.MinExposureSec), CancellationToken.None);
            Assert.That(svc.IsActive, Is.True);
            var running = await PollStatusAsync(svc, s => s.Seq >= 2);
            Assert.That(running.State, Is.EqualTo("running"));
            Assert.That(running.Latest!.Stars, Is.GreaterThanOrEqualTo(2));
            Assert.That(running.BestHfr, Is.GreaterThan(0));
            Assert.That(running.HasFrame, Is.True);

            await svc.StopAsync();
            var stopped = svc.GetStatus();
            Assert.That(stopped.State, Is.EqualTo("stopped"));
            Assert.That(stopped.Active, Is.False);
            Assert.That(svc.IsActive, Is.False);
        }

        [Test]
        public async Task Repeated_frame_failures_end_the_loop_in_error_and_a_start_restarts_it() {
            using var guider = NewGuider();
            var fail = true;
            using var svc = NewLoopService(guider, () => fail ? throw new InvalidOperationException("camera dropped") : StarFrame());

            await svc.StartAsync(new GuideFocusStartRequestDto(GuideFocusService.MinExposureSec), CancellationToken.None);
            var failed = await PollStatusAsync(svc, s => s.State != "running");
            Assert.That(failed.State, Is.EqualTo("error"));
            Assert.That(failed.Active, Is.False);
            Assert.That(failed.ConsecutiveFailures, Is.EqualTo(GuideFocusService.MaxConsecutiveFailures));
            Assert.That(failed.Error, Does.Contain("camera dropped"));

            fail = false;
            await svc.StartAsync(new GuideFocusStartRequestDto(GuideFocusService.MinExposureSec), CancellationToken.None);
            var restarted = await PollStatusAsync(svc, s => s.Latest is not null);
            Assert.That(restarted.State, Is.EqualTo("running"));
            Assert.That(restarted.Error, Is.Null);
            await svc.StopAsync();
        }

        [Test]
        public void An_ip_addressed_guider_camera_matches_the_device_ip() {
            var phd2 = Phd2("Alpaca Camera [192.168.1.235:6800/1]");
            Assert.That(CameraConnectGuard.IsGuiderCamera(Device("rc91.lan", "192.168.1.235", 6800, 1), phd2), Is.True);
            Assert.That(CameraConnectGuard.IsGuiderCamera(Device("rc91.lan", "192.168.1.236", 6800, 1), phd2), Is.False);
        }

        [Test]
        public void No_guider_camera_or_a_non_alpaca_one_never_matches() {
            Assert.That(CameraConnectGuard.IsGuiderCamera(Device("rc91.lan", "192.168.1.235", 6800, 1), Phd2("")), Is.False);
            Assert.That(CameraConnectGuard.IsGuiderCamera(Device("rc91.lan", "192.168.1.235", 6800, 1), Phd2("ZWO ASI Camera (1)")), Is.False);
        }

        [Test]
        public void Loopback_spellings_are_one_host() {
            Assert.That(CameraConnectGuard.HostsEqual("localhost", "127.0.0.1"), Is.True);
            Assert.That(CameraConnectGuard.HostsEqual("::1", "LOCALHOST"), Is.True);
            Assert.That(CameraConnectGuard.HostsEqual("rc91", "rc91.local"), Is.True);
            Assert.That(CameraConnectGuard.HostsEqual("rc91.lan", "rc91.local"), Is.False, "two qualified names differ");
            Assert.That(CameraConnectGuard.HostsEqual("192.168.1.2", "192.168.1.20"), Is.False, "no label match on addresses");
            Assert.That(CameraConnectGuard.HostsEqual("", "rc91"), Is.False);
        }

        [Test]
        public async Task The_connect_guard_only_bites_while_the_guider_is_connected() {
            var device = Device("rc91.lan", "192.168.1.235", 6800, 1);
            var profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetPhd2Settings()).Returns(Phd2("Alpaca Camera [rc91.lan:6800/1]"));

            var disconnected = new Mock<IGuiderService>();
            disconnected.Setup(g => g.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GuiderDto("phd2", "PHD2", EquipmentConnectionState.Disconnected, new GuiderStateDto("stopped", null, null, null, null)));
            Assert.That(await EquipmentEndpoints.GuideCameraInUseAsync(device, disconnected.Object, profiles.Object, CancellationToken.None), Is.False);

            var none = new Mock<IGuiderService>();
            none.Setup(g => g.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((GuiderDto?)null);
            Assert.That(await EquipmentEndpoints.GuideCameraInUseAsync(device, none.Object, profiles.Object, CancellationToken.None), Is.False);

            var connected = new Mock<IGuiderService>();
            connected.Setup(g => g.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GuiderDto("phd2", "PHD2", EquipmentConnectionState.Connected, new GuiderStateDto("stopped", null, null, null, null)));
            Assert.That(await EquipmentEndpoints.GuideCameraInUseAsync(device, connected.Object, profiles.Object, CancellationToken.None), Is.True);
            Assert.That(await EquipmentEndpoints.GuideCameraInUseAsync(Device("rc91.lan", "192.168.1.235", 6800, 0), connected.Object, profiles.Object, CancellationToken.None), Is.False);
            Assert.That(CameraConnectGuard.Detail(device, profiles.Object.GetPhd2Settings()), Does.Contain("Setup → Smart Focus"));
        }
    

        [Test]
        public void Production_decoder_reads_a_guider_fits_through_the_daemons_own_cfitsio() {
            // The NINA-era reader wanted cfitsionative.dll, which the Pi package does not ship:
            // "gave up after 5 failed frames" on every live-focus frame (2026-10-03). The decoder now
            // goes through OpenAstroAra.Fits like polar alignment does for the same guider frames.
            var dir = Path.Combine(Path.GetTempPath(), "ara-guide-decode-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                var path = Path.Combine(dir, "guide.fits");
                const int w = 64, h = 48;
                var pixels = new ushort[w * h];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = (ushort)(1000 + i % 251);
                pixels[20 * w + 30] = 60000;
                using (var fits = OpenAstroAra.Fits.FitsImage.Create(path, w, h, OpenAstroAra.Fits.FitsBitDepth.UnsignedShort)) {
                    fits.WriteImageData(pixels);
                    fits.Complete();
                }

                var (decoded, width, height) = new CfitsioGuideFrameDecoder().Decode(path);

                Assert.That((width, height), Is.EqualTo((w, h)));
                Assert.That(decoded, Is.EqualTo(pixels));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }
    

        [Test]
        public void ExpectedInFocusHfr_follows_the_guide_optics_and_never_drops_below_the_detector_floor() {
            // A 120 mm guide scope with 3.75 µm pixels: 6.45"/px, seeing-limited stars are sub-pixel,
            // so the floor is the answer (0.76 measured on the rig, 2026-10-03).
            var guideScope = GuideFocusService.ExpectedInFocusHfr(120, 3.75, 0);
            Assert.That(guideScope, Is.Not.Null);
            Assert.That(guideScope!.Value.PlateScaleArcsec, Is.EqualTo(6.45).Within(0.01));
            Assert.That(guideScope.Value.ExpectedHfrPx, Is.EqualTo(GuideFocusService.DetectorHfrFloorPx));

            // An OAG on a long focal length samples finely: the seeing disc spans pixels.
            var oag = GuideFocusService.ExpectedInFocusHfr(2000, 3.75, 200);
            Assert.That(oag!.Value.PlateScaleArcsec, Is.EqualTo(0.39).Within(0.01));
            Assert.That(oag.Value.ExpectedHfrPx, Is.GreaterThan(3.5).And.LessThan(4.5), "≈ 3.0\" seeing ⊕ 0.57\" Airy, halved, over 0.39\"/px");

            Assert.That(GuideFocusService.ExpectedInFocusHfr(0, 3.75, 0), Is.Null);
            Assert.That(GuideFocusService.ExpectedInFocusHfr(120, 0, 0), Is.Null);
            Assert.That(GuideFocusService.ExpectedInFocusHfr(double.NaN, 3.75, 0), Is.Null);
        }

        [Test]
        public void Status_carries_the_expected_hfr_from_the_optics_and_null_without_them() {
            using var guider = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            using var withOptics = new GuideFocusService(guider, Mock.Of<IPolarAlignFrameFetcher>(),
                decoder: Mock.Of<IGuideFrameDecoder>(), optics: () => (120, 3.75, 0));
            Assert.That(withOptics.GetStatus().ExpectedHfr, Is.EqualTo(GuideFocusService.DetectorHfrFloorPx));
            Assert.That(withOptics.GetStatus().PlateScaleArcsec, Is.EqualTo(6.45).Within(0.01));

            using var without = new GuideFocusService(guider, Mock.Of<IPolarAlignFrameFetcher>(), decoder: Mock.Of<IGuideFrameDecoder>());
            Assert.That(without.GetStatus().ExpectedHfr, Is.Null);
        }

        [Test]
        public void MedianHfrOfBrightest_ignores_faint_flickering_stars() {
            static DetectedStar Star(double hfr, double peak) => new() { HFR = hfr, MaxBrightness = peak };
            var brightestFirst = new List<DetectedStar> {
                Star(1.0, 50000), Star(1.1, 40000), Star(0.9, 30000), Star(1.0, 20000), Star(1.2, 10000),
                Star(3.0, 400), Star(0.2, 300), Star(5.0, 250), // noise-level blobs beyond the bright set
            };
            Assert.That(GuideFocusService.MedianHfrOfBrightest(brightestFirst, 5), Is.EqualTo(1.0));
            Assert.That(GuideFocusService.MedianHfrOfBrightest(brightestFirst, 4), Is.EqualTo(1.0));
            Assert.That(GuideFocusService.MedianHfrOfBrightest(new List<DetectedStar>(), 12), Is.EqualTo(0.0));
        }
    

        [Test]
        public async Task The_loop_stops_itself_once_the_median_hfr_has_held_under_the_target() {
            // An OAG at 2000 mm with a 200 mm aperture: the target is ~3.9 px × 1.3, and the synthetic
            // σ=1.8 stars read ~2 px, so the tenth measurable frame ends the run as "in_focus".
            using var guider = NewGuider();
            using var svc = new GuideFocusService(guider, Mock.Of<IPolarAlignFrameFetcher>(),
                decoder: Mock.Of<IGuideFrameDecoder>(), syntheticFrames: StarFrame, optics: () => (2000, 3.75, 200));

            await svc.StartAsync(new GuideFocusStartRequestDto(GuideFocusService.MinExposureSec), CancellationToken.None);
            var stopped = await PollStatusAsync(svc, s => s.State == "stopped");

            Assert.That(stopped.StopReason, Is.EqualTo(GuideFocusService.StopReasonInFocus));
            Assert.That(stopped.Seq, Is.EqualTo(GuideFocusService.InFocusHoldFrames));
            Assert.That(svc.IsActive, Is.False);

            // A new run starts over: frame counter, trend and stop reason.
            await svc.StartAsync(new GuideFocusStartRequestDto(GuideFocusService.MinExposureSec), CancellationToken.None);
            var fresh = svc.GetStatus();
            Assert.That(fresh.Seq, Is.LessThan(GuideFocusService.InFocusHoldFrames));
            Assert.That(fresh.StopReason, Is.Null);
            Assert.That(fresh.Recent.Count, Is.LessThan(GuideFocusService.InFocusHoldFrames));
            await svc.StopAsync();
        }

        [Test]
        public async Task The_loop_keeps_running_while_the_median_sits_above_the_target_or_without_a_target() {
            using var guider = NewGuider();
            // Guide scope at 120 mm: target 0.7 × 1.3 = 0.91 px, the σ=1.8 stars read ~2 px → never in focus.
            using var svc = new GuideFocusService(guider, Mock.Of<IPolarAlignFrameFetcher>(),
                decoder: Mock.Of<IGuideFrameDecoder>(), syntheticFrames: StarFrame, optics: () => (120, 3.75, 0));
            await svc.StartAsync(new GuideFocusStartRequestDto(GuideFocusService.MinExposureSec), CancellationToken.None);
            var running = await PollStatusAsync(svc, s => s.Seq >= GuideFocusService.InFocusHoldFrames + 2);
            Assert.That(running.State, Is.EqualTo("running"));
            Assert.That(running.StopReason, Is.Null);
            await svc.StopAsync();
            Assert.That(svc.GetStatus().StopReason, Is.Null, "a user stop carries no reason");

            Assert.That(svc.InFocusHeld(null), Is.False);
        }

        [Test]
        public void InFocusHeld_uses_the_median_so_single_bad_frames_do_not_reset_it() {
            using var guider = NewGuider();
            using var svc = NewLoopService(guider, StarFrame);
            // 10 frames at 0.8 with three seeing spikes at 1.6: median 0.8 ≤ 0.91.
            var hfrs = new[] { 0.8, 1.6, 0.8, 0.8, 1.6, 0.8, 0.8, 0.8, 1.6, 0.8 };
            for (int i = 0; i < hfrs.Length; i++) {
                svc.Record(Sample(i + 1, hfrs[i], 10), null);
                Assert.That(svc.InFocusHeld(0.7), Is.EqualTo(i == hfrs.Length - 1), $"after frame {i + 1}");
            }
        }
    }
}
