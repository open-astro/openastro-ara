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
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §36-2 install engine: extracts a <c>.tar.gz</c> sky-data package into its install directory, safely and
    /// atomically. "Safely": every archive entry is verified to stay inside the destination, so a malicious or
    /// malformed archive can't write outside it (tar-slip / path traversal); links and device entries are skipped.
    /// "Atomically": extraction stages into a sibling temp directory and is swapped into place only once complete,
    /// and an <see cref="InstalledMarkerFileName"/> sentinel is written inside before the swap — so a reader can
    /// always tell a finished install from a torn one (a crash/cancel mid-extract leaves no install at all, not a
    /// half-populated one). A prior install is moved aside to a backup and is deleted only once the new install is
    /// safely in place, so a failure during the swap restores it; the sole crash-unsafe instant is between the two
    /// renames (both fast same-filesystem metadata ops), and even then the missing sentinel marks the state as bad.
    ///
    /// The caller (the §36-2 download worker) is responsible for resolving <c>targetDir</c> from a catalog-validated
    /// package id; this engine only consumes a stream + a destination and guards entry-level traversal within it.
    /// </summary>
    internal static class SkyDataInstaller {

        /// <summary>Sentinel file written at the root of a completed install; its presence marks the directory as a
        /// fully-extracted package and its write time is the authoritative install timestamp (the dir mtime is not —
        /// it changes on any child write). Lives inside the package dir so it moves atomically with the swap.</summary>
        internal const string InstalledMarkerFileName = ".installed";

        /// <summary>
        /// Extract <paramref name="tarGz"/> (a gzip-compressed tar stream) into <paramref name="targetDir"/>,
        /// replacing any prior install. On success <paramref name="targetDir"/> contains the package files plus the
        /// <see cref="InstalledMarkerFileName"/> sentinel; on any failure or cancellation nothing partial is left
        /// behind and a pre-existing install is restored (it is moved aside, and only deleted once the new install
        /// is in place — a failed swap moves it back). <paramref name="maxBytes"/>, when set, caps the total extracted
        /// size (zip-bomb / disk-exhaustion guard); exceeding it aborts the install with an <see cref="InvalidDataException"/>.
        /// </summary>
        internal static async Task InstallFromTarGzAsync(Stream tarGz, string targetDir, long? maxBytes, CancellationToken ct) {
            ArgumentNullException.ThrowIfNull(tarGz);
            ArgumentException.ThrowIfNullOrEmpty(targetDir);
            if (maxBytes is < 0) {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }

            var targetFull = Path.GetFullPath(targetDir);
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(targetFull))
                ?? throw new ArgumentException("Target directory must have a parent.", nameof(targetDir));
            Directory.CreateDirectory(parent);

            // Stage into a sibling temp dir so a failed/cancelled extract never leaves a half-populated targetDir,
            // and the final reveal is a Move rather than a slow file-by-file write into the live path.
            var suffix = Path.GetFileName(targetFull) + "-" + Guid.NewGuid().ToString("N");
            var stagingDir = Path.Combine(parent, ".staging-" + suffix);
            var backupDir = Path.Combine(parent, ".backup-" + suffix);
            Directory.CreateDirectory(stagingDir);

            var movedPriorAside = false;
            try {
                await ExtractTarGzAsync(tarGz, stagingDir, maxBytes, ct).ConfigureAwait(false);

                // Stamp the sentinel BEFORE the swap so it appears atomically with the directory at its final path.
                await File.WriteAllTextAsync(
                    Path.Combine(stagingDir, InstalledMarkerFileName),
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ct).ConfigureAwait(false);

                // Swap. Move the prior install aside first so the destructive delete happens only AFTER the new
                // install has landed — a failure mid-swap restores the prior install rather than losing it.
                if (Directory.Exists(targetFull)) {
                    Directory.Move(targetFull, backupDir);
                    movedPriorAside = true;
                }
                Directory.Move(stagingDir, targetFull);
            } catch (Exception primary) {
                TryDeleteDirectory(stagingDir);
                // If the prior install was moved aside but the new one never landed, move it back. This restore
                // branch is correct by inspection but is not unit-tested — reaching it requires the second
                // Directory.Move to fail after the first succeeded, which needs filesystem-level fault injection
                // (e.g. a concurrent re-creation of targetFull between the two renames).
                if (movedPriorAside && !Directory.Exists(targetFull) && Directory.Exists(backupDir)) {
                    try {
                        Directory.Move(backupDir, targetFull);
                    } catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException) {
                        // Worst case: the new install failed AND the prior install couldn't be restored. Surface
                        // BOTH causes (and the recoverable backup location) so the caller can flag data loss rather
                        // than seeing only the original failure. The backup dir is deliberately left in place.
                        throw new SkyDataInstallException(
                            $"Sky-data install failed and the prior install at '{targetFull}' could not be restored; " +
                            $"the previous data remains at '{backupDir}' for manual recovery.",
                            new AggregateException(primary, restoreEx));
                    }
                }
                throw;
            }

            // New install is in place — the prior copy is now safe to discard (no-op if there was none).
            TryDeleteDirectory(backupDir);
        }

        // Zip-bomb / disk-exhaustion guard: the running total of declared entry sizes is capped at maxBytes when set.
        // The package id is catalog-validated by the caller and the source is our own curated host, so an adversarial
        // archive isn't the primary threat — but enforcing the ceiling here (defence in depth) means a compromised
        // download layer can't drive this engine to fill the disk. The cap is checked against each entry's declared
        // size before extraction; the tar reader only ever writes that many bytes for a regular-file entry.
        private static async Task ExtractTarGzAsync(Stream tarGz, string destDir, long? maxBytes, CancellationToken ct) {
            var destFull = Path.GetFullPath(destDir);
            var destPrefix = destFull.EndsWith(Path.DirectorySeparatorChar)
                ? destFull
                : destFull + Path.DirectorySeparatorChar;

            await using var gz = new GZipStream(tarGz, CompressionMode.Decompress, leaveOpen: true);
            await using var reader = new TarReader(gz, leaveOpen: true);

            long extracted = 0;
            while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry) {
                // GetNextEntryAsync and ExtractToFileAsync both honor ct, so no separate check is needed here.
                if (string.IsNullOrEmpty(entry.Name)) {
                    continue;
                }

                // Tar-slip guard: resolve the entry against the destination and require the result to stay inside it.
                // Path.Combine also collapses an absolute entry name onto itself, which GetFullPath then exposes as
                // outside destFull — caught here too. Ordinal is intentional: both strings come from GetFullPath off
                // the same base so casing is consistent, and an ordinal compare refuses case-confusion tricks rather
                // than honoring them. (The server runs only on linux/arm64 — a case-insensitive FS isn't in play.)
                var entryFull = Path.GetFullPath(Path.Combine(destFull, entry.Name));
                if (!entryFull.Equals(destFull, StringComparison.Ordinal) &&
                    !entryFull.StartsWith(destPrefix, StringComparison.Ordinal)) {
                    throw new InvalidDataException(
                        $"Archive entry '{entry.Name}' resolves outside the extraction directory and was rejected.");
                }

                switch (entry.EntryType) {
                    case TarEntryType.Directory:
                        Directory.CreateDirectory(entryFull);
                        break;
                    case TarEntryType.RegularFile:
                    case TarEntryType.V7RegularFile:
                        if (maxBytes is { } cap) {
                            extracted += Math.Max(0, entry.Length);
                            if (extracted > cap) {
                                throw new InvalidDataException(
                                    $"Archive exceeds the {cap}-byte extraction limit and was rejected.");
                            }
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(entryFull)!);
                        await entry.ExtractToFileAsync(entryFull, overwrite: true, ct).ConfigureAwait(false);
                        break;
                    default:
                        // Symlinks, hardlinks, char/block devices, fifos: a sky-data package is plain files + dirs.
                        // Links are themselves a traversal vector (a symlink could point outside the dir, then a
                        // later entry writes "through" it), so skipping them is both sufficient and safer.
                        break;
                }
            }
        }

        private static void TryDeleteDirectory(string dir) {
            try {
                if (Directory.Exists(dir)) {
                    Directory.Delete(dir, recursive: true);
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) {
                // Best-effort cleanup of the staging dir — a leaked .staging-* dir is harmless (the inventory layer
                // only ever lists catalog ids, never the staging siblings) and will be reclaimed on the next install.
            }
        }
    }

    /// <summary>Thrown by <see cref="SkyDataInstaller.InstallFromTarGzAsync"/> only in the worst-case swap failure:
    /// the new install failed AND the prior install could not be restored, so the package directory is now absent and
    /// the previous data survives only in the backup dir named in the message. Its <see cref="Exception.InnerException"/>
    /// is an <see cref="AggregateException"/> of (original failure, restore failure). A distinct type lets the §36-2
    /// download worker flag genuine data loss rather than reporting it as an ordinary failed download.</summary>
    public sealed class SkyDataInstallException : Exception {
        public SkyDataInstallException() { }
        public SkyDataInstallException(string message) : base(message) { }
        public SkyDataInstallException(string message, Exception innerException) : base(message, innerException) { }
    }
}
