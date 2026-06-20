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
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §54 bug-report service: bundling the daemon's logs + profile.json + a generated
    /// system-info.json into a ZIP, resolving it for download by preparation id, and the
    /// not-found path. Covers bundle contents, the always-present system-info, the real
    /// reported size, download round-trip, and unknown/foreign id rejection.
    /// </summary>
    [TestFixture]
    public class BugReportServiceTest {

        private string _profileDir = null!;
        private BugReportService _svc = null!;

        [SetUp]
        public void SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), "ara-bugreport-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_profileDir);
            _svc = new BugReportService(_profileDir, NullLogger<BugReportService>.Instance);
        }

        [TearDown]
        public void TearDown() {
            _svc.Dispose();
            if (Directory.Exists(_profileDir)) {
                Directory.Delete(_profileDir, recursive: true);
            }
        }

        private void WriteLog(string name, string content) {
            var logsDir = Path.Combine(_profileDir, "logs");
            Directory.CreateDirectory(logsDir);
            File.WriteAllText(Path.Combine(logsDir, name), content);
        }

        private void WriteProfile(string content = "{\"name\":\"default\"}") =>
            File.WriteAllText(Path.Combine(_profileDir, "profile.json"), content);

        private async Task<ZipArchive> OpenBundleAsync(Guid id) {
            var dl = await _svc.OpenDownloadAsync(id, CancellationToken.None);
            Assert.That(dl, Is.Not.Null);
            // Copy to a MemoryStream so the archive outlives the file handle for assertions.
            var ms = new MemoryStream();
            await using (var src = dl!.Value.Stream) {
                await src.CopyToAsync(ms);
            }
            ms.Position = 0;
            return new ZipArchive(ms, ZipArchiveMode.Read);
        }

        [Test]
        public async Task PrepareAsync_bundles_logs_profile_and_system_info() {
            WriteLog("openastroara-20260619.log", "log line one");
            WriteProfile();

            var prep = await _svc.PrepareAsync(null, CancellationToken.None);

            Assert.That(prep.Status, Is.EqualTo("ready"));
            Assert.That(prep.PreparationId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(prep.EstimatedSizeBytes, Is.GreaterThan(0));
            Assert.That(prep.DownloadUrl, Is.Not.Null);
            Assert.That(prep.DownloadUrl!.ToString(), Does.Contain(prep.PreparationId.ToString("D")));

            using var zip = await OpenBundleAsync(prep.PreparationId);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.That(names, Does.Contain("system-info.json"));
            Assert.That(names, Does.Contain("logs/openastroara-20260619.log"));
            Assert.That(names, Does.Contain("profile.json"));
        }

        [Test]
        public async Task PrepareAsync_bundles_only_the_newest_logs() {
            // More log files than the cap; only the newest MaxBundledLogs are included.
            for (var d = 1; d <= BugReportService.MaxBundledLogs + 3; d++) {
                WriteLog($"openastroara-202606{d:00}.log", $"day {d}");
            }

            var prep = await _svc.PrepareAsync(null, CancellationToken.None);

            using var zip = await OpenBundleAsync(prep.PreparationId);
            var logEntries = zip.Entries.Select(e => e.FullName).Where(n => n.StartsWith("logs/", StringComparison.Ordinal)).ToList();
            Assert.That(logEntries.Count, Is.EqualTo(BugReportService.MaxBundledLogs));
            // The newest day must be present; the oldest (day 01) must not.
            Assert.That(logEntries, Does.Contain($"logs/openastroara-202606{BugReportService.MaxBundledLogs + 3:00}.log"));
            Assert.That(logEntries, Does.Not.Contain("logs/openastroara-20260601.log"));
        }

        [Test]
        public async Task PrepareAsync_system_info_has_app_version_and_os() {
            var prep = await _svc.PrepareAsync(null, CancellationToken.None);

            using var zip = await OpenBundleAsync(prep.PreparationId);
            var entry = zip.GetEntry("system-info.json");
            Assert.That(entry, Is.Not.Null);
            using var reader = new StreamReader(await entry!.OpenAsync(CancellationToken.None));
            using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
            var root = doc.RootElement;
            Assert.That(root.GetProperty("app_version").GetString(), Is.Not.Empty);
            Assert.That(root.GetProperty("os_description").GetString(), Is.Not.Empty);
            Assert.That(root.GetProperty("report_id").GetString(), Is.EqualTo(prep.PreparationId.ToString("D")));
        }

        [Test]
        public async Task PrepareAsync_bundle_is_never_empty_with_no_logs_or_profile() {
            // Fresh profile dir: no logs, no profile.json — system-info.json still makes a valid bundle.
            var prep = await _svc.PrepareAsync(null, CancellationToken.None);

            Assert.That(prep.EstimatedSizeBytes, Is.GreaterThan(0));
            using var zip = await OpenBundleAsync(prep.PreparationId);
            Assert.That(zip.Entries.Select(e => e.FullName), Does.Contain("system-info.json"));
        }

        [Test]
        public async Task PrepareAsync_reported_size_matches_the_bundle_on_disk() {
            WriteLog("openastroara-20260619.log", new string('x', 4096));
            var prep = await _svc.PrepareAsync(null, CancellationToken.None);

            var dir = Path.Combine(_profileDir, "bug-reports");
            var file = Directory.EnumerateFiles(dir, "bugreport-*.zip").Single();
            Assert.That(prep.EstimatedSizeBytes, Is.EqualTo(new FileInfo(file).Length));
        }

        [Test]
        public async Task OpenDownloadAsync_streams_the_prepared_bundle() {
            var prep = await _svc.PrepareAsync(null, CancellationToken.None);

            var dl = await _svc.OpenDownloadAsync(prep.PreparationId, CancellationToken.None);

            Assert.That(dl, Is.Not.Null);
            Assert.That(dl!.Value.FileName, Does.StartWith("bugreport-"));
            Assert.That(dl.Value.FileName, Does.EndWith(".zip"));
            await dl.Value.Stream.DisposeAsync();
        }

        [Test]
        public async Task OpenDownloadAsync_returns_null_for_unknown_id() {
            await _svc.PrepareAsync(null, CancellationToken.None);

            var dl = await _svc.OpenDownloadAsync(Guid.NewGuid(), CancellationToken.None);

            Assert.That(dl, Is.Null);
        }

        [Test]
        public async Task OpenDownloadAsync_returns_null_when_nothing_prepared() {
            var dl = await _svc.OpenDownloadAsync(Guid.NewGuid(), CancellationToken.None);
            Assert.That(dl, Is.Null);
        }

        [Test]
        public async Task PrepareAsync_prunes_to_the_retention_cap() {
            // Stage more bundles than the cap; the oldest are reaped, newest kept.
            for (var i = 0; i < 13; i++) {
                await _svc.PrepareAsync(null, CancellationToken.None);
            }

            var dir = Path.Combine(_profileDir, "bug-reports");
            var count = Directory.EnumerateFiles(dir, "bugreport-*.zip").Count();
            Assert.That(count, Is.EqualTo(BugReportService.MaxRetainedBundles));
        }

        [Test]
        public async Task SweepStaleTempBundles_reaps_orphaned_temps_at_startup() {
            // Simulate a .tmp- leftover from a prepare that crashed before its rename.
            var dir = Path.Combine(_profileDir, "bug-reports");
            Directory.CreateDirectory(dir);
            var orphan = Path.Combine(dir, ".tmp-deadbeef.zip");
            await File.WriteAllTextAsync(orphan, "half-written");

            BugReportService.SweepStaleTempBundles(_profileDir);

            Assert.That(File.Exists(orphan), Is.False);
        }

        [Test]
        public void SweepStaleTempBundles_is_a_noop_when_dir_absent() {
            // No bug-reports/ yet — must not throw.
            Assert.DoesNotThrow(() => BugReportService.SweepStaleTempBundles(_profileDir));
        }

        [Test]
        public async Task PrepareAsync_each_call_stages_a_distinct_bundle() {
            var first = await _svc.PrepareAsync("same-key", CancellationToken.None);
            var second = await _svc.PrepareAsync("same-key", CancellationToken.None);

            Assert.That(first.PreparationId, Is.Not.EqualTo(second.PreparationId));
            var dir = Path.Combine(_profileDir, "bug-reports");
            Assert.That(Directory.EnumerateFiles(dir, "bugreport-*.zip").Count(), Is.EqualTo(2));
        }
    }
}
