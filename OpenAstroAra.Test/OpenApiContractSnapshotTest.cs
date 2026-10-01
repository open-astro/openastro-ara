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
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using NUnit.Framework;
using OpenAstroAra.Server;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1131 — <c>OpenAstroAra.Server/openapi.yaml</c> is a snapshot of the spec the
    /// daemon generates at runtime (<c>AddOpenApi()</c> / <c>MapOpenApi()</c>, served at
    /// <c>/openapi/v1.json</c>). The hand-written file froze at 28 paths against ~200 live
    /// paths (~300 route+verb mappings) and listed routes that no longer existed; this fixture pulls the document out of
    /// the real composition root, serialises it the same way every time, and fails when the
    /// committed file differs. To refresh after an endpoint change:
    /// <code>OPENASTROARA_UPDATE_OPENAPI=1 dotnet test OpenAstroAra.Test --filter OpenApiContractSnapshotTest</code>
    /// then commit the result.</summary>
    [TestFixture]
    // Same isolation as CompositionRootSmokeTest: BuildApp sets process-wide state (profile-dir
    // env var, the global Serilog logger), so fixtures that build the daemon cannot overlap.
    [NonParallelizable]
    public class OpenApiContractSnapshotTest {

        internal const string UpdateEnvVar = "OPENASTROARA_UPDATE_OPENAPI";

        // Kept in sync with the file by this test; the generator itself emits no comments.
        private const string Header =
            "# OpenAstro Ara — REST API contract (generated snapshot, do not hand-edit).\n" +
            "#\n" +
            "# Emitted from the daemon's own OpenAPI document (Program.cs AddOpenApi/MapOpenApi,\n" +
            "# served live at /openapi/v1.json with the Scalar UI at /scalar) by\n" +
            "# OpenAstroAra.Test/OpenApiContractSnapshotTest.cs, which fails CI when this file\n" +
            "# drifts from the mapped routes (#1131). Refresh with:\n" +
            "#   OPENASTROARA_UPDATE_OPENAPI=1 dotnet test OpenAstroAra.Test --filter OpenApiContractSnapshotTest\n" +
            "#\n" +
            "# Conventions that the generator cannot express live in design/API_CONTRACT.md:\n" +
            "# URL-versioned /api/v1, trusted-LAN (no auth, §67), WebSocket event envelope at\n" +
            "# /api/v1/ws (token catalogue: Contracts/WsEvents/WsEventCatalog.cs).\n" +
            "\n";

        private string profileDir = null!;
        private string? previousProfileDir;
        private WebApplication app = null!;

        [OneTimeSetUp]
        public void BuildTheDaemon() {
            profileDir = Path.Combine(Path.GetTempPath(), $"oara-openapi-{Guid.NewGuid():N}");
            Directory.CreateDirectory(profileDir);
            previousProfileDir = Environment.GetEnvironmentVariable("OPENASTROARA_PROFILE_DIR");
            Environment.SetEnvironmentVariable("OPENASTROARA_PROFILE_DIR", profileDir);
            app = Program.BuildApp([]);
            // The spec generator reads the DI EndpointDataSource, which WebApplication only fills
            // from its mapped routes when the host starts and auto-inserts routing. Register them
            // the same way here without starting Kestrel or the hosted services.
            app.UseRouting();
            app.UseEndpoints(_ => { });
        }

        [OneTimeTearDown]
        public async Task TearDownTheDaemon() {
            await app.DisposeAsync();
            await Serilog.Log.CloseAndFlushAsync();
            Environment.SetEnvironmentVariable("OPENASTROARA_PROFILE_DIR", previousProfileDir);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try {
                Directory.Delete(profileDir, recursive: true);
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
        }

        /// <summary>Walks up from the test binary to the checkout; the snapshot lives in the
        /// source tree, not the output dir, so a refresh lands where git sees it.</summary>
        internal static string SnapshotPath() {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent) {
                string candidate = Path.Combine(dir.FullName, "OpenAstroAra.Server", "openapi.yaml");
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("OpenAstroAra.Server/openapi.yaml not found above " + AppContext.BaseDirectory);
        }

        internal static async Task<string> RenderAsync(IServiceProvider services) {
            // AddOpenApi registers one provider per document name; the default document is "v1".
            var provider = services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
            OpenApiDocument document = await provider.GetOpenApiDocumentAsync();
            string yaml = await document.SerializeAsYamlAsync(OpenApiSpecVersion.OpenApi3_1);
            return Header + yaml.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
        }

        [Test]
        public async Task The_committed_spec_matches_what_the_daemon_generates() {
            string path = SnapshotPath();
            string generated = await RenderAsync(app.Services);
            if (Environment.GetEnvironmentVariable(UpdateEnvVar) == "1") {
                await File.WriteAllTextAsync(path, generated);
                Assert.Pass($"rewrote {path}");
            }
            string committed = (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n", StringComparison.Ordinal);
            if (committed == generated) return;

            // Name the first differing line so the CI log says what moved without a local diff.
            string[] want = generated.Split('\n');
            string[] have = committed.Split('\n');
            int line = Enumerable.Range(0, Math.Min(want.Length, have.Length)).FirstOrDefault(i => want[i] != have[i], Math.Min(want.Length, have.Length));
            Assert.Fail(
                $"OpenAstroAra.Server/openapi.yaml is out of date with the mapped routes, or the Microsoft.OpenApi serializer changed with a package bump (first difference at line {line + 1}):\n"
                + $"  generated: {(line < want.Length ? want[line] : "<end>")}\n"
                + $"  committed: {(line < have.Length ? have[line] : "<end>")}\n"
                + $"Refresh with {UpdateEnvVar}=1 dotnet test OpenAstroAra.Test --filter {nameof(OpenApiContractSnapshotTest)} and commit the result (#1131).");
        }

        [Test]
        public async Task The_generated_spec_covers_the_mapped_route_groups() {
            // A smoke check that the document really is the runtime one and not an empty shell:
            // route groups the hand-written file never had (#1131) must be present.
            string yaml = await RenderAsync(app.Services);
            foreach (string group in new[] { "/api/v1/storage", "/api/v1/frames", "/api/v1/equipment/guider", "/api/v1/profiles", "/api/v1/sequences" }) {
                Assert.That(yaml, Does.Contain(group), $"{group} missing from the generated document");
            }
            Assert.That(yaml.Split('\n').Where(l => l.StartsWith("  /api/v1/image", StringComparison.Ordinal)), Is.Empty,
                "the retired /image group (now /frames) must not come back");
        }
    }
}
