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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenAstroAra.PlateSolving.Solvers {

    /// <summary>One read of an ASTAP database directory (#1215): the file count, the complete
    /// databases and the partial ones (see <see cref="AstapStarDatabase.MinTileFiles"/>), plus the
    /// directory each complete database sits in (#1136: a Data Manager install lands in a
    /// subdirectory of the configured path, one per package, while a manual download sits in the
    /// path itself).</summary>
    public sealed record AstapDatabaseScan(int FileCount, IReadOnlyList<string> Databases, IReadOnlyList<string> Partial,
            IReadOnlyDictionary<string, string> Locations) {
        public AstapDatabaseScan(int fileCount, IReadOnlyList<string> databases, IReadOnlyList<string> partial)
            : this(fileCount, databases, partial, new Dictionary<string, string>(StringComparer.Ordinal)) {
        }

        public static readonly AstapDatabaseScan Empty = new(0, Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>
    /// §18.I / #1121 — what sits in an ASTAP star-database directory (the profile's index path,
    /// passed to <c>astap_cli</c> as <c>-d</c>), and which database to name with <c>-D</c> when
    /// several are installed. Shared by <see cref="ASTAPSolver"/> and the daemon's read-only
    /// status endpoint so the UI reports exactly what a solve will use.
    /// </summary>
    public static class AstapStarDatabase {

        /// <summary>Below this the deepest database (D80, 8000 stars/deg²) is the only one dense
        /// enough; ASTAP auto-selects D80 under 0.5°.</summary>
        public const double NarrowFieldBelowDeg = 0.5;

        /// <summary>Below this a frame holds too few stars for any database: ASTAP's practical floor
        /// (a C14 with a small guide-class sensor is ~0.05°). The wizard says so instead of
        /// offering a download.</summary>
        public const double SolverFloorDeg = 0.15;

        /// <summary>D05 (500 stars/deg²) is enough from 1° up (ASTAP: "0.6° or larger you can
        /// download either the D05 or D20 or D50 or D80"); between 0.5° and 1° D50 gives margin.</summary>
        public const double D05FromDeg = 1.0;

        /// <summary>ASTAP's own auto-selection (astap.htm): G05 above 6°, W08 above 20°. The Data
        /// Manager recommends those packages at the same thresholds (#1136).</summary>
        public const double MediumWideFieldAboveDeg = 6.0;

        public const double WideFieldAboveDeg = 20.0;

        // Per band, the database that ladder picks first, then the ones that still solve it. V50 is
        // D50 plus photometry, so it sits beside D50. H17/H18 are obsolete upstream but still solve.
        private static readonly string[] NarrowOrder = { "d80", "h18", "h17", "d50", "v50", "d20", "d05", "g05", "w08" };

        // 0.5° to 1°: D50 first (D80 solves too, slower); D05 is marginal here.
        private static readonly string[] SubDegreeOrder = { "d50", "v50", "d80", "h18", "h17", "d20", "d05", "g05", "w08" };

        // 1° to 6°: the smallest database that covers it first; the deeper ones only read more
        // stars for the same answer.
        private static readonly string[] GeneralOrder = { "d05", "d20", "d50", "v50", "d80", "h18", "h17", "g05", "w08" };

        // Above 6°: the 500 stars/deg² wide-field database first (a 15° field through D80 reads
        // tens of thousands of stars for nothing); W08 is too shallow until the field is wider still.
        private static readonly string[] MediumWideOrder = { "g05", "d05", "d20", "d50", "v50", "d80", "h17", "h18", "w08" };

        private static readonly string[] WideOrder = { "w08", "g05", "d05", "d20", "d50", "v50", "d80", "h17", "h18" };

        /// <summary>The §36.10 ladder: the smallest database that covers a frame
        /// <paramref name="fieldOfViewDeg"/> tall, as the Data Manager recommends it. Null when the
        /// field is unknown (NaN/0) or below <see cref="SolverFloorDeg"/> (nothing solves it).</summary>
        public static string? RecommendedFor(double fieldOfViewDeg) {
            if (double.IsNaN(fieldOfViewDeg) || fieldOfViewDeg <= 0 || fieldOfViewDeg < SolverFloorDeg) {
                return null;
            }
            return fieldOfViewDeg switch {
                > WideFieldAboveDeg => "w08",
                > MediumWideFieldAboveDeg => "g05",
                >= D05FromDeg => "d05",
                >= NarrowFieldBelowDeg => "d50",
                _ => "d80",
            };
        }

        /// <summary>The band name the wizard shows for a field: "all-sky", "wide", "general",
        /// "sub-degree", "narrow", or "too narrow" below the solver floor; "unknown" for no field.</summary>
        public static string BandFor(double fieldOfViewDeg) {
            if (double.IsNaN(fieldOfViewDeg) || fieldOfViewDeg <= 0) {
                return "unknown";
            }
            return fieldOfViewDeg switch {
                < SolverFloorDeg => "too narrow",
                > WideFieldAboveDeg => "all-sky",
                > MediumWideFieldAboveDeg => "wide",
                >= D05FromDeg => "general",
                >= NarrowFieldBelowDeg => "sub-degree",
                _ => "narrow",
            };
        }

        /// <summary>A database needs at least this many tile files to count as installed (#1215):
        /// an interrupted download that left one orphan tile next to a complete database must not
        /// be named with <c>-D</c> (ASTAP exits 33 on it). ASTAP's single-file databases carry the
        /// <c>.001</c> extension and are exempt.</summary>
        public const int MinTileFiles = 2;

        private const string SingleFileExtension = ".001";

        // #1215 — the solver enumerated the directory twice per solve (1.5–3k entries on a full
        // install); one scan is kept per directory, keyed on its last-write time, which every file
        // add or remove bumps. A partial download in progress therefore re-scans as it grows.
        private static readonly ConcurrentDictionary<string, (long Stamp, AstapDatabaseScan Scan)> Cache = new(StringComparer.Ordinal);

        /// <summary>Drop the cached scans (tests, or after a download the daemon ran itself).</summary>
        public static void InvalidateCache() => Cache.Clear();

        /// <summary>The configured directory (trimmed) when it exists and holds at least one file,
        /// else null. The .deb's tmpfiles entry creates the directory before any database is
        /// downloaded into it, so "exists" alone would hand ASTAP an empty dir and exit 32.</summary>
        public static string? EffectiveLocation(string? configured) {
            if (string.IsNullOrWhiteSpace(configured)) {
                return null;
            }
            var dir = configured.Trim();
            return Scan(dir).FileCount > 0 ? dir : null;
        }

        /// <summary>Files in <paramref name="dir"/>; 0 when unset, missing or unreadable.</summary>
        public static int CountFiles(string? dir) => Scan(dir).FileCount;

        /// <summary>The complete database abbreviations present in <paramref name="dir"/>, lower-case
        /// and sorted: ASTAP names its files <c>&lt;abbrev&gt;_&lt;tile&gt;.&lt;ext&gt;</c>, e.g.
        /// <c>d80_0101.1476</c> or <c>w08_0101.001</c>. A database with fewer than
        /// <see cref="MinTileFiles"/> tiles (an interrupted download) is left out — see
        /// <see cref="PartialDatabases"/>. Empty when unset, missing or unreadable.</summary>
        public static IReadOnlyList<string> Databases(string? dir) => Scan(dir).Databases;

        /// <summary>The abbreviations present but incomplete (#1215): one orphan tile of a database
        /// that ships many. Reported so the log and the UI can say why they are not offered.</summary>
        public static IReadOnlyList<string> PartialDatabases(string? dir) => Scan(dir).Partial;

        /// <summary>The directory to hand <c>astap_cli</c> as <c>-d</c> for <paramref name="database"/>:
        /// the configured directory itself for a manual download, or the Data Manager's package
        /// subdirectory (#1136). Null when the database is not installed under <paramref name="dir"/>.
        /// ASTAP reads one directory per run, so a solve names the database and points at where it is.</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "ASTAP database abbreviations are lower-case ASCII in its file names and its -D argument; not a security decision.")]
        public static string? DirectoryFor(string? dir, string? database) =>
            database is not null && Scan(dir).Locations.TryGetValue(database.ToLowerInvariant(), out var location) ? location : null;

        /// <summary>The directory's cached scan, re-read when its last-write time (or a package
        /// subdirectory's) changed.</summary>
        public static AstapDatabaseScan Scan(string? dir) {
            if (string.IsNullOrWhiteSpace(dir)) {
                return AstapDatabaseScan.Empty;
            }
            var key = dir.Trim();
            try {
                if (!Directory.Exists(key)) {
                    Cache.TryRemove(key, out _);
                    return AstapDatabaseScan.Empty;
                }
                var stamp = Stamp(key);
                if (Cache.TryGetValue(key, out var cached) && cached.Stamp == stamp) {
                    return cached.Scan;
                }
                var scan = ScanNow(key);
                Cache[key] = (stamp, scan);
                return scan;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return AstapDatabaseScan.Empty;
            }
        }

        // A stamp over the directory and every package subdirectory: a tile written into a
        // subdirectory bumps that subdirectory, not the root. Built from all of their mtimes (and
        // their names, so an added or removed subdirectory changes it), not the newest one, so a
        // subdirectory carrying a future mtime (clock jump, `cp -p`) cannot mask later changes
        // elsewhere.
        private static long Stamp(string dir) {
            var hash = new HashCode();
            hash.Add(Directory.GetLastWriteTimeUtc(dir).Ticks);
            foreach (var sub in PackageSubdirectories(dir).OrderBy(d => d, StringComparer.Ordinal)) {
                hash.Add(Path.GetFileName(sub), StringComparer.Ordinal);
                hash.Add(Directory.GetLastWriteTimeUtc(sub).Ticks);
            }
            return hash.ToHashCode();
        }

        // #1136 — the Data Manager installs each database package as {dir}/{package-id}/; its
        // scratch directories (.staging-*, .backup-*) start with a dot and are never read.
        private static IEnumerable<string> PackageSubdirectories(string dir) =>
            Directory.EnumerateDirectories(dir).Where(d => !Path.GetFileName(d).StartsWith('.'));

        private static AstapDatabaseScan ScanNow(string dir) {
            var files = 0;
            // Keyed by abbreviation and the directory the tiles were found in: the same database in
            // two places is two candidates, and only a complete one is offered.
            var tiles = new Dictionary<(string Abbrev, string Dir), (int Count, bool SingleFileLayout)>();
            void Read(string directory) {
                foreach (var path in Directory.EnumerateFiles(directory)) {
                    files++;
                    var name = Path.GetFileName(path);
                    if (AbbreviationOf(name) is not string abbrev) {
                        continue;
                    }
                    var single = name.EndsWith(SingleFileExtension, StringComparison.Ordinal);
                    var k = (abbrev, directory);
                    tiles[k] = tiles.TryGetValue(k, out var t) ? (t.Count + 1, t.SingleFileLayout && single) : (1, single);
                }
            }
            Read(dir);
            foreach (var sub in PackageSubdirectories(dir)) {
                Read(sub);
            }
            var locations = new Dictionary<string, string>(StringComparer.Ordinal);
            var partial = new HashSet<string>(StringComparer.Ordinal);
            // The configured directory itself wins over a subdirectory for the same database (a
            // manual install the user placed on purpose); subdirectories then in name order.
            foreach (var ((abbrev, directory), t) in tiles.OrderBy(e => e.Key.Dir == dir ? 0 : 1).ThenBy(e => e.Key.Dir, StringComparer.Ordinal)) {
                if (t.Count >= MinTileFiles || t.SingleFileLayout) {
                    locations.TryAdd(abbrev, directory);
                } else {
                    partial.Add(abbrev);
                }
            }
            partial.ExceptWith(locations.Keys); // complete somewhere → not reported as partial
            var complete = locations.Keys.OrderBy(a => a, StringComparer.Ordinal).ToList();
            return new AstapDatabaseScan(files, complete, partial.OrderBy(a => a, StringComparer.Ordinal).ToList(), locations);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "ASTAP database abbreviations are lower-case ASCII in its file names and its -D argument; not a security decision.")]
        // "d80_0101.1476" → "d80": a letter, two digits, '_', four tile digits, '.', an extension.
        private static string? AbbreviationOf(string name) {
            if (name.Length < 10 || !char.IsAsciiLetter(name[0]) || !char.IsAsciiDigit(name[1])
                    || !char.IsAsciiDigit(name[2]) || name[3] != '_' || name[8] != '.') {
                return null;
            }
            for (var i = 4; i < 8; i++) {
                if (!char.IsAsciiDigit(name[i])) {
                    return null;
                }
            }
            return name[..3].ToLowerInvariant();
        }

        /// <summary>The database to pass as <c>-D</c>, or null when fewer than two are installed
        /// (ASTAP then uses the only one there is). Deterministic: the field of view picks one of
        /// five preference orders (the ladder bands of <see cref="RecommendedFor"/>: D80 first
        /// under 0.5°, D50 first under 1°, D05 first up to 6°, G05 first up to 20°, W08 first
        /// above; an unknown field takes the general order), and the first installed entry wins. Abbreviations this list does not know
        /// rank after the known ones, alphabetically.</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "ASTAP database abbreviations are lower-case ASCII in its file names and its -D argument; not a security decision.")]
        public static string? Select(IReadOnlyCollection<string> installed, double fieldOfViewDeg) {
            if (installed.Count < 2) {
                return null;
            }
            var order = fieldOfViewDeg switch {
                > 0 and < NarrowFieldBelowDeg => NarrowOrder,
                > 0 and < D05FromDeg => SubDegreeOrder,
                > WideFieldAboveDeg => WideOrder,
                > MediumWideFieldAboveDeg => MediumWideOrder,
                _ => GeneralOrder,
            };
            var known = order.FirstOrDefault(a => installed.Contains(a, StringComparer.OrdinalIgnoreCase));
            return known ?? installed.Select(a => a.ToLowerInvariant()).OrderBy(a => a, StringComparer.Ordinal).First();
        }
    }
}
