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
using OpenAstroAra.Server.Contracts;
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

    /// <summary>#1122 — the upload route over real Kestrel on a loopback port: a ~40 MB .deb
    /// (bigger than Kestrel's 30 MB default body cap) must get through, and an oversized
    /// declared length must be refused with the <c>too_large</c> token before any body is read.
    /// The service is a fake that just counts the bytes it was handed.</summary>
    [TestFixture]
    public class ServerUpdateEndpointsTest {

        private sealed class CountingService : IServerUpdateService {
            public long Received;

            public async Task<ServerUpdateStagedDto> StageAsync(Stream body, long? declaredLength, string? expectedSha256, CancellationToken ct) {
                var buffer = new byte[1 << 16];
                int n;
                while ((n = await body.ReadAsync(buffer, ct)) > 0) {
                    Received += n;
                }
                return new ServerUpdateStagedDto("id", ServerUpdateService.PackageName, "2", "1", Received);
            }

            public Task<ServerUpdateStatusDto> ApplyAsync(string id, CancellationToken ct) => throw new NotSupportedException();

            public Task<ServerUpdateStatusDto?> GetStatusAsync(string id, CancellationToken ct) => Task.FromResult<ServerUpdateStatusDto?>(null);
        }

        private WebApplication app = null!;
        private CountingService service = null!;
        private HttpClient http = null!;

        [OneTimeSetUp]
        public async Task StartKestrel() {
            service = new CountingService();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<IServerUpdateService>(service);
            builder.Services.AddProblemDetails();
            app = builder.Build();
            app.MapServerUpdateEndpoints();
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
        public async Task A_40_MB_package_gets_past_Kestrels_default_30_MB_body_cap() {
            const int size = 40 * 1024 * 1024;
            service.Received = 0;
            using var content = new ByteArrayContent(new byte[size]);
            content.Headers.ContentType = new("application/vnd.debian.binary-package");
            using var response = await http.PostAsync(new Uri("/api/v1/server/update", UriKind.Relative), content);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
                "lifting MaxRequestBodySize for this route is what lets a real .deb through");
            Assert.That(service.Received, Is.EqualTo(size));
        }

        [Test]
        public async Task An_oversized_declared_length_is_refused_with_the_too_large_token_before_the_body_is_read() {
            service.Received = 0;
            // Headers only, over a raw socket: the pre-check must act on the declared length
            // alone (HttpClient refuses to send a body shorter than its Content-Length).
            var uri = new Uri(app.Urls.First());
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(uri.Host, uri.Port);
            await using var stream = tcp.GetStream();
            var head = "POST /api/v1/server/update HTTP/1.1\r\nHost: x\r\nConnection: close\r\n"
                + $"Content-Type: application/octet-stream\r\nContent-Length: {ServerUpdateService.MaxUploadBytes + 1}\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(head));
            using var reader = new StreamReader(stream);
            var status = await reader.ReadLineAsync();
            Assert.That(status, Does.StartWith("HTTP/1.1 413"));
            // The rest (headers + a possibly chunked body) until the server closes; the problem
            // document is the one JSON object in it.
            var rest = await reader.ReadToEndAsync();
            var json = rest[rest.IndexOf('{', StringComparison.Ordinal)..(rest.LastIndexOf('}') + 1)];
            using var doc = JsonDocument.Parse(json);
            Assert.That(doc.RootElement.GetProperty("title").GetString(), Is.EqualTo("too_large"));
            Assert.That(service.Received, Is.Zero);
        }
    }
}
