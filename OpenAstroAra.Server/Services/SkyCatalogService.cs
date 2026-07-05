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
using System.Linq;
using System.Threading;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §36 Catalogs — derives named catalogs (Messier / NGC / IC) and object-type filters (galaxies, clusters,
    /// nebulae…) from the installed <c>openngc-dso</c> catalog, so the planetarium can overlay them as toggleable
    /// layers. The engine can't filter its own DSOs by catalog/type, so we serve curated lists it draws on top.
    /// Reads <c>{skyDataRoot}/openngc-dso/catalog.csv</c> once, lazily, and caches the parsed rows.
    /// (Caldwell / Herschel / Sharpless / Astronomical-League programs — curated cross-ref lists — land in later
    /// slices; this is the directly-derivable set.)
    /// </summary>
    public interface ISkyCatalogService {
        /// <summary>The catalogs that have data available (empty until openngc-dso is installed).</summary>
        IReadOnlyList<CatalogInfoDto> List();

        /// <summary>Objects for one catalog id, or null when the id is unknown / its source isn't installed.</summary>
        IReadOnlyList<CatalogObjectDto>? GetObjects(string catalogId, int? limit, CancellationToken ct);

        /// <summary>Every installed DSO with the full field set (size / position angle / surface
        /// brightness) the §36.8 Tonight's Sky planner ranks on; null when openngc-dso isn't installed
        /// (mirrors <see cref="GetObjects"/>'s null-when-absent contract).</summary>
        IReadOnlyList<DsoEntryDto>? GetAllDsos(CancellationToken ct);
    }

    public sealed class SkyCatalogService : ISkyCatalogService {

        private readonly string _skyDataRoot;
        private List<DsoRow>? _dsos;     // parsed once, then cached for the process lifetime (published via Interlocked)
        private IReadOnlyList<DsoEntryDto>? _dsoEntries; // the GetAllDsos projection, cached so each request doesn't re-allocate it
        // LastWriteTimeUtc.Ticks of the catalog file whose parse last failed, so we don't re-parse a
        // known-bad file every request — but DO retry once the file changes on disk (corrupt → fixed
        // recovers without a daemon restart). NoFailureTicks = no failure recorded.
        private const long NoFailureTicks = long.MinValue;
        private long _loadFailedWriteTicks = NoFailureTicks;

        public SkyCatalogService(string skyDataRoot) {
            _skyDataRoot = skyDataRoot ?? throw new ArgumentNullException(nameof(skyDataRoot));
        }

        private string DsoCsvPath => Path.Combine(_skyDataRoot, "openngc-dso", "catalog.csv");

        private sealed record DsoRow(
            string Name, double RaDeg, double DecDeg, double? Mag, string Type,
            int? MessierNum, int? CaldwellNum,
            double? MajAxArcmin, double? MinAxArcmin, double? PosAngleDeg, double? SurfaceBrightness,
            string? CommonName);

        private sealed record CatalogDef(string Id, string Name, string Group, Func<DsoRow, bool> Match);

        // OpenNGC `Type` codes: G/GPair/GTrpl/GGroup galaxies, OCl open cluster, GCl globular,
        // PN planetary nebula, HII/EmN emission, RfN reflection, Neb/Cl+N nebula, SNR remnant.
        //
        // NGC/IC membership is the row's PRIMARY designation (the Name column). OpenNGC's `NGC`
        // and `IC` COLUMNS are cross-identification fields ("this object also carries that other
        // id"), NOT membership flags — e.g. NGC1027 carries IC='1824' while the actual IC0434 row
        // has an empty IC column — so matching on them inverted both catalogs (the IC overlay was
        // mostly NGC-named rows and vice versa). Messier/Caldwell have no rows of their own, so
        // those two really are the cross-reference columns (M / "C NNN" in Identifiers).
        private static readonly IReadOnlyList<CatalogDef> Defs = new[] {
            new CatalogDef("messier", "Messier", "Catalogs", r => r.MessierNum is not null),
            new CatalogDef("caldwell", "Caldwell", "Catalogs", r => r.CaldwellNum is not null),
            new CatalogDef("ngc", "NGC", "Catalogs",
                r => r.Name.StartsWith("NGC", StringComparison.Ordinal)),
            new CatalogDef("ic", "IC", "Catalogs",
                r => r.Name.StartsWith("IC", StringComparison.Ordinal)),
            new CatalogDef("galaxies", "Galaxies", "Types",
                r => r.Type is "G" or "GPair" or "GTrpl" or "GGroup"),
            new CatalogDef("open-clusters", "Open clusters", "Types", r => r.Type is "OCl"),
            new CatalogDef("globular-clusters", "Globular clusters", "Types", r => r.Type is "GCl"),
            new CatalogDef("planetary-nebulae", "Planetary nebulae", "Types", r => r.Type is "PN"),
            new CatalogDef("emission-nebulae", "Emission nebulae", "Types", r => r.Type is "HII" or "EmN"),
            new CatalogDef("reflection-nebulae", "Reflection nebulae", "Types", r => r.Type is "RfN"),
            new CatalogDef("nebulae", "Nebulae", "Types", r => r.Type is "Neb" or "Cl+N"),
            new CatalogDef("supernova-remnants", "Supernova remnants", "Types", r => r.Type is "SNR"),
        };

        public IReadOnlyList<CatalogInfoDto> List() {
            if (!File.Exists(DsoCsvPath)) {
                return Array.Empty<CatalogInfoDto>();
            }
            return Defs.Select(d => new CatalogInfoDto(d.Id, d.Name, d.Group)).ToList();
        }

        public IReadOnlyList<CatalogObjectDto>? GetObjects(string catalogId, int? limit, CancellationToken ct) {
            var def = Defs.FirstOrDefault(d => d.Id == catalogId);
            if (def is null) {
                return null;
            }
            var rows = LoadDsos(ct);
            if (rows is null) {
                return null;   // source not installed
            }
            // Brightest first (objects with no magnitude sort last) so a ?limit= on a large
            // type-filter (e.g. 10k galaxies) returns the most worthwhile to overlay, not an
            // arbitrary file-order slice.
            IEnumerable<DsoRow> q = rows.Where(def.Match)
                .OrderBy(r => r.Mag ?? double.PositiveInfinity);
            if (limit is { } l) {
                q = q.Take(Math.Max(0, l));
            }
            return q.Select(r => new CatalogObjectDto(DisplayName(catalogId, r), r.RaDeg, r.DecDeg, r.Mag))
                .ToList();
        }

        // The overlay label for a row, in the designation system of the catalog it was requested
        // through: the Messier overlay says "M 31", not the row's primary id "NGC0224"; Caldwell
        // says "C 14". Everything else shows the primary id in its conventional spelling
        // ("NGC 224" / "IC 434"), not OpenNGC's zero-padded key.
        private static string DisplayName(string catalogId, DsoRow r) => catalogId switch {
            "messier" => $"M {r.MessierNum}",
            "caldwell" => $"C {r.CaldwellNum}",
            _ => PrettyDsoName(r.Name),
        };

        // "NGC0224" → "NGC 224", "IC0080 NED01" → "IC 80 NED01"; any other shape is returned
        // unchanged. Purely presentational — GetAllDsos keeps the raw OpenNGC key.
        private static string PrettyDsoName(string name) {
            var prefixLen = name.StartsWith("NGC", StringComparison.Ordinal) ? 3
                : name.StartsWith("IC", StringComparison.Ordinal) ? 2 : 0;
            if (prefixLen == 0) {
                return name;
            }
            var i = prefixLen;
            while (i < name.Length && name[i] == '0') {
                i++;
            }
            var j = i;
            while (j < name.Length && char.IsAsciiDigit(name[j])) {
                j++;
            }
            if (j == i) {
                return name;   // no digits after the prefix — not a designation we understand
            }
            return $"{name[..prefixLen]} {name[i..j]}{name[j..]}";
        }

        public IReadOnlyList<DsoEntryDto>? GetAllDsos(CancellationToken ct) {
            // The projection is immutable once built, so cache it: GetAllDsos is hit on every
            // /planning/tonight request and re-Select(...).ToList()-ing the ~13k-row catalog each time
            // is pure waste. Published via Interlocked like _dsos (a rare concurrent first-build just
            // produces equal lists; the first to publish wins and everyone shares it).
            var cached = Volatile.Read(ref _dsoEntries);
            if (cached is not null) {
                return cached;
            }
            var rows = LoadDsos(ct);
            if (rows is null) {
                return null;   // source not installed
            }
            var projected = (IReadOnlyList<DsoEntryDto>)rows.Select(r => new DsoEntryDto(
                r.Name, r.CommonName, r.Type, r.RaDeg, r.DecDeg, r.Mag,
                r.MajAxArcmin, r.MinAxArcmin, r.PosAngleDeg, r.SurfaceBrightness)).ToList();
            return Interlocked.CompareExchange(ref _dsoEntries, projected, null) ?? projected;
        }

        private List<DsoRow>? LoadDsos(CancellationToken ct) {
            // Fast path: the catalog is parsed once and cached for the process lifetime.
            var cached = Volatile.Read(ref _dsos);
            if (cached is not null) {
                return cached;
            }
            if (!File.Exists(DsoCsvPath)) {
                return null;
            }
            // A prior parse failed on a corrupt/unreadable CSV — skip the re-read rather than re-parsing
            // the bad file every request, UNLESS the file has since changed on disk (the user replaced a
            // corrupt catalog with a good one), in which case retry so it recovers without a restart.
            var writeTicks = File.GetLastWriteTimeUtc(DsoCsvPath).Ticks;
            // Interlocked (not Volatile) for the 64-bit field: a torn read of a long is possible on a
            // 32-bit CLR; Interlocked.Read is atomic on every platform and carries the same ordering.
            if (Interlocked.Read(ref _loadFailedWriteTicks) == writeTicks) {
                return null;
            }
            // Lock-free first load: parse without any mutual exclusion, so every
            // concurrent first-caller runs ParseDsoCsv on its own thread and honors its
            // own CancellationToken per row (a Monitor would instead make all-but-one
            // block uncancellably on Monitor.Enter). The parse is pure + deterministic,
            // so on the rare concurrent first-access a couple of threads parse in
            // parallel and produce equal lists — CompareExchange publishes the first to
            // finish and everyone shares that one instance.
            List<DsoRow> parsed;
            try {
                parsed = ParseDsoCsv(ct);
            } catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException) {
                // Corrupt or unreadable catalog: remember THIS file version as bad so we don't retry the
                // full read per request, but a later replacement (different write-time) re-parses.
                // (Cancellation is NOT a load failure — it bubbles.)
                Interlocked.Exchange(ref _loadFailedWriteTicks, writeTicks);
                return null;
            }
            return Interlocked.CompareExchange(ref _dsos, parsed, null) ?? parsed;
        }

        private List<DsoRow> ParseDsoCsv(CancellationToken ct) {
            var list = new List<DsoRow>();
            using var reader = new StreamReader(DsoCsvPath);
            var header = reader.ReadLine();
            if (header is null) {
                return list;
            }
            var cols = header.Split(';');     // OpenNGC is semicolon-separated
            int Idx(string n) => Array.IndexOf(cols, n);
            int iName = Idx("Name"), iType = Idx("Type"), iRa = Idx("RA"), iDec = Idx("Dec"),
                iV = Idx("V-Mag"), iB = Idx("B-Mag"), iM = Idx("M"),
                iId = Idx("Identifiers"),
                iMaj = Idx("MajAx"), iMin = Idx("MinAx"), iPa = Idx("PosAng"), iSb = Idx("SurfBr"),
                iCommon = Idx("Common names");
            if (iName < 0 || iRa < 0 || iDec < 0) {
                return list;          // unexpected layout — nothing to place
            }
            string? line;
            while ((line = reader.ReadLine()) is not null) {
                ct.ThrowIfCancellationRequested();
                var f = line.Split(';');
                // Guard every required column, not just Dec — the row body reads
                // f[iName]/f[iRa]/f[iDec] and we don't assume a fixed column order.
                if (f.Length <= Math.Max(iName, Math.Max(iRa, iDec))) {
                    continue;
                }
                if (!TryRaToDeg(f[iRa], out var ra) || !TryDecToDeg(f[iDec], out var dec)) {
                    continue;
                }
                double? mag = null;
                if (iV >= 0 && f.Length > iV && TryNum(f[iV], out var v)) {
                    mag = v;
                } else if (iB >= 0 && f.Length > iB && TryNum(f[iB], out var b)) {
                    mag = b;
                }
                var type = iType >= 0 && f.Length > iType ? f[iType] : "";
                // "Dup" rows are duplicate stubs (a second historical id pointing at another row —
                // 651 of them) and "NonEx" rows are catalogued errors with no real object. Both
                // would double-mark or mis-mark positions in every consumer (catalog overlays,
                // Tonight's Sky ranking), and neither carries a Messier/Caldwell cross-reference
                // that exists nowhere else, so drop them at parse.
                if (type is "Dup" or "NonEx") {
                    continue;
                }
                bool Has(int i) => i >= 0 && f.Length > i && !string.IsNullOrWhiteSpace(f[i]);
                int? messier = iM >= 0 && f.Length > iM && int.TryParse(f[iM].Trim(),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var mNum) ? mNum : null;
                int? caldwell = iId >= 0 && f.Length > iId ? TryGetCaldwellNum(f[iId]) : null;
                // Optional measured columns — null when blank/absent (a row may carry size but no
                // surface brightness, etc.), so a missing field is "unknown", not zero.
                double? Num(int i) => i >= 0 && f.Length > i && TryNum(f[i], out var v) ? v : null;
                // OpenNGC "Common names" is a comma-separated list (e.g. "Andromeda Galaxy,Messier 31");
                // take the first as the display name rather than showing the whole joined string. Guard the
                // leading-comma case (",Alt Name") so the first token isn't an empty string — null, not "",
                // so the `CommonName ?? Name` fallback actually kicks in (?? only tests null).
                var firstCommon = Has(iCommon) ? f[iCommon].Split(',')[0].Trim() : "";
                string? common = firstCommon.Length > 0 ? firstCommon : null;
                list.Add(new DsoRow(f[iName], ra, dec, mag, type, messier, caldwell,
                    Num(iMaj), Num(iMin), Num(iPa), Num(iSb), common));
            }
            return list;
        }

        private static bool TryNum(string s, out double v) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        // Caldwell membership: OpenNGC lists it in the comma-separated Identifiers as "C NNN"
        // (e.g. "C 020"). Require the space + all-digits tail so other C-prefixed ids don't match;
        // return the number so the overlay can label the row "C 20".
        private static int? TryGetCaldwellNum(string identifiers) {
            foreach (var raw in identifiers.Split(',')) {
                var t = raw.Trim();
                if (t.Length > 2 && t[0] == 'C' && t[1] == ' ') {
                    var rest = t.Substring(2).Trim();
                    if (rest.Length > 0 && rest.All(char.IsDigit) &&
                        int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture,
                            out var num)) {
                        return num;
                    }
                }
            }
            return null;
        }

        // OpenNGC RA "HH:MM:SS.s" (hours) → decimal degrees.
        private static bool TryRaToDeg(string s, out double deg) {
            deg = 0;
            var p = s.Split(':');
            if (p.Length < 2 || !TryNum(p[0], out var h) || !TryNum(p[1], out var m)) {
                return false;
            }
            double sec = 0;
            // Reject a malformed seconds field rather than silently treating it as 0
            // — a bad token would otherwise mis-place the object instead of dropping
            // the row (matters for any future user-imported catalog; OpenNGC is clean).
            if (p.Length > 2 && !TryNum(p[2], out sec)) {
                return false;
            }
            // Arc-minutes/seconds must be in [0,60): NumberStyles.Float would accept a
            // stray "-" (e.g. "01:-30:00"), which still passes the degree-range gate but
            // places the object wrong. Reject the row instead.
            if (m < 0 || m >= 60 || sec < 0 || sec >= 60) {
                return false;
            }
            deg = (h + (m / 60.0) + (sec / 3600.0)) * 15.0;
            return deg >= 0 && deg < 360;
        }

        // OpenNGC Dec "±DD:MM:SS.s" → decimal degrees.
        private static bool TryDecToDeg(string s, out double deg) {
            deg = 0;
            s = s.Trim();
            if (s.Length < 2) {
                return false;
            }
            int sign = s[0] == '-' ? -1 : 1;
            // Strip exactly ONE leading sign char. TrimStart('+','-') is greedy — a
            // malformed "+-30:00:00" would lose both and silently return +30 instead of
            // rejecting. (Matches SkyCatalogReader.TryParseSexagesimal's single-char strip.)
            var body = (s[0] == '+' || s[0] == '-') ? s[1..] : s;
            var p = body.Split(':');
            if (p.Length < 2 || !TryNum(p[0], out var d) || !TryNum(p[1], out var m)) {
                return false;
            }
            double sec = 0;
            if (p.Length > 2 && !TryNum(p[2], out sec)) {
                return false;
            }
            // Degrees magnitude + arc-min/sec all non-negative (min/sec < 60). The overall
            // sign is carried by `sign`; a "-" inside a component — e.g. the "-30" left by a
            // malformed "+-30", or "30:-15:00" — is invalid, not a real value.
            if (d < 0 || m < 0 || m >= 60 || sec < 0 || sec >= 60) {
                return false;
            }
            deg = sign * (d + (m / 60.0) + (sec / 3600.0));
            return deg >= -90 && deg <= 90;
        }
    }
}
