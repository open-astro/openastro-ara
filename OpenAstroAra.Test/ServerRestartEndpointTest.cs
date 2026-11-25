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
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System.Net;
using System.Text.Json;

namespace OpenAstroAra.Test {

    /// <summary>↑ara#1951 — the 202 body of POST /server/restart-on-idle names <c>status</c>, and DELETE cancels.
    /// Real Kestrel over loopback.</summary>
    [TestFixture]
    [Category("IO")]
    public class ServerRestartEndpointTest {
        private sealed class NoRestart : IDaemonRestarter { public void Restart() { } }

        private WebApplication app = null!;
        private HttpClient http = null!;
        private ActiveRunSessionRegistry runs = null!;

        [SetUp]
        public async Task StartHost() {
            runs = new ActiveRunSessionRegistry();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.ConfigureHttpJsonOptions(opts => {
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            builder.Services.AddSingleton<IServerStateService>(new PlaceholderServerStateService(restarter: new NoRestart(), occupancy: new RigOccupancy().RegisterSession(runs, () => null)));
            // MapServerStateEndpoints builds every route of the group, so each service it takes must resolve.
            builder.Services.AddSingleton(Moq.Mock.Of<ITimeSyncService>());
            builder.Services.AddSingleton(Moq.Mock.Of<ILogService>());
            builder.Services.AddSingleton(new EmergencyStopService());
            builder.Services.AddSingleton(new ServerInternalStateService());
            app = builder.Build();
            app.MapServerStateEndpoints();
            await app.StartAsync();
            http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        }

        [TearDown]
        public async Task StopHost() {
            http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }

        private async Task<string> PostStatusAsync() {
            using var res = await http.PostAsync(new Uri("/api/v1/server/restart-on-idle", UriKind.Relative), null);
            Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("status").GetString()!;
        }

        [Test]
        public async Task Post_answers_restarting_with_no_run() => Assert.That(await PostStatusAsync(), Is.EqualTo("restarting"));

        [Test]
        public async Task Post_answers_queued_with_a_run_and_delete_answers_204_even_twice() {
            runs.Enter(Guid.NewGuid());
            Assert.That(await PostStatusAsync(), Is.EqualTo("queued"));
            for (var i = 0; i < 2; i++) {
                using var del = await http.DeleteAsync(new Uri("/api/v1/server/restart-on-idle", UriKind.Relative));
                Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            }
        }
    }
}