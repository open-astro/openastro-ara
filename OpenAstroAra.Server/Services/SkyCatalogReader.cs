#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §36 — parses an installed sky-data catalog CSV into a uniform list of {name, ra°, dec°, mag} objects for the
    /// Sky Atlas overlay. The on-disk format differs per package (the Data Manager installs each upstream file as
    /// <c>catalog.csv</c> verbatim), so the mapping is per-catalog:
    ///   • <b>hyg-stars</b> — comma-separated, quoted string fields; <c>ra</c> in decimal HOURS, <c>dec</c> in
    ///     decimal degrees, <c>proper</c> name, <c>mag</c> apparent magnitude.
    ///   • <b>openngc-dso</b> — semicolon-separated; <c>RA</c>/<c>Dec</c> in sexagesimal (HH:MM:SS / ±DD:MM:SS),
    ///     <c>V-Mag</c> magnitude, <c>Common names</c>/<c>Name</c>.
    /// Rows without a usable position are skipped; <paramref name="maxMag"/> / <paramref name="limit"/> trim the result.
    /// NOTE: when <paramref name="maxMag"/> is set, objects with NO recorded magnitude are dropped (so e.g. OpenNGC DSOs
    /// that carry neither V- nor B-Mag vanish under a magnitude filter) — callers that want those should omit the filter.
    /// </summary>
    internal static class SkyCatalogReader {

        /// <summary>Hard upper bound on the rows any single read returns — a backstop so a no-<c>limit</c> request for a
        /// large catalog (HYG is ~120k rows) can't force an unbounded ~10 MB+ response / heap spike. A caller's
        /// <c>limit</c> can only reduce this, never exceed it; brightness filtering is done with <c>max_mag</c>.</summary>
        public const int MaxObjects = 50_000;

        /// <summary>Whether a parser exists for this package id (only catalog packages with a known column layout).</summary>
        public static bool HasParser(string packageId) => packageId is "hyg-stars" or "openngc-dso";

        public static IReadOnlyList<CatalogObjectDto> Read(string packageId, Stream csv, double? maxMag, int? limit,
                CancellationToken ct) {
            ArgumentNullException.ThrowIfNull(csv);
            // The cap is intrinsic to Read, not just the caller — a null (or over-cap) limit is bounded to MaxObjects,
            // so this method can't be a footgun that returns an unbounded list. (A negative limit yields none.)
            var effectiveLimit = limit is { } l ? Math.Min(l, MaxObjects) : MaxObjects;
            using var reader = new StreamReader(csv);
            return packageId switch {
                "hyg-stars" => ParseHyg(reader, maxMag, effectiveLimit, ct),
                "openngc-dso" => ParseOpenNgc(reader, maxMag, effectiveLimit, ct),
                _ => throw new InvalidOperationException($"No sky-catalog parser for package '{packageId}'."),
            };
        }

        private static List<CatalogObjectDto> ParseHyg(TextReader reader, double? maxMag, int limit, CancellationToken ct) {
            var result = new List<CatalogObjectDto>();
            var header = reader.ReadLine();
            if (header is null) {
                return result;
            }
            var cols = IndexColumns(SplitCsv(header, ','));
            int raI = cols.GetValueOrDefault("ra", -1);
            int decI = cols.GetValueOrDefault("dec", -1);
            int magI = cols.GetValueOrDefault("mag", -1);
            int nameI = cols.GetValueOrDefault("proper", -1);
            if (raI < 0 || decI < 0) {
                return result; // unexpected layout — nothing we can place
            }

            string? line;
            while ((line = reader.ReadLine()) is not null) {
                ct.ThrowIfCancellationRequested();
                var f = SplitCsv(line, ',');
                if (!TryGetDouble(f, raI, out var raHours) || !TryGetDouble(f, decI, out var dec)) {
                    continue;
                }
                // Range-validate like the OpenNGC path: a corrupted row (e.g. ra=999 / dec=-200) is skipped rather than
                // wrapped by NormalizeRaDeg into a plausible-but-wrong position. RA: [0,24), Dec: [-90,90].
                if (raHours < 0 || raHours >= 24 || dec < -90 || dec > 90) {
                    continue;
                }
                double? mag = TryGetDouble(f, magI, out var m) ? m : null;
                if (maxMag is { } cap && (mag is null || mag > cap)) {
                    continue;
                }
                if (result.Count >= limit) {
                    break; // checked BEFORE Add so limit=0 (or negative) yields none, not one
                }
                var name = nameI >= 0 && nameI < f.Length ? f[nameI].Trim() : string.Empty;
                result.Add(new CatalogObjectDto(name, NormalizeRaDeg(raHours * 15.0), dec, mag));
            }
            return result;
        }

        private static List<CatalogObjectDto> ParseOpenNgc(TextReader reader, double? maxMag, int limit, CancellationToken ct) {
            var result = new List<CatalogObjectDto>();
            var header = reader.ReadLine();
            if (header is null) {
                return result;
            }
            // Semicolon-separated, no quoting (comma-bearing fields like Identifiers never contain ';').
            var cols = IndexColumns(header.Split(';'));
            int raI = cols.GetValueOrDefault("RA", -1);
            int decI = cols.GetValueOrDefault("Dec", -1);
            int vMagI = cols.GetValueOrDefault("V-Mag", -1);
            int bMagI = cols.GetValueOrDefault("B-Mag", -1);
            int commonI = cols.GetValueOrDefault("Common names", -1);
            int nameI = cols.GetValueOrDefault("Name", -1);
            if (raI < 0 || decI < 0) {
                return result;
            }

            string? line;
            while ((line = reader.ReadLine()) is not null) {
                ct.ThrowIfCancellationRequested();
                var f = line.Split(';');
                if (!TryParseSexagesimalHours(Get(f, raI), out var raHours) ||
                    !TryParseSexagesimalDegrees(Get(f, decI), out var dec)) {
                    continue; // rows without a resolved position (some IC/NGC stubs) are skipped
                }
                double? mag = TryParseDoubleField(Get(f, vMagI), out var v) ? v
                    : TryParseDoubleField(Get(f, bMagI), out var b) ? b : null;
                if (maxMag is { } cap && (mag is null || mag > cap)) {
                    continue;
                }
                if (result.Count >= limit) {
                    break; // checked BEFORE Add so limit=0 (or negative) yields none, not one
                }
                var common = Get(f, commonI).Trim();
                // "Common names" can list several comma-separated aliases — take the first; fall back to the catalog id.
                var name = common.Length > 0 ? common.Split(',')[0].Trim() : Get(f, nameI).Trim();
                result.Add(new CatalogObjectDto(name, NormalizeRaDeg(raHours * 15.0), dec, mag));
            }
            return result;
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────────────────────

        private static Dictionary<string, int> IndexColumns(string[] headers) {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < headers.Length; i++) {
                map[headers[i].Trim().Trim('"')] = i; // last-wins is irrelevant — catalog headers are unique
            }
            return map;
        }

        private static string Get(string[] fields, int index) =>
            index >= 0 && index < fields.Length ? fields[index] : string.Empty;

        private static bool TryGetDouble(string[] fields, int index, out double value) =>
            TryParseDoubleField(Get(fields, index), out value);

        private static bool TryParseDoubleField(string raw, out double value) {
            value = 0;
            var s = raw.Trim().Trim('"');
            return s.Length > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // Sexagesimal "HH:MM:SS.ss" → decimal hours. Empty / malformed / out-of-range → false.
        private static bool TryParseSexagesimalHours(string raw, out double hours) =>
            TryParseSexagesimal(raw, maxFirst: 24, upperInclusive: false, out hours); // RA: 0 ≤ hours < 24

        // Sexagesimal "±DD:MM:SS.s" → signed decimal degrees. Empty / malformed / out-of-range → false.
        private static bool TryParseSexagesimalDegrees(string raw, out double degrees) =>
            TryParseSexagesimal(raw, maxFirst: 90, upperInclusive: true, out degrees); // Dec: |deg| ≤ 90 (poles valid)

        // Parse "A:B:C" → A + B/60 + C/3600, with a leading sign. Rejects out-of-range components (B/C must be in
        // [0,60), A in [0, maxFirst] — upper bound inclusive for Dec so ±90° poles are valid, exclusive for RA so
        // 24:xx is rejected) so a placeholder stub like "99:99:99" is skipped rather than placed at a nonsense position.
        private static bool TryParseSexagesimal(string raw, double maxFirst, bool upperInclusive, out double result) {
            result = 0;
            var s = raw.Trim();
            if (s.Length == 0) {
                return false;
            }
            var negative = s[0] == '-';
            if (s[0] is '+' or '-') {
                s = s[1..];
            }
            var parts = s.Split(':');
            // Hours/degrees and minutes are integral in this notation (Integer rejects "01.5"); only seconds may carry a
            // fraction (e.g. "44.33"), so it alone parses as Float. This keeps "01.5:30:00" out rather than reading it as 2h.
            if (parts.Length != 3 ||
                !double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ||
                !double.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var c)) {
                return false;
            }
            var overMax = upperInclusive ? a > maxFirst : a >= maxFirst;
            if (a < 0 || overMax || b < 0 || b >= 60 || c < 0 || c >= 60) {
                return false;
            }
            // At the inclusive upper bound (Dec's ±90° pole) only exactly maxFirst:00:00 is valid — +90:30:00 would
            // otherwise slip through as 90.5°, an impossible declination. (RA is exclusive, so a==maxFirst is already out.)
            if (upperInclusive && a == maxFirst && (b != 0 || c != 0)) {
                return false;
            }
            result = a + b / 60.0 + c / 3600.0;
            if (negative) {
                result = -result;
            }
            return true;
        }

        // Fold RA degrees into [0, 360) (a star at exactly 24h / 360° lands at 0).
        private static double NormalizeRaDeg(double deg) {
            var wrapped = deg % 360.0;
            return wrapped < 0 ? wrapped + 360.0 : wrapped;
        }

        // A minimal RFC-4180-ish field splitter: handles double-quoted fields (with "" escapes) so a quoted value
        // containing the delimiter stays intact. HYG quotes its string columns; OpenNGC isn't quoted but this is safe
        // for it too. Allocations are per-line; the catalog is read once per request.
        private static string[] SplitCsv(string line, char delimiter) {
            var fields = new List<string>();
            var sb = new System.Text.StringBuilder();
            var inQuotes = false;
            for (var i = 0; i < line.Length; i++) {
                var ch = line[i];
                if (inQuotes) {
                    if (ch == '"') {
                        if (i + 1 < line.Length && line[i + 1] == '"') {
                            sb.Append('"');
                            i++; // consume the escaped quote
                        } else {
                            inQuotes = false;
                        }
                    } else {
                        sb.Append(ch);
                    }
                } else if (ch == '"') {
                    inQuotes = true;
                } else if (ch == delimiter) {
                    fields.Add(sb.ToString());
                    sb.Clear();
                } else {
                    sb.Append(ch);
                }
            }
            fields.Add(sb.ToString());
            return fields.ToArray();
        }
    }
}
