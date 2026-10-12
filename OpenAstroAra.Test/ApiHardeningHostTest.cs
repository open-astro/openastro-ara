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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1194 — the wiring, not just the pure functions: the <c>SessionGuard</c> middleware on a
    /// real Kestrel pipeline and the WebSocket cap's 503 on the real upgrade endpoint, over loopback.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — loopback HTTP
    [NonParallelizable] // WsConnectionGate is process-wide
    public class ApiHardeningHostTest {

        private WebApplication app = null!;
        private HttpClient http = null!;
        private ClientSessionService sessions = null!;

        [OneTimeSetUp]
        public async Task StartKestrel() {
            WsConnectionGate.ResetForTests();
            sessions = new ClientSessionService();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(sessions);
            builder.Services.AddSingleton<InMemoryWsServices>();
            builder.Services.AddSingleton<IWsBroadcaster>(sp => sp.GetRequiredService<InMemoryWsServices>());
            builder.Services.AddSingleton<IWsEventChannel>(sp => sp.GetRequiredService<InMemoryWsServices>());
            builder.Services.AddProblemDetails();
            app = builder.Build();
            app.UseWebSockets();
            app.Use(SessionGuard.Middleware); // the same registration Program.cs makes
            app.MapWebSocketEndpoints();
            // A stand-in mutating route under /api/v1 and one outside it.
            app.MapPost("/api/v1/probe", () => Results.NoContent());
            app.MapGet("/api/v1/probe", () => Results.NoContent());
            app.MapPost("/outside", () => Results.NoContent());
            await app.StartAsync();
            http = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromMinutes(1) };
        }

        [OneTimeTearDown]
        public async Task StopKestrel() {
            http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            WsConnectionGate.ResetForTests();
        }

        private async Task<HttpStatusCode> PostAsync(string path, Guid? session) {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
            if (session is { } id) {
                req.Headers.Add("X-Ara-Session", id.ToString());
            }
            using var res = await http.SendAsync(req);
            return res.StatusCode;
        }

        [Test]
        public async Task A_stale_session_is_refused_on_the_pipeline_and_the_holder_is_not() {
            Assert.That(await PostAsync("/api/v1/probe", Guid.NewGuid()), Is.EqualTo(HttpStatusCode.NoContent), "no holder: nothing is stale");
            var holder = await sessions.ConnectAsync("mac.local", null, CancellationToken.None);
            Assert.That(holder.Kind, Is.EqualTo(ConnectOutcomeKind.Granted));
            try {
                Assert.That(await PostAsync("/api/v1/probe", Guid.NewGuid()), Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(await PostAsync("/api/v1/probe", holder.SessionId), Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(await PostAsync("/api/v1/probe", null), Is.EqualTo(HttpStatusCode.NoContent), "header-less: advisory");
                Assert.That(await PostAsync("/outside", Guid.NewGuid()), Is.EqualTo(HttpStatusCode.NoContent), "not under /api/v1");
                using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/probe", UriKind.Relative));
                req.Headers.Add("X-Ara-Session", Guid.NewGuid().ToString());
                using var res = await http.SendAsync(req);
                Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "reads are never refused");

                using var stale = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/probe", UriKind.Relative));
                stale.Headers.Add("X-Ara-Session", Guid.NewGuid().ToString());
                using var staleRes = await http.SendAsync(stale);
                var body = await staleRes.Content.ReadAsStringAsync();
                Assert.That(body, Does.Contain("stale-session"), "a problem+json the client can key on");
            } finally {
                sessions.Disconnect(holder.SessionId);
            }
        }

        [Test]
        public async Task The_seventeenth_socket_gets_a_503_before_the_upgrade() {
            var wsUri = new Uri(http.BaseAddress!, "/api/v1/ws");
            var ws = new UriBuilder(wsUri) { Scheme = "ws" }.Uri;
            var open = new List<ClientWebSocket>();
            try {
                for (var i = 0; i < WsConnectionGate.MaxConcurrentConnections; i++) {
                    var socket = new ClientWebSocket();
                    socket.Options.SetRequestHeader("X-Ara-WS-Version", "1");
                    await socket.ConnectAsync(ws, CancellationToken.None);
                    open.Add(socket);
                }
                Assert.That(WsConnectionGate.Open, Is.EqualTo(WsConnectionGate.MaxConcurrentConnections));

                using var extra = new ClientWebSocket();
                extra.Options.SetRequestHeader("X-Ara-WS-Version", "1");
                extra.Options.CollectHttpResponseDetails = true;
                var ex = await Assert.ThrowsAsync<WebSocketException>(() => extra.ConnectAsync(ws, CancellationToken.None));
                Assert.That(extra.HttpStatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable), ex!.Message);
                Assert.That(extra.HttpResponseHeaders?.TryGetValue("Retry-After", out var retry) == true && retry.Contains("30"), Is.True, "Retry-After hint");
            } finally {
                foreach (var socket in open) {
                    try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch (WebSocketException) { } catch (OperationCanceledException) { }
                    socket.Dispose();
                }
            }
            // Slots are released when the handlers unwind.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (WsConnectionGate.Open > 0 && sw.Elapsed < TimeSpan.FromSeconds(10)) {
                await Task.Delay(50);
            }
            Assert.That(WsConnectionGate.Open, Is.EqualTo(0), "every closed socket gave its slot back");
        }
    }
}
