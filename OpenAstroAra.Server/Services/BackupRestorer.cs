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
            string zipPath, string profileDir, bool restoreProfile, bool restoreSequences, CancellationToken ct) {
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
                        throw new SkyDataInstallException(
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
        // profile as it was rather than a mix of old and new areas.
        private static void RollBack(List<AreaSwap> swaps, Exception primary) {
            for (var i = swaps.Count - 1; i >= 0; i--) {
                var swap = swaps[i];
                try {
                    if (swap.HadExisting) {
                        // Drop the just-swapped-in copy, then move the original back.
                        TryDeletePath(swap.LivePath, swap.IsDirectory);
                        MovePath(swap.BackupPath, swap.LivePath, swap.IsDirectory);
                    } else {
                        // There was no original — the area didn't exist before, so removing what we placed restores that.
                        TryDeletePath(swap.LivePath, swap.IsDirectory);
                    }
                } catch (Exception rollbackEx) {
                    // ANY failure to roll an area back is the data-loss case — wrap it together with the original
                    // failure (never let an unexpected rollback exception escape and discard `primary`) and name the
                    // backup location for manual recovery.
                    throw new SkyDataInstallException(
                        $"Backup restore failed and the area at '{swap.LivePath}' could not be rolled back; the previous " +
                        $"copy remains at '{swap.BackupPath}' for manual recovery.",
                        new AggregateException(primary, rollbackEx));
                }
            }
        }

        private static void MovePath(string source, string dest, bool isDirectory) {
            if (isDirectory) {
                Directory.Move(source, dest);
            } else {
                File.Move(source, dest, overwrite: false);
            }
        }

        // Zip-slip-guarded extraction: every entry must resolve inside destDir.
        [SuppressMessage("Security", "CA5389:Do not add archive item's path to the target file system path",
            Justification = "The destination is validated to stay inside the staging dir (the zip-slip guard below " +
                "rejects any entry that resolves outside destDir) before ExtractToFile is ever called.")]
        private static void ExtractZip(string zipPath, string destDir, CancellationToken ct) {
            var destFull = Path.GetFullPath(destDir);
            var destPrefix = destFull.EndsWith(Path.DirectorySeparatorChar)
                ? destFull
                : destFull + Path.DirectorySeparatorChar;

            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries) {
                ct.ThrowIfCancellationRequested();
                var entryFull = Path.GetFullPath(Path.Combine(destFull, entry.FullName));
                if (!entryFull.Equals(destFull, StringComparison.Ordinal) &&
                    !entryFull.StartsWith(destPrefix, StringComparison.Ordinal)) {
                    throw new InvalidDataException(
                        $"Backup entry '{entry.FullName}' resolves outside the restore staging directory and was rejected.");
                }
                if (entry.FullName.EndsWith('/')) {
                    Directory.CreateDirectory(entryFull);
                    continue;
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
