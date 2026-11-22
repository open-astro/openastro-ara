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

    /// <summary>#2068 — the polar-align routes over HTTP: the <c>hand_turn</c> query reaches the service as the
    /// start's hand-turn choice, and an unknown stage is a 400 before the service is asked.</summary>
    [TestFixture]
    public class EquipmentEndpointsPolarAlignHttpTest {

        private static readonly PolarAlignPreflightDto List = new(
            Ready: true,
            Checks: [new PolarAlignPreflightCheckDto(PolarAlignPreflightChecks.MountConnected, PolarAlignPreflightStatuses.Ok, "Connected.")],
            Planned: new PolarAlignPreflightPlannedDto(PolarAlignStages.Fine, PolarAlignDirections.East, 10.0, 10.0, 2, 20.0,
                true, 0.0, 30.0, 60.0, null, null),
            MountKey: "ZWO_AM5_1",
            MoveToStart: null);

        private static readonly PolarAlignStartPlanDto Plan = new(PolarAlignStages.Fine, PolarAlignDirections.East, 10.0, 2, 20.0);

        private sealed class Host : IAsyncDisposable {
            public readonly Mock<IPolarAlignService> Svc = new();
            private WebApplication _app = null!;
            private HttpClient _http = null!;

            public async Task StartAsync() {
                Svc.Setup(s => s.GetPreflightAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(List);
                Svc.Setup(s => s.GetStartPlanAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(Plan);
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

            public async ValueTask DisposeAsync() {
                _http?.Dispose();
                if (_app is not null) {
                    await _app.DisposeAsync();
                }
            }
        }

        [Test]
        public async Task Preflight_passes_hand_turn_true_through_and_no_query_is_a_motor_start() {
            await using var host = new Host();
            await host.StartAsync();

            Assert.That(await host.GetAsync("/preflight?hand_turn=true"), Is.EqualTo(200));
            Assert.That(await host.GetAsync("/preflight?stage=check"), Is.EqualTo(200));

            host.Svc.Verify(s => s.GetPreflightAsync(null, true, It.IsAny<CancellationToken>()), Times.Once, "hand_turn=true is a hand-turn list");
            host.Svc.Verify(s => s.GetPreflightAsync(PolarAlignStages.Check, false, It.IsAny<CancellationToken>()), Times.Once,
                "no hand_turn is a motor start's list");
        }

        [Test]
        public async Task Preflight_with_an_unknown_stage_is_400_and_the_service_is_not_asked() {
            await using var host = new Host();
            await host.StartAsync();

            Assert.That(await host.GetAsync("/preflight?stage=sideways&hand_turn=true"), Is.EqualTo(400));
            host.Svc.Verify(s => s.GetPreflightAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Start_plan_passes_hand_turn_true_through_and_no_query_is_a_motor_start() {
            await using var host = new Host();
            await host.StartAsync();

            Assert.That(await host.GetAsync("/start-plan?stage=fine&hand_turn=true"), Is.EqualTo(200));
            Assert.That(await host.GetAsync("/start-plan?stage=fine"), Is.EqualTo(200));

            host.Svc.Verify(s => s.GetStartPlanAsync(PolarAlignStages.Fine, true, It.IsAny<CancellationToken>()), Times.Once);
            host.Svc.Verify(s => s.GetStartPlanAsync(PolarAlignStages.Fine, false, It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
