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
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#2172 — POST /sequences/{id}/pause: no body pauses now (the exposure in progress stops);
    /// <c>{"after_frame": true}</c> lets the exposure finish first. Real Kestrel over loopback.</summary>
    [TestFixture]
    [Category("IO")] // loopback HTTP, like ApiHardeningHostTest
    public class SequencePauseEndpointTest {

        private WebApplication app = null!;
        private HttpClient http = null!;
        private Mock<ISequencerService> sequencer = null!;

        [SetUp]
        public async Task StartHost() {
            sequencer = new Mock<ISequencerService>();
            sequencer.Setup(s => s.PauseAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(PlaceholderEquipmentHelpers.Accepted("sequencer.pause", null));
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddProblemDetails();
            builder.Services.ConfigureHttpJsonOptions(opts => {
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            // The group maps every sequence route; endpoint construction needs each route's services resolvable.
            builder.Services.AddSingleton(sequencer.Object);
            builder.Services.AddSingleton(Mock.Of<ISequenceService>());
            builder.Services.AddSingleton(Mock.Of<ISequenceTemplateService>());
            builder.Services.AddSingleton(Mock.Of<IBahtinovFocusService>());
            builder.Services.AddSingleton(Mock.Of<ICameraService>());
            builder.Services.AddSingleton(Mock.Of<IEquipmentIdentityReconciler>());
            builder.Services.AddSingleton(Mock.Of<IAutoFlatsService>());
            app = builder.Build();
            app.MapSequenceEndpoints();
            await app.StartAsync();
            http = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromMinutes(1) };
        }

        [TearDown]
        public async Task StopHost() {
            http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }

        private async Task<HttpStatusCode> PauseAsync(Guid id, string? body) {
            using var content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(new Uri($"/api/v1/sequences/{id}/pause", UriKind.Relative), content);
            return resp.StatusCode;
        }

        [Test]
        public async Task Pause_with_no_body_pauses_now() {
            var id = Guid.NewGuid();
            Assert.That(await PauseAsync(id, null), Is.EqualTo(HttpStatusCode.Accepted));
            sequencer.Verify(s => s.PauseAsync(id, false, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Pause_with_after_frame_body_lets_the_frame_finish() {
            var id = Guid.NewGuid();
            Assert.That(await PauseAsync(id, """{ "after_frame": true }"""), Is.EqualTo(HttpStatusCode.Accepted));
            sequencer.Verify(s => s.PauseAsync(id, true, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
