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
using Microsoft.AspNetCore.Http.HttpResults;
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
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1215 — the plate-solve settings wiring the #1213 reviews found untested: the PUT
    /// echoes what the store holds after the normalizer ran, the boot warning reads right for a
    /// URL, and the status route is where the client and API_CONTRACT say it is.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    public class PlateSolveSettingsWiringTest {

        private static PlateSolveSettingsDto Settings(string path) =>
            ProfileSnapshotNormalizer.Defaults.PlateSolve with { PathOrEndpoint = path };

        [Test]
        public void The_PUT_echoes_the_stored_settings_after_the_path_migration() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-ps-put-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                // /usr/bin/astap is absent and /usr/bin/astap_cli present: the normalizer rewrites the path on write.
                var store = new FileProfileStore(dir, fileExists: p => p == SolverPathMigration.AstapCliPath);

                var result = ProfileEndpoints.PutPlateSolveSettings(Settings(SolverPathMigration.LegacyAstapPath), store);

                var echoed = (result as Ok<PlateSolveSettingsDto>)?.Value;
                Assert.That(echoed, Is.Not.Null);
                Assert.That(echoed!.PathOrEndpoint, Is.EqualTo(SolverPathMigration.AstapCliPath),
                    "the client adopts the echo, so it must be what the store holds, not what was typed");
                Assert.That(store.GetPlateSolveSettings().PathOrEndpoint, Is.EqualTo(SolverPathMigration.AstapCliPath));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void The_boot_warning_names_a_URL_as_unsupported_and_a_missing_binary_as_missing() {
            static bool Nothing(string _) => false;
            Assert.That(SolverPathMigration.BootWarning(Settings("https://nova.astrometry.net/api"), Nothing),
                Does.Contain("configured as a URL").And.Contain("astap_cli").And.Not.Contain("not found at"));
            Assert.That(SolverPathMigration.BootWarning(Settings("/usr/bin/astap_cli"), Nothing),
                Does.Contain("not found at /usr/bin/astap_cli").And.Contain("apt install astap-cli"));
            Assert.That(SolverPathMigration.BootWarning(Settings("/usr/bin/astap_cli"), static _ => true), Is.Null);
            Assert.That(SolverPathMigration.BootWarning(Settings(""), Nothing), Is.Null, "nothing configured: the normalizer's default applies, nothing to warn about");
            Assert.That(SolverPathMigration.IsEndpointUrl("http://localhost:8080"), Is.True);
            Assert.That(SolverPathMigration.IsEndpointUrl("C:\\astap\\astap.exe"), Is.False, "a drive letter is not a scheme we warn about");
        }

        [Test]
        public async Task The_database_status_route_is_where_the_client_looks() {
            var store = new Mock<IProfileStore>();
            store.Setup(s => s.GetPlateSolveSettings()).Returns(Settings("/usr/bin/astap_cli"));
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store.Object);
            // The group's other handlers infer their services at startup; stubs keep the map honest.
            builder.Services.AddSingleton(Mock.Of<IFrameRepository>());
            builder.Services.AddSingleton(Mock.Of<IPlateSolveService>());
            builder.Services.AddSingleton(Mock.Of<OpenAstroAra.Profile.Interfaces.IProfileService>());
            builder.Services.AddSingleton(Mock.Of<ICenteringService>());
            builder.Services.AddSingleton(Mock.Of<IBatchJobService>());
            // The daemon's AOT-safe JSON setup (Program.cs): the slim builder has no reflection serializer.
            builder.Services.ConfigureHttpJsonOptions(opts => {
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            await using var app = builder.Build();
            app.MapPlateSolveEndpoints();
            await app.StartAsync();
            try {
                using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
                using var response = await http.GetAsync(new Uri("/api/v1/platesolve/database", UriKind.Relative));

                var body = await response.Content.ReadAsStringAsync();
                Assert.That((int)response.StatusCode, Is.EqualTo(200), "the path the client and API_CONTRACT hard-code: " + body);
                using var doc = JsonDocument.Parse(body);
                Assert.That(doc.RootElement.TryGetProperty("databases", out _), Is.True);
                Assert.That(doc.RootElement.GetProperty("solver_path").GetString(), Is.EqualTo("/usr/bin/astap_cli"));
            } finally {
                await app.StopAsync();
            }
        }
    }
}
