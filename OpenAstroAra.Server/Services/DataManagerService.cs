#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §36 Data Manager — the real, disk-backed inventory + management layer (replaces the former
    /// placeholder). Tracks which curated sky-data packages (star catalogs, horizon profiles, and —
    /// once §36-2 lands the download engine — HiPS sky-survey imagery) are installed under the data
    /// root, reports their on-disk size, and deletes them.
    ///
    /// This is §36-1: <see cref="ListPackagesAsync"/> / <see cref="DeleteAsync"/> / <see cref="GetStateAsync"/>
    /// are real (read from + mutate the data root). <see cref="DownloadAsync"/> / <see cref="CancelAsync"/>
    /// are the acquisition half — the fetch-archive → extract → progress-WS → cancel engine lands in §36-2;
    /// for now they validate + acknowledge the request without downloading (tracked in PORT_TODO).
    ///
    /// Each package installs into <c>{dataRoot}/{packageId}/</c>. Only catalog ids map to a directory,
    /// so a caller-supplied <c>packageId</c> can never escape the data root (no path traversal).
    /// </summary>
    public sealed partial class DataManagerService : IDataManagerService {

        /// <summary>
        /// The curated set of installable §36 packages. Sizes/URLs are the catalog's advertised values;
        /// the installed size is measured from disk. (Real HiPS survey entries + hosting arrive with the
        /// §36-2 download engine.)
        /// </summary>
        internal static readonly IReadOnlyList<DataPackageDto> Catalog = new[] {
            new DataPackageDto(
                Id: "tycho-2",
                Name: "Tycho-2 star catalog",
                Description: "2.5M stars to mag 11 — plate-solve reference + §36.13 Sky Atlas overlay.",
                Category: "catalog",
                SizeBytes: 187_654_321,
                Version: "v2024.10",
                IsInstalled: false,
                InstalledUtc: null,
                SourceUrl: new Uri("https://data.openastro.net/tycho-2/2024.10.tar.gz")),
            new DataPackageDto(
                Id: "gaia-edr3-bright",
                Name: "Gaia EDR3 (mag ≤ 13)",
                Description: "Deeper plate-solve reference frame for long exposures; optional.",
                Category: "catalog",
                SizeBytes: 4_294_967_296,
                Version: "v2022",
                IsInstalled: false,
                InstalledUtc: null,
                SourceUrl: new Uri("https://data.openastro.net/gaia-edr3-bright/2022.tar.gz")),
            new DataPackageDto(
                Id: "horizon-default",
                Name: "Default 20° horizon profile",
                Description: "Flat 20° altitude horizon — sensible default; replace with a site survey in §37.12.",
                Category: "horizon",
                SizeBytes: 4_096,
                Version: "v1",
                IsInstalled: false,
                InstalledUtc: null,
                SourceUrl: new Uri("https://data.openastro.net/horizon-default/v1.tar.gz")),
        };

        // O(1) membership check used by the path-safety guard (the linear Catalog list is for ordered listing).
        private static readonly HashSet<string> CatalogIds =
            new(Catalog.Select(p => p.Id), StringComparer.Ordinal);

        // Don't flood the WS channel: a long download emits at most one progress event per this interval.
        private const long ProgressThrottleMs = 500;

        // Idle-progress watchdog: if no bytes arrive for this long the download is considered stalled and cancelled,
        // so a hung connection (the infinite HttpClient timeout can't catch it) doesn't pin a worker forever.
        private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

        private readonly string _dataRoot;
        private readonly ISkyDataFetcher _fetcher;
        private readonly IWsBroadcaster _ws;
        private readonly ILogger<DataManagerService> _logger;
        private readonly TimeSpan _idleTimeout;

        // In-flight downloads keyed by download id, plus a one-active-download-per-package guard.
        private readonly ConcurrentDictionary<Guid, DownloadJob> _downloads = new();
        private readonly ConcurrentDictionary<string, Guid> _activeByPackage = new(StringComparer.Ordinal);

        public DataManagerService(string dataRootPath, ISkyDataFetcher fetcher, IWsBroadcaster ws,
            ILogger<DataManagerService> logger, TimeSpan? idleTimeout = null) {
            _dataRoot = dataRootPath ?? throw new ArgumentNullException(nameof(dataRootPath));
            _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
            _ws = ws ?? throw new ArgumentNullException(nameof(ws));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        }

        public Task<IReadOnlyList<DataPackageDto>> ListPackagesAsync(CancellationToken ct) {
            var packages = new List<DataPackageDto>(Catalog.Count);
            foreach (var pkg in Catalog) {
                packages.Add(Describe(pkg));
            }
            return Task.FromResult<IReadOnlyList<DataPackageDto>>(packages);
        }

        public Task<DataManagerStateDto> GetStateAsync(CancellationToken ct) {
            var installed = 0;
            long total = 0;
            foreach (var pkg in Catalog) {
                var dir = PackageDir(pkg.Id);
                if (dir is not null && Measure(dir) is { } m) {
                    installed++;
                    total += m.Size;
                }
            }
            // Filter !Completed: a worker that has reached its finally (set Completed) but not yet removed itself
            // from _downloads must not surface as an active download for the brief window in between.
            var active = _downloads.Values.Where(j => !j.Completed).Select(SnapshotActive).ToList();
            return Task.FromResult(new DataManagerStateDto(
                InstalledPackageCount: installed,
                TotalInstalledBytes: total,
                ActiveDownloads: active,
                LastSyncUtc: null));
        }

        public Task<bool> DeleteAsync(string packageId, CancellationToken ct) {
            var dir = PackageDir(packageId);
            if (dir is null || !Directory.Exists(dir)) {
                return Task.FromResult(false); // unknown id or not installed — nothing to free.
            }
            try {
                Directory.Delete(dir, recursive: true);
                LogDeleted(packageId);
                return Task.FromResult(true);
            } catch (DirectoryNotFoundException) {
                return Task.FromResult(false); // raced with another delete — already gone.
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // A locked/permission-denied delete is reported as "not deleted" rather than thrown —
                // the client retries; the daemon must not 500 on a best-effort disk reclaim.
                LogDeleteFailed(packageId, ex);
                return Task.FromResult(false);
            }
        }

        public Task<OperationAcceptedDto> DownloadAsync(DownloadRequestDto request, string? idempotencyKey, CancellationToken ct) {
            ArgumentNullException.ThrowIfNull(request);
            // Validate against the catalog up front. Return a faulted Task rather than throwing synchronously —
            // this is a Task-returning (non-async) method, so a bare throw would surface at call time, before any
            // await, violating the TAP contract (the endpoint maps the fault to a 404).
            if (!CatalogIds.Contains(request.PackageId)) {
                return Task.FromException<OperationAcceptedDto>(PackageNotFoundException.ForPackageId(request.PackageId));
            }
            // NOTE: request.ForceReinstall is not yet honored — a download always runs (i.e. behaves as force).
            // Short-circuiting on "already installed" needs the sentinel-aware inventory that lands in §36-2b-2
            // (a dir is only "installed" once its .installed sentinel exists), so the skip-if-installed path is
            // wired there rather than guessing from a bare directory here. Tracked in PORT_TODO.
            var pkg = Catalog.First(p => p.Id == request.PackageId);
            if (pkg.SourceUrl is null) {
                // A catalog entry without a source URL is a server-config invariant violation (every curated entry
                // has one), not a client "unknown package" — surface it as a 500, not a misleading 404.
                return Task.FromException<OperationAcceptedDto>(
                    new InvalidOperationException($"Catalog entry '{request.PackageId}' has no source URL."));
            }

            // One active download per package: if another is already running, return its id (idempotent) instead
            // of racing two writers into the same install directory. TryAdd is the atomic claim; the loop closes a
            // race where an existing job finishes (and removes its entry) between our failed TryAdd and the lookup —
            // we'd otherwise hand back an id that was never registered. Re-attempting the claim resolves it.
            while (true) {
                var downloadId = Guid.NewGuid();
                if (_activeByPackage.TryAdd(pkg.Id, downloadId)) {
                    var job = new DownloadJob(downloadId, pkg.Id);
                    _downloads[downloadId] = job;
                    // Fire the worker on the thread pool; it owns the job's lifecycle + cleanup.
                    _ = Task.Run(() => RunDownloadAsync(job, pkg), CancellationToken.None);
                    LogDownloadStarted(pkg.Id, downloadId);
                    return Task.FromResult(Accepted("data-manager.download", idempotencyKey, downloadId));
                }
                if (_activeByPackage.TryGetValue(pkg.Id, out var existing)) {
                    return Task.FromResult(Accepted("data-manager.download", idempotencyKey, existing));
                }
                // The other job vanished in the window — loop and re-claim.
            }
        }

        public Task<OperationAcceptedDto> CancelAsync(Guid downloadId, CancellationToken ct) {
            // !Completed so a download whose worker has reached its finally (done, just not yet removed from
            // _downloads) reports 404 "nothing to cancel" rather than a misleading 202.
            if (_downloads.TryGetValue(downloadId, out var job) && !job.Completed) {
                try {
                    job.Cts.Cancel();
                } catch (ObjectDisposedException) {
                    // The worker finished and disposed its CTS between our lookup and here — the download is already
                    // over, so there's nothing to cancel. Report Accepted rather than 500 on a benign race.
                }
                LogCancelRequested(downloadId);
                return Task.FromResult(Accepted("data-manager.cancel", null, downloadId));
            }
            // Unknown or already-finished id — surface a 404 (mapped at the endpoint) rather than a misleading 202.
            return Task.FromException<OperationAcceptedDto>(
                new DownloadNotFoundException($"No active download '{downloadId}'."));
        }

        // The download worker: fetch the package archive, stream it through the §36-2a installer (which extracts +
        // atomically swaps it into place), reporting progress over the WS channel, and always clean up the registry.
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Top-level background worker: every failure must surface as a download.failed WS event, and an unobserved exception here would otherwise be lost. It is reported and logged, not swallowed silently.")]
        private async Task RunDownloadAsync(DownloadJob job, DataPackageDto pkg) {
            // Idle watchdog: idleCts fires if no bytes arrive within _idleTimeout; the install runs under a token
            // linked to both the user-cancel CTS and the idle CTS, so a stalled transfer is cancelled, not hung.
            using var idleCts = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cts.Token, idleCts.Token);
            var ct = linked.Token;
            try {
                await EmitAsync(WsEventCatalog.DataManagerDownloadProgress, job, error: null).ConfigureAwait(false);

                await using var fetch = await _fetcher.OpenAsync(pkg.SourceUrl!, ct).ConfigureAwait(false);
                Volatile.Write(ref job.TotalBytes, fetch.TotalBytes ?? -1);

                idleCts.CancelAfter(_idleTimeout); // arm once headers are in; reset below on each byte of progress.
                var targetDir = PackageDir(pkg.Id)!; // pkg came from the catalog, so this is non-null + in-root.
                await using var counting = new CountingStream(fetch.Content, read => {
                    Volatile.Write(ref job.DownloadedBytes, read);
                    // Reset the stall deadline + emit progress on the SAME throttle tick — avoids rescheduling the
                    // idle timer on every read chunk (the throttle already gates "enough time has passed").
                    if (MaybeEmitProgress(job)) {
                        idleCts.CancelAfter(_idleTimeout);
                    }
                });
                await SkyDataInstaller.InstallFromTarGzAsync(counting, targetDir, ExtractionCeiling(pkg), ct).ConfigureAwait(false);

                await EmitAsync(WsEventCatalog.DataManagerDownloadComplete, job, error: null).ConfigureAwait(false);
                LogDownloadComplete(pkg.Id, job.Id);
            } catch (OperationCanceledException) {
                // Distinguish a user cancel (job.Cts) from the idle watchdog firing (idleCts) — only the idle CTS
                // fired means the transfer stalled.
                var stalled = idleCts.IsCancellationRequested && !job.Cts.IsCancellationRequested;
                await EmitAsync(WsEventCatalog.DataManagerDownloadFailed, job, error: stalled ? "stalled" : "cancelled").ConfigureAwait(false);
                if (stalled) {
                    LogDownloadStalled(pkg.Id, job.Id);
                } else {
                    LogDownloadCancelled(pkg.Id, job.Id);
                }
            } catch (SkyDataInstallException ex) {
                // The install failed AND the prior install couldn't be restored — genuine data loss. Surface it
                // distinctly (error string + an error log) rather than as an ordinary failed download.
                await EmitAsync(WsEventCatalog.DataManagerDownloadFailed, job, error: "install failed; prior data may be lost").ConfigureAwait(false);
                LogDownloadDataLoss(pkg.Id, ex);
            } catch (Exception ex) {
                // Catch-all so an unexpected failure (UnauthorizedAccessException, ObjectDisposedException, …) still
                // reports a download.failed event rather than silently vanishing from ActiveDownloads.
                await EmitAsync(WsEventCatalog.DataManagerDownloadFailed, job, error: ex.Message).ConfigureAwait(false);
                LogDownloadFailed(pkg.Id, ex);
            } finally {
                // Mark done BEFORE any removal so a concurrent CancelAsync that still finds the job in _downloads
                // sees it's finished and 404s rather than returning a misleading 202 for a completed download.
                job.Completed = true;
                // Release the package claim BEFORE the registry entry. In the window between the two, a concurrent
                // same-package DownloadAsync then finds no claim and starts a fresh download, rather than seeing the
                // stale claim, looking up _downloads, and handing back this now-finished job's (dead) id.
                _activeByPackage.TryRemove(new KeyValuePair<string, Guid>(job.PackageId, job.Id));
                _downloads.TryRemove(job.Id, out _);
                job.Dispose();
            }
        }

        // Cap uncompressed extraction at the catalog's advertised installed size + 25% headroom (never below 16 MiB),
        // so a tampered archive can't expand without bound even though the package id is catalog-validated. Guard the
        // addition against long overflow (a corrupt near-MaxValue size would otherwise wrap negative and disable the cap).
        private static long ExtractionCeiling(DataPackageDto pkg) {
            var size = Math.Max(0, pkg.SizeBytes);
            var headroom = size / 4;
            var ceiling = size > long.MaxValue - headroom ? long.MaxValue : size + headroom;
            return Math.Max(ceiling, 16L * 1024 * 1024);
        }

        private static DataManagerActiveDownloadDto SnapshotActive(DownloadJob job) {
            var downloaded = Volatile.Read(ref job.DownloadedBytes);
            var total = Volatile.Read(ref job.TotalBytes);
            return new DataManagerActiveDownloadDto(job.Id, job.PackageId, downloaded, total, Percent(downloaded, total));
        }

        // Throttled, fire-and-forget progress emit driven by the stream read callback (which runs on the worker).
        // Returns true when this call passed the throttle gate (and thus emitted) — the caller piggybacks the idle
        // watchdog reset on the same gate.
        private bool MaybeEmitProgress(DownloadJob job) {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref job.LastEmitTick);
            if (now - last < ProgressThrottleMs) {
                return false;
            }
            // CAS so concurrent reads don't both fire for the same window (Read here is single-threaded today, but
            // this keeps the throttle correct if the stream is ever pumped from more than one thread).
            if (Interlocked.CompareExchange(ref job.LastEmitTick, now, last) != last) {
                return false;
            }
            _ = EmitAsync(WsEventCatalog.DataManagerDownloadProgress, job, error: null);
            return true;
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Best-effort WS emit, also called fire-and-forget: every failure (incl. an incidental OperationCanceledException from a broadcaster) is logged and swallowed so it can neither fail the download nor surface as an unobserved task exception.")]
        private async Task EmitAsync(string eventType, DownloadJob job, string? error) {
            // Whole body in the try: this is also called fire-and-forget from MaybeEmitProgress, so even the payload
            // build must not throw an unobserved exception. Publish is best-effort — a WS hiccup must not fail the download.
            try {
                var downloaded = Volatile.Read(ref job.DownloadedBytes);
                var total = Volatile.Read(ref job.TotalBytes);
                var payload = new JsonObject {
                    ["download_id"] = job.Id.ToString(),
                    ["package_id"] = job.PackageId,
                    ["downloaded_bytes"] = downloaded,
                    ["total_bytes"] = total,
                    ["percent_complete"] = Percent(downloaded, total),
                };
                if (error is not null) {
                    payload["error"] = error;
                }
                // ToJsonString()+Parse is the AOT-safe way to a JsonElement (SerializeToElement takes the reflection
                // path the warnings=errors gate rejects).
                using var doc = JsonDocument.Parse(payload.ToJsonString());
                await _ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
            } catch (Exception ex) {
                // Catch-all incl. OperationCanceledException: this runs fire-and-forget, so an OCE leaking here would
                // fault an unobserved task. Log + swallow — a WS publish failure must never affect the download.
                LogPublishFailed(eventType, ex);
            }
        }

        private static double Percent(long downloaded, long total) =>
            total > 0 ? Math.Round(Math.Clamp((double)downloaded / total, 0, 1) * 100, 2) : 0;

        // Reflect the on-disk state of a catalog package. Measure() folds the existence check into the
        // size/time read so a concurrent DeleteAsync between "exists?" and "measure" can't throw — it just
        // reads as not-installed.
        private DataPackageDto Describe(DataPackageDto pkg) {
            var dir = PackageDir(pkg.Id);
            if (dir is null || Measure(dir) is not { } m) {
                return pkg with { IsInstalled = false, InstalledUtc = null };
            }
            return pkg with { IsInstalled = true, InstalledUtc = m.LastWriteUtc, SizeBytes = m.Size };
        }

        // Per-package directory — ONLY for a known catalog id, so a caller-supplied packageId (Delete,
        // Download) can never traverse out of the data root.
        private string? PackageDir(string packageId) =>
            CatalogIds.Contains(packageId) ? Path.Combine(_dataRoot, packageId) : null;

        // Measure an installed package dir, or null if it isn't there (incl. a delete that raced this read).
        // NOTE: O(files) per call and re-walked by both ListPackages + GetState — fine for the small catalog;
        // §36-2 can cache the size at install time if the catalog grows large.
        private static (long Size, DateTimeOffset LastWriteUtc)? Measure(string dir) {
            try {
                var info = new DirectoryInfo(dir);
                if (!info.Exists) {
                    return null;
                }
                var size = info.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                return (size, info.LastWriteTimeUtc);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // IOException (incl. its DirectoryNotFoundException subclass — a delete that raced this
                // read mid-enumeration) or a restricted / device-faulted child dir: read as not-installed
                // rather than 500 every ListPackages/GetState. DeleteAsync catches the same family.
                return null;
            }
        }

        private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey, Guid operationId) =>
            new(OperationId: operationId,
                OperationType: operationType,
                AcceptedUtc: DateTimeOffset.UtcNow,
                IdempotencyKey: idempotencyKey);

        // One in-flight download: the cancellation source the worker observes plus thread-shared progress counters
        // (read by GetStateAsync from another thread, hence Volatile/Interlocked access).
        private sealed class DownloadJob : IDisposable {
            public DownloadJob(Guid id, string packageId) {
                Id = id;
                PackageId = packageId;
            }

            public Guid Id { get; }
            public string PackageId { get; }
            public CancellationTokenSource Cts { get; } = new();
            public long DownloadedBytes;
            public long TotalBytes = -1;
            public long LastEmitTick;
            public volatile bool Completed;

            public void Dispose() => Cts.Dispose();
        }

        // Read-through stream that reports cumulative bytes read; a leaveOpen wrapper that does NOT own the inner
        // stream — the SkyDataFetch the worker disposes owns it.
        [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
            Justification = "Borrowing wrapper: the wrapped stream is owned and disposed by the SkyDataFetch, like a leaveOpen GZipStream.")]
        private sealed class CountingStream : Stream {
            private readonly Stream _inner;
            private readonly Action<long> _onRead;
            private long _total;

            public CountingStream(Stream inner, Action<long> onRead) {
                _inner = inner;
                _onRead = onRead;
            }

            private int Count(int read) {
                if (read > 0) {
                    _onRead(Interlocked.Add(ref _total, read));
                }
                return read;
            }

            public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));

            public override int Read(Span<byte> buffer) => Count(_inner.Read(buffer));

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                Count(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

            // Modern .NET routes the legacy array overload through ReadAsync(Memory<byte>); the explicit override
            // keeps the count correct even if a consumer calls this form directly.
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                Count(await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => Interlocked.Read(ref _total); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Data package '{PackageId}' deleted from the data root")]
        partial void LogDeleted(string packageId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Data package '{PackageId}' could not be deleted")]
        partial void LogDeleteFailed(string packageId, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Data package '{PackageId}' download {DownloadId} started")]
        partial void LogDownloadStarted(string packageId, Guid downloadId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Data package '{PackageId}' download {DownloadId} complete")]
        partial void LogDownloadComplete(string packageId, Guid downloadId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Data package '{PackageId}' download {DownloadId} cancelled")]
        partial void LogDownloadCancelled(string packageId, Guid downloadId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Data package '{PackageId}' download {DownloadId} stalled (no progress within the idle timeout)")]
        partial void LogDownloadStalled(string packageId, Guid downloadId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Data package '{PackageId}' download failed")]
        partial void LogDownloadFailed(string packageId, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Data package '{PackageId}' install failed AND prior data could not be restored — possible data loss")]
        partial void LogDownloadDataLoss(string packageId, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Data download cancel requested for {DownloadId}")]
        partial void LogCancelRequested(Guid downloadId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Data manager WS publish of '{EventType}' failed")]
        partial void LogPublishFailed(string eventType, Exception ex);
    }

    /// <summary>Thrown by <see cref="DataManagerService.DownloadAsync"/> when the requested package id is not in the
    /// curated catalog (incl. a path-traversal attempt). The endpoint maps it to a 404. A dedicated type keeps the
    /// "unknown package" semantic from being conflated with an unrelated <see cref="KeyNotFoundException"/> that a
    /// future dictionary lookup inside the §36-2 download engine might throw.</summary>
    public sealed class PackageNotFoundException : Exception {
        public PackageNotFoundException() { }
        public PackageNotFoundException(string message) : base(message) { }
        public PackageNotFoundException(string message, Exception innerException) : base(message, innerException) { }
        public static PackageNotFoundException ForPackageId(string packageId) =>
            new($"Unknown data package '{packageId}'.");
    }

    /// <summary>Thrown by <see cref="DataManagerService.CancelAsync"/> when the download id has no in-flight job
    /// (unknown, or already finished). The endpoint maps it to a 404, so a cancel of a completed download is an
    /// honest "nothing to cancel" rather than a misleading 202.</summary>
    public sealed class DownloadNotFoundException : Exception {
        public DownloadNotFoundException() { }
        public DownloadNotFoundException(string message) : base(message) { }
        public DownloadNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }
}
