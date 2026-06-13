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
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
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
            if (Directory.Exists(_root)) {
                Directory.Delete(_root, recursive: true);
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

        // The first catalog package id (tycho-2) — used so the download path resolves a real SourceUrl + target.
        private static string PackageId => DataManagerService.Catalog[0].Id;

        private DataManagerService NewService(ISkyDataFetcher fetcher, CapturingBroadcaster ws) =>
            new(_root, fetcher, ws, NullLogger<DataManagerService>.Instance);

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
        public async Task Cancel_of_an_unknown_download_throws_for_a_404() {
            var svc = NewService(new UnusedFetcher(), new CapturingBroadcaster());
            Assert.That(
                async () => await svc.CancelAsync(Guid.NewGuid(), CancellationToken.None),
                Throws.InstanceOf<DownloadNotFoundException>(), "an unknown download id is a 404, not a 202");
            await Task.CompletedTask;
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
