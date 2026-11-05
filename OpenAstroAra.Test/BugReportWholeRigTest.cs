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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The whole-rig bug report: with a range, the bundle carries what was pulled off the CM4 by hand for the
    /// 2026-10-16 and -18 night forensics: Ara's daemon and NINA-layer logs, AlpacaBridge, the guider's PHD2
    /// logs, package history and the journal. Each source is a temp folder here and the journal a fake reader.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk
    public class BugReportWholeRigTest {

        private static readonly string[] JournalEntries = ["journal/boots.txt", "journal/kernel.log", "journal/openastroara-server.log",
            "journal/alpacabridge.log", "journal/openastro-guider.log"];
        private static readonly string[] AllSources = ["ara", "ara-nina", "alpacabridge", "guider", "system", "journal"];
        private static readonly string[] Units = ["openastroara-server", "alpacabridge", "openastro-guider"];

        private string _root = null!;
        private string _profileDir = null!;
        private string _bridgeDir = null!;
        private string _guiderDir = null!;
        private string _aptHistory = null!;
        private readonly ConcurrentQueue<IReadOnlyList<string>> _journalCalls = new();
        private Func<IReadOnlyList<string>, IEnumerable<string>> _journal = args => [$"journal for {string.Join(' ', args)}"];

        [SetUp]
        public void SetUp() {
            _root = Path.Combine(Path.GetTempPath(), "ara-bugreport-rig-" + Path.GetRandomFileName());
            _profileDir = Path.Combine(_root, "var-lib-openastroara");
            _bridgeDir = Path.Combine(_root, "AlpacaBridge");
            _guiderDir = Path.Combine(_root, "PHD2");
            _aptHistory = Path.Combine(_root, "history.log");
            foreach (var d in new[] { _profileDir, _bridgeDir, _guiderDir, Path.Combine(_profileDir, "logs"), Path.Combine(_profileDir, "NINA", "Logs") }) {
                Directory.CreateDirectory(d);
            }
            _journalCalls.Clear();
            _journal = args => [$"journal for {string.Join(' ', args)}"];
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private BugReportService Service(long fileCap = BugReportService.MaxFileBytes, long totalCap = BugReportService.MaxTotalRawBytes) =>
            new(_profileDir, NullLogger<BugReportService>.Instance, new BugReportSources(
                AlpacaBridgeLogDir: _bridgeDir,
                GuiderLogDir: _guiderDir,
                SystemFiles: [_aptHistory, Path.Combine(_root, "no-such-term.log")],
                Journal: (args, output, _) => {
                    _journalCalls.Enqueue(args);
                    foreach (var line in _journal(args)) {
                        output(line);
                    }
                    return 0;
                },
                JournalUnits: ["openastroara-server", "alpacabridge", "openastro-guider"],
                KernelRing: (_, output, _) => {
                    output("[2026-10-28T23:41:02] usb 2-1: USB disconnect, device number 4");
                    return 0;
                })) {
                FileCap = fileCap,
                TotalCap = totalCap,
            };

        private static void Write(string path, string content, DateTime? writtenUtc = null) {
            File.WriteAllText(path, content);
            if (writtenUtc is DateTime t) {
                File.SetLastWriteTimeUtc(path, t);
            }
        }

        private static async Task<ZipArchive> OpenAsync(BugReportService svc, Guid id) {
            var dl = await svc.OpenDownloadAsync(id, CancellationToken.None);
            Assert.That(dl, Is.Not.Null);
            var ms = new MemoryStream();
            await using (var src = dl!.Value.Stream) {
                await src.CopyToAsync(ms);
            }
            ms.Position = 0;
            return new ZipArchive(ms, ZipArchiveMode.Read);
        }

        private static string Read(ZipArchive zip, string entry) {
            using var reader = new StreamReader(zip.GetEntry(entry)!.Open());
            return reader.ReadToEnd();
        }

        private void SeedLastNight(DateTime old) {
            Write(Path.Combine(_profileDir, "logs", "openastroara-20261028.log"), "ara daemon line\n");
            Write(Path.Combine(_profileDir, "NINA", "Logs", "20261028-220306.log"), "nina layer line\n");
            Write(Path.Combine(_bridgeDir, "alpacabridge-2026-10-28.log"), "bridge line\n");
            Write(Path.Combine(_bridgeDir, "alpacabridge-2026-10-20.log"), "a week-old bridge log\n", old);
            Write(Path.Combine(_guiderDir, "PHD2_DebugLog_2026-10-28_220000.txt"), "guider debug line\n");
            Write(Path.Combine(_guiderDir, "PHD2_GuideLog_2026-10-28_220000.txt"), "guide log line\n");
            Write(_aptHistory, "Start-Date: 2026-10-29  03:41:20\n");
        }

        [Test]
        public async Task A_range_bundles_every_source_of_the_rig_written_in_it() {
            using var svc = Service();
            SeedLastNight(old: DateTime.UtcNow.AddDays(-8));
            var since = DateTimeOffset.UtcNow.AddDays(-1);

            var prep = await svc.PrepareAsync(null, CancellationToken.None, BugReportSharingMode.Full, since);

            using var zip = await OpenAsync(svc, prep.PreparationId);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Multiple(() => {
                Assert.That(names, Does.Contain("logs/openastroara-20261028.log"));
                Assert.That(names, Does.Contain("ara-nina-logs/20261028-220306.log"));
                Assert.That(names, Does.Contain("alpacabridge/alpacabridge-2026-10-28.log"));
                Assert.That(names, Does.Not.Contain("alpacabridge/alpacabridge-2026-10-20.log"), "written before the range");
                Assert.That(names, Does.Contain("guider/PHD2_DebugLog_2026-10-28_220000.txt"));
                Assert.That(names, Does.Contain("guider/PHD2_GuideLog_2026-10-28_220000.txt"));
                Assert.That(names, Does.Contain("system/history.log"));
                Assert.That(names, Does.Contain("system/dmesg.txt"));
                Assert.That(names, Is.SupersetOf(JournalEntries));
                Assert.That(names, Does.Contain("manifest.json"));
                Assert.That(names, Does.Contain("system-info.json"));
                Assert.That(Read(zip, "alpacabridge/alpacabridge-2026-10-28.log"), Does.Contain("bridge line"));
                Assert.That(Read(zip, "system/dmesg.txt"), Does.Contain("USB disconnect"), "a journal without kernel messages still has the ring buffer");
            });
            Assert.That(prep.Sources!.Select(s => s.Source), Is.SupersetOf(AllSources));
            Assert.That(prep.Sources!.Single(s => s.Source == "alpacabridge").Files, Is.EqualTo(1));
            Assert.That(prep.SinceUtc, Is.EqualTo(since));
        }

        [Test]
        public async Task The_journal_is_read_since_the_range_for_each_unit_and_the_kernel() {
            using var svc = Service();
            var since = DateTimeOffset.UtcNow.AddHours(-20);
            await svc.PrepareAsync(null, CancellationToken.None, BugReportSharingMode.Full, since);

            var calls = _journalCalls.Select(a => string.Join(' ', a)).ToList();
            var sinceArg = "--since @" + since.ToUnixTimeSeconds();
            Assert.Multiple(() => {
                Assert.That(calls, Does.Contain("--no-pager --list-boots"));
                Assert.That(calls.Single(c => c.StartsWith("-k", StringComparison.Ordinal)), Does.Contain(sinceArg));
                foreach (var unit in Units) {
                    Assert.That(calls.Single(c => c.StartsWith("-u " + unit + " ", StringComparison.Ordinal)), Does.Contain(sinceArg));
                }
            });
        }

        [Test]
        public async Task A_journal_the_daemon_may_not_read_is_named_in_the_manifest() {
            _journal = _ => ["Hint: You are currently not seeing messages from other users and the system."];
            using var svc = Service();
            var prep = await svc.PrepareAsync(null, CancellationToken.None, BugReportSharingMode.Full, DateTimeOffset.UtcNow.AddHours(-1));

            var journal = prep.Sources!.Single(s => s.Source == "journal");
            Assert.That(journal.Note, Does.Contain("systemd-journal group"));
        }

        [Test]
        public async Task A_journal_reader_that_throws_does_not_fail_the_bundle() {
            _journal = _ => throw new InvalidOperationException("journalctl went away");
            using var svc = Service();
            SeedLastNight(old: DateTime.UtcNow.AddDays(-8));

            var prep = await svc.PrepareAsync(null, CancellationToken.None, BugReportSharingMode.Full, DateTimeOffset.UtcNow.AddHours(-1));

            Assert.That(prep.Status, Is.EqualTo("ready"));
            Assert.That(prep.Sources!.Single(s => s.Source == "journal").Note, Does.Contain("journalctl went away"));
            using var zip = await OpenAsync(svc, prep.PreparationId);
            Assert.That(zip.GetEntry("alpacabridge/alpacabridge-2026-10-28.log"), Is.Not.Null, "the rest of the bundle stands");
        }

        [Test]
        public async Task An_oversized_log_keeps_its_newest_part_and_says_so() {
            using var svc = Service(fileCap: 1000);
            var lines = Enumerable.Range(1, 400).Select(i => $"guide frame {i:D4}");
            Write(Path.Combine(_guiderDir, "PHD2_DebugLog_2026-10-28_220000.txt"), string.Join('\n', lines) + "\n");

            var prep = await svc.PrepareAsync(null, CancellationToken.None, BugReportSharingMode.Full, DateTimeOffset.UtcNow.AddHours(-1));

            using var zip = await OpenAsync(svc, prep.PreparationId);
            var text = Read(zip, "guider/PHD2_DebugLog_2026-10-28_220000.txt");
            Assert.Multiple(() => {
                Assert.That(text, Does.StartWith("[Ara bug report: the first"));
                Assert.That(text, Does.Contain("guide frame 0400"), "the newest lines are kept");
                Assert.That(text, Does.Not.Contain("guide frame 0001"), "the oldest are left out");
                Assert.That(text.Length, Is.LessThan(1200));
                Assert.That(prep.Sources!.Single(s => s.Source == "guider").Truncated, Is.True);
            });
            var manifest = JsonNode.Parse(Read(zip, "manifest.json"))!["entries"]!.AsArray();
            var entry = manifest.Single(e => (string?)e!["source"] == "guider")!;
            Assert.That((string?)entry["status"], Is.EqualTo("truncated"));
            Assert.That((long)entry["omitted_bytes"]!, Is.GreaterThan(0));
        }

        [Test]
        public async Task The_bundle_stops_adding_files_at_its_total_budget() {
            using var svc = Service(totalCap: 30);
            Write(Path.Combine(_bridgeDir, "alpacabridge-2026-10-28.log"), new string('b', 25) + "\n");
            Write(Path.Combine(_guiderDir, "PHD2_GuideLog_2026-10-28_220000.txt"), new string('g', 25) + "\n");
            Write(Path.Combine(_guiderDir, "PHD2_DebugLog_2026-10-28_220000.txt"), new string('d', 25) + "\n");

            var prep = await svc.PrepareAsync(null, CancellationToken.None, BugReportSharingMode.Full, DateTimeOffset.UtcNow.AddHours(-1));

            using var zip = await OpenAsync(svc, prep.PreparationId);
            var manifest = JsonNode.Parse(Read(zip, "manifest.json"))!["entries"]!.AsArray();
            // The file that crosses the budget keeps its newest part; the ones after it are left out.
            Assert.That(manifest.Count(e => (string?)e!["status"] == "truncated"), Is.EqualTo(1));
            Assert.That(manifest.Count(e => (string?)e!["status"] == "over_budget"), Is.EqualTo(1));
        }

        [Test]
        public async Task Every_source_is_scrubbed_for_the_sharing_mode() {
            using var svc = Service();
            Write(Path.Combine(_bridgeDir, "alpacabridge-2026-10-28.log"), "camera on 192.168.1.5\n");

            var prep = await svc.PrepareAsync(null, CancellationToken.None, BugReportSharingMode.Redacted, DateTimeOffset.UtcNow.AddHours(-1));

            using var zip = await OpenAsync(svc, prep.PreparationId);
            var text = Read(zip, "alpacabridge/alpacabridge-2026-10-28.log");
            Assert.That(text, Does.Not.Contain("192.168.1.5"), "a private address is scrubbed in the redacted mode");
        }

        [Test]
        public async Task A_missing_source_folder_is_named_not_fatal() {
            Directory.Delete(_guiderDir);
            using var svc = Service();

            var prep = await svc.PrepareAsync(null, CancellationToken.None, BugReportSharingMode.Full, DateTimeOffset.UtcNow.AddHours(-1));

            Assert.That(prep.Status, Is.EqualTo("ready"));
            Assert.That(prep.Sources!.Single(s => s.Source == "guider").Note, Does.Contain("does not exist"));
        }

        [Test]
        public async Task Without_a_range_the_bundle_is_aras_newest_logs_only() {
            using var svc = Service();
            SeedLastNight(old: DateTime.UtcNow.AddDays(-8));

            var prep = await svc.PrepareAsync(null, CancellationToken.None);

            using var zip = await OpenAsync(svc, prep.PreparationId);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.That(names, Does.Contain("logs/openastroara-20261028.log"));
            Assert.That(names.Where(n => n.StartsWith("alpacabridge/", StringComparison.Ordinal) || n.StartsWith("journal/", StringComparison.Ordinal)), Is.Empty);
            Assert.That(_journalCalls, Is.Empty);
        }

        [Test]
        public async Task A_background_preparation_reports_preparing_then_ready() {
            using var svc = Service();
            SeedLastNight(old: DateTime.UtcNow.AddDays(-8));

            var started = svc.StartPrepare(BugReportSharingMode.Full, DateTimeOffset.UtcNow.AddDays(-1));
            Assert.That(started.Status, Is.EqualTo("preparing"));

            var deadline = DateTime.UtcNow.AddSeconds(30);
            BugReportPreparationDto? state;
            while ((state = svc.GetPreparation(started.PreparationId))?.Status == "preparing") {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline));
                await Task.Delay(20);
            }
            Assert.That(state!.Status, Is.EqualTo("ready"));
            Assert.That(state.EstimatedSizeBytes, Is.GreaterThan(0));
            Assert.That(state.Sources, Is.Not.Empty);
            Assert.That(state.DownloadUrl, Is.Not.Null);
            using var zip = await OpenAsync(svc, started.PreparationId);
            Assert.That(zip.GetEntry("guider/PHD2_GuideLog_2026-10-28_220000.txt"), Is.Not.Null);
        }

        [Test]
        public async Task A_background_preparation_that_cannot_write_reports_failed_with_why() {
            // bug-reports/ is a plain file: the bundle has nowhere to go.
            Write(Path.Combine(_profileDir, "bug-reports"), "not a folder");
            using var svc = Service();

            var started = svc.StartPrepare(BugReportSharingMode.Full, DateTimeOffset.UtcNow.AddHours(-1));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            BugReportPreparationDto? state;
            while ((state = svc.GetPreparation(started.PreparationId))?.Status == "preparing") {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline));
                await Task.Delay(20);
            }
            Assert.That(state!.Status, Is.EqualTo("failed"));
            Assert.That(state.Error, Is.Not.Empty);
        }

        [Test]
        public void An_unknown_preparation_is_null() {
            using var svc = Service();
            Assert.That(svc.GetPreparation(Guid.NewGuid()), Is.Null);
        }
    }
}
