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
    /// #1156 / §63.4 — the profile routes' guider-twin hooks. Duplicate (POST without settings) clones
    /// the ACTIVE profile's twin under the new name; rename carries the twin over, unless another
    /// ARA profile still uses the old name (identically-named profiles share one twin); delete asks
    /// the guider for the twin only on <c>?guider_profile=delete</c> and reports what happened.
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
            guider.Setup(g => g.DeleteAraGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GuiderTwinDeleteOutcome(GuiderTwinDeleteStatus.Deleted, null));
            guider.Setup(g => g.DescribeAraGuiderTwinAsync(It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GuiderTwinStatus(Exists: true, SelectedOnDaemon: false, CanDeleteNow: true, Reason: null));
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
            // Per-test Setup overrides (specific-argument setups) must not leak into the next test.
            guider.Reset();
            guider.Setup(g => g.TryCloneAraGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            guider.Setup(g => g.TryRenameAraGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            guider.Setup(g => g.DeleteAraGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GuiderTwinDeleteOutcome(GuiderTwinDeleteStatus.Deleted, null));
            guider.Setup(g => g.DescribeAraGuiderTwinAsync(It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GuiderTwinStatus(Exists: true, SelectedOnDaemon: false, CanDeleteNow: true, Reason: null));
        }

        private static readonly string[] OnlyRigA = { "Rig A" };

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
        public async Task The_rename_hook_gets_the_name_as_the_repository_stored_it() {
            // The repository normalizes names (blank → "Untitled profile", over-long → truncated); the
            // twin must follow the STORED name or the next connect looks for a twin that isn't there.
            var id = repo.List().ActiveId!.Value;
            var resp = await http.PutAsJsonAsync($"/api/v1/profiles/{id}", new { name = "   " }, Json);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            var stored = repo.List().Profiles.Single(p => p.Id == id).Name;
            Assert.That(stored, Is.Not.EqualTo("   "), "precondition: the repository normalized the blank name");

            guider.Verify(g => g.TryRenameAraGuiderProfileAsync("Rig A", id, stored, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Renaming_one_of_two_identically_named_profiles_leaves_the_shared_twin_alone() {
            var second = repo.Create("Rig A", settings: null, makeActive: false);
            var resp = await http.PutAsJsonAsync($"/api/v1/profiles/{second.Id}", new { name = "Rig B" }, Json);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

            guider.Verify(g => g.TryRenameAraGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never,
                "identically-named ARA profiles share one twin — the survivor still needs it under the old name");
        }

        // ── §63.4 — delete asks about the twin ──────────────────────────────────────────────────

        [Test]
        public async Task Deleting_without_the_parameter_or_with_keep_never_touches_the_guider() {
            var a = repo.Create("Rig B", settings: null, makeActive: false);
            var b = repo.Create("Rig C", settings: null, makeActive: false);

            var resp = await http.DeleteAsync(new Uri($"/api/v1/profiles/{a.Id}", UriKind.Relative));
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = await resp.Content.ReadFromJsonAsync<ProfileDeleteResultDto>(Json);
            Assert.That(body!.GuiderProfile, Is.EqualTo("kept"), "an old client that sends no parameter never deletes guider data");

            resp = await http.DeleteAsync(new Uri($"/api/v1/profiles/{b.Id}?guider_profile=keep", UriKind.Relative));
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await resp.Content.ReadFromJsonAsync<ProfileDeleteResultDto>(Json))!.GuiderProfile, Is.EqualTo("kept"));

            guider.Verify(g => g.DeleteAraGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(repo.List().Profiles.Select(p => p.Name), Is.EqualTo(OnlyRigA), "the ARA profiles themselves went");
        }

        [Test]
        public async Task Deleting_with_delete_asks_the_guider_for_the_twin_and_reports_the_outcome() {
            var b = repo.Create("Rig B", settings: null, makeActive: false);
            guider.Setup(g => g.DeleteAraGuiderProfileAsync("Rig B", b.Id, true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GuiderTwinDeleteOutcome(GuiderTwinDeleteStatus.Queued, "The guider is guiding. It will be deleted once guiding stops."));

            var resp = await http.DeleteAsync(new Uri($"/api/v1/profiles/{b.Id}?guider_profile=delete", UriKind.Relative));

            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = await resp.Content.ReadFromJsonAsync<ProfileDeleteResultDto>(Json);
            Assert.That(body!.GuiderProfile, Is.EqualTo("queued"));
            Assert.That(body.Reason, Does.Contain("guiding"));
            guider.Verify(g => g.DeleteAraGuiderProfileAsync("Rig B", b.Id, true, It.IsAny<CancellationToken>()), Times.Once,
                "the hook gets the profile's name (the twin's) and id, dark library included by default");
        }

        [Test]
        public async Task The_dark_library_flag_and_the_failure_reason_pass_through() {
            var b = repo.Create("Rig B", settings: null, makeActive: false);
            guider.Setup(g => g.DeleteAraGuiderProfileAsync("Rig B", b.Id, false, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GuiderTwinDeleteOutcome(GuiderTwinDeleteStatus.Failed, "profile is locked"));

            var resp = await http.DeleteAsync(new Uri($"/api/v1/profiles/{b.Id}?guider_profile=delete&dark_library=keep", UriKind.Relative));

            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = await resp.Content.ReadFromJsonAsync<ProfileDeleteResultDto>(Json);
            Assert.That(body!.GuiderProfile, Is.EqualTo("failed"));
            Assert.That(body.Reason, Is.EqualTo("profile is locked"));
        }

        [Test]
        public async Task An_unknown_choice_is_400_and_deletes_nothing() {
            var b = repo.Create("Rig B", settings: null, makeActive: false);

            var resp = await http.DeleteAsync(new Uri($"/api/v1/profiles/{b.Id}?guider_profile=maybe", UriKind.Relative));
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            resp = await http.DeleteAsync(new Uri($"/api/v1/profiles/{b.Id}?guider_profile=delete&dark_library=later", UriKind.Relative));
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

            Assert.That(repo.List().Profiles.Select(p => p.Id), Does.Contain(b.Id), "a refused request leaves the ARA profile in place");
            guider.Verify(g => g.DeleteAraGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Deleting_an_unknown_profile_is_404_even_with_delete() {
            var resp = await http.DeleteAsync(new Uri($"/api/v1/profiles/{Guid.NewGuid()}?guider_profile=delete", UriKind.Relative));
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            guider.Verify(g => g.DeleteAraGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task The_twin_preview_names_both_twins_and_flags_a_shared_one() {
            var second = repo.Create("rig a ", settings: null, makeActive: false); // same twin: trimmed, case-insensitive
            var id = repo.List().ActiveId!.Value;

            var preview = await http.GetFromJsonAsync<GuiderTwinPreviewDto>($"/api/v1/profiles/{id}/guider-twin", Json);

            Assert.That(preview!.TwinNames, Is.EqualTo(new[] { "Rig A", OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PHD2Guider.AraGuiderProfileName("Rig A", id) }));
            Assert.That(preview.Shared, Is.True, "identically named ARA profiles share one twin — deleting it takes the survivor's calibration");
            Assert.That(preview.SharedWith, Is.EqualTo(repo.List().Profiles.Single(p => p.Id == second.Id).Name));
            Assert.That(preview.Exists, Is.True);
            Assert.That(preview.CanDeleteNow, Is.True);

            repo.Delete(second.Id);
            preview = await http.GetFromJsonAsync<GuiderTwinPreviewDto>($"/api/v1/profiles/{id}/guider-twin", Json);
            Assert.That(preview!.Shared, Is.False);
            Assert.That(preview.SharedWith, Is.Null);
        }

        [Test]
        public async Task The_twin_preview_relays_the_guiders_answer_and_is_404_for_an_unknown_profile() {
            var id = repo.List().ActiveId!.Value;
            guider.Setup(g => g.DescribeAraGuiderTwinAsync("Rig A", id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GuiderTwinStatus(Exists: null, SelectedOnDaemon: false, CanDeleteNow: false, Reason: "No guider is connected."));

            var preview = await http.GetFromJsonAsync<GuiderTwinPreviewDto>($"/api/v1/profiles/{id}/guider-twin", Json);
            Assert.That(preview!.Exists, Is.Null);
            Assert.That(preview.CanDeleteNow, Is.False);
            Assert.That(preview.Reason, Does.Contain("No guider"));

            var resp = await http.GetAsync(new Uri($"/api/v1/profiles/{Guid.NewGuid()}/guider-twin", UriKind.Relative));
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }

        [Test]
        public async Task Renaming_an_unknown_profile_is_404_and_touches_no_twin() {
            var resp = await http.PutAsJsonAsync($"/api/v1/profiles/{Guid.NewGuid()}", new { name = "Nope" }, Json);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            guider.Verify(g => g.TryRenameAraGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
