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
    /// §36 Data Manager — the real, disk-backed inventory + acquisition layer. Tracks which curated sky-data
    /// packages (star + deep-sky catalogs, horizon profiles) are installed under the data root, reports their
    /// on-disk size, downloads + installs them, and deletes them.
    ///
    /// All of <see cref="ListPackagesAsync"/> / <see cref="DeleteAsync"/> / <see cref="GetStateAsync"/> (the
    /// inventory half, §36-1) and <see cref="DownloadAsync"/> / <see cref="CancelAsync"/> (the acquisition half,
    /// §36-2) are real: a download streams the package from its source URL, installs it atomically (stage → swap →
    /// rollback-on-failure via <see cref="SkyDataInstaller"/>) under a size cap, and reports progress + completion
    /// over the <c>data-manager.*</c> WS stream; a cancel tears down the in-flight transfer. Both <c>.tar.gz</c>
    /// archives and bare <c>.csv</c>/<c>.csv.gz</c> catalog files are supported.
    ///
    /// Each package installs into <c>{dataRoot}/{packageId}/</c>. Only catalog ids map to a directory,
    /// so a caller-supplied <c>packageId</c> can never escape the data root (no path traversal).
    /// </summary>
    public sealed partial class DataManagerService : IDataManagerService {

        /// <summary>
        /// The canonical file name a bare-CSV catalog package installs as, so the §36 catalog consumer can read
        /// <c>{packageDir}/catalog.csv</c> regardless of the upstream file name (.csv or .csv.gz).
        /// </summary>
        internal const string CatalogFileName = "catalog.csv";

        /// <summary>
        /// The curated set of installable §36 packages. Sizes/URLs are the catalog's advertised values;
        /// the installed size is measured from disk. Catalog star/DSO sources are commit-pinned upstream
        /// (HYG, OpenNGC) — immutable while the repo exists; a self-hosted snapshot is the eventual robust home
        /// (tracked in PORT_TODO).
        /// </summary>
        internal static readonly IReadOnlyList<DataPackageDto> Catalog = new[] {
            new DataPackageDto(
                Id: "hyg-stars",
                Name: "HYG star catalog (named stars)",
                // ~120k stars with proper names (Hipparcos + Yale + Gliese), the source for the §36 Sky Atlas
                // star-label overlay. Downloaded as a 13.6 MB .csv.gz; SizeBytes is the uncompressed on-disk footprint
                // (the extraction cap is taken from it). Commit-pinned to the archived upstream repo so the URL is
                // immutable. License: CC BY-SA (Astronomy Nexus) — see NOTICE.md.
                // DELIMITER: comma-separated. NOTE for the future catalog consumer: delimiters differ per package —
                // this is a real CSV, but OpenNGC below is SEMICOLON-separated, so don't assume one delimiter.
                Description: "≈120,000 stars with proper names (Hipparcos/Yale/Gliese) — the Sky Atlas star overlay.",
                Category: "catalog",
                // Measured decompressed size of hygdata_v40.csv (curl | gunzip | wc -c). This is the extraction
                // ceiling (×1.25 ≈ 42 MB), so it must be ≥ the real on-disk size or the install would size-cap-abort.
                SizeBytes: 33_932_465,
                Version: "v40",
                IsInstalled: false,
                InstalledUtc: null,
                SourceUrl: new Uri("https://raw.githubusercontent.com/astronexus/HYG-Database/c7f7f883fe678cc7680169a50ccd7dcc49b060ce/hyg/CURRENT/hygdata_v40.csv.gz")),
            new DataPackageDto(
                Id: "openngc-dso",
                Name: "OpenNGC deep-sky objects",
                // The NGC/IC deep-sky catalog (galaxies, nebulae, clusters) for the §36 DSO overlay. A bare semicolon-
                // separated CSV (no archive). Commit-pinned upstream URL. License: CC BY-SA 4.0 — see NOTICE.md.
                Description: "NGC/IC deep-sky catalog — galaxies, nebulae, clusters (the Sky Atlas DSO overlay).",
                Category: "catalog",
                SizeBytes: 3_876_288,
                Version: "git-36cb178",
                IsInstalled: false,
                InstalledUtc: null,
                SourceUrl: new Uri("https://raw.githubusercontent.com/mattiaverga/OpenNGC/36cb178a0f69dba8bfc03a99c10512831edf1c6b/database_files/NGC.csv")),
            // NOTE: the former "horizon-default" entry was removed — it pointed at the dead data.openastro.net host
            // and was miscategorised as a download. A site horizon (flat default or survey) is generated LOCALLY for
            // the §36 Tonight's-Sky overlay, not fetched via the Data Manager; the real horizon feature re-adds it
            // there. Tracked in PORT_TODO.
        };

        // Expected SHA-256 (hex) of each package's downloaded artifact (the raw bytes at SourceUrl — the .csv.gz for
        // hyg-stars, the .csv for openngc-dso), so a corrupted/wrong download is rejected before it replaces a good
        // install (see SkyDataInstaller's verify-before-swap). Measured from the commit-pinned upstream files; kept
        // internal (an install-integrity detail, not part of the wire DTO). Packages without an entry skip the check.
        internal static readonly IReadOnlyDictionary<string, string> CatalogSha256 =
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["hyg-stars"] = "8e3ff9e67445e558a759b117910850cff1b1d4d492f45f715c2ee2db3d869bac",
                ["openngc-dso"] = "840fe0c9ee1332e551b2e722a0e92726cd7b157914a3d2177602832aadd3aa9e",
            };

        /// <summary>A `.tar.gz`/`.tgz` archive package — installed via the tar-extraction path.</summary>
        internal static bool IsArchiveFormat(Uri url) =>
            url.AbsolutePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            url.AbsolutePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

        /// <summary>A bare `.csv`/`.csv.gz` catalog file — installed via the single-file path.</summary>
        internal static bool IsBareCatalogFormat(Uri url) =>
            url.AbsolutePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
            url.AbsolutePath.EndsWith(".csv.gz", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether the download worker can install this package's source format (else it fails fast).</summary>
        internal static bool IsSupportedDownloadFormat(Uri url) => IsArchiveFormat(url) || IsBareCatalogFormat(url);

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
        // The package set this instance serves + the per-package expected download digests. Default to the production
        // Catalog/CatalogSha256; injectable so tests can drive the worker with synthetic packages + controlled
        // integrity policy without depending on the production catalog's exact contents.
        private readonly IReadOnlyList<DataPackageDto> _catalog;
        private readonly HashSet<string> _catalogIds;
        private readonly IReadOnlyDictionary<string, string> _expectedDigests;

        // In-flight downloads keyed by download id, plus a one-active-download-per-package guard.
        private readonly ConcurrentDictionary<Guid, DownloadJob> _downloads = new();
        private readonly ConcurrentDictionary<string, Guid> _activeByPackage = new(StringComparer.Ordinal);

        public DataManagerService(string dataRootPath, ISkyDataFetcher fetcher, IWsBroadcaster ws,
            ILogger<DataManagerService> logger, TimeSpan? idleTimeout = null,
            IReadOnlyList<DataPackageDto>? catalog = null,
            IReadOnlyDictionary<string, string>? expectedDigests = null) {
            _dataRoot = dataRootPath ?? throw new ArgumentNullException(nameof(dataRootPath));
            _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
            _ws = ws ?? throw new ArgumentNullException(nameof(ws));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
            _catalog = catalog ?? Catalog;
            _catalogIds = new HashSet<string>(_catalog.Select(p => p.Id), StringComparer.Ordinal);
            _expectedDigests = expectedDigests ?? CatalogSha256;
        }

        public Task<IReadOnlyList<DataPackageDto>> ListPackagesAsync(CancellationToken ct) {
            var packages = new List<DataPackageDto>(_catalog.Count);
            foreach (var pkg in _catalog) {
                packages.Add(Describe(pkg));
            }
            return Task.FromResult<IReadOnlyList<DataPackageDto>>(packages);
        }

        public Task<DataManagerStateDto> GetStateAsync(CancellationToken ct) {
            var installed = 0;
            long total = 0;
            foreach (var pkg in _catalog) {
                var dir = PackageDir(pkg.Id);
                if (dir is not null && ReadInstall(dir) is { } info) {
                    installed++;
                    total += info.Size;
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

        public Task<IReadOnlyList<CatalogObjectDto>?> ReadCatalogAsync(string packageId, double? maxMag, int? limit,
                CancellationToken ct) {
            if (!SkyCatalogReader.HasParser(packageId)) {
                return Task.FromResult<IReadOnlyList<CatalogObjectDto>?>(null); // not a parseable catalog package
            }
            var dir = PackageDir(packageId);
            if (dir is null || ReadInstall(dir) is null) {
                return Task.FromResult<IReadOnlyList<CatalogObjectDto>?>(null); // unknown id, or not installed
            }
            var path = Path.Combine(dir, CatalogFileName);
            // Enforce a server-side hard cap (a caller's limit can only reduce it, never exceed it) so a no-limit
            // request for a large catalog can't force an unbounded response / heap spike.
            var effectiveLimit = limit is { } l ? Math.Min(l, SkyCatalogReader.MaxObjects) : SkyCatalogReader.MaxObjects;
            // Parse off the request thread — a full catalog can be tens of MB, and the read+parse is CPU + blocking I/O.
            // The open lives inside the lambda (not a File.Exists pre-check) so a missing or concurrently-deleted
            // catalog.csv returns null → 404, rather than throwing across the await into a 500.
            return Task.Run<IReadOnlyList<CatalogObjectDto>?>(() => {
                try {
                    using var stream = File.OpenRead(path);
                    return SkyCatalogReader.Read(packageId, stream, maxMag, effectiveLimit, ct);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // Any file-read failure — missing/removed-mid-read catalog.csv, a permission error, a mid-stream
                    // I/O fault — maps to null → 404 ("catalog unavailable"), never an unhandled 500. (FileNotFound /
                    // DirectoryNotFound are IOException subclasses, so they're covered too.)
                    LogCatalogReadFailed(packageId, ex);
                    return null;
                }
            }, ct);
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
            if (ct.IsCancellationRequested) {
                return Task.FromCanceled<OperationAcceptedDto>(ct); // honor a request cancelled before we start.
            }
            // Validate against the catalog in a single pass. Return a faulted Task rather than throwing synchronously —
            // this is a Task-returning (non-async) method, so a bare throw would surface at call time, before any
            // await, violating the TAP contract (the endpoint maps the fault to a 404).
            var pkg = _catalog.FirstOrDefault(p => p.Id == request.PackageId);
            if (pkg is null) {
                return Task.FromException<OperationAcceptedDto>(PackageNotFoundException.ForPackageId(request.PackageId));
            }
            if (pkg.SourceUrl is null) {
                // A catalog entry without a source URL is a server-config invariant violation (every curated entry
                // has one), not a client "unknown package" — surface it as a 500, not a misleading 404.
                return Task.FromException<OperationAcceptedDto>(
                    new InvalidOperationException($"Catalog entry '{request.PackageId}' has no source URL."));
            }
            // Honor ForceReinstall: a non-force request for a package that's already fully installed (its .installed
            // sentinel exists) is a no-op — surfaced as a 409 so the caller knows it didn't re-download, rather than
            // silently re-fetching a multi-GB package. ForceReinstall=true skips this and re-downloads.
            if (!request.ForceReinstall && IsInstalled(pkg.Id)) {
                return Task.FromException<OperationAcceptedDto>(PackageAlreadyInstalledException.ForPackageId(pkg.Id));
            }

            // One active download per package: if another is already running, return its id (idempotent) instead
            // of racing two writers into the same install directory. TryAdd is the atomic claim; the loop closes a
            // race where an existing job finishes (and removes its entry) between our failed TryAdd and the lookup —
            // we'd otherwise hand back an id that was never registered. Re-attempting the claim resolves it.
            while (true) {
                var downloadId = Guid.NewGuid();
                if (_activeByPackage.TryAdd(pkg.Id, downloadId)) {
                    // Re-check under the claim: a concurrent download may have finished installing the package in the
                    // IsInstalled→TryAdd window. If so, release the claim and report already-installed rather than
                    // spuriously re-downloading what's now on disk.
                    if (!request.ForceReinstall && IsInstalled(pkg.Id)) {
                        _activeByPackage.TryRemove(new KeyValuePair<string, Guid>(pkg.Id, downloadId));
                        return Task.FromException<OperationAcceptedDto>(PackageAlreadyInstalledException.ForPackageId(pkg.Id));
                    }
                    var job = new DownloadJob(downloadId, pkg.Id);
                    _downloads[downloadId] = job;
                    // Fire the worker on the thread pool; it owns the job's lifecycle + cleanup.
                    _ = Task.Run(() => RunDownloadAsync(job, pkg), CancellationToken.None);
                    LogDownloadStarted(pkg.Id, downloadId);
                    return Task.FromResult(Accepted("data-manager.download", idempotencyKey, downloadId));
                }
                if (_activeByPackage.TryGetValue(pkg.Id, out var existing)) {
                    // A download for this package is already running — join it (return its id) regardless of the
                    // ForceReinstall flag: the in-flight job installs the same package, so a concurrent force request
                    // would be redundant. If that job fails, the caller can resubmit with force.
                    return Task.FromResult(Accepted("data-manager.download", idempotencyKey, existing));
                }
                // The other job vanished in the window — loop and re-claim.
            }
        }

        public Task<OperationAcceptedDto> CancelAsync(Guid downloadId, CancellationToken ct) {
            if (ct.IsCancellationRequested) {
                return Task.FromCanceled<OperationAcceptedDto>(ct);
            }
            // !Completed so a download whose worker has reached its finally (done, just not yet removed from
            // _downloads) reports 404 "nothing to cancel" rather than a misleading 202.
            if (_downloads.TryGetValue(downloadId, out var job) && !job.Completed) {
                try {
                    job.Cts.Cancel();
                } catch (ObjectDisposedException) {
                    // The worker finished and disposed its CTS between our check and here — the download is already
                    // over, so it's "nothing to cancel": a 404, consistent with the unknown-id contract (not a 202
                    // that would imply we interrupted something).
                    return Task.FromException<OperationAcceptedDto>(NoActiveDownload(downloadId));
                }
                LogCancelRequested(downloadId);
                return Task.FromResult(Accepted("data-manager.cancel", null, downloadId));
            }
            // Unknown or already-finished id — surface a 404 (mapped at the endpoint) rather than a misleading 202.
            return Task.FromException<OperationAcceptedDto>(NoActiveDownload(downloadId));
        }

        // The download worker: fetch the package archive, stream it through the §36-2a installer (which extracts +
        // atomically swaps it into place), reporting progress over the WS channel, and always clean up the registry.
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Top-level background worker: every failure must surface as a download.failed WS event, and an unobserved exception here would otherwise be lost. It is reported and logged, not swallowed silently.")]
        private async Task RunDownloadAsync(DownloadJob job, DataPackageDto pkg) {
            // Read-progress watchdog: idleCts fires if no byte is read from the package stream within _idleTimeout.
            // It measures whole-pipeline progress (network receive paced by disk-write of each extracted chunk), so
            // it trips on a genuine stall of either; the 60s default is far longer than any single disk-paced chunk
            // read, so healthy extraction (even on slow RPi storage) keeps resetting it. The install runs under a
            // token linked to both the user-cancel CTS and this one, so a stalled transfer is cancelled, not hung.
            using var idleCts = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cts.Token, idleCts.Token);
            var ct = linked.Token;
            try {
                await EmitAsync(WsEventCatalog.DataManagerDownloadProgress, job, error: null).ConfigureAwait(false);

                // Arm BEFORE OpenAsync so the header-wait phase is bounded too — the infinite HttpClient.Timeout
                // removes the header-receipt deadline, so a CDN that accepts the connection but never sends headers
                // would otherwise hang forever (escapable only by POST /cancel). OpenAsync gets the linked token.
                var targetDir = PackageDir(pkg.Id)!; // pkg came from the catalog, so this is non-null + in-root.
                // §36 incremental update: when re-fetching an already-installed package (a force reinstall), make the
                // GET conditional on the validator the prior install recorded. A 304 means nothing changed → keep the
                // install untouched. Null for a fresh install, so the GET is unconditional.
                var knownValidator = SkyDataInstaller.ReadRemoteLastModified(targetDir);

                idleCts.CancelAfter(_idleTimeout);
                await using var fetch = await _fetcher.OpenAsync(pkg.SourceUrl!, knownValidator, ct).ConfigureAwait(false);

                if (fetch.NotModified) {
                    // The remote package is unchanged since the last install — there's nothing to download or extract,
                    // and the existing install stays in place. Report it as a completed (up-to-date) download.
                    await EmitAsync(WsEventCatalog.DataManagerDownloadComplete, job, error: null).ConfigureAwait(false);
                    LogDownloadUpToDate(pkg.Id, job.Id);
                    return;
                }
                Volatile.Write(ref job.TotalBytes, fetch.TotalBytes ?? -1);

                idleCts.CancelAfter(_idleTimeout); // fresh deadline for the body; reset below on each byte of progress.
                await using var counting = new CountingStream(fetch.Content,
                    read => {
                        Volatile.Write(ref job.DownloadedBytes, read);
                        MaybeEmitProgress(job);                  // WS flood control (ProgressThrottleMs)
                        MaybeResetIdleDeadline(job, idleCts);    // watchdog reset — independent of the emit throttle
                    },
                    // At end-of-stream the whole archive is fetched; disarm the read-idle watchdog so a slow local
                    // tail (final disk flush / atomic swap of a fully-downloaded package) can't false-fire "stalled".
                    onEof: () => idleCts.CancelAfter(Timeout.InfiniteTimeSpan));
                if (pkg.SizeBytes <= 0) {
                    LogCatalogSizeMissing(pkg.Id);
                }
                var ceiling = ExtractionCeiling(pkg);
                var expectedSha = _expectedDigests.GetValueOrDefault(pkg.Id);
                if (IsArchiveFormat(pkg.SourceUrl!)) {
                    await SkyDataInstaller.InstallFromTarGzAsync(counting, targetDir, ceiling, fetch.LastModified, ct, expectedSha).ConfigureAwait(false);
                } else if (IsBareCatalogFormat(pkg.SourceUrl!)) {
                    // A bare catalog file (a CSV, optionally gzip-compressed). Install it under a canonical name so the
                    // §36 catalog consumer reads {packageDir}/catalog.csv regardless of the upstream file name.
                    var gunzip = pkg.SourceUrl!.AbsolutePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
                    await SkyDataInstaller.InstallFromFileAsync(counting, targetDir, CatalogFileName, gunzip, ceiling, fetch.LastModified, ct, expectedSha).ConfigureAwait(false);
                } else {
                    // Fail fast (rather than silently writing raw bytes as catalog.csv) if a future catalog entry uses an
                    // unrecognised format — a curator mistake. SupportedDownloadFormat catches the same at test time.
                    throw new InvalidOperationException(
                        $"Unsupported sky-data package format for '{pkg.Id}': {pkg.SourceUrl!.AbsolutePath}");
                }

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
                LogDownloadFailed(pkg.Id, job.Id, ex);
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

        // Fallback extraction cap for a catalog entry missing its size: the largest advertised package + 25% headroom
        // (≥16 MiB), so an unsized entry can't open a wider window than our biggest real package.
        private static readonly long UnknownSizeCeiling = ComputeUnknownSizeCeiling();

        private static long ComputeUnknownSizeCeiling() {
            var max = Catalog.Max(p => p.SizeBytes);
            if (max <= 0) {
                return 16L * 1024 * 1024;
            }
            var headroom = max / 4;
            var ceiling = max > long.MaxValue - headroom ? long.MaxValue : max + headroom;
            return Math.Max(ceiling, 16L * 1024 * 1024);
        }

        // Cap uncompressed extraction at the catalog's advertised installed size + 25% headroom (never below 16 MiB),
        // so a tampered archive can't expand without bound even though the package id is catalog-validated. Guard the
        // addition against long overflow (a corrupt near-MaxValue size would otherwise wrap negative and disable the cap).
        private static long ExtractionCeiling(DataPackageDto pkg) {
            if (pkg.SizeBytes <= 0) {
                // Unknown advertised size: fall back to a generous ceiling rather than the small-package 16 MiB floor,
                // so a legitimate large download isn't clipped (the caller logs the missing size). Still bounded.
                return UnknownSizeCeiling;
            }
            var headroom = pkg.SizeBytes / 4;
            var ceiling = pkg.SizeBytes > long.MaxValue - headroom ? long.MaxValue : pkg.SizeBytes + headroom;
            return Math.Max(ceiling, 16L * 1024 * 1024);
        }

        private static DataManagerActiveDownloadDto SnapshotActive(DownloadJob job) {
            var downloaded = Volatile.Read(ref job.DownloadedBytes);
            var total = Volatile.Read(ref job.TotalBytes);
            return new DataManagerActiveDownloadDto(job.Id, job.PackageId, downloaded, total, Percent(downloaded, total));
        }

        // Throttled, fire-and-forget progress emit driven by the stream read callback (which runs on the worker).
        private void MaybeEmitProgress(DownloadJob job) {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref job.LastEmitTick);
            if (now - last < ProgressThrottleMs) {
                return;
            }
            // CAS so concurrent reads don't both fire for the same window (Read here is single-threaded today, but
            // this keeps the throttle correct if the stream is ever pumped from more than one thread).
            if (Interlocked.CompareExchange(ref job.LastEmitTick, now, last) != last) {
                return;
            }
            _ = EmitAsync(WsEventCatalog.DataManagerDownloadProgress, job, error: null);
        }

        // Reset the idle-watchdog deadline on byte progress, throttled to ~4 resets per idle window so a fast stream
        // doesn't reschedule the timer on every read. Deliberately INDEPENDENT of the WS-emit throttle: the watchdog's
        // correctness must not couple to ProgressThrottleMs (it has to work even when idleTimeout < ProgressThrottleMs).
        private void MaybeResetIdleDeadline(DownloadJob job, CancellationTokenSource idleCts) {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref job.LastIdleResetTick);
            var interval = Math.Max(1, (long)(_idleTimeout.TotalMilliseconds / 4));
            if (now - last < interval) {
                return;
            }
            if (Interlocked.CompareExchange(ref job.LastIdleResetTick, now, last) != last) {
                return;
            }
            idleCts.CancelAfter(_idleTimeout);
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

        private static DownloadNotFoundException NoActiveDownload(Guid downloadId) =>
            new($"No active download '{downloadId}'.");

        private static double Percent(long downloaded, long total) =>
            total > 0 ? Math.Round(Math.Clamp((double)downloaded / total, 0, 1) * 100, 2) : 0;

        // Reflect the on-disk state of a catalog package. A package is "installed" only when §36-2a's .installed
        // sentinel is present (a bare dir from a torn/interrupted install reads as not-installed); InstalledUtc comes
        // from the sentinel's write time (the install-complete stamp), not the dir mtime which moves on any child write.
        private DataPackageDto Describe(DataPackageDto pkg) {
            var dir = PackageDir(pkg.Id);
            if (dir is null || ReadInstall(dir) is not { } info) {
                return pkg with { IsInstalled = false, InstalledUtc = null };
            }
            return pkg with { IsInstalled = true, InstalledUtc = info.InstalledUtc, SizeBytes = info.Size };
        }

        // Per-package directory — ONLY for a known catalog id, so a caller-supplied packageId (Delete,
        // Download) can never traverse out of the data root.
        private string? PackageDir(string packageId) =>
            _catalogIds.Contains(packageId) ? Path.Combine(_dataRoot, packageId) : null;

        // A package counts as installed once §36-2a's .installed sentinel is present (a bare dir from a torn install
        // does not). Used by the ForceReinstall skip.
        private bool IsInstalled(string packageId) {
            var dir = PackageDir(packageId);
            return dir is not null && File.Exists(Path.Combine(dir, SkyDataInstaller.InstalledMarkerFileName));
        }

        // Read a completed install's (size, install-stamp), or null if the package dir has no .installed sentinel
        // (absent, or a torn install) — folding the sentinel check into the read so a concurrent DeleteAsync can't
        // throw, it just reads as not-installed. Size excludes the sentinel file itself (package data only).
        // NOTE: O(files) per call and re-walked by both ListPackages + GetState — fine for the small catalog;
        // §36-2 can cache the size at install time if the catalog grows large.
        private static (long Size, DateTimeOffset InstalledUtc)? ReadInstall(string dir) {
            try {
                var sentinel = new FileInfo(Path.Combine(dir, SkyDataInstaller.InstalledMarkerFileName));
                if (!sentinel.Exists) {
                    return null; // no sentinel → not a completed install
                }
                var info = new DirectoryInfo(dir);
                var size = info.EnumerateFiles("*", SearchOption.AllDirectories)
                    // Exclude the root sentinel by full path, not name — so a package that legitimately ships a file
                    // named ".installed" in a subdirectory still has its bytes counted.
                    .Where(f => !string.Equals(f.FullName, sentinel.FullName, StringComparison.Ordinal))
                    .Sum(f => f.Length);
                return (size, sentinel.LastWriteTimeUtc);
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
            public long LastIdleResetTick;
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
            private readonly Action? _onEof;
            private long _total;
            private volatile bool _eofSeen;

            public CountingStream(Stream inner, Action<long> onRead, Action? onEof = null) {
                _inner = inner;
                _onRead = onRead;
                _onEof = onEof;
            }

            // `requested` is the size of the read request: a 0-byte read of a non-empty request is genuine EOF, but a
            // 0-byte read of a 0-length request (legal, unusual) is NOT — don't let it falsely disarm the watchdog.
            private int Count(int read, int requested) {
                if (read > 0) {
                    _onRead(Interlocked.Add(ref _total, read));
                } else if (requested > 0 && !_eofSeen) {
                    _eofSeen = true;
                    _onEof?.Invoke();
                }
                return read;
            }

            public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count), count);

            public override int Read(Span<byte> buffer) => Count(_inner.Read(buffer), buffer.Length);

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                Count(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false), buffer.Length);

            // Modern .NET routes the legacy array overload through ReadAsync(Memory<byte>); the explicit override
            // keeps the count correct even if a consumer calls this form directly.
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                Count(await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false), count);

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

        [LoggerMessage(Level = LogLevel.Warning, Message = "Catalog '{PackageId}' could not be read — serving 'unavailable'")]
        partial void LogCatalogReadFailed(string packageId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Data package '{PackageId}' could not be deleted")]
        partial void LogDeleteFailed(string packageId, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Data package '{PackageId}' download {DownloadId} started")]
        partial void LogDownloadStarted(string packageId, Guid downloadId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Data package '{PackageId}' download {DownloadId} complete")]
        partial void LogDownloadComplete(string packageId, Guid downloadId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Data package '{PackageId}' download {DownloadId} already up to date (304 Not Modified) — install kept")]
        partial void LogDownloadUpToDate(string packageId, Guid downloadId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Data package '{PackageId}' download {DownloadId} cancelled")]
        partial void LogDownloadCancelled(string packageId, Guid downloadId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Data package '{PackageId}' download {DownloadId} stalled (no progress within the idle timeout)")]
        partial void LogDownloadStalled(string packageId, Guid downloadId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Catalog entry '{PackageId}' has no advertised size — using the generous fallback extraction ceiling")]
        partial void LogCatalogSizeMissing(string packageId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Data package '{PackageId}' download {DownloadId} failed")]
        partial void LogDownloadFailed(string packageId, Guid downloadId, Exception ex);

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

    /// <summary>Thrown by <see cref="DataManagerService.DownloadAsync"/> when a non-force request targets a package
    /// that is already fully installed. The endpoint maps it to a 409 so the caller learns it did NOT re-download
    /// (and can retry with forceReinstall=true), rather than the request silently re-fetching the package.</summary>
    public sealed class PackageAlreadyInstalledException : Exception {
        public PackageAlreadyInstalledException() { }
        public PackageAlreadyInstalledException(string message) : base(message) { }
        public PackageAlreadyInstalledException(string message, Exception innerException) : base(message, innerException) { }
        public static PackageAlreadyInstalledException ForPackageId(string packageId) =>
            new($"Data package '{packageId}' is already installed; pass forceReinstall=true to re-download.");
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
