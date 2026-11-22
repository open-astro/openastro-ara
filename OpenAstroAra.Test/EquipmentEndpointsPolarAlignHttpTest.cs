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

    /// <summary>#2068 — the polar-align routes over HTTP: an unknown stage is a 400 before the service is asked. #2170 —
    /// the removed <c>hand_turn</c> query and body field open no manual path: the route reads them as any unknown name
    /// (ignored), so the service is asked for the one motor start there is.</summary>
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
                Svc.Setup(s => s.GetPreflightAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(List);
                Svc.Setup(s => s.GetStartPlanAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Plan);
                Svc.Setup(s => s.StartAsync(It.IsAny<string?>(), It.IsAny<PolarAlignStartConfirmation?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new OperationAcceptedDto(Guid.NewGuid(), "polar-align.start", DateTimeOffset.UtcNow, null));
                Svc.Setup(s => s.MoveToStartAsync(It.IsAny<PolarAlignMoveToStartConfirmation?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PolarAlignMoveToStartResultDto(PolarAlignMoveToStartOutcomes.Arrived, null, null, List));
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

            public async Task<int> PostEmptyAsync(string path) {
                using var response = await _http.PostAsync(new Uri("/api/v1/equipment/polaralign" + path, UriKind.Relative), null);
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
        public async Task Preflight_ignores_the_removed_hand_turn_query() {
            await using var host = new Host();
            await host.StartAsync();

            Assert.That(await host.GetAsync("/preflight?hand_turn=true"), Is.EqualTo(200));
            Assert.That(await host.GetAsync("/preflight?stage=check&hand_turn=true"), Is.EqualTo(200));

            host.Svc.Verify(s => s.GetPreflightAsync(null, It.IsAny<CancellationToken>()), Times.Once, "the motor start's list");
            host.Svc.Verify(s => s.GetPreflightAsync(PolarAlignStages.Check, It.IsAny<CancellationToken>()), Times.Once, "the motor check's list");
        }

        [Test]
        public async Task Preflight_with_an_unknown_stage_is_400_and_the_service_is_not_asked() {
            await using var host = new Host();
            await host.StartAsync();

            Assert.That(await host.GetAsync("/preflight?stage=sideways"), Is.EqualTo(400));
            host.Svc.Verify(s => s.GetPreflightAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Start_plan_ignores_the_removed_hand_turn_query() {
            await using var host = new Host();
            await host.StartAsync();

            Assert.That(await host.GetAsync("/start-plan?stage=fine&hand_turn=true"), Is.EqualTo(200));

            host.Svc.Verify(s => s.GetStartPlanAsync(PolarAlignStages.Fine, It.IsAny<CancellationToken>()), Times.Once, "the motor start's plan");
        }

        // #2170 — an older app's start body that still says hand_turn: the service gets the plain motor confirmation, so
        // the start runs the same pre-flight list and moves the mount only as a motor start does.
        [Test]
        public async Task A_start_body_with_the_removed_hand_turn_field_is_a_motor_start() {
            await using var host = new Host();
            await host.StartAsync();

            var status = await host.PostJsonAsync("/start",
                """{"stage":"fine","confirmed":true,"direction":"east","new_origin":true,"hand_turn":true}""");

            Assert.That(status, Is.EqualTo(202));
            host.Svc.Verify(s => s.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(PolarAlignDirections.East, true),
                null, It.IsAny<CancellationToken>()), Times.Once, "the confirmation of a motor start, nothing else");
        }
    
        // #2166 — the move to the start position's body: only confirmed: true with the offer's numbers reaches the service
        // as a confirmation; no body and confirmed: false reach it as none, which it refuses with nothing moved.
        [Test]
        public async Task A_move_to_start_body_carries_the_confirmation_and_its_offer_numbers() {
            await using var host = new Host();
            await host.StartAsync();

            Assert.That(await host.PostEmptyAsync("/move-to-start"), Is.EqualTo(200));
            Assert.That(await host.PostJsonAsync("/move-to-start", """{"confirmed":false,"target_off_pole_deg":4.0,"reach_deg":4.3}"""), Is.EqualTo(200));
            Assert.That(await host.PostJsonAsync("/move-to-start", """{"confirmed":true,"target_off_pole_deg":4.0,"reach_deg":4.3}"""), Is.EqualTo(200));

            host.Svc.Verify(s => s.MoveToStartAsync(null, It.IsAny<CancellationToken>()), Times.Exactly(2), "no body, and confirmed false: no confirmation");
            host.Svc.Verify(s => s.MoveToStartAsync(new PolarAlignMoveToStartConfirmation(4.0, 4.3), It.IsAny<CancellationToken>()), Times.Once,
                "the confirmation with the numbers the confirm showed");
        }
    }
}
