#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Data.Sqlite;
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
using System.Text.Json.Nodes;
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
    public sealed partial class BackupService : IBackupService, IDisposable {

        // Areas captured by a §43-1 backup, relative to profileDir. "profiles" = the single profile.json document;
        // "sequences" = the whole sequences/ tree (library + active + templates + imported). Both are config-sized.
        // §43-2b(c) adds "frames_metadata": a consistent snapshot of the §28 catalog at db/openastroara.db inside
        // the zip (playbook §43.4 lists it unconditionally) — metadata only, never the FITS files (§43.8).
        private const string ProfileFileName = "profile.json";
        private const string SequencesDirName = "sequences";
        private const string DatabaseFileName = "openastroara.db";
        private const string DatabaseZipEntry = "db/openastroara.db";
        internal const string FrameMetadataArea = "frames_metadata";

        // Hard cap on sequences/ nesting depth — guards the recursive AddDirectory against a stack-overflowing
        // (uncatchable) pathological tree. Real trees are a handful of levels; 64 only trips on a corrupt/adversarial one.
        private const int MaxBackupTreeDepth = 64;

        private const string BackupsDirName = "backups";
        private const string ZipPrefix = "backup-";
        private const string ZipExtension = ".zip";
        private const string ManifestExtension = ".meta.json";
        private const string TempPrefix = ".tmp-";

        // §43-2b clone-status state machine. A restore runs on a background worker (RestoreZipAsync returns 202
        // immediately), so the poll-able clone-status reports the worker's live state. "idle" until the first
        // restore; then "running" → terminal "done"/"failed" (the last terminal stays visible until the next
        // restore overwrites it). State strings match the wire contract the client polls.
        private const string IdleState = "idle";
        private const string RunningState = "running";
        private const string DoneState = "done";
        private const string FailedState = "failed";
        private sealed record CloneState(string State, double? ProgressPct, string? CurrentArea, string? Message);
        private static readonly CloneState IdleClone = new(IdleState, null, null, null);
        // Pre-allocated terminal for the worker's last-resort finally — used only if building the real failed-state
        // (or anything else in the catch) itself throws, so clone-status can never strand at "running".
        private static readonly CloneState FailedFallback = new(FailedState, null, null, "Restore failed.");
        private readonly object _cloneLock = new();
        private CloneState _clone = IdleClone;

        private readonly string _profileDir;
        private readonly string _backupsDir;
        private readonly ILogger<BackupService> _logger;
        private readonly IBackupRestorer _restorer;

        // Serializes create + restore against each other: both touch the live profile (create reads it, restore
        // swaps it), so a concurrent pair could otherwise zip a torn state or interleave two area-swaps into a
        // mixed old/new profile. One backup operation at a time — the daemon is single-user and these are infrequent.
        private readonly SemaphoreSlim _gate = new(1, 1);

        // Cancelled on Dispose (this is a DI singleton, so dispose == host shutdown), so a restore worker blocked on
        // the gate or inside the restorer unblocks instead of hanging the host. The request token can't serve this —
        // it's cancelled the moment the 202 response is sent, before the worker has done anything.
        private readonly CancellationTokenSource _shutdown = new();

        // §43-2b(b) — how much remote archive the daemon will accept before refusing the
        // restore. Config areas are KBs; the cap only exists so a wrong/hostile URL can't
        // fill the disk. Internal test seam (shrink to exercise the cap without a big file).
        internal long MaxRemoteArchiveBytes { get; set; } = 512L * 1024 * 1024;

        // §43-2b(b) — read-progress watchdog for the remote download (the DataManagerService
        // pattern): the deadline re-arms on every byte, so a healthy transfer never trips it,
        // but a peer that accepts the connection and then stalls (headers OR body) is cancelled
        // instead of holding the single clone slot — and with it EVERY future restore — hostage
        // until a daemon restart. Internal test seam.
        internal TimeSpan RemoteIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

        private readonly IBackupSourceFetcher? _remoteFetcher;
        // §43-2b retention — read live per create so a settings change applies to the next backup
        // without a restart. Null (tests / minimal wiring) → the DTO default applies.
        private readonly IProfileStore? _profiles;

        public BackupService(string profileDir, ILogger<BackupService> logger, IBackupRestorer? restorer = null,
                IBackupSourceFetcher? remoteFetcher = null, IProfileStore? profiles = null) {
            ArgumentException.ThrowIfNullOrEmpty(profileDir);
            ArgumentNullException.ThrowIfNull(logger);
            _profileDir = profileDir;
            _backupsDir = Path.Combine(profileDir, BackupsDirName);
            _logger = logger;
            _restorer = restorer ?? new DefaultBackupRestorer();
            _remoteFetcher = remoteFetcher;
            _profiles = profiles;
        }

        // Registered as a DI singleton, so the container disposes this on host shutdown.
        public void Dispose() {
            // Cancel first so any in-flight restore worker unblocks (gate wait / restorer) before we dispose the gate.
            _shutdown.Cancel();
            _shutdown.Dispose();
            _gate.Dispose();
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
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try {
                await Task.Run(() => CreateBackupCore(id, createdUtc, ct), ct).ConfigureAwait(false);
            } finally {
                _gate.Release();
            }

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
            long? framesRows = null;
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

                    framesRows = TryAddDatabaseSnapshot(archive, id, areas, ct);
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
                var manifest = new BackupManifest(id, createdUtc, sizeBytes, sha256, areas, framesRows);
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

            // §43-2b retention — runs after the reveal, still under the caller's _gate (so it can't
            // interleave with another create or with a restore's extract+swap). Best-effort: a prune
            // fault must never fail the backup that just succeeded.
            PruneOldSnapshots();
        }

        /// <summary>
        /// §43-2b(c) — snapshot the §28 catalog into the archive as <c>db/openastroara.db</c> and
        /// return its frames row count for the manifest. <c>BackupDatabase</c> produces a
        /// consistent point-in-time copy even while writers are active (the catalog runs WAL),
        /// and the copy is self-contained — no <c>-wal</c>/<c>-shm</c> sidecars — so restore is a
        /// single-file swap. Best-effort by design: a catalog hiccup (locked, torn, absent on a
        /// fresh daemon) must degrade to a config-only backup with a logged warning and an honest
        /// area list, never deny the profile/sequences backup that motivated the request.
        /// </summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Best-effort area boundary: any SQLite/IO fault while snapshotting degrades to a config-only backup whose manifest honestly omits the area; the fault is logged, not swallowed. CA1031's log-and-recover boundary applies.")]
        private long? TryAddDatabaseSnapshot(ZipArchive archive, Guid id, List<string> areas, CancellationToken ct) {
            var dbPath = Path.Combine(_profileDir, DatabaseFileName);
            if (!File.Exists(dbPath)) {
                return null; // fresh daemon before first catalog init — nothing to capture
            }
            var snapshotTmp = Path.Combine(_backupsDir,
                TempPrefix + id.ToString("N", CultureInfo.InvariantCulture) + ".db");
            try {
                ct.ThrowIfCancellationRequested();
                long? rows;
                var sourceCs = new SqliteConnectionStringBuilder {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString();
                var destCs = new SqliteConnectionStringBuilder {
                    DataSource = snapshotTmp,
                    Pooling = false,
                }.ToString();
                using (var source = new SqliteConnection(sourceCs))
                using (var dest = new SqliteConnection(destCs)) {
                    source.Open();
                    dest.Open();
                    source.BackupDatabase(dest);
                    rows = CountFramesRows(dest);
                }
                ct.ThrowIfCancellationRequested();
                archive.CreateEntryFromFile(snapshotTmp, DatabaseZipEntry, CompressionLevel.Optimal);
                areas.Add(FrameMetadataArea);
                return rows;
            } catch (OperationCanceledException) {
                throw; // cancellation is the caller's contract, not a degradable snapshot fault
            } catch (Exception ex) {
                LogDbSnapshotFailed(ex);
                return null;
            } finally {
                TryDelete(snapshotTmp);
            }
        }

        private static long? CountFramesRows(SqliteConnection snapshot) {
            try {
                using var cmd = snapshot.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM frames";
                return cmd.ExecuteScalar() is long n ? n : null;
            } catch (SqliteException) {
                return null; // no frames table (pre-§28 or empty-schema db) — the snapshot itself still counts
            }
        }

        /// <summary>Keeps the newest <c>storage.backup_retention_count</c> snapshots (by manifest
        /// CreatedUtc, the ListSnapshots ordering) and deletes the rest — manifest FIRST, then zip,
        /// the reverse of the create-reveal order, so a reader never sees a manifest naming a
        /// deleted archive. 0 (or an unreadable profile reporting nothing) with a negative value
        /// disables pruning; no profile store → the DTO default (20). Best-effort per file.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Retention is best-effort by design: a prune fault (locked file, torn manifest, profile read error) must never fail the create that just succeeded. Faults are logged, not swallowed silently.")]
        private void PruneOldSnapshots() {
            try {
                // Mirrors StorageSettingsDto's ctor default — the value a null store or failed read falls to.
                const int DefaultBackupRetention = 20;
                var keep = DefaultBackupRetention;
                try {
                    var configured = _profiles?.GetStorageSettings().BackupRetentionCount;
                    if (configured is { } v) keep = v;
                } catch (Exception ex) {
                    LogPolicyReadFailedForPrune(ex);
                }
                if (keep <= 0) return; // 0 = keep everything (negative treated the same, defensively)

                var manifests = Directory.GetFiles(_backupsDir, ZipPrefix + "*" + ManifestExtension, SearchOption.TopDirectoryOnly);
                if (manifests.Length <= keep) return;
                var dated = new List<(string ManifestPath, DateTimeOffset CreatedUtc)>(manifests.Length);
                foreach (var manifestPath in manifests) {
                    try {
                        var manifest = JsonSerializer.Deserialize(
                            File.ReadAllText(manifestPath), AraJsonSerializerContext.Default.BackupManifest);
                        if (manifest is not null) {
                            dated.Add((manifestPath, manifest.CreatedUtc));
                        }
                    } catch (Exception) {
                        // Unreadable manifest — leave the pair alone (the orphan sweep story owns torn
                        // artifacts); deleting on a parse error could destroy a good archive.
                    }
                }
                var pruned = 0;
                foreach (var (manifestPath, _) in dated.OrderByDescending(m => m.CreatedUtc).Skip(keep)) {
                    var zipPath = manifestPath[..^ManifestExtension.Length] + ZipExtension;
                    TryDelete(manifestPath); // manifest first — never a listed-but-missing archive
                    TryDelete(zipPath);
                    pruned++;
                }
                if (pruned > 0) {
                    LogSnapshotsPruned(pruned, keep);
                }
            } catch (Exception ex) {
                LogPruneFailed(ex);
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

        /// <summary>
        /// Reclaim crash-only orphan archives under <c>{profileDir}/backups/</c> that <see cref="ListSnapshotsAsync"/>
        /// already ignores but never deletes: (a) a <c>.tmp-*.zip</c> staged by a create that was hard-killed before
        /// its <c>File.Move</c> reveal, (b) a fully-named <c>backup-*.zip</c> with no matching <c>.meta.json</c>
        /// sidecar — a SIGKILL in the window between the reveal and the manifest write — and (c) a <c>.tmp-*.db</c>
        /// catalog snapshot from a create hard-killed mid-<c>BackupDatabase</c> (§43-2b(c)). A graceful create reclaims
        /// its own temps on an exception, so these only linger after a hard kill. Returns the count removed. Best-effort
        /// + synchronous (a handful of files); a locked/permission-denied file is skipped (the next boot retries). Mirrors
        /// §36-2c <see cref="SkyDataInstaller.SweepStaleScratch"/>. Called at startup before the daemon accepts
        /// requests, so no concurrent create can race a half-written archive into the sweep.
        /// </summary>
        internal static int SweepOrphans(string profileDir, ILogger? logger = null) {
            ArgumentException.ThrowIfNullOrEmpty(profileDir);
            var backupsDir = Path.Combine(profileDir, BackupsDirName);
            string[] zips;
            try {
                if (!Directory.Exists(backupsDir)) {
                    return 0;
                }
                // GetFiles (materialized), not EnumerateFiles: we delete during the loop, and removing entries from a
                // directory mid lazy-enumeration can skip or repeat names. The read-only instance methods can stream.
                // Both temp extensions we stage: the archive being packaged AND the §43-2b(c) catalog snapshot.
                zips = [
                    .. Directory.GetFiles(backupsDir, "*" + ZipExtension, SearchOption.TopDirectoryOnly),
                    .. Directory.GetFiles(backupsDir, TempPrefix + "*.db", SearchOption.TopDirectoryOnly),
                ];
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return 0;
            }

            var removedNames = new List<string>();
            foreach (var path in zips) {
                var name = Path.GetFileName(path);
                bool orphan;
                if (name.StartsWith(TempPrefix, StringComparison.Ordinal)) {
                    // (a) staged temp from a create hard-killed before its File.Move reveal.
                    orphan = true;
                } else if (name.StartsWith(ZipPrefix, StringComparison.Ordinal)) {
                    // (b) a revealed archive whose manifest write never landed.
                    var manifestPath = Path.Combine(backupsDir, Path.GetFileNameWithoutExtension(path) + ManifestExtension);
                    orphan = !File.Exists(manifestPath);
                } else {
                    // A foreign .zip we didn't write — leave it untouched.
                    continue;
                }
                if (!orphan) {
                    continue;
                }
                try {
                    File.Delete(path);
                    removedNames.Add(name);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // best-effort — leave it; the next boot retries.
                }
            }

            // Surface a hard-kill to the operator: a non-empty sweep means the daemon died mid-backup last run. Silent
            // deletion would leave a missing backup unexplained. Only logs when something was actually reclaimed.
            if (removedNames.Count > 0 && logger is not null) {
                LogOrphansSwept(logger, removedNames.Count, string.Join(", ", removedNames));
            }
            return removedNames.Count;
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

        public Task<OperationAcceptedDto> RestoreZipAsync(RestoreRequestDto request, string? idempotencyKey, CancellationToken ct) {
            ArgumentNullException.ThrowIfNull(request);

            // §43-2b(c): three restorable areas — profiles, sequences, frames_metadata (the catalog snapshot).
            // restore_logs remains reserved: logs are not a §43.4 zip area (§43.8 only prunes them), so the flag
            // is honoured only insofar as an archive contains a logs area (none do) — a no-op, not an error.
            if (!request.RestoreProfiles && !request.RestoreSequences && !request.RestoreFrameMetadata) {
                throw new BackupRestoreNoAreaSelectedException(
                    "No restorable area selected — set restore_profiles, restore_sequences and/or restore_frame_metadata.");
            }

            // The DTO declares the URL non-nullable, but the JSON deserializer doesn't enforce that at runtime —
            // a body with "backup_source_url": null must stay the clean 422 it was pre-remote-support, not an NRE.
            if (request.BackupSourceUrl is null) {
                throw new BackupRestoreSourceUnsupportedException("backup_source_url is required.");
            }

            // §43-2b(b) — source discrimination. A RELATIVE URL is always local (that's the shape
            // ListSnapshots hands out and the client echoes back). An ABSOLUTE http(s) URL whose
            // path parses as our snapshot route AND whose id exists on disk stays local too (the
            // pre-(b) parser was host-blind — a client that resolved the relative URL against the
            // daemon base keeps working). Any other absolute http(s) URL is a REMOTE source:
            // download → checksum → restore, on the worker. Everything else stays 422.
            var parsed = ParseLocalSnapshotId(request.BackupSourceUrl);
            string? zipPath = parsed is { } localId ? FindZipPath(localId) : null;
            if (zipPath is null && IsRemoteSource(request.BackupSourceUrl)) {
                return Task.FromResult(BeginRemoteRestore(request, idempotencyKey));
            }
            if (parsed is not { } id) {
                throw new BackupRestoreSourceUnsupportedException(
                    "Restore source must be a local snapshot URL (/api/v1/backup/snapshot/{id}/download) "
                    + "or a remote http(s) backup archive URL.");
            }

            // §43-2b: validate cheaply + SYNCHRONOUSLY (so unknown-snapshot 404 still surfaces as an HTTP error
            // before the 202), then run everything slow — the checksum hash (§43-2b(c)) and the
            // live-config-mutating extract+swap — on a background worker reporting via the poll-able
            // clone-status. The 202/operation-id wire contract was already in place for exactly this.
            if (zipPath is null) {
                throw new BackupSnapshotNotFoundException($"No backup snapshot {id} to restore from.");
            }

            // Fast-fail a concurrent restore BEFORE the (relatively) expensive checksum hash: a restore already in
            // flight can't proceed regardless of this archive's validity, so 409 wins over a would-be 422 and we skip
            // hashing the archive for nothing. The authoritative claim under the lock below re-checks to close the
            // peek→claim race.
            if (RestoreInProgress()) {
                throw new BackupRestoreInProgressException("A restore is already in progress.");
            }
            // The checksum hash moved onto the worker with §43-2b(c): the archive now carries the
            // catalog snapshot, so hashing it can take real time on a large library — a corrupt
            // archive surfaces as a failed clone-status instead of a synchronous 422 (the flagged
            // §43-2b follow-up, warranted now that the payload outgrew "config-sized").

            // Claim the single clone slot. Only one restore at a time — a second while one runs is a 409, not a
            // queued op (both mutate the same live profile). Atomic under the lock so two requests can't both claim.
            var opId = Guid.NewGuid();
            lock (_cloneLock) {
                if (_clone.State == RunningState) {
                    throw new BackupRestoreInProgressException("A restore is already in progress.");
                }
                _clone = new CloneState(RunningState, null, null, "Restoring…");
            }

            // Fire-and-forget on the thread pool; the worker owns the gate + the terminal clone-status. ct is the
            // request token — it's cancelled once the 202 response completes, so the worker must NOT observe it.
            _ = Task.Run(() => RunRestoreAsync(opId, zipPath, request, snapshotId: id), CancellationToken.None);
            LogRestoreStarted(opId, id);
            return Task.FromResult(new OperationAcceptedDto(
                OperationId: opId,
                OperationType: "backup.restore-zip",
                AcceptedUtc: DateTimeOffset.UtcNow,
                IdempotencyKey: idempotencyKey));
        }

        // §43-2b(b) — a remote source is any absolute http(s) URL (that didn't resolve to a local snapshot).
        // Plain http is deliberately allowed: the typical source is another LAN daemon and v0.0.1 has no TLS
        // (§2.3); integrity rides on the request's REQUIRED sha256, not the transport.
        private static bool IsRemoteSource(Uri url) =>
            url.IsAbsoluteUri
            && (string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                || string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal));

        // Sync half of a remote restore: cheap validation (fetcher present, sha256 well-formed, no concurrent
        // restore) so the caller still gets a crisp HTTP error, then claim the clone slot and hand the slow
        // download+verify+swap to the worker. Mirrors the local flow's shape exactly.
        private OperationAcceptedDto BeginRemoteRestore(RestoreRequestDto request, string? idempotencyKey) {
            if (_remoteFetcher is null) {
                throw new BackupRestoreSourceUnsupportedException(
                    "This daemon has no remote backup fetcher configured — only local snapshot restores are available.");
            }
            // The out-of-band checksum is MANDATORY for remote sources (the local manifest warn-and-proceed
            // fallback does not apply here — an unvalidated remote archive must never reach the live-config swap).
            if (request.Sha256 is not { Length: 64 } sha || !sha.All(Uri.IsHexDigit)) {
                throw new BackupRestoreSourceUnsupportedException(
                    "A remote restore requires sha256: the expected SHA-256 of the archive as 64 hex characters "
                    + "(shown in the source daemon's snapshot list).");
            }
            if (RestoreInProgress()) {
                throw new BackupRestoreInProgressException("A restore is already in progress.");
            }
            var opId = Guid.NewGuid();
            lock (_cloneLock) {
                if (_clone.State == RunningState) {
                    throw new BackupRestoreInProgressException("A restore is already in progress.");
                }
                _clone = new CloneState(RunningState, null, null, "Downloading…");
            }
            _ = Task.Run(() => RunRemoteRestoreAsync(opId, request), CancellationToken.None);
            LogRemoteRestoreStarted(opId, request.BackupSourceUrl);
            return new OperationAcceptedDto(
                OperationId: opId,
                OperationType: "backup.restore-zip",
                AcceptedUtc: DateTimeOffset.UtcNow,
                IdempotencyKey: idempotencyKey);
        }

        // Remote-restore worker: download the archive to a staged temp (the orphan sweep's .tmp-*.zip pattern, so
        // a hard kill mid-download is reclaimed at next boot), verify it against the request's sha256, then hand
        // the verified file to the SAME extract+swap worker a local restore uses. A download/verify failure lands
        // in the failed clone-status; RunRestoreAsync owns the terminal state for everything after.
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Top-level fire-and-forget worker (same contract as RunRestoreAsync): every download/verify " +
                "failure must land in the 'failed' clone-status rather than escape as an unobserved task exception.")]
        private async Task RunRemoteRestoreAsync(Guid opId, RestoreRequestDto request) {
            var tempPath = Path.Combine(_backupsDir,
                TempPrefix + "restore-" + opId.ToString("N", CultureInfo.InvariantCulture) + ZipExtension);
            try {
                try {
                    Directory.CreateDirectory(_backupsDir);
                    await DownloadAndVerifyAsync(request.BackupSourceUrl, request.Sha256!, tempPath, _shutdown.Token).ConfigureAwait(false);
                } catch (Exception ex) {
                    SetClone(new CloneState(FailedState, null, null, ex.Message));
                    LogRemoteRestoreFailed(opId, ex);
                    return;
                }
                await RunRestoreAsync(opId, tempPath, request).ConfigureAwait(false);
            } finally {
                TryDelete(tempPath);
            }
        }

        // Streams the remote archive to disk while hashing it, bounded three ways: by the advertised
        // Content-Length (refuse before the first byte), by actual bytes received (a server that lies about —
        // or omits — the length still can't fill the disk), and by the RemoteIdleTimeout read-progress watchdog
        // (a peer that stalls on headers or mid-body is cancelled — MaxRemoteArchiveBytes bounds volume, not time,
        // and the backup-source HttpClient deliberately has no Timeout).
        private async Task DownloadAndVerifyAsync(Uri source, string expectedSha256, string tempPath, CancellationToken ct) {
            using var idleCts = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idleCts.Token);
            var linkedCt = linked.Token;
            try {
                // Arm BEFORE OpenAsync so the header-wait phase is bounded too (the DataManagerService pattern).
                idleCts.CancelAfter(RemoteIdleTimeout);
                await using var fetch = await _remoteFetcher!.OpenAsync(source, linkedCt).ConfigureAwait(false);
                if (fetch.TotalBytes is { } advertised && advertised > MaxRemoteArchiveBytes) {
                    throw new InvalidOperationException(
                        $"Remote backup archive advertises {advertised} bytes — over this daemon's {MaxRemoteArchiveBytes}-byte cap; not restored.");
                }
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long received = 0;
                var stagedOk = false;
                try {
                    await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
                        var buffer = new byte[81920];
                        int read;
                        idleCts.CancelAfter(RemoteIdleTimeout); // fresh deadline for the body; re-armed per read below.
                        while ((read = await fetch.Content.ReadAsync(buffer, linkedCt).ConfigureAwait(false)) > 0) {
                            idleCts.CancelAfter(RemoteIdleTimeout);
                            received += read;
                            if (received > MaxRemoteArchiveBytes) {
                                throw new InvalidOperationException(
                                    $"Remote backup archive exceeded this daemon's {MaxRemoteArchiveBytes}-byte cap mid-download; not restored.");
                            }
                            hash.AppendData(buffer, 0, read);
                            await file.WriteAsync(buffer.AsMemory(0, read), linkedCt).ConfigureAwait(false);
                        }
                    }
                    var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                    if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase)) {
                        throw new BackupCorruptException(
                            "The downloaded backup archive failed its checksum and was not restored (transfer corruption, or the wrong sha256).");
                    }
                    stagedOk = true;
                } finally {
                    // Never leave a failed/oversize/corrupt download staged; the sweep would reclaim it at next
                    // boot, but there is no reason to hold disk until then.
                    if (!stagedOk) {
                        TryDelete(tempPath);
                    }
                }
            } catch (OperationCanceledException) when (idleCts.IsCancellationRequested && !ct.IsCancellationRequested) {
                // The watchdog fired (not a daemon shutdown): report the stall as the restore failure — the
                // clone slot must free up for the next attempt instead of wedging until a process restart.
                throw new InvalidOperationException(
                    $"Remote backup download stalled (no data received for {RemoteIdleTimeout.TotalSeconds:F0}s); not restored.");
            }
        }

        // The restore worker: extract + atomically swap the validated archive into the live profile, driving the
        // clone-status from idle→running (set by the caller) → done/failed here.
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Top-level fire-and-forget worker: every failure must land in the 'failed' clone-status " +
                "(and be logged) rather than escaping as an unobserved task exception. It is reported, not swallowed.")]
        [SuppressMessage("Performance", "CA1873:Avoid potentially expensive logging",
            Justification = "The joined argument is at most three short area names — trivial, " +
                "once per restore, not a hot path.")]
        private async Task RunRestoreAsync(Guid opId, string zipPath, RestoreRequestDto request, Guid? snapshotId = null) {
            // Default terminal — if the body somehow exits without assigning one (e.g. an OOM while building the
            // success/failure state), the finally still drives clone-status off "running" so it can't 409 every
            // future restore. SetClone runs in the finally so a throw in the catch can't strand the state machine.
            var terminal = FailedFallback;
            try {
                // §43-2b(c) — the manifest checksum gate runs HERE, off the request thread (the archive now
                // carries the catalog snapshot, so hashing can take real time on a large library). Only local
                // snapshots carry a manifest; a remote source was already verified against the request's
                // required sha256 by the download worker.
                if (snapshotId is { } localSnapshot) {
                    ValidateChecksum(localSnapshot, zipPath);
                }
                // _shutdown lets a graceful host shutdown unblock this worker: the request token is cancelled once
                // the 202 is sent, so it can't be used; without a token a shutdown during an in-flight create would
                // block this WaitAsync (and the restorer) forever. Serialize against create + any other restore.
                await _gate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                try {
                    var restored = _restorer.Restore(
                        zipPath, _profileDir, request.RestoreProfiles, request.RestoreSequences,
                        request.RestoreFrameMetadata, _shutdown.Token);
                    terminal = new CloneState(DoneState, 100, null,
                        restored.Count == 0 ? "Nothing to restore" : "Restored: " + string.Join(", ", restored));
                    LogRestored(opId, string.Join(",", restored));
                } finally {
                    // A host-shutdown Dispose() can race this Release after _gate is disposed. Swallow that specific
                    // race so the ObjectDisposedException can't bubble to the outer catch and overwrite an
                    // already-successful 'done' terminal with a misleading 'failed'.
                    try {
                        _gate.Release();
                    } catch (ObjectDisposedException) {
                    }
                }
            } catch (Exception ex) {
                terminal = new CloneState(FailedState, null, null, ex.Message);
                LogRestoreFailed(opId, ex);
            } finally {
                SetClone(terminal);
            }
        }

        // Integrity gate before any live-config mutation: a corrupt archive must not reach the worker's swap.
        private void ValidateChecksum(Guid id, string zipPath) {
            var manifestPath = zipPath[..^ZipExtension.Length] + ManifestExtension;
            var expectedSha = TryReadManifestSha(manifestPath);
            if (expectedSha is null) {
                // No readable manifest → the checksum gate is bypassed. Surface it so an operator can see a restore
                // ran unvalidated (a listed snapshot always has a manifest; a missing one means a torn/edited backup).
                LogManifestSkipped(manifestPath, new FileNotFoundException("backup manifest missing or unreadable", manifestPath));
            } else if (!string.Equals(HashFile(zipPath), expectedSha, StringComparison.OrdinalIgnoreCase)) {
                throw new BackupCorruptException($"Backup snapshot {id} failed its checksum and was not restored.");
            }
        }

        private void SetClone(CloneState s) {
            lock (_cloneLock) {
                _clone = s;
            }
        }

        private bool RestoreInProgress() {
            lock (_cloneLock) {
                return _clone.State == RunningState;
            }
        }

        // A restore source is supported only when its path is EXACTLY our snapshot-download route —
        // api/v1/backup/snapshot/{guid}/download — not merely "snapshot/{guid}/download" appearing somewhere in an
        // arbitrary (e.g. external-host) URL. The guid is resolved against on-disk snapshots regardless of host.
        private static Guid? ParseLocalSnapshotId(Uri? url) {
            if (url is null) {
                return null;
            }
            var path = url.IsAbsoluteUri ? url.AbsolutePath : url.OriginalString;
            var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 6 &&
                segs[0] == "api" && segs[1] == "v1" && segs[2] == "backup" &&
                segs[3] == "snapshot" && segs[5] == "download" &&
                Guid.TryParse(segs[4], out var id)) {
                return id;
            }
            return null;
        }

        private static string? TryReadManifestSha(string manifestPath) {
            try {
                var manifest = JsonSerializer.Deserialize(
                    File.ReadAllText(manifestPath), AraJsonSerializerContext.Default.BackupManifest);
                return manifest?.Sha256;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                // No readable manifest → skip the checksum gate rather than block a restore on a missing sidecar.
                return null;
            }
        }

        public Task<JsonElement> GetCloneStatusAsync(CancellationToken ct) {
            // §43-2b: report the restore worker's live state. Snapshot under the lock, then build the wire object.
            // (ToJsonString()+Parse is the AOT-safe path to a JsonElement; the doc is disposed once cloned.)
            CloneState s;
            lock (_cloneLock) {
                s = _clone;
            }
            var obj = new JsonObject {
                ["state"] = s.State,
                ["progress_pct"] = s.ProgressPct,
                ["current_area"] = s.CurrentArea,
                ["message"] = s.Message,
            };
            using var doc = JsonDocument.Parse(obj.ToJsonString());
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

        [LoggerMessage(Level = LogLevel.Information, Message = "Backup restore {OperationId} restored areas [{Areas}]")]
        partial void LogRestored(Guid operationId, string areas);

        // Static (the sweep runs at startup before the service instance exists), so it takes the logger explicitly.
        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Reclaimed {Count} crash-orphaned backup archive(s) at startup (daemon died mid-backup): {Files}")]
        private static partial void LogOrphansSwept(ILogger logger, int count, string files);

        [LoggerMessage(Level = LogLevel.Information, Message = "Backup restore {OperationId} started from snapshot {BackupId}")]
        partial void LogRestoreStarted(Guid operationId, Guid backupId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Backup restore {OperationId} started from remote source {Source}")]
        partial void LogRemoteRestoreStarted(Guid operationId, Uri source);

        [LoggerMessage(Level = LogLevel.Information, Message = "Backup retention pruned {Count} snapshot(s) beyond the keep-{Keep} policy")]
        partial void LogSnapshotsPruned(int count, int keep);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Backup retention could not read the storage settings — using the default keep count")]
        partial void LogPolicyReadFailedForPrune(Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Backup retention prune failed — the new snapshot is unaffected")]
        partial void LogPruneFailed(Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Backup restore {OperationId} failed downloading/verifying its remote source")]
        partial void LogRemoteRestoreFailed(Guid operationId, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Backup restore {OperationId} failed")]
        partial void LogRestoreFailed(Guid operationId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Backup archive {ArchivePath} vanished between resolve and open — serving 404")]
        partial void LogSnapshotVanished(string archivePath, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Backup create-zip received an Idempotency-Key but dedup is not implemented (§43-2) — a retry will create another archive")]
        partial void LogDeduplicationNotImplemented();

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Backup could not snapshot the frames catalog — this backup is config-only (its manifest omits frames_metadata)")]
        partial void LogDbSnapshotFailed(Exception ex);
    }

    /// <summary>Thrown by <see cref="BackupService.CreateZipAsync"/> when there is nothing to back up — neither a
    /// <c>profile.json</c> nor a <c>sequences/</c> tree exists — so a content-free zip would otherwise list as a
    /// real-but-useless snapshot. The create endpoint maps it to <c>422 Unprocessable Entity</c>.</summary>
    public sealed class BackupNothingToArchiveException : Exception {
        public BackupNothingToArchiveException() { }
        public BackupNothingToArchiveException(string message) : base(message) { }
        public BackupNothingToArchiveException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>Thrown by <see cref="BackupService.RestoreZipAsync"/> when the requested snapshot doesn't exist on
    /// disk. The restore endpoint maps it to <c>404 Not Found</c>.</summary>
    public sealed class BackupSnapshotNotFoundException : Exception {
        public BackupSnapshotNotFoundException() { }
        public BackupSnapshotNotFoundException(string message) : base(message) { }
        public BackupSnapshotNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>Thrown by <see cref="BackupService.RestoreZipAsync"/> when a restore is already running (only one at a
    /// time — both mutate the live profile). The restore endpoint maps it to <c>409 Conflict</c>.</summary>
    public sealed class BackupRestoreInProgressException : Exception {
        public BackupRestoreInProgressException() { }
        public BackupRestoreInProgressException(string message) : base(message) { }
        public BackupRestoreInProgressException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>Thrown by <see cref="BackupService.RestoreZipAsync"/> when the restore source isn't a supported
    /// local snapshot URL. The restore endpoint maps it to <c>422 Unprocessable Entity</c>.</summary>
    public sealed class BackupRestoreSourceUnsupportedException : Exception {
        public BackupRestoreSourceUnsupportedException() { }
        public BackupRestoreSourceUnsupportedException(string message) : base(message) { }
        public BackupRestoreSourceUnsupportedException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>Thrown by <see cref="BackupService.RestoreZipAsync"/> when the request selects no restorable area —
    /// a distinct validation failure from an unsupported source. The restore endpoint maps it to <c>422</c>.</summary>
    public sealed class BackupRestoreNoAreaSelectedException : Exception {
        public BackupRestoreNoAreaSelectedException() { }
        public BackupRestoreNoAreaSelectedException(string message) : base(message) { }
        public BackupRestoreNoAreaSelectedException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>Thrown by <see cref="BackupService.RestoreZipAsync"/> when the archive fails its manifest checksum,
    /// so it is refused before any live config is touched. The restore endpoint maps it to <c>422</c>.</summary>
    public sealed class BackupCorruptException : Exception {
        public BackupCorruptException() { }
        public BackupCorruptException(string message) : base(message) { }
        public BackupCorruptException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>Thrown by the §43-2 restore engine in the worst case — a swap failed AND the prior copy could not be
    /// rolled back, so an area may be in a mixed state and the previous copy survives only in the backup dir named in
    /// the message. <see cref="Exception.InnerException"/> is an <see cref="AggregateException"/> of (original
    /// failure, rollback failure). A dedicated type (not the sky-data installer's) so it isn't caught cross-subsystem;
    /// the restore endpoint leaves it uncaught → <c>500</c>, the correct status for genuine data loss.</summary>
    public sealed class BackupRestoreException : Exception {
        public BackupRestoreException() { }
        public BackupRestoreException(string message) : base(message) { }
        public BackupRestoreException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>On-disk backup manifest (sidecar <c>.meta.json</c>). The download URL in <see cref="BackupZipDto"/>
    /// is derived from <see cref="BackupId"/> at list time, so it is not persisted here.</summary>
    public sealed record BackupManifest(
        Guid BackupId,
        DateTimeOffset CreatedUtc,
        long SizeBytes,
        string Sha256,
        IReadOnlyList<string> IncludedAreas,
        // §43.7 contents count — additive-optional so pre-(c) manifests (no field) keep
        // deserializing; null also means "snapshot skipped" (the area list is authoritative).
        long? FramesMetadataRows = null);
}
