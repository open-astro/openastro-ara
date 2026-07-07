#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §36-2b download worker: a DownloadAsync kicks off a background fetch → §36-2a install, emits
    /// progress/complete/failed WS events, reflects in-flight state, and cancels.
    /// </summary>
    [TestFixture]
    public class DataManagerDownloadTest {

        private string _root = null!;

        [SetUp]
        public void SetUp() {
            _root = Path.Combine(Path.GetTempPath(), "ara-dl-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown() {
            // A background download worker may still be writing under _root as the test ends; retry the cleanup a
            // few times so a momentary "in use" race doesn't fail the test on its teardown.
            for (var attempt = 0; ; attempt++) {
                try {
                    if (Directory.Exists(_root)) {
                        Directory.Delete(_root, recursive: true);
                    }
                    return;
                } catch (IOException) when (attempt < 20) {
                    Thread.Sleep(25);
                } catch (UnauthorizedAccessException) when (attempt < 20) {
                    Thread.Sleep(25);
                }
            }
        }

        private static byte[] TarGz(params (string name, byte[] data)[] entries) {
            var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
            using (var tar = new TarWriter(gz, leaveOpen: true)) {
                foreach (var (name, data) in entries) {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
                }
            }
            ms.Position = 0;
            return ms.ToArray();
        }

        // Gzip raw bytes — the bare-CSV.gz catalog download shape.
        private static byte[] Gz(byte[] data) {
            var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true)) {
                gz.Write(data, 0, data.Length);
            }
            ms.Position = 0;
            return ms.ToArray();
        }

        // A self-contained synthetic catalog injected into the service, so the worker tests don't depend on the
        // production catalog's exact contents (which changes as real packages are added/removed). These tests exercise
        // the worker — progress / WS events / cancel / force / conditional GET / format routing — and
        // FakeSkyDataFetcher ignores the URL, so each URL only selects the install path.
        private const string PackageId = "test-archive";    // .tar.gz → archive-install path
        private const string CsvGzPackageId = "test-csvgz";  // .csv.gz → single-file gunzip path

        private static readonly IReadOnlyList<DataPackageDto> TestCatalog = new[] {
            new DataPackageDto(PackageId, "Test archive", "", "catalog", 1 << 20, "1", false, null,
                new Uri("https://example.test/test.tar.gz")),
            new DataPackageDto(CsvGzPackageId, "Test csv.gz", "", "catalog", 1 << 20, "1", false, null,
                new Uri("https://example.test/test.csv.gz")),
        };

        private static readonly IReadOnlyDictionary<string, string> NoDigests = new Dictionary<string, string>();

        // Worker-mechanics tests don't verify integrity (empty digest map); the dedicated digest test injects its own.
        private DataManagerService NewService(ISkyDataFetcher fetcher, CapturingBroadcaster ws) =>
            new(_root, fetcher, ws, NullLogger<DataManagerService>.Instance,
                catalog: TestCatalog, expectedDigests: NoDigests);

        // Poll until predicate holds or the timeout elapses (the worker runs on a background task).
        private static async Task<bool> Eventually(Func<bool> predicate, int timeoutMs = 5000) {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline) {
                if (predicate()) {
                    return true;
                }
                await Task.Delay(15);
            }
            return predicate();
        }

        [Test]
        public async Task A_download_installs_the_package_and_emits_complete() {
            var archive = TarGz(("catalog.dat", Encoding.UTF8.GetBytes("star-data")));
            var ws = new CapturingBroadcaster();
            var svc = NewService(new FakeSkyDataFetcher(archive), ws);

            var accepted = await svc.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: false), "idem-1", CancellationToken.None);
            Assert.That(accepted.OperationType, Is.EqualTo("data-manager.download"));

            var done = await Eventually(() => ws.Events.Any(e => e.EventType == WsEventCatalog.DataManagerDownloadComplete));
            Assert.That(done, Is.True, "a complete event is emitted");

            var installed = Path.Combine(_root, PackageId, "catalog.dat");
            Assert.That(File.Exists(installed), Is.True, "the package archive is extracted into the data root");
            Assert.That(await File.ReadAllTextAsync(installed), Is.EqualTo("star-data"));
            Assert.That(File.Exists(Path.Combine(_root, PackageId, ".installed")), Is.True, "the install sentinel is written");

            // The complete event carries the download id matching the accepted op.
            var complete = ws.Events.First(e => e.EventType == WsEventCatalog.DataManagerDownloadComplete).Payload;
            Assert.That(complete.GetProperty("download_id").GetString(), Is.EqualTo(accepted.OperationId.ToString()));
            Assert.That(complete.GetProperty("package_id").GetString(), Is.EqualTo(PackageId));
        }

        [Test]
        public async Task A_csv_gz_download_with_a_matching_digest_is_gunzipped_to_catalog_csv() {
            // Inject an expected digest equal to the served .csv.gz bytes — the worker verifies it, then the
            // single-file path gunzips it to the canonical catalog.csv.
            var gz = Gz(Encoding.UTF8.GetBytes("id,proper,ra,dec\n0,Sol,0,0\n"));
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(gz));
            var ws = new CapturingBroadcaster();
            var svc = new DataManagerService(_root, new FakeSkyDataFetcher(gz), ws,
                NullLogger<DataManagerService>.Instance, catalog: TestCatalog,
                expectedDigests: new Dictionary<string, string> { [CsvGzPackageId] = digest });

            await svc.DownloadAsync(new DownloadRequestDto(CsvGzPackageId, ForceReinstall: false), null, CancellationToken.None);

            var done = await Eventually(() => ws.Events.Any(e => e.EventType == WsEventCatalog.DataManagerDownloadComplete));
            Assert.That(done, Is.True, "a matching digest + csv.gz install completes");
            Assert.That(await File.ReadAllTextAsync(Path.Combine(_root, CsvGzPackageId, DataManagerService.CatalogFileName)),
                Is.EqualTo("id,proper,ra,dec\n0,Sol,0,0\n"), "the .csv.gz is gunzipped to catalog.csv");
        }

        [Test]
        public async Task A_download_whose_digest_does_not_match_fails_and_installs_nothing() {
            // Inject an expected digest the served bytes can't match, so the integrity check rejects the download
            // before it's committed — exercising the worker → installer digest wiring end-to-end.
            var gz = Gz(Encoding.UTF8.GetBytes("not the expected bytes\n"));
            var ws = new CapturingBroadcaster();
            var svc = new DataManagerService(_root, new FakeSkyDataFetcher(gz), ws,
                NullLogger<DataManagerService>.Instance, catalog: TestCatalog,
                expectedDigests: new Dictionary<string, string> { [CsvGzPackageId] = new string('0', 64) });

            await svc.DownloadAsync(new DownloadRequestDto(CsvGzPackageId, ForceReinstall: false), null, CancellationToken.None);

            var failed = await Eventually(() => ws.Events.Any(e => e.EventType == WsEventCatalog.DataManagerDownloadFailed));
            Assert.That(failed, Is.True, "a digest mismatch surfaces a failed event");
            Assert.That(Directory.Exists(Path.Combine(_root, CsvGzPackageId)), Is.False,
                "nothing is installed when the downloaded bytes don't match the expected digest");
        }

        [Test]
        public async Task ForceReinstall_false_skips_an_already_installed_package() {
            var archive = TarGz(("catalog.dat", Encoding.UTF8.GetBytes("data")));
            var ws = new CapturingBroadcaster();
            var svc = NewService(new FakeSkyDataFetcher(archive), ws);

            // First install writes the .installed sentinel.
            await svc.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: false), null, CancellationToken.None);
            await Eventually(() => ws.Events.Any(e => e.EventType == WsEventCatalog.DataManagerDownloadComplete));

            // A second non-force request is rejected (409) rather than silently re-downloading.
            Assert.That(
                async () => await svc.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: false), null, CancellationToken.None),
                Throws.InstanceOf<PackageAlreadyInstalledException>(), "a non-force re-download of an installed package is a 409");

            // ForceReinstall: true re-downloads.
            var forced = await svc.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: true), null, CancellationToken.None);
            Assert.That(forced.OperationType, Is.EqualTo("data-manager.download"), "forceReinstall=true re-downloads");
            // Let the re-download worker finish before teardown so it isn't writing under _root as it's deleted.
            await Eventually(() => svc.GetStateAsync(CancellationToken.None).Result.ActiveDownloads.Count == 0);
        }

        [Test]
        public async Task A_fresh_install_fetches_unconditionally_and_records_the_validator() {
            var lastMod = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
            var archive = TarGz(("catalog.dat", Encoding.UTF8.GetBytes("data")));
            var ws = new CapturingBroadcaster();
            var cond = new ConditionalFetcher(archive, lastMod);
            var svc = NewService(cond, ws);

            await svc.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: false), null, CancellationToken.None);
            await Eventually(() => ws.Events.Any(e => e.EventType == WsEventCatalog.DataManagerDownloadComplete));

            Assert.That(cond.LastIfModifiedSince, Is.Null, "a fresh install has no stored validator → unconditional GET");
            Assert.That(SkyDataInstaller.ReadRemoteLastModified(Path.Combine(_root, PackageId)), Is.EqualTo(lastMod),
                "the install records the remote Last-Modified for a later conditional GET");
        }

        [Test]
        public async Task A_force_reinstall_sends_the_stored_validator_and_a_304_keeps_the_install() {
            var lastMod = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
            var archive = TarGz(("catalog.dat", Encoding.UTF8.GetBytes("v1")));

            // First install records the remote validator in the sentinel.
            var ws1 = new CapturingBroadcaster();
            var svc1 = NewService(new ConditionalFetcher(archive, lastMod), ws1);
            await svc1.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: false), null, CancellationToken.None);
            await Eventually(() => ws1.Events.Any(e => e.EventType == WsEventCatalog.DataManagerDownloadComplete));

            // A force reinstall issues a conditional GET; the fake answers 304 Not Modified.
            var ws2 = new CapturingBroadcaster();
            var cond = ConditionalFetcher.NotModified();
            var svc2 = NewService(cond, ws2);
            await svc2.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: true), null, CancellationToken.None);
            await Eventually(() => ws2.Events.Any(e => e.EventType == WsEventCatalog.DataManagerDownloadComplete));

            Assert.That(cond.LastIfModifiedSince, Is.EqualTo(lastMod), "the stored validator drives the conditional GET");
            Assert.That(await File.ReadAllTextAsync(Path.Combine(_root, PackageId, "catalog.dat")), Is.EqualTo("v1"),
                "a 304 leaves the existing install untouched");
            await Eventually(() => svc2.GetStateAsync(CancellationToken.None).Result.ActiveDownloads.Count == 0);
        }

        [Test]
        public async Task A_failed_fetch_emits_failed_and_installs_nothing() {
            var ws = new CapturingBroadcaster();
            var fetcher = new FakeSkyDataFetcher(_ => throw new System.Net.Http.HttpRequestException("boom"));
            var svc = NewService(fetcher, ws);

            await svc.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: false), null, CancellationToken.None);

            var failed = await Eventually(() => ws.Events.Any(e => e.EventType == WsEventCatalog.DataManagerDownloadFailed));
            Assert.That(failed, Is.True, "a failed event is emitted when the fetch throws");
            Assert.That(Directory.Exists(Path.Combine(_root, PackageId)), Is.False, "no package dir is created on a failed fetch");

            // And the job is cleared from active state.
            var cleared = await Eventually(() => svc.GetStateAsync(CancellationToken.None).Result.ActiveDownloads.Count == 0);
            Assert.That(cleared, Is.True, "the failed download is removed from active state");
        }

        [Test]
        public async Task A_corrupt_archive_emits_failed_and_installs_nothing() {
            // Not a gzip stream — the installer throws while decoding; the worker must still report failed.
            var ws = new CapturingBroadcaster();
            var svc = NewService(new FakeSkyDataFetcher(Encoding.UTF8.GetBytes("definitely not a tar.gz")), ws);

            await svc.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: false), null, CancellationToken.None);

            var failed = await Eventually(() => ws.Events.Any(e => e.EventType == WsEventCatalog.DataManagerDownloadFailed));
            Assert.That(failed, Is.True, "a corrupt archive surfaces a failed event");
            Assert.That(Directory.Exists(Path.Combine(_root, PackageId)), Is.False, "nothing is installed from a corrupt archive");
            var cleared = await Eventually(() => svc.GetStateAsync(CancellationToken.None).Result.ActiveDownloads.Count == 0);
            Assert.That(cleared, Is.True, "the failed download is removed from active state");
        }

        [Test]
        public async Task A_slow_but_progressing_download_does_not_false_stall_with_a_short_idle_timeout() {
            // idleTimeout (400ms) < ProgressThrottleMs (500ms): incompressible bytes trickle in 16-byte chunks every
            // 40ms, so the whole transfer spans well past 400ms. The watchdog reset must be independent of the emit
            // throttle (which wouldn't open inside the deadline), or this would false-fire "stalled".
            var blob = new byte[512];
            System.Security.Cryptography.RandomNumberGenerator.Fill(blob); // high-entropy so gzip can't shrink it below the trickle window
            var archive = TarGz(("catalog.dat", blob));
            var ws = new CapturingBroadcaster();
            var svc = new DataManagerService(_root,
                new TricklingFetcher(archive, chunk: 16, gap: TimeSpan.FromMilliseconds(40)),
                ws, NullLogger<DataManagerService>.Instance, idleTimeout: TimeSpan.FromMilliseconds(400),
                catalog: TestCatalog, expectedDigests: NoDigests);

            await svc.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: false), null, CancellationToken.None);

            var done = await Eventually(() => ws.Events.Any(e => e.EventType == WsEventCatalog.DataManagerDownloadComplete), timeoutMs: 15000);
            Assert.That(done, Is.True, "a steadily-progressing download must not be killed by the idle watchdog");
            Assert.That(ws.Events.Any(e => e.EventType == WsEventCatalog.DataManagerDownloadFailed), Is.False, "no stall/failure event");
            Assert.That(File.Exists(Path.Combine(_root, PackageId, "catalog.dat")), Is.True, "the trickled package is installed");
        }

        [Test]
        public async Task A_stalled_header_wait_is_cancelled_by_the_idle_watchdog() {
            // OpenAsync never returns (no response headers) — the watchdog must bound the pre-fetch phase too.
            var ws = new CapturingBroadcaster();
            var svc = new DataManagerService(_root, new StallingHeaderFetcher(), ws,
                NullLogger<DataManagerService>.Instance, idleTimeout: TimeSpan.FromMilliseconds(150),
                catalog: TestCatalog, expectedDigests: NoDigests);

            await svc.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: false), null, CancellationToken.None);

            var stalled = await Eventually(() => ws.Events.Any(e =>
                e.EventType == WsEventCatalog.DataManagerDownloadFailed &&
                e.Payload.TryGetProperty("error", out var err) && err.GetString() == "stalled"));
            Assert.That(stalled, Is.True, "a fetch whose headers never arrive is cancelled as stalled");
        }

        [Test]
        public async Task State_reflects_an_in_flight_download() {
            // A fetch that blocks until released keeps the download in-flight so GetState can observe it.
            using var release = new SemaphoreSlim(0, 1);
            var archive = TarGz(("catalog.dat", Encoding.UTF8.GetBytes("data")));
            var fetcher = new FakeSkyDataFetcher(async ct => {
                await release.WaitAsync(ct);
                return archive;
            });
            var ws = new CapturingBroadcaster();
            var svc = NewService(fetcher, ws);

            var accepted = await svc.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: false), null, CancellationToken.None);

            var active = await Eventually(() => svc.GetStateAsync(CancellationToken.None).Result.ActiveDownloads
                .Any(d => d.DownloadId == accepted.OperationId && d.PackageId == PackageId));
            Assert.That(active, Is.True, "the in-flight download is listed in active state");

            release.Release();
            var done = await Eventually(() => svc.GetStateAsync(CancellationToken.None).Result.ActiveDownloads.Count == 0);
            Assert.That(done, Is.True, "active state drains once the download completes");
        }

        [Test]
        public async Task A_duplicate_request_for_an_in_flight_package_returns_the_same_id() {
            using var release = new SemaphoreSlim(0, 1);
            var archive = TarGz(("catalog.dat", Encoding.UTF8.GetBytes("data")));
            var fetcher = new FakeSkyDataFetcher(async ct => {
                await release.WaitAsync(ct);
                return archive;
            });
            var svc = NewService(fetcher, new CapturingBroadcaster());

            var first = await svc.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: false), null, CancellationToken.None);
            await Eventually(() => svc.GetStateAsync(CancellationToken.None).Result.ActiveDownloads.Count == 1);

            var second = await svc.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: false), null, CancellationToken.None);
            Assert.That(second.OperationId, Is.EqualTo(first.OperationId), "a duplicate request returns the in-flight download id");
            var state = await svc.GetStateAsync(CancellationToken.None);
            Assert.That(state.ActiveDownloads.Count, Is.EqualTo(1), "only one job is registered per package");

            release.Release();
            var drained = await Eventually(() => svc.GetStateAsync(CancellationToken.None).Result.ActiveDownloads.Count == 0);
            Assert.That(drained, Is.True);
        }

        [Test]
        public async Task Cancel_of_a_completed_download_returns_404() {
            var archive = TarGz(("catalog.dat", Encoding.UTF8.GetBytes("data")));
            var ws = new CapturingBroadcaster();
            var svc = NewService(new FakeSkyDataFetcher(archive), ws);

            var accepted = await svc.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: false), null, CancellationToken.None);
            await Eventually(() => ws.Events.Any(e => e.EventType == WsEventCatalog.DataManagerDownloadComplete));
            await Eventually(() => svc.GetStateAsync(CancellationToken.None).Result.ActiveDownloads.Count == 0);

            Assert.That(
                async () => await svc.CancelAsync(accepted.OperationId, CancellationToken.None),
                Throws.InstanceOf<DownloadNotFoundException>(), "cancelling a finished download is a 404, not a 202");
        }

        [Test]
        public async Task A_stalled_download_is_cancelled_by_the_idle_watchdog() {
            var ws = new CapturingBroadcaster();
            var svc = new DataManagerService(_root, new StallingFetcher(), ws,
                NullLogger<DataManagerService>.Instance, idleTimeout: TimeSpan.FromMilliseconds(150),
                catalog: TestCatalog, expectedDigests: NoDigests);

            await svc.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: false), null, CancellationToken.None);

            var stalled = await Eventually(() => ws.Events.Any(e =>
                e.EventType == WsEventCatalog.DataManagerDownloadFailed &&
                e.Payload.TryGetProperty("error", out var err) && err.GetString() == "stalled"));
            Assert.That(stalled, Is.True, "a transfer with no progress within the idle timeout is cancelled as stalled");
            Assert.That(Directory.Exists(Path.Combine(_root, PackageId)), Is.False, "a stalled download installs nothing");
        }

        [Test]
        public async Task Shutdown_drain_cancels_and_awaits_in_flight_downloads() {
            using var release = new SemaphoreSlim(0, 1);
            var archive = TarGz(("catalog.dat", Encoding.UTF8.GetBytes("data")));
            var fetcher = new FakeSkyDataFetcher(async ct => {
                await release.WaitAsync(ct); // never released — hangs until the drain cancels it.
                return archive;
            });
            var ws = new CapturingBroadcaster();
            var svc = NewService(fetcher, ws);

            await svc.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: false), null, CancellationToken.None);
            await Eventually(() => svc.GetStateAsync(CancellationToken.None).Result.ActiveDownloads.Count == 1);

            // §36-2b(b): a graceful stop cancels AND awaits the worker — by the time StopAsync
            // returns, the worker's own teardown has already run (event emitted, job reclaimed);
            // nothing is left running detached behind the daemon's exit.
            await ((IHostedService)svc).StopAsync(CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(ws.Events.Any(e =>
                    e.EventType == WsEventCatalog.DataManagerDownloadFailed &&
                    e.Payload.TryGetProperty("error", out var err) && err.GetString() == "cancelled"),
                    Is.True, "the drained worker reported its cancellation before StopAsync returned");
                Assert.That(svc.GetStateAsync(CancellationToken.None).Result.ActiveDownloads, Is.Empty);
                Assert.That(Directory.Exists(Path.Combine(_root, PackageId)), Is.False, "a drained download installs nothing");
            });
        }

        [Test]
        public async Task Shutdown_drain_with_nothing_in_flight_is_a_no_op() {
            var svc = NewService(new UnusedFetcher(), new CapturingBroadcaster());
            await ((IHostedService)svc).StopAsync(CancellationToken.None); // must not hang or throw
        }

        [Test]
        public void Cancel_of_an_unknown_download_throws_for_a_404() {
            var svc = NewService(new UnusedFetcher(), new CapturingBroadcaster());
            Assert.That(
                async () => await svc.CancelAsync(Guid.NewGuid(), CancellationToken.None),
                Throws.InstanceOf<DownloadNotFoundException>(), "an unknown download id is a 404, not a 202");
        }

        [Test]
        public async Task Cancel_stops_an_in_flight_download() {
            using var release = new SemaphoreSlim(0, 1);
            var archive = TarGz(("catalog.dat", Encoding.UTF8.GetBytes("data")));
            var fetcher = new FakeSkyDataFetcher(async ct => {
                await release.WaitAsync(ct); // never released — the download hangs here until cancelled.
                return archive;
            });
            var ws = new CapturingBroadcaster();
            var svc = NewService(fetcher, ws);

            var accepted = await svc.DownloadAsync(new DownloadRequestDto(PackageId, ForceReinstall: false), null, CancellationToken.None);
            await Eventually(() => svc.GetStateAsync(CancellationToken.None).Result.ActiveDownloads.Count == 1);

            var cancel = await svc.CancelAsync(accepted.OperationId, CancellationToken.None);
            Assert.That(cancel.OperationType, Is.EqualTo("data-manager.cancel"));

            var cancelled = await Eventually(() => ws.Events.Any(e =>
                e.EventType == WsEventCatalog.DataManagerDownloadFailed &&
                e.Payload.TryGetProperty("error", out var err) && err.GetString() == "cancelled"));
            Assert.That(cancelled, Is.True, "cancelling the token surfaces a cancelled failure event");
            Assert.That(Directory.Exists(Path.Combine(_root, PackageId)), Is.False, "a cancelled download installs nothing");
        }
    }
}
