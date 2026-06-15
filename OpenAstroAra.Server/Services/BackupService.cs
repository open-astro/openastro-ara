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

        public BackupService(string profileDir, ILogger<BackupService> logger, IBackupRestorer? restorer = null) {
            ArgumentException.ThrowIfNullOrEmpty(profileDir);
            ArgumentNullException.ThrowIfNull(logger);
            _profileDir = profileDir;
            _backupsDir = Path.Combine(profileDir, BackupsDirName);
            _logger = logger;
            _restorer = restorer ?? new DefaultBackupRestorer();
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

        /// <summary>
        /// Reclaim crash-only orphan archives under <c>{profileDir}/backups/</c> that <see cref="ListSnapshotsAsync"/>
        /// already ignores but never deletes: (a) a <c>.tmp-*.zip</c> staged by a create that was hard-killed before
        /// its <c>File.Move</c> reveal, and (b) a fully-named <c>backup-*.zip</c> with no matching <c>.meta.json</c>
        /// sidecar — a SIGKILL in the window between the reveal and the manifest write. A graceful create reclaims its
        /// own temp on an exception, so these only linger after a hard kill. Returns the count removed. Best-effort +
        /// synchronous (a handful of files); a locked/permission-denied file is skipped (the next boot retries). Mirrors
        /// §36-2c <see cref="SkyDataInstaller.SweepStaleScratch"/>. Called at startup before the daemon accepts
        /// requests, so no concurrent create can race a half-written archive into the sweep.
        /// </summary>
        internal static int SweepOrphans(string profileDir) {
            ArgumentException.ThrowIfNullOrEmpty(profileDir);
            var backupsDir = Path.Combine(profileDir, BackupsDirName);
            string[] zips;
            try {
                if (!Directory.Exists(backupsDir)) {
                    return 0;
                }
                zips = Directory.GetFiles(backupsDir, "*" + ZipExtension, SearchOption.TopDirectoryOnly);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return 0;
            }

            var removed = 0;
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
                    removed++;
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // best-effort — leave it; the next boot retries.
                }
            }
            return removed;
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

            // §43-2a: restore from a LOCAL snapshot only — the source URL must be our own snapshot-download route.
            // Restoring from an arbitrary/remote URL (e.g. §44 cloud backup) is a separate slice.
            var id = ParseLocalSnapshotId(request.BackupSourceUrl)
                ?? throw new BackupRestoreSourceUnsupportedException(
                    "Restore source must be a local snapshot URL (/api/v1/backup/snapshot/{id}/download).");

            // §43-1 backups carry the two config areas; frame-metadata/logs aren't captured yet, so those flags are
            // honoured only insofar as the archive contains them (it won't) — they're no-ops, not errors.
            if (!request.RestoreProfiles && !request.RestoreSequences) {
                throw new BackupRestoreNoAreaSelectedException(
                    "No restorable area selected — set restore_profiles and/or restore_sequences.");
            }

            // §43-2b: validate cheaply + SYNCHRONOUSLY (so unknown-snapshot 404 and corrupt-archive 422 still surface
            // as HTTP errors before the 202), then run the slow, live-config-mutating extract+swap on a background
            // worker and report its progress via the poll-able clone-status. The 202/operation-id wire contract was
            // already in place for exactly this.
            var zipPath = FindZipPath(id)
                ?? throw new BackupSnapshotNotFoundException($"No backup snapshot {id} to restore from.");

            // Fast-fail a concurrent restore BEFORE the (relatively) expensive checksum hash: a restore already in
            // flight can't proceed regardless of this archive's validity, so 409 wins over a would-be 422 and we skip
            // hashing the archive for nothing. The authoritative claim under the lock below re-checks to close the
            // peek→claim race.
            if (RestoreInProgress()) {
                throw new BackupRestoreInProgressException("A restore is already in progress.");
            }
            ValidateChecksum(id, zipPath);

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
            _ = Task.Run(() => RunRestoreAsync(opId, zipPath, request), CancellationToken.None);
            LogRestoreStarted(opId, id);
            return Task.FromResult(new OperationAcceptedDto(
                OperationId: opId,
                OperationType: "backup.restore-zip",
                AcceptedUtc: DateTimeOffset.UtcNow,
                IdempotencyKey: idempotencyKey));
        }

        // The restore worker: extract + atomically swap the validated archive into the live profile, driving the
        // clone-status from idle→running (set by the caller) → done/failed here.
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Top-level fire-and-forget worker: every failure must land in the 'failed' clone-status " +
                "(and be logged) rather than escaping as an unobserved task exception. It is reported, not swallowed.")]
        [SuppressMessage("Performance", "CA1873:Avoid potentially expensive logging",
            Justification = "The joined argument is at most two short area names ('profiles','sequences') — trivial, " +
                "once per restore, not a hot path.")]
        private async Task RunRestoreAsync(Guid opId, string zipPath, RestoreRequestDto request) {
            // Default terminal — if the body somehow exits without assigning one (e.g. an OOM while building the
            // success/failure state), the finally still drives clone-status off "running" so it can't 409 every
            // future restore. SetClone runs in the finally so a throw in the catch can't strand the state machine.
            var terminal = FailedFallback;
            try {
                // _shutdown lets a graceful host shutdown unblock this worker: the request token is cancelled once
                // the 202 is sent, so it can't be used; without a token a shutdown during an in-flight create would
                // block this WaitAsync (and the restorer) forever. Serialize against create + any other restore.
                await _gate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                try {
                    var restored = _restorer.Restore(
                        zipPath, _profileDir, request.RestoreProfiles, request.RestoreSequences, _shutdown.Token);
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

        [LoggerMessage(Level = LogLevel.Information, Message = "Backup restore {OperationId} started from snapshot {BackupId}")]
        partial void LogRestoreStarted(Guid operationId, Guid backupId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Backup restore {OperationId} failed")]
        partial void LogRestoreFailed(Guid operationId, Exception ex);

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
        IReadOnlyList<string> IncludedAreas);
}
