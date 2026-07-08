#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §43-2 restore engine: extracts a §43-1 backup <c>.zip</c> and swaps the selected config areas
    /// (<c>profile.json</c>, the <c>sequences/</c> tree) into the live profile directory <b>atomically</b>, with
    /// all-or-nothing rollback. The destructive part of §43 — overwriting live config — so it is built the same way
    /// as the §36-2a installer: extract into a staging dir, move each live area aside to a backup, move the staged
    /// area in, and only delete the backups once <em>every</em> requested area has landed. If any area fails to
    /// swap, the already-swapped areas are restored from their backups in reverse order, so a partial restore can
    /// never leave the profile in a mixed old/new state.
    ///
    /// Entry extraction guards against zip-slip (an entry resolving outside the staging dir is rejected); only the
    /// two §43-1 areas are recognised, so a backup id is never a path component the caller controls.
    /// </summary>
    internal static class BackupRestorer {

        private const string ProfileFileName = "profile.json";
        private const string SequencesDirName = "sequences";
        private const string DatabaseFileName = "openastroara.db";
        private const string DatabaseZipDir = "db";
        private const string StagingPrefix = ".restore-staging-";
        private const string BackupPrefix = ".restore-backup-";

        /// <summary>
        /// Restore the requested areas from <paramref name="zipPath"/> into <paramref name="profileDir"/>. Returns
        /// the areas actually restored (an area requested but absent from the archive is skipped, not an error).
        /// Throws <see cref="InvalidDataException"/> for a malformed archive (incl. zip-slip) and
        /// <see cref="SkyDataInstallException"/> only in the worst case where a swap failed AND a swapped-in area
        /// could not be rolled back (genuine data loss — the message names the backup location).
        /// </summary>
        internal static IReadOnlyList<string> Restore(
            string zipPath, string profileDir, bool restoreProfile, bool restoreSequences,
            bool restoreFrameMetadata, CancellationToken ct) {
            ArgumentException.ThrowIfNullOrEmpty(zipPath);
            ArgumentException.ThrowIfNullOrEmpty(profileDir);

            var token = Guid.NewGuid().ToString("N");
            var stagingDir = Path.Combine(profileDir, StagingPrefix + token);
            Directory.CreateDirectory(stagingDir);

            // Records a completed aside+swap so it can be committed (delete backup) or rolled back (restore backup).
            var swaps = new List<AreaSwap>();
            var restored = new List<string>();
            try {
                ExtractZip(zipPath, stagingDir, ct);

                if (restoreProfile) {
                    var staged = Path.Combine(stagingDir, ProfileFileName);
                    if (File.Exists(staged)) {
                        swaps.Add(SwapIn(staged, Path.Combine(profileDir, ProfileFileName), profileDir, token, isDirectory: false));
                        restored.Add("profiles");
                    }
                }
                if (restoreSequences) {
                    var staged = Path.Combine(stagingDir, SequencesDirName);
                    if (Directory.Exists(staged)) {
                        swaps.Add(SwapIn(staged, Path.Combine(profileDir, SequencesDirName), profileDir, token, isDirectory: true));
                        restored.Add("sequences");
                    }
                }
                if (restoreFrameMetadata) {
                    var staged = Path.Combine(stagingDir, DatabaseZipDir, DatabaseFileName);
                    if (File.Exists(staged)) {
                        var liveDb = Path.Combine(profileDir, DatabaseFileName);
                        // Release every pooled handle so the file moves cleanly. Repositories open a
                        // connection PER CALL (no long-lived handles), so an operation racing the swap
                        // fails once into its own log-and-recover boundary rather than corrupting
                        // either file; the restore gate already serializes against create/restore.
                        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                        // The catalog runs WAL: stale -wal/-shm sidecars belonging to the OLD database
                        // must never sit next to the swapped-in file — SQLite would replay the old WAL
                        // into the new db. They move aside WITH the main file (the §43-2b(c) snapshot
                        // is self-contained) and roll back — in reverse order, after the db — as a set.
                        swaps.Add(SetAside(liveDb + "-wal", profileDir, token));
                        swaps.Add(SetAside(liveDb + "-shm", profileDir, token));
                        swaps.Add(SwapIn(staged, liveDb, profileDir, token, isDirectory: false));
                        restored.Add(BackupService.FrameMetadataArea);
                    }
                }

                // Every requested-and-present area landed — discard the set-aside originals.
                foreach (var swap in swaps) {
                    TryDeletePath(swap.BackupPath, swap.IsDirectory);
                }
            } catch (Exception primary) {
                RollBack(swaps, primary);
                throw;
            } finally {
                TryDeleteDirectory(stagingDir);
            }
            return restored;
        }

        private readonly record struct AreaSwap(string LivePath, string BackupPath, bool IsDirectory, bool HadExisting);

        // Move a live file aside WITHOUT placing a staged replacement (the WAL/SHM sidecars of a
        // restored database have no counterpart in the snapshot). Commit deletes the aside copy;
        // rollback flows through the same machinery as a real swap — DeletePath on the (absent)
        // live path is a no-op, then the aside copy moves back.
        private static AreaSwap SetAside(string livePath, string profileDir, string token) {
            var backupPath = Path.Combine(profileDir, BackupPrefix + token + "-" + Path.GetFileName(livePath));
            var hadExisting = File.Exists(livePath);
            if (hadExisting) {
                File.Move(livePath, backupPath, overwrite: false);
            }
            return new AreaSwap(livePath, backupPath, IsDirectory: false, HadExisting: hadExisting);
        }

        // Move the live area aside (if present) and move the staged area into its place. The returned record carries
        // the info needed to either commit (delete the aside backup) or roll back (move the backup back).
        private static AreaSwap SwapIn(string stagedPath, string livePath, string profileDir, string token, bool isDirectory) {
            var backupPath = Path.Combine(profileDir, BackupPrefix + token + "-" + Path.GetFileName(livePath));
            var hadExisting = isDirectory ? Directory.Exists(livePath) : File.Exists(livePath);
            if (hadExisting) {
                MovePath(livePath, backupPath, isDirectory);
            }
            try {
                MovePath(stagedPath, livePath, isDirectory);
            } catch (Exception placeEx) {
                // Couldn't place the new area — put the original back before surfacing, so this area is untouched.
                if (hadExisting) {
                    try {
                        MovePath(backupPath, livePath, isDirectory);
                    } catch (Exception recoverEx) {
                        // The new area didn't land AND the original couldn't be restored — surface both and name the
                        // backup location, rather than letting the recovery failure escape and hide the placement error.
                        throw new BackupRestoreException(
                            $"Backup restore failed to place '{livePath}' and could not restore the previous copy; " +
                            $"it remains at '{backupPath}' for manual recovery.",
                            new AggregateException(placeEx, recoverEx));
                    }
                }
                throw;
            }
            return new AreaSwap(livePath, backupPath, isDirectory, hadExisting);
        }

        // Restore every already-swapped area from its backup, in reverse order, so a mid-restore failure leaves the
        // profile as it was rather than a mix of old and new areas. Every area is attempted even if an earlier one
        // fails — a single failing rollback must not abandon the remaining areas — and ALL failures are collected so
        // the surfaced error names every unrecovered backup, not just the first.
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Each area's rollback failure is collected (not swallowed) and re-surfaced together in a " +
                "BackupRestoreException after the loop; catching broadly is required to attempt every area's rollback " +
                "rather than bail on the first failure.")]
        private static void RollBack(List<AreaSwap> swaps, Exception primary) {
            var failures = new List<Exception> { primary };
            var unrecovered = new List<string>();
            for (var i = swaps.Count - 1; i >= 0; i--) {
                var swap = swaps[i];
                try {
                    // Drop the just-swapped-in copy (throwing delete, so a failure surfaces as data loss rather than
                    // being swallowed), then — if there was an original — move it back.
                    DeletePath(swap.LivePath, swap.IsDirectory);
                    if (swap.HadExisting) {
                        MovePath(swap.BackupPath, swap.LivePath, swap.IsDirectory);
                    }
                } catch (Exception rollbackEx) {
                    failures.Add(rollbackEx);
                    unrecovered.Add(swap.BackupPath);
                }
            }
            if (unrecovered.Count > 0) {
                // Data-loss case: surface the original failure AND every rollback failure together, naming each
                // unrecovered backup so an operator can recover manually.
                throw new BackupRestoreException(
                    $"Backup restore failed and {unrecovered.Count} area(s) could not be rolled back; the previous " +
                    $"copies remain for manual recovery at: {string.Join("; ", unrecovered)}.",
                    new AggregateException(failures));
            }
        }

        private static void MovePath(string source, string dest, bool isDirectory) {
            if (isDirectory) {
                Directory.Move(source, dest);
            } else {
                File.Move(source, dest, overwrite: false);
            }
        }

        // Throwing delete (vs the best-effort TryDeletePath) — used on the rollback path where a cleanup failure must
        // surface as data loss, not be swallowed.
        private static void DeletePath(string path, bool isDirectory) {
            if (isDirectory) {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            } else if (File.Exists(path)) {
                File.Delete(path);
            }
        }

        // Zip-slip-guarded extraction: every entry must resolve inside destDir. The file-extraction sink is guarded
        // immediately by a pure `!StartsWith(destPrefix) → throw` barrier — the exact form CodeQL's cs/zipslip query
        // recognises as a sanitizer (a compound `Equals || StartsWith` guard is not recognised and reads as a flow).
        private static void ExtractZip(string zipPath, string destDir, CancellationToken ct) {
            var destFull = Path.GetFullPath(destDir);
            var destPrefix = destFull.EndsWith(Path.DirectorySeparatorChar)
                ? destFull
                : destFull + Path.DirectorySeparatorChar;

            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries) {
                ct.ThrowIfCancellationRequested();

                if (entry.FullName.EndsWith('/')) {
                    // Directory entry — a benign one may resolve to destDir itself, which doesn't start with the
                    // separator-suffixed prefix; allow that, reject anything that escapes. (Not the cs/zipslip sink.)
                    var dirFull = Path.GetFullPath(Path.Combine(destFull, entry.FullName));
                    if (!dirFull.Equals(destFull, StringComparison.Ordinal) &&
                        !dirFull.StartsWith(destPrefix, StringComparison.Ordinal)) {
                        throw new InvalidDataException(
                            $"Backup entry '{entry.FullName}' resolves outside the restore staging directory and was rejected.");
                    }
                    Directory.CreateDirectory(dirFull);
                    continue;
                }

                var entryFull = Path.GetFullPath(Path.Combine(destFull, entry.FullName));
                if (!entryFull.StartsWith(destPrefix, StringComparison.Ordinal)) {
                    throw new InvalidDataException(
                        $"Backup entry '{entry.FullName}' resolves outside the restore staging directory and was rejected.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(entryFull)!);
                entry.ExtractToFile(entryFull, overwrite: true);
            }
        }

        private static void TryDeletePath(string path, bool isDirectory) {
            if (isDirectory) {
                TryDeleteDirectory(path);
            } else {
                try {
                    if (File.Exists(path)) {
                        File.Delete(path);
                    }
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // best-effort
                }
            }
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Best-effort cleanup of a staging/backup directory; a leaked .restore-* dir is harmless " +
                "and reclaimed on the next restore, and must never mask the restore's own outcome.")]
        private static void TryDeleteDirectory(string dir) {
            try {
                if (Directory.Exists(dir)) {
                    Directory.Delete(dir, recursive: true);
                }
            } catch (Exception) {
                // best-effort
            }
        }
    }
}
