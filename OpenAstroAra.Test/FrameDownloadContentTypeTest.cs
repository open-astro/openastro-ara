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
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1148 — GET /frames/{id}/download over a real Kestrel host serves a FITS frame as
    /// <c>application/fits</c> and an XISF frame as <c>application/octet-stream</c> (XISF has no
    /// registered media type), each under its own file name.</summary>
    [TestFixture]
    [Category("IO")]
    public class FrameDownloadContentTypeTest {
        private static readonly Guid FitsId = Guid.NewGuid();
        private static readonly Guid XisfId = Guid.NewGuid();

        private WebApplication app = null!;
        private HttpClient http = null!;

        [OneTimeSetUp]
        public async Task StartKestrel() {
            var repo = new Mock<IFrameRepository>();
            repo.Setup(r => r.OpenDownloadAsync(FitsId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ((Stream)new MemoryStream("SIMPLE  =                    T"u8.ToArray()), "M31_L_180s.fits"));
            repo.Setup(r => r.OpenDownloadAsync(XisfId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ((Stream)new MemoryStream("XISF0100"u8.ToArray()), "M31_L_180s.xisf"));
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(repo.Object);
            // Every other service an image endpoint injects must be registered, or the route table fails to
            // build on the first request. None is called by the download endpoint.
            builder.Services.AddSingleton(Mock.Of<IBackupStreamService>());
            builder.Services.AddSingleton(Mock.Of<ISessionService>());
            builder.Services.AddProblemDetails();
            builder.Services.ConfigureHttpJsonOptions(opts => {
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            app = builder.Build();
            app.MapImageEndpoints();
            await app.StartAsync();
            http = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromMinutes(1) };
        }

        [OneTimeTearDown]
        public async Task StopKestrel() {
            http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }

        [TestCase("fits", "application/fits", "M31_L_180s.fits")]
        [TestCase("xisf", "application/octet-stream", "M31_L_180s.xisf")]
        public async Task A_frame_downloads_with_its_containers_content_type(string which, string contentType, string fileName) {
            var id = which == "xisf" ? XisfId : FitsId;
            using var resp = await http.GetAsync(new Uri($"/api/v1/frames/{id}/download", UriKind.Relative));
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(resp.Content.Headers.ContentType?.MediaType, Is.EqualTo(contentType));
            Assert.That(resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"'),
                Is.EqualTo(fileName));
        }
    }
}
