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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>Disk-to-disk copy over a real Kestrel host: the copy routes map the service's
    /// answers, and the store's configure/check/eject refuse while a copy runs (the copy reads
    /// the store and writes the target, so neither may be re-pointed or unmounted under it).</summary>
    [TestFixture]
    [Category("IO")]
    public class StorageCopyEndpointTest {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        private static readonly StorageCopyStatusDto Preparing = new("preparing", "6EF6-FD21", [], 0, 0, 0, 0, 0, 0, 0,
            null, null, null, null, false, DateTimeOffset.UtcNow, null);
        private static readonly string[] M31 = ["manual/M31"];

        private WebApplication app = null!;
        private HttpClient http = null!;
        private Mock<IStorageCopyService> copy = null!;
        private Mock<IStorageDeviceService> devices = null!;
        private ActiveRunSessionRegistry runs = null!;
        private Mock<ICameraService> camera = null!;

        [OneTimeSetUp]
        public async Task StartKestrel() {
            copy = new Mock<IStorageCopyService>();
            devices = new Mock<IStorageDeviceService>(MockBehavior.Strict);
            runs = new ActiveRunSessionRegistry();
            camera = new Mock<ICameraService>();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(copy.Object);
            builder.Services.AddSingleton(devices.Object);
            builder.Services.AddSingleton(runs);
            // The rest of the system endpoints' services: registered so the route table builds.
            builder.Services.AddSingleton(Mock.Of<IDataManagerService>());
            builder.Services.AddSingleton(Mock.Of<IBackupService>());
            builder.Services.AddSingleton(Mock.Of<IBugReportService>());
            builder.Services.AddSingleton(camera.Object);
            builder.Services.AddSingleton(Mock.Of<IProfileShareService>());
            builder.Services.AddSingleton(Mock.Of<IProfileStore>());
            builder.Services.AddSingleton(Mock.Of<ISkyCatalogService>());
            builder.Services.AddSingleton(Mock.Of<IStorageBrowseService>());
            builder.Services.AddSingleton(Mock.Of<ISolverRecommender>());
            builder.Services.AddSingleton(Mock.Of<IGuiderService>());
            builder.Services.AddSingleton(Mock.Of<IAraDatabase>());
            builder.Services.AddSingleton<CaptureScanService>();
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
        public void ResetMocks() {
            copy.Reset();
            copy.Setup(c => c.Status()).Returns(Preparing);
            camera.Reset();
        }

        [Test]
        public async Task A_started_copy_is_202_with_its_status() {
            IReadOnlyList<string>? seen = null;
            copy.Setup(c => c.Start("6EF6-FD21", It.IsAny<IReadOnlyList<string>?>()))
                .Callback<string, IReadOnlyList<string>?>((_, f) => seen = f)
                .Returns((StorageCopyRefusal?)null);

            using var resp = await http.PostAsJsonAsync("/api/v1/storage/copy",
                new StorageCopyRequestDto("6EF6-FD21", M31), Json);

            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            var body = await resp.Content.ReadFromJsonAsync<StorageCopyStatusDto>(Json);
            Assert.That(body!.State, Is.EqualTo("preparing"));
            Assert.That(seen, Is.EqualTo(M31));
        }

        [Test]
        public async Task A_refusal_keeps_its_status_code_and_code() {
            copy.Setup(c => c.Start(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>()))
                .Returns(new StorageCopyRefusal(409, "copy_running", "A copy is already running."));

            using var resp = await http.PostAsJsonAsync("/api/v1/storage/copy", new StorageCopyRequestDto("6EF6-FD21"), Json);

            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.That(doc.RootElement.GetProperty("title").GetString(), Is.EqualTo("copy_running"));
        }

        [Test]
        public async Task No_copy_starts_during_a_sequence_run() {
            var session = Guid.NewGuid();
            runs.Enter(session);
            try {
                using var resp = await http.PostAsJsonAsync("/api/v1/storage/copy", new StorageCopyRequestDto("6EF6-FD21"), Json);
                Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            } finally {
                runs.Exit(session);
            }
            copy.Verify(c => c.Start(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>()), Times.Never);
        }

        [Test]
        public async Task Cancel_with_nothing_running_is_404() {
            copy.Setup(c => c.Cancel()).Returns(false);
            using var resp = await http.DeleteAsync(new Uri("/api/v1/storage/copy", UriKind.Relative));
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }

        [TestCase("/api/v1/storage/configure")]
        [TestCase("/api/v1/storage/check")]
        [TestCase("/api/v1/storage/eject")]
        public async Task The_store_is_not_touched_while_a_copy_runs(string route) {
            copy.Setup(c => c.IsRunning).Returns(true);
            using var resp = await http.PostAsJsonAsync(route, new { uuid = "EBDF-D246" }, Json);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await resp.Content.ReadAsStringAsync(), Does.Contain("disk-to-disk copy"));
            // Strict mock: any helper call would have thrown.
        }

        [TestCase("/api/v1/storage/configure")]
        [TestCase("/api/v1/storage/check")]
        [TestCase("/api/v1/storage/eject")]
        public async Task The_store_is_not_touched_while_a_frame_is_still_being_saved(string route) {
            // Background frame saves: the camera is free, but a frame is still on its way to disk.
            camera.Setup(c => c.IsFreeToCapture(It.IsAny<object>())).Returns(true);
            camera.Setup(c => c.HasPendingFrameSaves).Returns(true);
            using var resp = await http.PostAsJsonAsync(route, new { uuid = "EBDF-D246" }, Json);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await resp.Content.ReadAsStringAsync(), Does.Contain("still being saved"));
            // Strict mock: any helper call would have thrown.
        }
    }
}
