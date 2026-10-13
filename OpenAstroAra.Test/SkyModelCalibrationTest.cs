#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1140 P2 — the sky-model calibration fit: k per filter kind from the library's backgrounds.</summary>
    [TestFixture]
    public class SkyModelCalibrationTest {

        private static readonly DateTimeOffset Now = new(2026, 10, 13, 6, 0, 0, TimeSpan.Zero);
        // A RedCat 51 + IMX571-ish rig under a Bortle 4 sky: 250 mm f/4.9, 3.76 µm, e⁻/ADU 0.25, QE 0.8.
        private static readonly SiteSettingsDto Site = new InMemoryProfileStore().GetSiteSettings() with { BortleClass = 4, SqmMagPerArcsec2 = 0 };
        private static readonly OpticsSettingsDto Optics = new(FocalLengthMm: 250, ReducerFactor: 1, SensorWidthPx: 6248, SensorHeightPx: 4176, PixelSizeUm: 3.76, ApertureMm: 51);
        private static readonly CameraElectronicsDto Camera = new(ElectronsPerAdu: 0.25, QuantumEfficiencyPeak: 0.8);
        private static readonly FilterSetDto Filters = new(new[] {
            new PlanningFilterDto("L", FilterKind.L), new PlanningFilterDto("Ha", FilterKind.Ha, 7), new PlanningFilterDto("OIII", FilterKind.Oiii, 7) });

        private static FrameListItemDto Frame(string? filter, double exposure, double background, FrameType type = FrameType.Light, int bin = 1, int daysAgo = 1) =>
            new(Guid.NewGuid(), Guid.Empty, "M31", type, filter, exposure, Now.AddDays(-daysAgo), null, null, null, 0,
                Gain: 100, BackgroundAdu: background, MedianAdu: background + 20, Bin: bin);

        /// <summary>Frames whose background follows the model exactly: pedestal + k × predicted rate × exposure, in ADU.</summary>
        private static List<FrameListItemDto> Library(string filter, FilterKind kind, double k, double pedestal, params double[] exposures) {
            var rate = SkyModelCalibrationService.PredictedSkyRateEPerSecPerPx(kind, 1, Site, Optics, Camera, Filters) * k / Camera.ElectronsPerAdu;
            return exposures.Select(t => Frame(filter, t, pedestal + rate * t)).ToList();
        }

        [Test]
        public void Varied_exposures_recover_k_and_the_pedestal_with_no_bias_frame() {
            var frames = Library("L", FilterKind.L, k: 0.8, pedestal: 501, 30, 60, 120, 60, 30, 120);
            var record = SkyModelCalibrationService.Fit(frames, Site, Optics, Camera, Filters, Now);
            var l = record.Filters.Single();
            Assert.That(l.Kind, Is.EqualTo("l"));
            Assert.That(l.Bin, Is.EqualTo(1));
            Assert.That(l.K, Is.EqualTo(0.8).Within(1e-3));
            Assert.That(l.PedestalAdu, Is.EqualTo(501).Within(0.1));
            Assert.That(l.ScatterPct, Is.EqualTo(0).Within(0.1));
            Assert.That(l.Passed, Is.True);
            Assert.That(l.Note, Is.Null);
            Assert.That(l.FrameCount, Is.EqualTo(6));
            Assert.That(record.FramesConsidered, Is.EqualTo(6));
        }

        [Test]
        public void One_exposure_length_needs_a_bias_frame_to_pin_the_pedestal() {
            var frames = Library("Ha", FilterKind.Ha, k: 1.1, pedestal: 501, 300, 300, 300, 300);
            var without = SkyModelCalibrationService.Fit(frames, Site, Optics, Camera, Filters, Now).Filters.Single();
            Assert.That(without.Passed, Is.False);
            Assert.That(without.K, Is.Null, "never guessed");
            Assert.That(without.Note, Does.Contain("one exposure length").And.Contain("bias"));

            frames.Add(Frame(null, 0.001, 501, FrameType.Bias));
            var with = SkyModelCalibrationService.Fit(frames, Site, Optics, Camera, Filters, Now).Filters.Single(f => f.Kind == "ha");
            Assert.That(with.K, Is.EqualTo(1.1).Within(1e-3));
            Assert.That(with.PedestalAdu, Is.EqualTo(501));
            Assert.That(with.Passed, Is.True);
        }

        [Test]
        public void The_gate_fails_an_implausible_k_or_a_scattered_night_and_says_which() {
            var tooBright = SkyModelCalibrationService.Fit(Library("L", FilterKind.L, k: 2.5, pedestal: 500, 30, 60, 120), Site, Optics, Camera, Filters, Now).Filters.Single();
            Assert.That(tooBright.K, Is.EqualTo(2.5).Within(1e-3));
            Assert.That(tooBright.Passed, Is.False);
            Assert.That(tooBright.Note, Does.Contain("outside the 0.4–1.5 band"));

            // Three nights at k = 1 and three at k = 0.4: a 60 % spread in sky rate.
            var scattered = Library("L", FilterKind.L, k: 1.0, pedestal: 500, 30, 60, 120).Concat(Library("L", FilterKind.L, k: 0.4, pedestal: 500, 30, 60, 120)).ToList();
            var s = SkyModelCalibrationService.Fit(scattered, Site, Optics, Camera, Filters, Now).Filters.Single();
            Assert.That(s.Passed, Is.False);
            Assert.That(s.ScatterPct, Is.GreaterThan(SkyModelCalibrationService.GateMaxScatterPct));
            Assert.That(s.Note, Does.Contain("Scatter"));
        }

        [Test]
        public void Kinds_and_binnings_fit_separately_unknown_filters_are_skipped_and_calibration_frames_are_not_lights() {
            var frames = Library("L", FilterKind.L, 0.9, 500, 30, 60, 120);
            frames.AddRange(Library("Ha", FilterKind.Ha, 0.7, 500, 120, 240, 300));
            frames.Add(Frame("Mystery", 60, 900));
            frames.Add(Frame("L", 60, 50, FrameType.Flat));
            // Bin 2: the predicted rate is 4× bin 1; a frame at k = 1 for bin 2 fits as its own entry.
            var bin2Rate = SkyModelCalibrationService.PredictedSkyRateEPerSecPerPx(FilterKind.L, 2, Site, Optics, Camera, Filters) / Camera.ElectronsPerAdu;
            frames.AddRange(new[] { 30.0, 60, 120 }.Select(t => Frame("L", t, 500 + bin2Rate * t, bin: 2)));

            var record = SkyModelCalibrationService.Fit(frames, Site, Optics, Camera, Filters, Now);
            Assert.That(record.Filters.Select(f => (f.Kind, f.Bin)), Is.EqualTo(new[] { ("ha", 1), ("l", 1), ("l", 2) }));
            Assert.That(record.Filters.Single(f => f.Kind == "l" && f.Bin == 1).K, Is.EqualTo(0.9).Within(1e-3));
            Assert.That(record.Filters.Single(f => f.Kind == "l" && f.Bin == 2).K, Is.EqualTo(1.0).Within(1e-3));
            Assert.That(record.Filters.Single(f => f.Kind == "ha").K, Is.EqualTo(0.7).Within(1e-3));
            Assert.That(record.FramesConsidered, Is.EqualTo(frames.Count), "the flat and the unknown filter count as considered, not fitted");
        }

        [Test]
        public void Missing_profile_figures_or_too_few_frames_are_named_not_guessed() {
            var frames = Library("L", FilterKind.L, 0.9, 500, 30, 60, 120);
            Assert.That(SkyModelCalibrationService.Fit(frames, Site, Optics, new CameraElectronicsDto(), Filters, Now).Filters.Single().Note, Does.Contain("e⁻/ADU"));
            Assert.That(SkyModelCalibrationService.Fit(frames, Site, Optics with { ApertureMm = 0 }, Camera, Filters, Now).Filters.Single().Note, Does.Contain("optics"));
            Assert.That(SkyModelCalibrationService.Fit(frames.Take(2).ToList(), Site, Optics, Camera, Filters, Now).Filters.Single().Note, Does.Contain("Only 2 frames"));
            Assert.That(SkyModelCalibrationService.Fit(Array.Empty<FrameListItemDto>(), Site, Optics, Camera, Filters, Now).Filters, Is.Empty);
        }

        [Test]
        public void Filter_kind_resolution_follows_the_declared_set() {
            Assert.That(SkyModelCalibrationService.KindOf("ha", Filters), Is.EqualTo(FilterKind.Ha), "case-insensitive");
            Assert.That(SkyModelCalibrationService.KindOf("Mystery", Filters), Is.Null);
            Assert.That(SkyModelCalibrationService.KindOf(null, Filters), Is.Null, "a mono rig's frame with no FILTER is not guessed");
            Assert.That(SkyModelCalibrationService.KindOf(null, new FilterSetDto(Array.Empty<PlanningFilterDto>())), Is.EqualTo(FilterKind.Osc));
            Assert.That(SkyModelCalibrationService.KindOf(null, new FilterSetDto(new[] { new PlanningFilterDto("none", FilterKind.Osc) })), Is.EqualTo(FilterKind.Osc));
            Assert.That(SkyModelCalibrationService.BandwidthNm(FilterKind.Ha, Filters), Is.EqualTo(7));
            Assert.That(SkyModelCalibrationService.BandwidthNm(FilterKind.L, Filters), Is.EqualTo(100), "undeclared bandwidth falls back to the kind's default");
        }

        [Test]
        public void Predicted_rate_matches_the_client_sky_model() {
            // The client's skyFluxEPerSecPerPx for this rig at Bortle 4 (mag 20.5), L (100 nm):
            // 1e4 · 10^(-0.4·20.5) · 100 · π·(51/20)² · (206.265·3.76/250)² · 0.8
            var scale = 206.265 * 3.76 / 250;
            var expected = 1e4 * Math.Pow(10, -0.4 * 20.5) * 100 * Math.PI * Math.Pow(51 / 20.0, 2) * scale * scale * 0.8;
            Assert.That(SkyModelCalibrationService.PredictedSkyRateEPerSecPerPx(FilterKind.L, 1, Site, Optics, Camera, Filters), Is.EqualTo(expected).Within(1e-9));
            Assert.That(SkyModelCalibrationService.PredictedSkyRateEPerSecPerPx(FilterKind.L, 1, Site with { SqmMagPerArcsec2 = 21.5 }, Optics, Camera, Filters),
                Is.LessThan(expected), "a measured SQM beats the Bortle ladder");
        }

        [Test]
        public async Task Recalibrate_reads_the_recent_library_pages_and_stores_the_record() {
            var store = new InMemoryProfileStore();
            store.PutSiteSettings(Site); store.PutOpticsSettings(Optics); store.PutCameraElectronics(Camera); store.PutFilterSet(Filters);
            var frames = Library("L", FilterKind.L, 0.85, 500, 30, 60, 120);
            frames.Add(Frame("L", 60, 999, daysAgo: 200)); // older than the window: ignored
            var repo = new Mock<IFrameRepository>();
            repo.Setup(r => r.ListAsync(200, null, null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CursorPage<FrameListItemDto>(frames.Take(2).ToList(), "2", true));
            repo.Setup(r => r.ListAsync(200, "2", null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CursorPage<FrameListItemDto>(frames.Skip(2).ToList(), null, false));
            var svc = new SkyModelCalibrationService(store, repo.Object) { UtcNow = () => Now };

            var record = await svc.RecalibrateAsync(CancellationToken.None);

            Assert.That(record.FramesConsidered, Is.EqualTo(3));
            Assert.That(record.Filters.Single().K, Is.EqualTo(0.85).Within(1e-3));
            Assert.That(store.GetSkyModelCalibration(), Is.SameAs(record));
            Assert.That(record.FittedUtc, Is.EqualTo(Now));
        }

        [Test]
        public void Snapshot_round_trip_and_share_strip() {
            var store = new InMemoryProfileStore();
            var record = new SkyModelCalibrationDto(Now, 3, new[] { new SkyModelFilterCalibrationDto("l", 1, 0.9, 4.2, 501, 1.1, 1.2, 3, true, null) });
            store.PutSkyModelCalibration(record);
            var snap = ProfileStoreSnapshot.Capture(store);
            Assert.That(snap.SkyModelCalibration, Is.SameAs(record));
            var json = JsonSerializer.Serialize(snap, AraJsonSerializerContext.Default.ProfileSnapshotDto);
            var back = JsonSerializer.Deserialize(json, AraJsonSerializerContext.Default.ProfileSnapshotDto)!;
            Assert.That(back.SkyModelCalibration!.Filters.Single().K, Is.EqualTo(0.9));
            var fresh = new InMemoryProfileStore();
            ProfileStoreSnapshot.Apply(fresh, back with { SkyModelCalibration = null });
            Assert.That(fresh.GetSkyModelCalibration(), Is.Null, "a never-fitted profile clears the live store");
        }
    }

    /// <summary>#1140 P2 — <c>/api/v1/planning/sky-model</c> over a real Kestrel host.</summary>
    [TestFixture]
    [Category("IO")]
    public class PlanningEndpointsTest {

        private WebApplication app = null!;
        private HttpClient http = null!;
        private InMemoryProfileStore store = null!;

        [OneTimeSetUp]
        public async Task StartKestrel() {
            store = new InMemoryProfileStore();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<IProfileStore>(store);
            builder.Services.AddSingleton(new SkyModelCalibrationService(store, frames: null));
            builder.Services.AddProblemDetails();
            builder.Services.ConfigureHttpJsonOptions(opts => {
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            app = builder.Build();
            app.MapPlanningEndpoints();
            await app.StartAsync();
            http = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromMinutes(2) };
        }

        [OneTimeTearDown]
        public async Task StopKestrel() {
            http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }

        [Test]
        public async Task Get_is_404_until_a_recalibrate_stores_a_record_and_delete_clears_it() {
            Assert.That((await http.GetAsync(new Uri("/api/v1/planning/sky-model", UriKind.Relative))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

            var fit = await http.PostAsync(new Uri("/api/v1/planning/sky-model/recalibrate", UriKind.Relative), content: null);
            Assert.That(fit.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var doc = JsonDocument.Parse(await fit.Content.ReadAsStringAsync());
            Assert.That(doc.RootElement.GetProperty("frames_considered").GetInt32(), Is.EqualTo(0), "no catalog wired: an empty but stored record");
            Assert.That(doc.RootElement.GetProperty("filters").GetArrayLength(), Is.EqualTo(0));

            Assert.That((await http.GetAsync(new Uri("/api/v1/planning/sky-model", UriKind.Relative))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await http.DeleteAsync(new Uri("/api/v1/planning/sky-model", UriKind.Relative))).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(store.GetSkyModelCalibration(), Is.Null);
            Assert.That((await http.GetAsync(new Uri("/api/v1/planning/sky-model", UriKind.Relative))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
    }
}
