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
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§50 rebuild-catalog wipes the frame catalog: it must not run while a sequence frame is
    /// still waiting to be registered (background frame saves).</summary>
    [TestFixture]
    [Category("IO")] // #1265 — loopback HTTP
    public class StatsRebuildCatalogEndpointTest {

        private WebApplication app = null!;
        private HttpClient http = null!;
        private Mock<ICameraService> camera = null!;
        private Mock<IAraDatabase> db = null!;

        [OneTimeSetUp]
        public async Task StartKestrel() {
            camera = new Mock<ICameraService>();
            // Strict: a rebuild that got past the gates would touch the database and throw.
            db = new Mock<IAraDatabase>(MockBehavior.Strict);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(camera.Object);
            builder.Services.AddSingleton(new ActiveRunSessionRegistry());
            builder.Services.AddSingleton(Mock.Of<IStatsService>());
            builder.Services.AddSingleton(Mock.Of<IProfileStore>());
            builder.Services.AddSingleton(db.Object);
            builder.Services.AddSingleton<CaptureScanService>();
            builder.Services.AddProblemDetails();
            app = builder.Build();
            app.MapStatsEndpoints();
            await app.StartAsync();
            http = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromMinutes(1) };
        }

        [OneTimeTearDown]
        public async Task StopKestrel() {
            http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }

        [Test]
        public async Task The_catalog_is_not_rebuilt_while_a_frame_is_still_being_saved() {
            camera.Setup(c => c.IsFreeToCapture(It.IsAny<object>())).Returns(true);
            camera.Setup(c => c.HasPendingFrameSaves).Returns(true);
            using var resp = await http.PostAsync(new Uri("/api/v1/stats/rebuild-catalog", UriKind.Relative), null);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await resp.Content.ReadAsStringAsync(), Does.Contain("still being saved"));
        }
    }
}
