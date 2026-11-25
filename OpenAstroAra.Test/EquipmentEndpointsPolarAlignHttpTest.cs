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
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>The polar-align routes over HTTP: a start takes no body, so whatever an older app or a script still sends
    /// is ignored, and the routes this daemon no longer has answer 404.</summary>
    [TestFixture]
    public class EquipmentEndpointsPolarAlignHttpTest {

        private sealed class Host : IAsyncDisposable {
            public readonly Mock<IPolarAlignService> Svc = new();
            private WebApplication _app = null!;
            private HttpClient _http = null!;

            public async Task StartAsync() {
                Svc.Setup(s => s.StartAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new OperationAcceptedDto(Guid.NewGuid(), "polar-align.start", DateTimeOffset.UtcNow, null));
                var builder = WebApplication.CreateSlimBuilder();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Services.AddSingleton(Svc.Object);
                builder.Services.ConfigureHttpJsonOptions(opts => {
                    opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                    opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower; // as Program.cs
                });
                _app = builder.Build();
                EquipmentEndpoints.MapPolarAlign(_app.MapGroup("/api/v1/equipment/polaralign"));
                await _app.StartAsync();
                _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
            }

            public async Task<int> GetAsync(string pathAndQuery) {
                using var response = await _http.GetAsync(new Uri("/api/v1/equipment/polaralign" + pathAndQuery, UriKind.Relative));
                return (int)response.StatusCode;
            }

            public async Task<int> PostJsonAsync(string path, string json) {
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(new Uri("/api/v1/equipment/polaralign" + path, UriKind.Relative), content);
                return (int)response.StatusCode;
            }

            public async ValueTask DisposeAsync() {
                _http?.Dispose();
                if (_app is not null) {
                    await _app.DisposeAsync();
                }
            }
        }

        [TestCase("""{"stage":"check","confirmed":true,"direction":"east","new_origin":true,"direction_test":true,"hand_turn":true}""")]
        [TestCase("""{"stage":"coarse"}""")]
        [TestCase("{}")]
        [TestCase("")]
        [TestCase("not json")]
        public async Task A_start_has_no_stage_and_whatever_body_it_is_sent_is_ignored(string body) {
            await using var host = new Host();
            await host.StartAsync();

            var status = await host.PostJsonAsync("/start", body);

            Assert.That(status, Is.EqualTo(202));
            host.Svc.Verify(s => s.StartAsync(null, It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestCase("/preflight")]
        [TestCase("/start-plan")]
        [TestCase("/motor-direction")]
        public async Task The_removed_checklist_plan_and_motor_direction_routes_are_gone(string path) {
            await using var host = new Host();
            await host.StartAsync();

            Assert.That(await host.GetAsync(path), Is.EqualTo(404));
        }

        [TestCase("/next")]
        [TestCase("/motor-direction/retest")]
        public async Task The_removed_Next_and_retest_routes_are_gone(string path) {
            await using var host = new Host();
            await host.StartAsync();

            Assert.That(await host.PostJsonAsync(path, "{}"), Is.EqualTo(404));
        }
    }
}
