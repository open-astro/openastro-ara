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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §36 Data Manager — the real, disk-backed inventory + management layer (replaces
    /// <c>PlaceholderDataManagerService</c>). Tracks which curated sky-data packages (star catalogs,
    /// horizon profiles, and — once §36-2 lands the download engine — HiPS sky-survey imagery) are
    /// installed under the data root, reports their on-disk size, and deletes them.
    ///
    /// This is §36-1: <see cref="ListPackagesAsync"/> / <see cref="DeleteAsync"/> / <see cref="GetStateAsync"/>
    /// are real (read from + mutate the data root). <see cref="DownloadAsync"/> / <see cref="CancelAsync"/>
    /// are the acquisition half — the fetch-archive → extract → progress-WS → cancel engine lands in §36-2;
    /// for now they acknowledge the request without downloading (tracked in PORT_TODO).
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

        private readonly string _dataRoot;
        private readonly ILogger<DataManagerService> _logger;

        public DataManagerService(string dataRootPath, ILogger<DataManagerService> logger) {
            _dataRoot = dataRootPath ?? throw new ArgumentNullException(nameof(dataRootPath));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
                if (dir is not null && Directory.Exists(dir)) {
                    installed++;
                    total += DirectorySize(dir);
                }
            }
            return Task.FromResult(new DataManagerStateDto(
                InstalledPackageCount: installed,
                TotalInstalledBytes: total,
                ActiveDownloads: Array.Empty<DataManagerActiveDownloadDto>(),
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
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // A locked/permission-denied delete is reported as "not deleted" rather than thrown —
                // the client retries; the daemon must not 500 on a best-effort disk reclaim.
                LogDeleteFailed(packageId, ex);
                return Task.FromResult(false);
            }
        }

        public Task<OperationAcceptedDto> DownloadAsync(DownloadRequestDto request, string? idempotencyKey, CancellationToken ct) {
            ArgumentNullException.ThrowIfNull(request);
            // §36-2: the fetch-archive → extract → progress-WS → cancel engine. For now the request is
            // accepted (so the wire contract is stable) but no download runs yet.
            LogDownloadDeferred(request.PackageId);
            return Task.FromResult(Accepted("data-manager.download", idempotencyKey));
        }

        public Task<OperationAcceptedDto> CancelAsync(Guid downloadId, CancellationToken ct) =>
            Task.FromResult(Accepted("data-manager.cancel", null));

        // Reflect the on-disk state of a catalog package: installed dir → IsInstalled + measured size +
        // last-write time; absent → the catalog's advertised size with IsInstalled=false.
        private DataPackageDto Describe(DataPackageDto pkg) {
            var dir = PackageDir(pkg.Id);
            if (dir is null || !Directory.Exists(dir)) {
                return pkg with { IsInstalled = false, InstalledUtc = null };
            }
            return pkg with {
                IsInstalled = true,
                InstalledUtc = Directory.GetLastWriteTimeUtc(dir),
                SizeBytes = DirectorySize(dir),
            };
        }

        // Per-package directory — ONLY for a known catalog id, so a caller-supplied packageId (Delete,
        // Download) can never traverse out of the data root.
        private string? PackageDir(string packageId) =>
            Catalog.Any(p => p.Id == packageId) ? Path.Combine(_dataRoot, packageId) : null;

        private static long DirectorySize(string dir) =>
            new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

        private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
            new(OperationId: Guid.NewGuid(),
                OperationType: operationType,
                AcceptedUtc: DateTimeOffset.UtcNow,
                IdempotencyKey: idempotencyKey);

        [LoggerMessage(Level = LogLevel.Information, Message = "Data package '{PackageId}' deleted from the data root")]
        partial void LogDeleted(string packageId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Data package '{PackageId}' could not be deleted")]
        partial void LogDeleteFailed(string packageId, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Data package '{PackageId}' download requested — deferred to the §36-2 download engine")]
        partial void LogDownloadDeferred(string packageId);
    }
}
