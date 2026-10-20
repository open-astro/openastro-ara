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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1136 (review round 4) — the download endpoint's error mapping over a real Kestrel host: a
    /// package the volume cannot hold is a 507 with the service's message as the problem detail, next to the
    /// existing 404 / 409 mappings. Without the catch in SystemEndpoints the same throw would be a 500.</summary>
    [TestFixture]
    [Category("IO")]
    public class DataManagerDownloadEndpointTest {
        private WebApplication app = null!;
        private HttpClient http = null!;
        private Mock<IDataManagerService> svc = null!;

        [OneTimeSetUp]
        public async Task StartKestrel() {
            svc = new Mock<IDataManagerService>(MockBehavior.Strict);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(svc.Object);
            // Every other service a system endpoint injects must be registered, or the request-delegate factory
            // treats the parameter as a second body and the whole route table fails to build on the first request
            // (a bare 500 before any middleware). None of these is called by the download endpoint.
            builder.Services.AddSingleton(Mock.Of<IBackupService>());
            builder.Services.AddSingleton(Mock.Of<IBugReportService>());
            builder.Services.AddSingleton(Mock.Of<ICameraService>());
            builder.Services.AddSingleton(Mock.Of<IProfileShareService>());
            builder.Services.AddSingleton(Mock.Of<IProfileStore>());
            builder.Services.AddSingleton(Mock.Of<ISkyCatalogService>());
            builder.Services.AddSingleton(Mock.Of<IStorageBrowseService>());
            builder.Services.AddSingleton(Mock.Of<IStorageDeviceService>());
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

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        private Task<HttpResponseMessage> Post(string packageId) =>
            http.PostAsJsonAsync("/api/v1/data-manager/download", new DownloadRequestDto(packageId, ForceReinstall: false), Json);

        [Test]
        public async Task A_package_the_volume_cannot_hold_is_a_507_with_the_sizes_in_the_detail() {
            svc.Setup(s => s.DownloadAsync(It.Is<DownloadRequestDto>(r => r.PackageId == "astap-d80"), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InsufficientDiskSpaceException("astap-d80", neededBytes: 1_665_271_318L, freeBytes: 300_000_000L));

            using var resp = await Post("astap-d80");

            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.InsufficientStorage));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var detail = doc.RootElement.GetProperty("detail").GetString();
            Assert.That(detail, Does.Contain("astap-d80").And.Contain("1.7 GB").And.Contain("0.3 GB"), "the client shows this text as-is");
        }

        [Test]
        public async Task An_unknown_package_is_404_and_an_installed_one_409() {
            svc.Setup(s => s.DownloadAsync(It.Is<DownloadRequestDto>(r => r.PackageId == "nope"), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(PackageNotFoundException.ForPackageId("nope"));
            svc.Setup(s => s.DownloadAsync(It.Is<DownloadRequestDto>(r => r.PackageId == "hyg-stars"), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(PackageAlreadyInstalledException.ForPackageId("hyg-stars"));

            using var notFound = await Post("nope");
            using var conflict = await Post("hyg-stars");

            Assert.That(notFound.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(conflict.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        }

        [Test]
        public async Task An_unwritable_install_root_is_409_with_the_path_in_the_detail() {
            svc.Setup(s => s.DownloadAsync(It.Is<DownloadRequestDto>(r => r.PackageId == "astap-g05"), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InstallRootNotWritableException("astap-g05", "/srv/astap", "Read-only file system"));

            using var resp = await Post("astap-g05");

            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.That(doc.RootElement.GetProperty("detail").GetString(), Does.Contain("/srv/astap").And.Contain("Read-only file system"));
        }

        [Test]
        public async Task An_accepted_download_is_202_with_the_operation() {
            var accepted = new OperationAcceptedDto(Guid.NewGuid(), "data-manager.download", DateTimeOffset.UtcNow, null);
            svc.Setup(s => s.DownloadAsync(It.Is<DownloadRequestDto>(r => r.PackageId == "astap-w08"), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(accepted);

            using var resp = await Post("astap-w08");

            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.That(doc.RootElement.GetProperty("operation_id").GetGuid(), Is.EqualTo(accepted.OperationId));
        }
    }
}
