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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§54.3 (#1185) — the prepare endpoint's mode binding over a real Kestrel host: a known mode
    /// reaches the service, an unknown one is a 400, and the body-less call older clients make is Full.</summary>
    [TestFixture]
    [Category("IO")]
    public class BugReportPrepareEndpointTest {
        private WebApplication app = null!;
        private HttpClient http = null!;
        private Mock<IBugReportService> svc = null!;

        [OneTimeSetUp]
        public async Task StartKestrel() {
            svc = new Mock<IBugReportService>(MockBehavior.Strict);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(svc.Object);
            // Every other service a system endpoint injects must be registered, or the route table fails
            // to build on the first request (see DataManagerDownloadEndpointTest).
            builder.Services.AddSingleton(Mock.Of<IBackupService>());
            builder.Services.AddSingleton(Mock.Of<ICameraService>());
            builder.Services.AddSingleton(Mock.Of<IDataManagerService>());
            builder.Services.AddSingleton(Mock.Of<IProfileShareService>());
            builder.Services.AddSingleton(Mock.Of<IProfileStore>());
            builder.Services.AddSingleton(Mock.Of<ISkyCatalogService>());
            builder.Services.AddSingleton(Mock.Of<IStorageBrowseService>());
            builder.Services.AddSingleton(Mock.Of<IStorageDeviceService>());
            builder.Services.AddSingleton(Mock.Of<IStorageCopyService>());
            builder.Services.AddSingleton(Mock.Of<ISolverRecommender>());
            builder.Services.AddSingleton(Mock.Of<IGuiderService>());
            builder.Services.AddSingleton<CaptureScanService>(_ => throw new InvalidOperationException("not used by this test"));
            builder.Services.AddSingleton<ActiveRunSessionRegistry>(_ => throw new InvalidOperationException("not used by this test"));
            builder.Services.AddProblemDetails();
            builder.Services.ConfigureHttpJsonOptions(opts => {
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            app = builder.Build();
            app.MapSystemEndpoints();
            await app.StartAsync();
            http = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromMinutes(1) };
        }

        [OneTimeTearDown]
        public async Task StopKestrel() {
            http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }

        [SetUp]
        public void Reset() => svc.Reset();

        private static BugReportPreparationDto Prepared(BugReportSharingMode mode) =>
            new(Guid.NewGuid(), "ready", 1234, null, DateTimeOffset.UtcNow, BugReportRedactor.ToWire(mode));

        [Test]
        public async Task A_known_mode_reaches_the_service_and_is_echoed() {
            svc.Setup(s => s.PrepareAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>(), BugReportSharingMode.Redacted))
                .ReturnsAsync(Prepared(BugReportSharingMode.Redacted));

            using var resp = await http.PostAsJsonAsync("/api/v1/bugreport/prepare", new { mode = "redacted" });

            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.That(doc.RootElement.GetProperty("sharing_mode").GetString(), Is.EqualTo("redacted"));
            svc.Verify(s => s.PrepareAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>(), BugReportSharingMode.Redacted), Times.Once);
        }

        [Test]
        public async Task An_unknown_mode_is_a_400_and_builds_nothing() {
            using var resp = await http.PostAsJsonAsync("/api/v1/bugreport/prepare", new { mode = "bogus" });

            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.That(doc.RootElement.GetProperty("detail").GetString(), Does.Contain("bogus").And.Contain("redacted"));
            svc.Verify(s => s.PrepareAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<BugReportSharingMode>()), Times.Never);
        }

        [Test]
        public async Task A_post_with_no_body_and_no_content_type_is_full() {
            // A pre-#1185 Dio client posts nothing at all: Kestrel reports no body, the parameter binds null.
            svc.Setup(s => s.PrepareAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>(), BugReportSharingMode.Full))
                .ReturnsAsync(Prepared(BugReportSharingMode.Full));

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/bugreport/prepare", UriKind.Relative));
            using var resp = await http.SendAsync(request);

            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), await resp.Content.ReadAsStringAsync());
            svc.Verify(s => s.PrepareAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>(), BugReportSharingMode.Full), Times.Once);
        }

        [Test]
        public async Task An_empty_body_from_an_older_client_is_full() {
            svc.Setup(s => s.PrepareAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>(), BugReportSharingMode.Full))
                .ReturnsAsync(Prepared(BugReportSharingMode.Full));

            using var content = new StringContent("", Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(new Uri("/api/v1/bugreport/prepare", UriKind.Relative), content);

            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), await resp.Content.ReadAsStringAsync());
            svc.Verify(s => s.PrepareAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>(), BugReportSharingMode.Full), Times.Once);
        }

        [Test]
        public async Task A_range_starts_a_background_whole_rig_bundle() {
            var since = DateTimeOffset.UtcNow.AddHours(-24);
            var started = new BugReportPreparationDto(Guid.NewGuid(), "preparing", 0, null, null, "full", SinceUtc: since);
            svc.Setup(s => s.StartPrepare(BugReportSharingMode.Full, It.Is<DateTimeOffset?>(d => d!.Value.ToUnixTimeSeconds() == since.ToUnixTimeSeconds())))
                .Returns(started);

            using var resp = await http.PostAsJsonAsync("/api/v1/bugreport/prepare", new { mode = "full", since_utc = since });

            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), await resp.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.That(doc.RootElement.GetProperty("status").GetString(), Is.EqualTo("preparing"));
            svc.Verify(s => s.PrepareAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<BugReportSharingMode>(), It.IsAny<DateTimeOffset?>()), Times.Never,
                "a range is never packed inside the request");
        }

        [Test]
        public async Task A_range_in_the_future_is_a_400() {
            using var resp = await http.PostAsJsonAsync("/api/v1/bugreport/prepare", new { mode = "full", since_utc = DateTimeOffset.UtcNow.AddHours(2) });
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        [Test]
        public async Task A_preparation_reports_its_state_and_an_unknown_one_is_404() {
            var id = Guid.NewGuid();
            var ready = new BugReportPreparationDto(id, "ready", 4321, null, DateTimeOffset.UtcNow, "full",
                Sources: [new BugReportSourceSummaryDto("alpacabridge", 2, 999, false, null)]);
            svc.Setup(s => s.GetPreparation(id)).Returns(ready);
            svc.Setup(s => s.GetPreparation(It.Is<Guid>(g => g != id))).Returns((BugReportPreparationDto?)null);

            using var resp = await http.GetAsync(new Uri($"/api/v1/bugreport/{id}", UriKind.Relative));
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.That(doc.RootElement.GetProperty("status").GetString(), Is.EqualTo("ready"));
            Assert.That(doc.RootElement.GetProperty("sources")[0].GetProperty("source").GetString(), Is.EqualTo("alpacabridge"));

            using var missing = await http.GetAsync(new Uri($"/api/v1/bugreport/{Guid.NewGuid()}", UriKind.Relative));
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
    }
}
