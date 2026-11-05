#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>GET /api/v1/server/versions reports every version on the rig for Simple Mode's Settings ›
    /// Rig and the About panel: the board, OpenAstro Guider, each Alpaca server (AlpacaBridge) and ASTAP.</summary>
    [TestFixture]
    public class RigVersionsTest {
        private static readonly string[] ExpectedServers = ["10.0.30.223:6800", "station:11111"];

        [Test]
        public void The_board_model_drops_the_device_trees_trailing_nul_and_is_null_when_absent() {
            var path = Path.GetTempFileName();
            try {
                File.WriteAllText(path, "Raspberry Pi 5 Model B Rev 1.1\0");
                Assert.That(RigVersions.ReadBoardModel(path), Is.EqualTo("Raspberry Pi 5 Model B Rev 1.1"));
            } finally {
                File.Delete(path);
            }
            Assert.That(RigVersions.ReadBoardModel(path), Is.Null, "no device tree (macOS, a container)");
        }

        [Test]
        public void ASTAPs_version_is_read_from_its_help_banner() {
            const string banner = "ASTAP astrometric solver version CLI-2026.09.01\n(C) 2018, 2026 by Han Kleijn. License MPL 2.0\nUsage:\n";
            Assert.That(RigVersions.ParseAstapVersion(banner), Is.EqualTo("CLI-2026.09.01"));
            // 2026.11.01 adds ", multi-threaded" after the version (the rig's /opt/openastroara/astap_cli).
            Assert.That(RigVersions.ParseAstapVersion("ASTAP astrometric solver version CLI-2026.11.01, multi-threaded\n"),
                Is.EqualTo("CLI-2026.11.01"));
            Assert.That(RigVersions.ParseAstapVersion("command not found"), Is.Null);
        }

        [Test]
        [Platform(Exclude = "Win", Reason = "the fake solver is a shell script")]
        public async Task An_ASTAP_that_had_no_version_yet_is_asked_again() {
            // A solver that answers only once it is "installed": the first probe finds nothing, which
            // must not stick until the daemon restarts.
            var dir = Directory.CreateTempSubdirectory("astap-probe").FullName;
            try {
                var installed = Path.Combine(dir, "installed");
                var astap = Path.Combine(dir, "astap_cli");
                await File.WriteAllTextAsync(astap,
                    $"#!/bin/sh\nif [ -f '{installed}' ]; then echo 'ASTAP astrometric solver version CLI-2026.09.01'; fi\n");
                File.SetUnixFileMode(astap, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                Assert.That(await RigVersions.CachedAstapVersionAsync(astap), Is.Null);
                await File.WriteAllTextAsync(installed, "");
                Assert.That(await RigVersions.CachedAstapVersionAsync(astap), Is.EqualTo("CLI-2026.09.01"));
                File.Delete(installed);
                Assert.That(await RigVersions.CachedAstapVersionAsync(astap), Is.EqualTo("CLI-2026.09.01"),
                    "a version once read is kept");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void An_Alpaca_description_gives_the_servers_name_maker_and_version() {
            const string json = """
                {"ErrorNumber":0,"Value":{"Manufacturer":"OpenAstro.net","ManufacturerVersion":"6.0.0~nightly202610301026.g34986ef","ServerName":"AlpacaHTTP","Location":""}}
                """;
            var d = AlpacaManagementClient.ParseDescription(json);
            Assert.That(d, Is.EqualTo(new AlpacaServerDescription("AlpacaHTTP", "OpenAstro.net", "6.0.0~nightly202610301026.g34986ef")));
            Assert.That(AlpacaManagementClient.ParseDescription("{\"Value\":[]}"), Is.Null);
            Assert.That(AlpacaManagementClient.ManagementUri("10.0.30.223", 6800, "description").ToString(),
                Is.EqualTo("http://10.0.30.223:6800/management/v1/description"));
        }

        [Test]
        public async Task The_description_is_fetched_from_the_management_api() {
            using var handler = new FakeHandler("""{"Value":{"ServerName":"AlpacaHTTP","Manufacturer":"OpenAstro.net","ManufacturerVersion":"6.0.0"}}""");
            using var client = new AlpacaManagementClient(handler: handler);
            var d = await client.GetDescriptionAsync("10.0.30.223", 6800, CancellationToken.None);
            Assert.That(d?.Version, Is.EqualTo("6.0.0"));
            Assert.That(handler.LastUri?.AbsolutePath, Is.EqualTo("/management/v1/description"));
        }

        [Test]
        public async Task Versions_cover_the_board_the_guider_each_Alpaca_server_once_and_ASTAP() {
            var guider = new Mock<IGuiderService>();
            guider.SetupGet(g => g.GuiderVersion).Returns("2.6.13dev7");
            var selections = new Mock<IEquipmentSelectionStore>();
            selections.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<DiscoveredDeviceDto> {
                new("c", "ZWO ASI2600MM", DeviceType.Camera, "rig.local", "10.0.30.223", 6800, 0, false),
                new("m", "AM5", DeviceType.Telescope, "rig.local", "10.0.30.223", 6800, 0, false),
                new("w", "Weather", DeviceType.ObservingConditions, "station", "", 11111, 0, false),
            });
            var asked = new List<string>();
            var alpaca = new Mock<IAlpacaManagementClient>();
            alpaca.Setup(a => a.GetDescriptionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string host, int port, CancellationToken _) => {
                    lock (asked) { asked.Add($"{host}:{port}"); }
                    return host == "station" ? null : new AlpacaServerDescription("AlpacaHTTP", "OpenAstro.net", "6.0.0");
                });
            var rig = new RigVersions(guider.Object, selections.Object, alpaca.Object, () => "/opt/openastroara/astap_cli",
                boardModel: () => "Raspberry Pi 5 Model B Rev 1.1",
                astapVersion: (path, _) => Task.FromResult<string?>(path.EndsWith("astap_cli", StringComparison.Ordinal) ? "CLI-2026.09.01" : null));

            var v = await rig.CollectAsync(CancellationToken.None);

            Assert.That(v.BoardModel, Is.EqualTo("Raspberry Pi 5 Model B Rev 1.1"));
            Assert.That(v.GuiderVersion, Is.EqualTo("2.6.13dev7"));
            Assert.That(v.AstapVersion, Is.EqualTo("CLI-2026.09.01"));
            Assert.That(asked.OrderBy(a => a), Is.EqualTo(ExpectedServers), "each server asked once");
            Assert.That(v.AlpacaServers, Has.Count.EqualTo(2));
            var bridge = v.AlpacaServers.Single(s => s.Port == 6800);
            Assert.That(bridge, Is.EqualTo(new AlpacaServerVersionDto("10.0.30.223", 6800, "AlpacaHTTP", "OpenAstro.net", "6.0.0")));
            var silent = v.AlpacaServers.Single(s => s.Port == 11111);
            Assert.That(silent.Version, Is.Null, "a server that doesn't answer is listed without a version");
        }

        [Test]
        public async Task With_nothing_wired_every_extra_is_empty_not_an_error() {
            var v = await new RigVersions(null, null, null, null, boardModel: () => null,
                astapVersion: (_, _) => Task.FromResult<string?>(null)).CollectAsync(CancellationToken.None);
            Assert.That(v.BoardModel, Is.Null);
            Assert.That(v.GuiderVersion, Is.Null);
            Assert.That(v.AlpacaServers, Is.Empty);
            Assert.That(v.AstapVersion, Is.Null);
        }

        [Test]
        public void Only_an_ASTAP_solver_is_asked_its_version() {
            static PlateSolveSettingsDto Solver(string engine, string path) =>
                new(engine, path, "", 30, 0, 60, false, false, false, 3, 30);
            Assert.That(RigVersions.AstapPathOf(Solver("astap", " /opt/openastroara/astap_cli ")), Is.EqualTo("/opt/openastroara/astap_cli"));
            Assert.That(RigVersions.AstapPathOf(Solver("ASTAP", "/usr/bin/astap")), Is.EqualTo("/usr/bin/astap"));
            Assert.That(RigVersions.AstapPathOf(Solver("astrometry.net", "/usr/bin/solve-field")), Is.Null,
                "another solver is never started with -h on every /server/versions call");
        }

        [Test]
        public void The_versions_serialize_through_the_aot_context() {
            var dto = new ApiVersionsDto("1.0.0.0", "abc", "10.0.14", "Debian GNU/Linux 13 (trixie)", "arm64", "2.1.0", "0.4.6",
                Array.Empty<ApiSurfaceVersionDto>(),
                BoardModel: "Raspberry Pi 5 Model B Rev 1.1", GuiderVersion: "2.6.13",
                AlpacaServers: new[] { new AlpacaServerVersionDto("10.0.30.223", 6800, "AlpacaHTTP", "OpenAstro.net", "6.0.0") },
                AstapVersion: "CLI-2026.09.01");
            var json = JsonSerializer.Serialize(dto, OpenAstroAra.Server.AraJsonSerializerContext.Default.ApiVersionsDto);
            Assert.That(json, Does.Contain("\"board_model\":\"Raspberry Pi 5 Model B Rev 1.1\""));
            Assert.That(json, Does.Contain("\"alpaca_servers\":[{\"host\":\"10.0.30.223\",\"port\":6800"));
            Assert.That(json, Does.Contain("\"astap_version\":\"CLI-2026.09.01\""));
        }

        private sealed class FakeHandler(string body) : HttpMessageHandler {
            public Uri? LastUri { get; private set; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
                LastUri = request.RequestUri;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }
        }
    }
}
