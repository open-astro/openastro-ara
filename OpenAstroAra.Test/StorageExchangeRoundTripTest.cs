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
using NUnit.Framework;
using OpenAstroAra.Server.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1135 — the §29.1.4 request/result exchange, exercised end to end against the
    /// REAL packaged wrapper (<c>storage-request.sh</c>, honouring its <c>DIR</c>/<c>HELPER</c>
    /// overrides) with a stand-in helper that echoes its argv, plus the startup sweep of a
    /// stale exchange directory. The wrapper benches are Linux-only (<c>mv -T</c>, the tmpfs
    /// layout); the sweep runs everywhere.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    public class StorageExchangeRoundTripTest {

        private static readonly string RepoRoot = FindRepoRoot();
        private static readonly string[] KeptFiles = ["c.other"];
        private static string Wrapper => Path.Combine(RepoRoot, "packaging", "debian", "opt", "openastroara", "scripts", "storage-request.sh");

        private string root = null!;
        private string exchange = null!;   // <root>/openastroara/storage — two levels under root, like /run
        private string helper = null!;
        private string unitTemplate = null!;

        private static string FindRepoRoot() {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenAstroAra.sln"))) {
                dir = dir.Parent;
            }
            return dir?.FullName ?? throw new InvalidOperationException("repo root not found above " + AppContext.BaseDirectory);
        }

        [SetUp]
        public void SetUp() {
            root = Path.Combine(Path.GetTempPath(), $"oara-storage-{Guid.NewGuid():N}");
            exchange = Path.Combine(root, "openastroara", "storage");
            Directory.CreateDirectory(exchange);
            // The stand-in helper: one line per argv element so the test can read the argv the
            // wrapper rebuilt, reported through the "ERROR: <code> <detail>" shape the daemon parses.
            helper = Path.Combine(root, "fake-helper.sh");
            File.WriteAllText(helper, "#!/bin/sh\nprintf 'ERROR: probe argc=%s' \"$#\"\nfor a; do printf ' [%s]' \"$a\"; done\nprintf '\\n'\nexit 4\n");
            unitTemplate = Path.Combine(root, "openastroara-storage@.service");
            File.WriteAllText(unitTemplate, "[Unit]\n"); // its presence selects the request-file path
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        [TearDown]
        public void TearDown() {
            try {
                Directory.Delete(root, recursive: true);
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
        }

        private StorageHelperPaths Paths => new(helper, unitTemplate, exchange);

        // The daemon's `systemctl start openastroara-storage@<id>.service` becomes a direct run of
        // the wrapper with the overrides; everything else (lsblk/findmnt) is not reached here.
        private Task<(int ExitCode, string Output)> RunViaWrapper(string file, string[] args, CancellationToken ct) {
            Assert.That(file, Is.EqualTo("systemctl"), "only the unit start is expected on this path");
            var id = args[1]["openastroara-storage@".Length..^".service".Length];
            return RunWrapperAsync(id);
        }

        private async Task<(int ExitCode, string Output)> RunWrapperAsync(string id) {
            var info = new ProcessStartInfo("sh") {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            info.ArgumentList.Add(Wrapper);
            info.ArgumentList.Add(id);
            info.Environment["DIR"] = exchange;
            info.Environment["HELPER"] = helper;
            using var process = Process.Start(info)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout + stderr);
        }

        [Test]
        public void The_startup_sweep_removes_stale_request_and_result_files_only() {
            File.WriteAllText(Path.Combine(exchange, "a.request"), "x\n");
            File.WriteAllText(Path.Combine(exchange, "b.result"), "0\n");
            File.WriteAllText(Path.Combine(exchange, "c.other"), "keep\n");

            var svc = new StorageDeviceService(NullLogger<StorageDeviceService>.Instance, Paths, run: null);

            Assert.That(Directory.GetFiles(exchange).Select(Path.GetFileName), Is.EquivalentTo(KeptFiles),
                "construction sweeps the daemon's own leftovers and nothing else");
            Assert.That(svc.SweepStaleExchangeFiles(), Is.EqualTo(0), "a second sweep finds nothing");
        }

        [Test]
        public void The_startup_sweep_is_a_no_op_without_the_exchange_directory() {
            Directory.Delete(exchange, recursive: true);
            var svc = new StorageDeviceService(NullLogger<StorageDeviceService>.Instance, Paths, run: null);
            Assert.That(svc.SweepStaleExchangeFiles(), Is.EqualTo(0));
            Assert.That(Directory.Exists(exchange), Is.False, "the sweep never creates it — tmpfiles.d does");
        }

        [Test]
        [Category("bench")]
        public async Task The_wrapper_rebuilds_argv_from_the_request_and_the_daemon_reads_the_result() {
            if (!OperatingSystem.IsLinux()) Assert.Ignore("the wrapper is Linux-only (mv -T, tmpfs layout)");
            var svc = new StorageDeviceService(NullLogger<StorageDeviceService>.Instance, Paths, RunViaWrapper);

            // A format with an EMPTY confirm label: the empty line must survive as an empty argv
            // element (the unlabeled-disk case), so the helper sees five arguments, not four.
            var result = await svc.ConfigureAsync("ABCD-1234", format: true, expectedLabel: "", filesystem: "exfat", CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Code, Is.EqualTo("probe"), "the helper's own ERROR code travels through the result file");
            Assert.That(result.Detail, Is.EqualTo("argc=5 [--format] [--fs] [exfat] [ABCD-1234] []"));
            Assert.That(Directory.GetFiles(exchange), Is.Empty, "request and result are both cleaned up after the round trip");
        }

        [Test]
        [Category("bench")]
        public async Task The_wrapper_refuses_a_request_with_more_than_MAX_ARGS_lines() {
            if (!OperatingSystem.IsLinux()) Assert.Ignore("the wrapper is Linux-only");
            var id = "runaway";
            await File.WriteAllTextAsync(Path.Combine(exchange, id + ".request"), string.Concat(Enumerable.Repeat("x\n", 9)));

            var (exit, output) = await RunWrapperAsync(id);

            Assert.That(exit, Is.EqualTo(9), output);
            Assert.That(output, Does.Contain("too many arguments"));
            Assert.That(File.Exists(Path.Combine(exchange, id + ".result")), Is.False, "a refused request writes no result");
        }

        [Test]
        [Category("bench")]
        public async Task The_wrapper_refuses_a_request_that_is_not_a_regular_file() {
            if (!OperatingSystem.IsLinux()) Assert.Ignore("the wrapper is Linux-only");
            var id = "planted";
            var target = Path.Combine(root, "elsewhere.txt");
            await File.WriteAllTextAsync(target, "--format\n");
            File.CreateSymbolicLink(Path.Combine(exchange, id + ".request"), target);

            var (exit, output) = await RunWrapperAsync(id);

            Assert.That(exit, Is.EqualTo(9), output);
            Assert.That(output, Does.Contain("not a regular file"));
            Assert.That(File.Exists(target), Is.True, "the symlink's target is never touched");
        }

        [Test]
        [Category("bench")]
        public async Task The_wrapper_refuses_a_bad_request_id() {
            if (!OperatingSystem.IsLinux()) Assert.Ignore("the wrapper is Linux-only");
            var (exit, output) = await RunWrapperAsync("../escape");
            Assert.That(exit, Is.EqualTo(9), output);
            Assert.That(output, Does.Contain("bad request id"));
        }
    }
}
