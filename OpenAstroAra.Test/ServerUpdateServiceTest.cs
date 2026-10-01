#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1122 — the daemon side of the §33 update push: stage + inspect through the
    /// dpkg tools (faked at the process seam), the request file + <c>systemctl start --no-block</c>
    /// hand-off with the <c>server.restart_imminent</c> event, and the result-file parse.</summary>
    [TestFixture]
    public class ServerUpdateServiceTest {

        private static readonly string[] ExpectedCompare = ["--compare-versions", "0.0.2-ara.1", "gt", "0.0.1-ara.1"];

        private string root = null!;
        private ServerUpdatePaths paths = null!;
        private List<(string File, string[] Args)> calls = null!;
        private Dictionary<string, (int Code, string Output)> answers = null!;
        private Mock<IWsBroadcaster> ws = null!;
        private List<(string Type, JsonElement Payload)> published = null!;

        [SetUp]
        public void SetUp() {
            root = Path.Combine(Path.GetTempPath(), $"oara-update-{Guid.NewGuid():N}");
            var unit = Path.Combine(root, "openastroara-update@.service");
            Directory.CreateDirectory(root);
            File.WriteAllText(unit, "[Service]\n");
            paths = new ServerUpdatePaths(Path.Combine(root, "stage"), Path.Combine(root, "run", "update"), unit);
            calls = [];
            answers = new Dictionary<string, (int, string)>(StringComparer.Ordinal) {
                ["dpkg-query"] = (0, "0.0.1-ara.1"),
                ["dpkg --print-architecture"] = (0, "arm64\n"),
                ["dpkg --compare-versions"] = (0, ""),
                ["dpkg-deb"] = (0, "Package: openastroara-server\nVersion: 0.0.2-ara.1\nArchitecture: arm64\n"),
                ["systemctl"] = (0, ""),
            };
            published = [];
            ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
              .Callback<string, JsonElement, CancellationToken>((t, p, _) => published.Add((t, p.Clone())))
              .Returns(Task.CompletedTask);
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        private ServerUpdateService Service() => new(NullLogger.Instance, paths, ws.Object, 5555, (file, args, _) => {
            calls.Add((file, args));
            var key = file == "dpkg" ? $"dpkg {args[0]}" : file;
            return Task.FromResult(answers.TryGetValue(key, out var a) ? a : (127, $"{file}: not found"));
        });

        private static MemoryStream Body(int bytes = 4096) => new(Encoding.ASCII.GetBytes(new string('x', bytes)));

        [Test]
        public async Task Stage_keeps_the_file_and_reports_both_versions() {
            var staged = await Service().StageAsync(Body(), 4096, null, CancellationToken.None);
            Assert.Multiple(() => {
                Assert.That(staged.Package, Is.EqualTo("openastroara-server"));
                Assert.That(staged.Version, Is.EqualTo("0.0.2-ara.1"));
                Assert.That(staged.InstalledVersion, Is.EqualTo("0.0.1-ara.1"));
                Assert.That(staged.SizeBytes, Is.EqualTo(4096));
                Assert.That(File.Exists(Path.Combine(paths.StageDirectory, staged.Id + ".deb")), Is.True);
            });
            var compare = calls.Single(c => c.File == "dpkg" && c.Args[0] == "--compare-versions");
            Assert.That(compare.Args, Is.EqualTo(ExpectedCompare),
                "dpkg's own ordering decides 'newer', not a home-grown parser");
        }

        [TestCase("dpkg-deb", 2, "", "not_a_package")]
        [TestCase("dpkg-deb", 0, "Package: other\nVersion: 9\nArchitecture: arm64\n", "wrong_package")]
        [TestCase("dpkg-deb", 0, "Package: openastroara-server\nVersion: 9\nArchitecture: amd64\n", "wrong_architecture")]
        [TestCase("dpkg --compare-versions", 1, "", "not_newer")]
        [TestCase("dpkg-query", 1, "", "not_packaged")]
        public async Task Stage_refuses_and_deletes_the_upload(string tool, int code, string output, string reason) {
            answers[tool] = (code, output);
            var ex = await Assert.ThrowsAsync<ServerUpdateRejectedException>(() => Service().StageAsync(Body(), 4096, null, CancellationToken.None));
            Assert.That(ex!.Reason, Is.EqualTo(reason));
            Assert.That(Directory.Exists(paths.StageDirectory) ? Directory.GetFiles(paths.StageDirectory) : [], Is.Empty,
                "a refused upload must not stay on disk");
        }

        [Test]
        public async Task Stage_checks_the_declared_sha256_when_one_is_sent() {
            var bytes = Encoding.ASCII.GetBytes(new string('x', 4096));
            var good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));   // upper-case: compared case-insensitively
            var staged = await Service().StageAsync(new MemoryStream(bytes), bytes.Length, good, CancellationToken.None);
            Assert.That(staged.SizeBytes, Is.EqualTo(4096));

            var ex = await Assert.ThrowsAsync<ServerUpdateRejectedException>(() =>
                Service().StageAsync(new MemoryStream(bytes, 0, 2048), 2048, good, CancellationToken.None));
            Assert.That(ex!.Reason, Is.EqualTo("checksum_mismatch"), "a truncated transfer");
            Assert.That(Directory.GetFiles(paths.StageDirectory), Has.Length.EqualTo(1), "only the good upload stays");
        }

        [Test]
        public async Task Stage_refuses_when_there_is_no_helper_unit() {
            File.Delete(paths.HelperUnitTemplate);
            var ex = await Assert.ThrowsAsync<ServerUpdateRejectedException>(() => Service().StageAsync(Body(), 4096, null, CancellationToken.None));
            Assert.That(ex!.Reason, Is.EqualTo("not_packaged"));
            Assert.That(calls, Is.Empty, "no tool runs on a dev rig");
        }

        [Test]
        public async Task Stage_refuses_an_oversized_body_by_declared_length_without_reading_it() {
            var ex = await Assert.ThrowsAsync<ServerUpdateRejectedException>(() =>
                Service().StageAsync(Body(1), ServerUpdateService.MaxUploadBytes + 1, null, CancellationToken.None));
            Assert.That(ex!.Reason, Is.EqualTo("too_large"));
            Assert.That(calls, Is.Empty);
        }

        [Test]
        public async Task Stage_stops_a_chunked_body_at_the_cap() {
            // No declared length: the copy itself must stop at the cap rather than fill the disk.
            var endless = new Mock<Stream>();
            endless.Setup(s => s.ReadAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
                   .Returns<Memory<byte>, CancellationToken>((m, _) => ValueTask.FromResult(m.Length));
            var ex = await Assert.ThrowsAsync<ServerUpdateRejectedException>(() => Service().StageAsync(endless.Object, null, null, CancellationToken.None));
            Assert.That(ex!.Reason, Is.EqualTo("too_large"));
            Assert.That(Directory.GetFiles(paths.StageDirectory), Is.Empty);
        }

        [Test]
        public async Task Apply_writes_the_request_announces_the_restart_and_starts_the_unit_without_blocking() {
            var svc = Service();
            var staged = await svc.StageAsync(Body(), 4096, null, CancellationToken.None);
            calls.Clear();

            var status = await svc.ApplyAsync(staged.Id, CancellationToken.None);

            Assert.That(status.Status, Is.EqualTo("pending"));
            var request = await File.ReadAllTextAsync(Path.Combine(paths.RequestDirectory, staged.Id + ".request"));
            Assert.That(request, Is.EqualTo(Path.Combine(paths.StageDirectory, staged.Id + ".deb") + "\n5555\n"),
                "staged path then the daemon's port, one per line, like the storage request");
            var start = calls.Single(c => c.File == "systemctl");
            Assert.That(start.Args, Is.EqualTo(new[] { "start", "--no-block", $"openastroara-update@{staged.Id}.service" }.ToList()),
                "--no-block: the helper restarts this process before a blocking start could return");
            var evt = published.Single();
            Assert.That(evt.Type, Is.EqualTo(WsEventCatalog.ServerRestartImminent));
            Assert.That(evt.Payload.GetProperty("reason").GetString(), Is.EqualTo("update"));
            Assert.That(evt.Payload.GetProperty("version").GetString(), Is.EqualTo("0.0.2-ara.1"));
            Assert.That(evt.Payload.GetProperty("update_id").GetString(), Is.EqualTo(staged.Id));
        }

        [Test]
        public async Task Apply_of_an_unknown_id_is_a_404_and_touches_nothing() {
            var ex = await Assert.ThrowsAsync<ServerUpdateRejectedException>(() => Service().ApplyAsync("deadbeef", CancellationToken.None));
            Assert.That(ex!.Reason, Is.EqualTo("unknown_id"));
            Assert.That(published, Is.Empty);
            Assert.That(await Service().GetStatusAsync("deadbeef", CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task Apply_that_systemd_refuses_removes_the_request_and_reports_the_reason() {
            answers["systemctl"] = (1, "Failed to start openastroara-update@x.service: Access denied");
            var svc = Service();
            var staged = await svc.StageAsync(Body(), 4096, null, CancellationToken.None);
            var ex = await Assert.ThrowsAsync<ServerUpdateRejectedException>(() => svc.ApplyAsync(staged.Id, CancellationToken.None));
            Assert.That(ex!.Reason, Is.EqualTo("helper_unavailable"));
            Assert.That(ex.Message, Does.Contain("Access denied"));
            Assert.That(published, Is.Empty, "a refused start must not announce a restart");
            Assert.That(File.Exists(Path.Combine(paths.RequestDirectory, staged.Id + ".request")), Is.False);
        }

        [Test]
        public async Task A_second_apply_while_one_is_running_is_refused() {
            var svc = Service();
            var first = await svc.StageAsync(Body(), 4096, null, CancellationToken.None);
            var second = await svc.StageAsync(Body(), 4096, null, CancellationToken.None);
            await svc.ApplyAsync(first.Id, CancellationToken.None);
            published.Clear();
            var ex = await Assert.ThrowsAsync<ServerUpdateRejectedException>(() => svc.ApplyAsync(second.Id, CancellationToken.None));
            Assert.That(ex!.Reason, Is.EqualTo("update_in_progress"));
            Assert.That(published, Is.Empty, "no restart is announced for a refused apply");
        }

        [Test]
        public async Task A_request_older_than_the_helper_timeout_does_not_block_a_new_apply() {
            Directory.CreateDirectory(paths.RequestDirectory);
            var dead = Path.Combine(paths.RequestDirectory, "0bad.request");
            await File.WriteAllTextAsync(dead, "x");
            File.SetLastWriteTimeUtc(dead, DateTime.UtcNow - ServerUpdateService.HelperTimeout - TimeSpan.FromMinutes(1));
            var svc = Service();
            var staged = await svc.StageAsync(Body(), 4096, null, CancellationToken.None);
            Assert.That((await svc.ApplyAsync(staged.Id, CancellationToken.None)).Status, Is.EqualTo("pending"));
        }

        [Test]
        public async Task Uploads_older_than_a_day_are_swept_on_the_next_upload() {
            Directory.CreateDirectory(paths.StageDirectory);
            var old = Path.Combine(paths.StageDirectory, "0123abcd.deb");
            await File.WriteAllTextAsync(old, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow - ServerUpdateService.StaleUploadAge - TimeSpan.FromMinutes(1));
            var staged = await Service().StageAsync(Body(), 4096, null, CancellationToken.None);
            Assert.That(File.Exists(old), Is.False);
            Assert.That(File.Exists(Path.Combine(paths.StageDirectory, staged.Id + ".deb")), Is.True);
        }

        [Test]
        public async Task Status_is_pending_while_the_request_waits_and_parses_the_result_once_written() {
            var svc = Service();
            var staged = await svc.StageAsync(Body(), 4096, null, CancellationToken.None);
            await svc.ApplyAsync(staged.Id, CancellationToken.None);
            Assert.That((await svc.GetStatusAsync(staged.Id, CancellationToken.None))!.Status, Is.EqualTo("pending"));

            await File.WriteAllTextAsync(Path.Combine(paths.RequestDirectory, staged.Id + ".result"),
                "3\nfrom=0.0.1-ara.1\nto=0.0.2-ara.1\nrollback=available\nnew daemon did not answer /healthz within 90s\nstatus=rolled_back\n");
            var status = await svc.GetStatusAsync(staged.Id, CancellationToken.None);
            Assert.Multiple(() => {
                Assert.That(status!.Status, Is.EqualTo("rolled_back"));
                Assert.That(status.FromVersion, Is.EqualTo("0.0.1-ara.1"));
                Assert.That(status.ToVersion, Is.EqualTo("0.0.2-ara.1"));
                Assert.That(status.RollbackAvailable, Is.True);
                Assert.That(status.Output, Does.Contain("did not answer /healthz"));
            });
        }

        [TestCase("0\nstatus=applied\n", "applied")]
        [TestCase("0\nfrom=1\nto=2\n", "applied", TestName = "Status_exit_0_without_a_status_line_is_applied")]
        [TestCase("9\nupdate-request: bad request id\n", "failed", TestName = "Status_wrapper_refusal_is_failed")]
        [TestCase("1\nstatus=failed\nERROR: package is 'other'\n", "failed")]
        public void Status_result_shapes(string text, string expected) =>
            Assert.That(ServerUpdateService.ParseResult("id", text).Status, Is.EqualTo(expected));
    }
}
