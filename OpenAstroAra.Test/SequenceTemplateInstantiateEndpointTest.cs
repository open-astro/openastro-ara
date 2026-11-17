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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>↑ara#1952 — the 422 of POST /sequences/templates/{name}/instantiate is problem details,
    /// and the endpoint maps only <see cref="TemplateBodyRefusedException"/> to it. Real Kestrel over loopback.</summary>
    [TestFixture]
    [Category("IO")] // loopback HTTP, like ApiHardeningHostTest
    public class SequenceTemplateInstantiateEndpointTest {

        private string profileDir = null!;
        private WebApplication app = null!;
        private HttpClient http = null!;
        private FileSequenceService sequences = null!;

        [SetUp]
        public async Task StartHost() {
            profileDir = Path.Combine(Path.GetTempPath(), "ara-tpl-ep-" + Guid.NewGuid().ToString("N"));
            sequences = new FileSequenceService(profileDir);
            var templatesDir = Path.Combine(profileDir, "sequences", FileSequenceService.TemplatesDirName);
            await File.WriteAllTextAsync(Path.Combine(templatesDir, "no-schema.json"), """
                { "name": "no-schema", "category": "single-target", "description": "d",
                  "isBuiltIn": false, "body": { "target": "x" } }
                """);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddProblemDetails();
            builder.Services.ConfigureHttpJsonOptions(opts => {
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            builder.Services.AddSingleton<ISequenceService>(sequences);
            builder.Services.AddSingleton<ISequenceTemplateService>(
                new PlaceholderSequenceTemplateService(sequences, profileDir, seedStarterTemplates: false));
            // The group maps every sequence route; endpoint construction needs each route's services resolvable.
            builder.Services.AddSingleton(Mock.Of<ISequencerService>());
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
            Directory.Delete(profileDir, recursive: true);
        }

        private async Task<HttpResponseMessage> InstantiateAsync(string name) {
            using var content = new StringContent("""{ "new_sequence_name": "x" }""", Encoding.UTF8, "application/json");
            return await http.PostAsync(new Uri($"/api/v1/sequences/templates/{name}/instantiate", UriKind.Relative), content);
        }

        private async Task<int> SequenceCountAsync() {
            var page = await sequences.ListAsync(100, null, CancellationToken.None);
            return page.Items.Count;
        }

        [Test]
        public async Task Instantiating_an_invalid_template_answers_422_problem_json_and_creates_nothing() {
            var before = await SequenceCountAsync();

            using var resp = await InstantiateAsync("no-schema");

            Assert.That(resp.StatusCode, Is.EqualTo((HttpStatusCode)422));
            Assert.That(resp.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.That(doc.RootElement.GetProperty("status").GetInt32(), Is.EqualTo(422));
            Assert.That(doc.RootElement.GetProperty("detail").GetString(), Does.Contain("schemaVersion"));
            Assert.That(await SequenceCountAsync(), Is.EqualTo(before));
        }

        [Test]
        public async Task Instantiating_an_unknown_template_still_answers_404() {
            using var resp = await InstantiateAsync("nope");

            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
    }
}