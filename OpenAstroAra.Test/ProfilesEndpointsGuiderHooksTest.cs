#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

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
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1156 — the profile routes' guider-twin hooks. Duplicate (POST without settings) clones
    /// the ACTIVE profile's twin under the new name; rename carries the twin over, unless another
    /// ARA profile still uses the old name (identically-named profiles share one twin).
    /// </summary>
    [TestFixture]
    [Category("IO")]
    public class ProfilesEndpointsGuiderHooksTest {
        private WebApplication app = null!;
        private HttpClient http = null!;
        private string dir = null!;
        private FileProfileStore store = null!;
        private FileProfileRepository repo = null!;
        private Mock<IGuiderService> guider = null!;

        [OneTimeSetUp]
        public async Task StartKestrel() {
            dir = Path.Combine(Path.GetTempPath(), "ara-profiles-hooks-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            store = new FileProfileStore(dir);
            repo = new FileProfileRepository(dir, store);
            guider = new Mock<IGuiderService>();
            guider.Setup(g => g.TryCloneAraGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            guider.Setup(g => g.TryRenameAraGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<IProfileRepository>(repo);
            builder.Services.AddSingleton(guider.Object);
            builder.Services.AddProblemDetails();
            builder.Services.ConfigureHttpJsonOptions(opts => {
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            app = builder.Build();
            app.MapProfilesEndpoints();
            await app.StartAsync();
            http = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromMinutes(2) };
        }

        [OneTimeTearDown]
        public async Task StopKestrel() {
            http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            repo.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        [SetUp]
        public void Reset() {
            foreach (var p in repo.List().Profiles.ToList()) {
                repo.Delete(p.Id);
            }
            repo.Create("Rig A", settings: null, makeActive: true);
            guider.Invocations.Clear();
        }

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        [Test]
        public async Task Duplicating_the_active_profile_clones_its_guider_twin_under_the_new_name() {
            var resp = await http.PostAsJsonAsync("/api/v1/profiles?activate=false", new { name = "Rig A copy" }, Json);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Created));

            guider.Verify(g => g.TryCloneAraGuiderProfileAsync("Rig A", "Rig A copy", It.IsAny<CancellationToken>()), Times.Once,
                "the duplicate's twin is cloned from the ACTIVE profile's twin, named after the new profile");
        }

        [Test]
        public async Task Creating_a_profile_from_explicit_settings_is_not_a_duplicate_and_clones_nothing() {
            var settings = repo.GetProfile(repo.List().ActiveId!.Value)!.Settings;
            var resp = await http.PostAsJsonAsync("/api/v1/profiles", new CreateProfileRequestDto("Fresh", settings), Json);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Created));

            guider.Verify(g => g.TryCloneAraGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never,
                "a profile built from a settings body (the wizard) has no source twin to copy");
        }

        [Test]
        public async Task Renaming_a_profile_renames_its_guider_twin() {
            var id = repo.List().ActiveId!.Value;
            var resp = await http.PutAsJsonAsync($"/api/v1/profiles/{id}", new { name = "Rig A2" }, Json);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

            guider.Verify(g => g.TryRenameAraGuiderProfileAsync("Rig A", id, "Rig A2", It.IsAny<CancellationToken>()), Times.Once,
                "the hook gets the OLD name (the twin's) and the new one");
        }

        [Test]
        public async Task Renaming_one_of_two_identically_named_profiles_leaves_the_shared_twin_alone() {
            var second = repo.Create("Rig A", settings: null, makeActive: false);
            var resp = await http.PutAsJsonAsync($"/api/v1/profiles/{second.Id}", new { name = "Rig B" }, Json);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

            guider.Verify(g => g.TryRenameAraGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never,
                "identically-named ARA profiles share one twin — the survivor still needs it under the old name");
        }

        [Test]
        public async Task Renaming_an_unknown_profile_is_404_and_touches_no_twin() {
            var resp = await http.PutAsJsonAsync($"/api/v1/profiles/{Guid.NewGuid()}", new { name = "Nope" }, Json);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            guider.Verify(g => g.TryRenameAraGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
