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
    /// databases and the partial ones (see <see cref="AstapStarDatabase.MinTileFiles"/>).</summary>
    public sealed record AstapDatabaseScan(int FileCount, IReadOnlyList<string> Databases, IReadOnlyList<string> Partial) {
        public static readonly AstapDatabaseScan Empty = new(0, Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>
    /// §18.I / #1121 — what sits in an ASTAP star-database directory (the profile's index path,
    /// passed to <c>astap_cli</c> as <c>-d</c>), and which database to name with <c>-D</c> when
    /// several are installed. Shared by <see cref="ASTAPSolver"/> and the daemon's read-only
    /// status endpoint so the UI reports exactly what a solve will use.
    /// </summary>
    public static class AstapStarDatabase {

        /// <summary>DEPLOY.md: D80 covers "roughly 0.25° to 30°". Narrower fields want the
        /// deepest database, wider ones the shallowest.</summary>
        public const double NarrowFieldBelowDeg = 0.25;

        public const double WideFieldAboveDeg = 30.0;

        // Deepest first. D80 leads the general order: it is the database DEPLOY.md installs and
        // the one sized for the usual range of fields. V50 is D50 plus photometry, so it sits
        // beside D50.
        private static readonly string[] NarrowOrder = { "h18", "h17", "d80", "d50", "v50", "d20", "d05", "g05", "w08" };

        private static readonly string[] GeneralOrder = { "d80", "d50", "v50", "h18", "h17", "d20", "d05", "g05", "w08" };

        private static readonly string[] WideOrder = { "w08", "g05", "d05", "d20", "d50", "v50", "d80", "h17", "h18" };

        /// <summary>A database needs at least this many tile files to count as installed (#1215):
        /// an interrupted download that left one orphan tile next to a complete database must not
        /// be named with <c>-D</c> (ASTAP exits 33 on it). ASTAP's single-file databases carry the
        /// <c>.001</c> extension and are exempt.</summary>
        public const int MinTileFiles = 2;

        private const string SingleFileExtension = ".001";

        // #1215 — the solver enumerated the directory twice per solve (1.5–3k entries on a full
        // install); one scan is kept per directory, keyed on its last-write time, which every file
        // add or remove bumps. A partial download in progress therefore re-scans as it grows.
        private static readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, AstapDatabaseScan Scan)> Cache = new(StringComparer.Ordinal);

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

        /// <summary>The directory's cached scan, re-read when its last-write time changed.</summary>
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
                var stamp = Directory.GetLastWriteTimeUtc(key);
                if (Cache.TryGetValue(key, out var cached) && cached.LastWriteUtc == stamp) {
                    return cached.Scan;
                }
                var scan = ScanNow(key);
                Cache[key] = (stamp, scan);
                return scan;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return AstapDatabaseScan.Empty;
            }
        }

        private static AstapDatabaseScan ScanNow(string dir) {
            var files = 0;
            var tiles = new Dictionary<string, (int Count, bool SingleFileLayout)>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(dir)) {
                files++;
                var name = Path.GetFileName(path);
                if (AbbreviationOf(name) is not string abbrev) {
                    continue;
                }
                var single = name.EndsWith(SingleFileExtension, StringComparison.Ordinal);
                tiles[abbrev] = tiles.TryGetValue(abbrev, out var t) ? (t.Count + 1, t.SingleFileLayout && single) : (1, single);
            }
            var complete = new List<string>();
            var partial = new List<string>();
            foreach (var (abbrev, t) in tiles) {
                (t.Count >= MinTileFiles || t.SingleFileLayout ? complete : partial).Add(abbrev);
            }
            complete.Sort(StringComparer.Ordinal);
            partial.Sort(StringComparer.Ordinal);
            return new AstapDatabaseScan(files, complete, partial);
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
        /// three preference orders (narrower than <see cref="NarrowFieldBelowDeg"/>: deepest
        /// first; wider than <see cref="WideFieldAboveDeg"/>: shallowest first; otherwise, or when
        /// the field is unknown, D80 first), and the first installed entry wins. Abbreviations
        /// this list does not know rank after the known ones, alphabetically.</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "ASTAP database abbreviations are lower-case ASCII in its file names and its -D argument; not a security decision.")]
        public static string? Select(IReadOnlyCollection<string> installed, double fieldOfViewDeg) {
            if (installed.Count < 2) {
                return null;
            }
            var order = fieldOfViewDeg switch {
                > 0 and < NarrowFieldBelowDeg => NarrowOrder,
                > WideFieldAboveDeg => WideOrder,
                _ => GeneralOrder,
            };
            var known = order.FirstOrDefault(a => installed.Contains(a, StringComparer.OrdinalIgnoreCase));
            return known ?? installed.Select(a => a.ToLowerInvariant()).OrderBy(a => a, StringComparer.Ordinal).First();
        }
    }
}
