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
using System.Diagnostics.CodeAnalysis;
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

        // Hard cap on sequences/ nesting depth — guards the recursive AddDirectory against a stack-overflowing
        // (uncatchable) pathological tree. Real trees are a handful of levels; 64 only trips on a corrupt/adversarial one.
        private const int MaxBackupTreeDepth = 64;

        private const string BackupsDirName = "backups";
        private const string ZipPrefix = "backup-";
        private const string ZipExtension = ".zip";
        private const string ManifestExtension = ".meta.json";
        private const string TempPrefix = ".tmp-";

        // §43-2: a real restore will report progress here; until then clone-status is a fixed idle blob.
        private const string IdleCloneStatusJson =
            "{\"state\":\"idle\",\"progress_pct\":null,\"current_area\":null,\"message\":null}";

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
            // idempotencyKey is echoed back but NOT enforced in §43-1: a retried POST with the same key produces a new
            // archive each time. De-dup (key → already-created snapshot id) lands with the §43-2 worker rework; tracked
            // in PORT_TODO. Harmless for now beyond extra archives (no retention pruning yet — also §43-2). When a key
            // is actually supplied, log it so an operator retrying a timed-out POST has a signal that dedup is off.
            if (!string.IsNullOrEmpty(idempotencyKey)) {
                LogDeduplicationNotImplemented();
            }
            var id = Guid.NewGuid();
            var createdUtc = DateTimeOffset.UtcNow;

            // All file IO is synchronous (ZipArchive has no async write path); run it off the request thread so a
            // larger sequences/ tree doesn't tie up the connection's thread-pool slot while it packages. ct is
            // threaded in so a cancellation between checkpoints aborts the work and reclaims its artifacts, rather
            // than only making the await throw while the zip finishes writing in the background.
            await Task.Run(() => CreateBackupCore(id, createdUtc, ct), ct).ConfigureAwait(false);

            LogCreated(id, _backupsDir);
            return new OperationAcceptedDto(
                OperationId: id,
                OperationType: "backup.create-zip",
                AcceptedUtc: createdUtc,
                IdempotencyKey: idempotencyKey);
        }

        private void CreateBackupCore(Guid id, DateTimeOffset createdUtc, CancellationToken ct) {
            // Check before any side effect (incl. creating backups/) so a cancellation that fires after the task
            // starts but before the first write truly leaves nothing behind, matching the documented behaviour.
            ct.ThrowIfCancellationRequested();
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
                    if (File.Exists(profilePath) && !IsReparsePoint(profilePath)) {
                        // Skip a symlinked profile.json for the same reason AddDirectory skips links in sequences/ —
                        // CreateEntryFromFile would follow it and bundle whatever it points at, possibly outside the
                        // profile root. A real profile.json is a plain file.
                        archive.CreateEntryFromFile(profilePath, ProfileFileName, CompressionLevel.Optimal);
                        areas.Add("profiles");
                    }

                    var sequencesDir = Path.Combine(_profileDir, SequencesDirName);
                    if (Directory.Exists(sequencesDir) && AddDirectory(archive, sequencesDir, SequencesDirName, depth: 0, ct)) {
                        areas.Add("sequences");
                    }
                }

                // Nothing to back up (no profile.json, no sequences/) — don't reveal a content-free zip that would
                // list as a real-but-useless snapshot. The catch reclaims the staged temp; the endpoint maps this
                // to 422. (Near-unreachable in practice: the daemon writes a default profile.json.)
                if (areas.Count == 0) {
                    throw new BackupNothingToArchiveException(
                        "Nothing to back up: neither profile.json nor a sequences/ tree is present.");
                }

                ct.ThrowIfCancellationRequested();
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
        private static bool AddDirectory(ZipArchive archive, string sourceDir, string entryRoot, int depth, CancellationToken ct) {
            // Depth cap: this recurses per directory level, so a pathologically deep (but symlink-free, hence
            // acyclic) tree could otherwise blow the stack — and a StackOverflowException is uncatchable and crashes
            // the daemon. Convert that into an ordinary catchable failure the create path's catch can reclaim from.
            // A real sequences/ tree is a handful of levels deep; 64 only trips on an adversarial/corrupt tree.
            if (depth > MaxBackupTreeDepth) {
                throw new InvalidDataException(
                    $"Backup source tree exceeds the {MaxBackupTreeDepth}-level nesting limit at '{entryRoot}'.");
            }
            var added = false;
            var dir = new DirectoryInfo(sourceDir);
            foreach (var entry in dir.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly)) {
                // Cancellation checkpoint per entry so a large sequences/ tree aborts promptly rather than only at the
                // outer checks before/after the whole archive is assembled (matters once §43-2 areas grow the payload).
                ct.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) {
                    continue; // symlink / junction — don't follow it out of the backup root.
                }
                if (entry is DirectoryInfo sub) {
                    added |= AddDirectory(archive, sub.FullName, entryRoot + "/" + sub.Name, depth + 1, ct);
                } else {
                    archive.CreateEntryFromFile(entry.FullName, entryRoot + "/" + entry.Name, CompressionLevel.Optimal);
                    added = true;
                }
            }
            return added;
        }

        private static bool IsReparsePoint(string path) =>
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        private static string HashFile(string path) {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexStringLower(hash);
        }

        public Task<IReadOnlyList<BackupZipDto>> ListSnapshotsAsync(CancellationToken ct) =>
            // Offloaded: the manifest reads + directory scans are synchronous file IO, so run them off the request
            // thread rather than blocking a thread-pool thread through the whole enumeration on a slow/large dir.
            Task.Run<IReadOnlyList<BackupZipDto>>(() => {
                if (!Directory.Exists(_backupsDir)) {
                    return Array.Empty<BackupZipDto>();
                }

                // Enumerate the archives once into a set of present backup ids, so confirming each manifest has a
                // matching zip is an O(1) lookup rather than a fresh directory scan per manifest (which made listing
                // n snapshots O(n^2) — and snapshots accumulate with no retention policy yet).
                var presentZipIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var path in Directory.EnumerateFiles(_backupsDir, ZipPrefix + "*" + ZipExtension, SearchOption.TopDirectoryOnly)) {
                    ct.ThrowIfCancellationRequested();
                    var idN = ExtractIdSuffix(Path.GetFileNameWithoutExtension(path));
                    if (idN is not null) {
                        presentZipIds.Add(idN);
                    }
                }

                var snapshots = new List<BackupZipDto>();
                foreach (var manifestPath in Directory.EnumerateFiles(_backupsDir, "*" + ManifestExtension, SearchOption.TopDirectoryOnly)) {
                    ct.ThrowIfCancellationRequested();
                    var dto = TryReadSnapshot(manifestPath, presentZipIds);
                    if (dto is not null) {
                        snapshots.Add(dto);
                    }
                }

                // Newest first — the §43 "Past backups" UI lists most-recent at the top.
                snapshots.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
                return snapshots;
            }, ct);

        private BackupZipDto? TryReadSnapshot(string manifestPath, HashSet<string> presentZipIds) {
            try {
                var manifest = JsonSerializer.Deserialize(
                    File.ReadAllText(manifestPath), AraJsonSerializerContext.Default.BackupManifest);
                if (manifest is null) {
                    return null;
                }
                // The manifest is authoritative, but only list a backup whose archive is actually present — a
                // manifest orphaned by a half-deleted backup shouldn't surface a snapshot that can't be downloaded.
                if (!presentZipIds.Contains(manifest.BackupId.ToString("N", CultureInfo.InvariantCulture))) {
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

        public async Task<(Stream Stream, string FileName)?> OpenSnapshotAsync(Guid id, CancellationToken ct) {
            // Offload the directory scan off the request thread, then open the handle here: holding the open stream
            // closes the resolve→serve TOCTOU window (a delete after this point can't turn Results.File into a 500).
            var path = await Task.Run(() => FindZipPath(id), ct).ConfigureAwait(false);
            if (path is null) {
                return null;
            }
            try {
                // FileStream owned by the response pipeline — ASP.NET Core disposes it once the response is sent.
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
                return (stream, Path.GetFileName(path));
            } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException) {
                // Vanished or became inaccessible between the scan and the open (deleted, perms revoked, or a transient
                // sharing-lock) — report not-found rather than a torn 500, symmetric with how TryReadSnapshot treats an
                // unreadable archive. (FileNotFoundException/DirectoryNotFoundException derive from IOException but are
                // listed explicitly for clarity.)
                LogSnapshotVanished(path, ex);
                return null;
            }
        }

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

        // The id is the trailing dash-delimited segment of a "backup-{ts}-{id:N}" name (N-format, no dashes of its
        // own), so the last '-' splits it cleanly. Returns null for a name with no '-' (not one of ours).
        private static string? ExtractIdSuffix(string fileNameWithoutExtension) {
            var dash = fileNameWithoutExtension.LastIndexOf('-');
            return dash >= 0 && dash < fileNameWithoutExtension.Length - 1
                ? fileNameWithoutExtension[(dash + 1)..]
                : null;
        }

        private static Uri SnapshotDownloadUrl(Guid id) =>
            new("/api/v1/backup/snapshot/" + id.ToString("D", CultureInfo.InvariantCulture) + "/download", UriKind.Relative);

        public Task RestoreZipAsync(RestoreRequestDto request, string? idempotencyKey, CancellationToken ct) {
            ArgumentNullException.ThrowIfNull(request);
            // §43-2: restore overwrites live config (profile.json / sequences) and is destructive — it lands with the
            // staged-swap + restore-progress state machine. Until then it does nothing; the endpoint responds 501 Not
            // Implemented rather than a 202 a client would read as a successful rollback. Nothing is started, so this
            // returns no operation id (an earlier revision allocated a discarded DTO) — §43-2 reintroduces it. We only
            // log that an operator attempted a restore; idempotencyKey is irrelevant until there's an op to dedup.
            LogRestoreNotImplemented();
            return Task.CompletedTask;
        }

        public Task<JsonElement> GetCloneStatusAsync(CancellationToken ct) {
            // §43-2: idle until RestoreZipAsync runs a real restore worth reporting progress on. Parsed per call
            // (cheap, polled rarely) and the document disposed once cloned — no long-lived static JsonDocument.
            using var doc = JsonDocument.Parse(IdleCloneStatusJson);
            return Task.FromResult(doc.RootElement.Clone());
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Best-effort cleanup running inside a catch block before a re-throw: any exception from " +
                "Delete (incl. SecurityException/PathTooLongException) must be swallowed so it can't mask the " +
                "original failure being re-thrown. A leaked scratch file is harmless — ListSnapshots keys off " +
                "*.meta.json and ignores it.")]
        private static void TryDelete(string path) {
            try {
                if (File.Exists(path)) {
                    File.Delete(path);
                }
            } catch (Exception) {
                // Best-effort cleanup; never let a cleanup failure replace the exception we're unwinding.
            }
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Backup snapshot {BackupId} created under {BackupsDir}")]
        partial void LogCreated(Guid backupId, string backupsDir);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping unreadable backup manifest {ManifestPath}")]
        partial void LogManifestSkipped(string manifestPath, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Backup restore requested but not yet implemented (§43-2); responding 501 — no config was rolled back")]
        partial void LogRestoreNotImplemented();

        [LoggerMessage(Level = LogLevel.Warning, Message = "Backup archive {ArchivePath} vanished between resolve and open — serving 404")]
        partial void LogSnapshotVanished(string archivePath, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Backup create-zip received an Idempotency-Key but dedup is not implemented (§43-2) — a retry will create another archive")]
        partial void LogDeduplicationNotImplemented();
    }

    /// <summary>Thrown by <see cref="BackupService.CreateZipAsync"/> when there is nothing to back up — neither a
    /// <c>profile.json</c> nor a <c>sequences/</c> tree exists — so a content-free zip would otherwise list as a
    /// real-but-useless snapshot. The create endpoint maps it to <c>422 Unprocessable Entity</c>.</summary>
    public sealed class BackupNothingToArchiveException : Exception {
        public BackupNothingToArchiveException() { }
        public BackupNothingToArchiveException(string message) : base(message) { }
        public BackupNothingToArchiveException(string message, Exception innerException) : base(message, innerException) { }
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
