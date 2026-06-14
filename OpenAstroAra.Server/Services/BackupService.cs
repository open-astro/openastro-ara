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
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §43 backup — the real, disk-backed ZIP-snapshot service (replaces the former placeholder).
    /// This is §43-1, the non-destructive half: <see cref="CreateZipAsync"/> packages the profile's config areas
    /// (<c>profile.json</c> + the <c>sequences/</c> tree) into a self-contained <c>.zip</c> under
    /// <c>{profileDir}/backups/</c> with a sidecar JSON manifest, <see cref="ListSnapshotsAsync"/> enumerates those
    /// manifests, and <see cref="ResolveSnapshotFilePathAsync"/> serves a snapshot for download. The destructive
    /// half — <see cref="RestoreZipAsync"/> (overwrites live config) and the <see cref="GetCloneStatusAsync"/>
    /// restore-progress state machine — is deferred to §43-2 and still returns the safe placeholder behaviour
    /// (accept + report idle), documented on each method and tracked in <c>design/PORT_TODO.md</c>.
    ///
    /// <para>Scope choice (§43-1): a backup covers the two small config areas only — frame-metadata and log areas
    /// from the §43 selector set arrive with the restore work in §43-2. <see cref="CreateZipAsync"/> completes the
    /// packaging within the request (the payload is config-sized — kilobytes, not the frame library) rather than on
    /// a background worker with progress WS; that async+progress path is a §43-2 follow-up. The 202/operation-id
    /// contract is preserved so the wire shape doesn't change when it becomes truly asynchronous.</para>
    ///
    /// Each backup lives at <c>{profileDir}/backups/backup-{utc}-{id:N}.zip</c> with a
    /// <c>{...}.meta.json</c> sidecar. The id is a server-minted guid, so the on-disk name carries no
    /// caller-controlled path component (no traversal). Packaging writes to a hidden <c>.tmp-</c> file and renames
    /// it into place, so a crash mid-zip never leaves a half-written archive a reader could list.
    /// </summary>
    public sealed partial class BackupService : IBackupService {

        // Areas captured by a §43-1 backup, relative to profileDir. "profiles" = the single profile.json document;
        // "sequences" = the whole sequences/ tree (library + active + templates + imported). Both are config-sized.
        private const string ProfileFileName = "profile.json";
        private const string SequencesDirName = "sequences";

        private const string BackupsDirName = "backups";
        private const string ZipPrefix = "backup-";
        private const string ZipExtension = ".zip";
        private const string ManifestExtension = ".meta.json";
        private const string TempPrefix = ".tmp-";

        private static readonly JsonDocument _idleStatus = JsonDocument.Parse(
            "{\"state\":\"idle\",\"progress_pct\":null,\"current_area\":null,\"message\":null}");

        private readonly string _profileDir;
        private readonly string _backupsDir;
        private readonly ILogger<BackupService> _logger;

        public BackupService(string profileDir, ILogger<BackupService> logger) {
            ArgumentException.ThrowIfNullOrEmpty(profileDir);
            ArgumentNullException.ThrowIfNull(logger);
            _profileDir = profileDir;
            _backupsDir = Path.Combine(profileDir, BackupsDirName);
            _logger = logger;
        }

        public async Task<OperationAcceptedDto> CreateZipAsync(string? idempotencyKey, CancellationToken ct) {
            var id = Guid.NewGuid();
            var createdUtc = DateTimeOffset.UtcNow;

            // All file IO is synchronous (ZipArchive has no async write path); run it off the request thread so a
            // larger sequences/ tree doesn't tie up the connection's thread-pool slot while it packages.
            await Task.Run(() => CreateBackupCore(id, createdUtc), ct).ConfigureAwait(false);

            LogCreated(id, _backupsDir);
            return new OperationAcceptedDto(
                OperationId: id,
                OperationType: "backup.create-zip",
                AcceptedUtc: createdUtc,
                IdempotencyKey: idempotencyKey);
        }

        private void CreateBackupCore(Guid id, DateTimeOffset createdUtc) {
            Directory.CreateDirectory(_backupsDir);

            var baseName = ZipPrefix + createdUtc.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture)
                + "-" + id.ToString("N", CultureInfo.InvariantCulture);
            var tempPath = Path.Combine(_backupsDir, TempPrefix + id.ToString("N", CultureInfo.InvariantCulture) + ZipExtension);
            var zipPath = Path.Combine(_backupsDir, baseName + ZipExtension);
            var manifestPath = Path.Combine(_backupsDir, baseName + ManifestExtension);

            var areas = new List<string>();
            try {
                using (var zipStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create)) {
                    var profilePath = Path.Combine(_profileDir, ProfileFileName);
                    if (File.Exists(profilePath)) {
                        archive.CreateEntryFromFile(profilePath, ProfileFileName, CompressionLevel.Optimal);
                        areas.Add("profiles");
                    }

                    var sequencesDir = Path.Combine(_profileDir, SequencesDirName);
                    if (Directory.Exists(sequencesDir) && AddDirectory(archive, sequencesDir, SequencesDirName)) {
                        areas.Add("sequences");
                    }
                }

                var sha256 = HashFile(tempPath);
                var sizeBytes = new FileInfo(tempPath).Length;

                // Reveal the archive first, then its manifest. A reader (ListSnapshots / download) keys off the
                // manifest, so the zip is always present by the time the manifest names it — never the reverse.
                File.Move(tempPath, zipPath, overwrite: false);
                var manifest = new BackupManifest(id, createdUtc, sizeBytes, sha256, areas);
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, AraJsonSerializerContext.Default.BackupManifest));
            } catch {
                // Reclaim every artifact this attempt may have created, whichever step failed: the temp archive (if
                // the rename hadn't happened yet), the revealed zip (if the manifest write threw after the rename —
                // otherwise an unlisted orphan nothing reclaims), and any half-written manifest.
                TryDelete(tempPath);
                TryDelete(zipPath);
                TryDelete(manifestPath);
                throw;
            }
        }

        // Add every regular file under sourceDir to the archive under entryRoot/, preserving relative structure.
        // Returns true if at least one file was added (an empty sequences/ tree shouldn't claim the "sequences" area).
        // Symlinks (file and directory) are skipped: Directory.EnumerateFiles(AllDirectories) would follow a directory
        // symlink and bundle whatever it points at — including a target outside _profileDir — so the walk is manual
        // and refuses to descend into or capture any reparse point.
        private static bool AddDirectory(ZipArchive archive, string sourceDir, string entryRoot) {
            var added = false;
            var dir = new DirectoryInfo(sourceDir);
            foreach (var entry in dir.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly)) {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) {
                    continue; // symlink / junction — don't follow it out of the backup root.
                }
                if (entry is DirectoryInfo sub) {
                    added |= AddDirectory(archive, sub.FullName, entryRoot + "/" + sub.Name);
                } else {
                    archive.CreateEntryFromFile(entry.FullName, entryRoot + "/" + entry.Name, CompressionLevel.Optimal);
                    added = true;
                }
            }
            return added;
        }

        private static string HashFile(string path) {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexStringLower(hash);
        }

        public Task<IReadOnlyList<BackupZipDto>> ListSnapshotsAsync(CancellationToken ct) {
            if (!Directory.Exists(_backupsDir)) {
                return Task.FromResult<IReadOnlyList<BackupZipDto>>(Array.Empty<BackupZipDto>());
            }

            var snapshots = new List<BackupZipDto>();
            foreach (var manifestPath in Directory.EnumerateFiles(_backupsDir, "*" + ManifestExtension, SearchOption.TopDirectoryOnly)) {
                ct.ThrowIfCancellationRequested();
                var dto = TryReadSnapshot(manifestPath);
                if (dto is not null) {
                    snapshots.Add(dto);
                }
            }

            // Newest first — the §43 "Past backups" UI lists most-recent at the top.
            snapshots.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
            return Task.FromResult<IReadOnlyList<BackupZipDto>>(snapshots);
        }

        private BackupZipDto? TryReadSnapshot(string manifestPath) {
            try {
                var manifest = JsonSerializer.Deserialize(
                    File.ReadAllText(manifestPath), AraJsonSerializerContext.Default.BackupManifest);
                if (manifest is null) {
                    return null;
                }
                // The manifest is authoritative, but only list a backup whose archive is actually present — a
                // manifest orphaned by a half-deleted backup shouldn't surface a snapshot that can't be downloaded.
                if (FindZipPath(manifest.BackupId) is null) {
                    return null;
                }
                return new BackupZipDto(
                    BackupId: manifest.BackupId,
                    CreatedUtc: manifest.CreatedUtc,
                    SizeBytes: manifest.SizeBytes,
                    Sha256: manifest.Sha256,
                    DownloadUrl: SnapshotDownloadUrl(manifest.BackupId),
                    IncludedAreas: manifest.IncludedAreas);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                // A single corrupt/locked manifest shouldn't fail the whole list — skip it and report the rest.
                LogManifestSkipped(manifestPath, ex);
                return null;
            }
        }

        public Task<string?> ResolveSnapshotFilePathAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(FindZipPath(id));

        // Resolve a backup id to its archive path by matching the id-suffixed name. The id is a guid, so the glob
        // pattern contains no caller-controlled wildcard surface beyond the 32-hex id itself.
        private string? FindZipPath(Guid id) {
            if (!Directory.Exists(_backupsDir)) {
                return null;
            }
            var suffix = "-" + id.ToString("N", CultureInfo.InvariantCulture) + ZipExtension;
            foreach (var path in Directory.EnumerateFiles(_backupsDir, ZipPrefix + "*" + ZipExtension, SearchOption.TopDirectoryOnly)) {
                if (Path.GetFileName(path).EndsWith(suffix, StringComparison.Ordinal)) {
                    return path;
                }
            }
            return null;
        }

        private static Uri SnapshotDownloadUrl(Guid id) =>
            new("/api/v1/backup/snapshot/" + id.ToString("D", CultureInfo.InvariantCulture) + "/download", UriKind.Relative);

        public Task<OperationAcceptedDto> RestoreZipAsync(RestoreRequestDto request, string? idempotencyKey, CancellationToken ct) {
            ArgumentNullException.ThrowIfNull(request);
            // §43-2: restore overwrites live config (profile.json / sequences) and is destructive, so it stays a
            // no-op accept until the staged-swap + restore-progress state machine lands. Accepting (not 501) keeps
            // the client's restore flow wired end-to-end against the real service.
            return Task.FromResult(new OperationAcceptedDto(
                OperationId: Guid.NewGuid(),
                OperationType: "backup.restore-zip",
                AcceptedUtc: DateTimeOffset.UtcNow,
                IdempotencyKey: idempotencyKey));
        }

        public Task<JsonElement> GetCloneStatusAsync(CancellationToken ct) =>
            // §43-2: idle until RestoreZipAsync runs a real restore worth reporting progress on.
            Task.FromResult(_idleStatus.RootElement.Clone());

        private static void TryDelete(string path) {
            try {
                if (File.Exists(path)) {
                    File.Delete(path);
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Best-effort cleanup of the temp archive; a leaked .tmp-*.zip is ignored by ListSnapshots (it keys
                // off *.meta.json) and reclaimed on the next create with the same id (never — ids are unique).
            }
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Backup snapshot {BackupId} created under {BackupsDir}")]
        partial void LogCreated(Guid backupId, string backupsDir);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping unreadable backup manifest {ManifestPath}")]
        partial void LogManifestSkipped(string manifestPath, Exception ex);
    }

    /// <summary>On-disk backup manifest (sidecar <c>.meta.json</c>). The download URL in <see cref="BackupZipDto"/>
    /// is derived from <see cref="BackupId"/> at list time, so it is not persisted here.</summary>
    public sealed record BackupManifest(
        Guid BackupId,
        DateTimeOffset CreatedUtc,
        long SizeBytes,
        string Sha256,
        IReadOnlyList<string> IncludedAreas);
}
