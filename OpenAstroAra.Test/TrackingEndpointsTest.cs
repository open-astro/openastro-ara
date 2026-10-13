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
using NUnit.Framework;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1264 — <c>GET /api/v1/tracking/calibration</c> (404 = never measured) and
    /// <c>POST /api/v1/tracking/recalibrate</c> (clears) over a real Kestrel host on a loopback port.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    public class TrackingEndpointsTest {

        private WebApplication app = null!;
        private InMemoryProfileStore store = null!;
        private HttpClient http = null!;

        [OneTimeSetUp]
        public async Task StartKestrel() {
            store = new InMemoryProfileStore();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<IProfileStore>(store);
            // #1264 stage 1 — the prior route asks the sequencer's executor; the real one with no sheet.
            builder.Services.AddSingleton<OpenAstroAra.Sequencer.SequenceItem.Telescope.ITrackingProbeExecutor>(
                new TrackingProbeService(store, new NoFrames(), specs: specs));
            builder.Services.AddProblemDetails();
            builder.Services.ConfigureHttpJsonOptions(opts => {
                // Program.cs's wire shape: the source-gen context plus snake_case names.
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            app = builder.Build();
            app.MapTrackingEndpoints();
            await app.StartAsync();
            http = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromMinutes(2) };
        }

        [OneTimeTearDown]
        public async Task StopKestrel() {
            http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }

        private sealed class FixedSpecs : IMountSpecSheetSource {
            public MountSpecSheetDto? CurrentMountSpecs { get; set; }
        }
        private readonly FixedSpecs specs = new();

        private sealed class NoFrames : IAnalysisFrameSource {
            public Task<AnalysisFrame> CaptureForAnalysisAsync(double exposureSec, int binning, CancellationToken ct) =>
                throw new InvalidOperationException("the prior never captures");
        }

        [Test]
        public async Task The_prior_route_names_the_stored_ceiling_or_nothing() {
            store.PutTrackingCalibration(null);
            using (var none = await http.GetAsync(new Uri("/api/v1/tracking/prior", UriKind.Relative))) {
                Assert.That(none.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                using var doc = JsonDocument.Parse(await none.Content.ReadAsStringAsync());
                Assert.That(doc.RootElement.GetProperty("seconds").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(doc.RootElement.GetProperty("source").ValueKind, Is.EqualTo(JsonValueKind.Null));
            }
            store.PutTrackingCalibration(new TrackingCalibrationDto(45, new DateTimeOffset(2026, 10, 11, 2, 0, 0, TimeSpan.Zero),
                null, null, null, null, "measured", []));
            using var stored = await http.GetAsync(new Uri("/api/v1/tracking/prior", UriKind.Relative));
            using var doc2 = JsonDocument.Parse(await stored.Content.ReadAsStringAsync());
            Assert.That(doc2.RootElement.GetProperty("seconds").GetDouble(), Is.EqualTo(45));
            Assert.That(doc2.RootElement.GetProperty("source").GetString(), Is.EqualTo("the last probe"));
            store.PutTrackingCalibration(null);
        }

        [Test]
        public async Task The_prior_route_serves_a_spec_sheet_prior_with_its_source_on_the_wire() {
            store.PutTrackingCalibration(null);
            store.PutOpticsSettings(new OpticsSettingsDto(447, 1.0, 6248, 4176, 3.76, 91));
            specs.CurrentMountSpecs = MountSpecSheet.Parse("{\"model\":\"EQM-35 Pro\",\"source\":\"manufacturer\",\"worm_period_s\":638,\"periodic_error_pp_arcsec\":25}");
            try {
                using var res = await http.GetAsync(new Uri("/api/v1/tracking/prior", UriKind.Relative));
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                Assert.That(doc.RootElement.GetProperty("seconds").GetDouble(), Is.GreaterThan(MountSpecSheet.MinPriorSeconds));
                Assert.That(doc.RootElement.GetProperty("source").GetString(), Is.EqualTo("the mount's spec sheet (manufacturer)"));
            } finally {
                specs.CurrentMountSpecs = null;
            }
        }

        [Test]
        public async Task Calibration_is_404_until_measured_then_the_stored_record_then_404_after_recalibrate() {
            store.PutTrackingCalibration(null);
            using (var missing = await http.GetAsync(new Uri("/api/v1/tracking/calibration", UriKind.Relative))) {
                Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            }

            store.PutTrackingCalibration(new TrackingCalibrationDto(45, new DateTimeOffset(2026, 10, 11, 2, 0, 0, TimeSpan.Zero),
                4.2, 3.1, 2.8, 20, "measured", [new TrackingProbeRungDto(10, 0.3, 3.0, 80, true, true)]));
            using (var found = await http.GetAsync(new Uri("/api/v1/tracking/calibration", UriKind.Relative))) {
                Assert.That(found.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                using var doc = JsonDocument.Parse(await found.Content.ReadAsStringAsync());
                Assert.That(doc.RootElement.GetProperty("ceiling_seconds").GetDouble(), Is.EqualTo(45));
                Assert.That(doc.RootElement.GetProperty("source").GetString(), Is.EqualTo("measured"));
                Assert.That(doc.RootElement.GetProperty("rungs").GetArrayLength(), Is.EqualTo(1));
            }

            using (var cleared = await http.PostAsync(new Uri("/api/v1/tracking/recalibrate", UriKind.Relative), content: null)) {
                Assert.That(cleared.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            }
            Assert.That(store.GetTrackingCalibration(), Is.Null);
            using var gone = await http.GetAsync(new Uri("/api/v1/tracking/calibration", UriKind.Relative));
            Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
    }
}
