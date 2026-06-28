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
    }

    public sealed class SkyCatalogService : ISkyCatalogService {

        private readonly string _skyDataRoot;
        private readonly object _gate = new();
        private List<DsoRow>? _dsos;     // parsed once, then cached for the process lifetime

        public SkyCatalogService(string skyDataRoot) {
            _skyDataRoot = skyDataRoot ?? throw new ArgumentNullException(nameof(skyDataRoot));
        }

        private string DsoCsvPath => Path.Combine(_skyDataRoot, "openngc-dso", "catalog.csv");

        private sealed record DsoRow(
            string Name, double RaDeg, double DecDeg, double? Mag, string Type,
            bool HasM, bool HasNgc, bool HasIc, bool HasCaldwell);

        private sealed record CatalogDef(string Id, string Name, string Group, Func<DsoRow, bool> Match);

        // OpenNGC `Type` codes: G/GPair/GTrpl/GGroup galaxies, OCl open cluster, GCl globular,
        // PN planetary nebula, HII/EmN emission, RfN reflection, Neb/Cl+N nebula, SNR remnant.
        private static readonly IReadOnlyList<CatalogDef> Defs = new[] {
            new CatalogDef("messier", "Messier", "Catalogs", r => r.HasM),
            new CatalogDef("caldwell", "Caldwell", "Catalogs", r => r.HasCaldwell),
            new CatalogDef("ngc", "NGC", "Catalogs", r => r.HasNgc),
            new CatalogDef("ic", "IC", "Catalogs", r => r.HasIc),
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
            return q.Select(r => new CatalogObjectDto(r.Name, r.RaDeg, r.DecDeg, r.Mag)).ToList();
        }

        private List<DsoRow>? LoadDsos(CancellationToken ct) {
            lock (_gate) {
                if (_dsos is not null) {
                    return _dsos;
                }
                if (!File.Exists(DsoCsvPath)) {
                    return null;
                }
                var list = new List<DsoRow>();
                using var reader = new StreamReader(DsoCsvPath);
                var header = reader.ReadLine();
                if (header is null) {
                    return _dsos = list;
                }
                var cols = header.Split(';');     // OpenNGC is semicolon-separated
                int Idx(string n) => Array.IndexOf(cols, n);
                int iName = Idx("Name"), iType = Idx("Type"), iRa = Idx("RA"), iDec = Idx("Dec"),
                    iV = Idx("V-Mag"), iB = Idx("B-Mag"), iM = Idx("M"), iNgc = Idx("NGC"), iIc = Idx("IC"),
                    iId = Idx("Identifiers");
                if (iName < 0 || iRa < 0 || iDec < 0) {
                    return _dsos = list;          // unexpected layout — nothing to place
                }
                string? line;
                while ((line = reader.ReadLine()) is not null) {
                    ct.ThrowIfCancellationRequested();
                    var f = line.Split(';');
                    if (f.Length <= iDec) {
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
                    bool Has(int i) => i >= 0 && f.Length > i && !string.IsNullOrWhiteSpace(f[i]);
                    bool hasCaldwell = iId >= 0 && f.Length > iId && HasCaldwellId(f[iId]);
                    list.Add(new DsoRow(f[iName], ra, dec, mag, type, Has(iM), Has(iNgc), Has(iIc), hasCaldwell));
                }
                return _dsos = list;
            }
        }

        private static bool TryNum(string s, out double v) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        // Caldwell membership: OpenNGC lists it in the comma-separated Identifiers as "C NNN"
        // (e.g. "C 020"). Require the space + all-digits tail so other C-prefixed ids don't match.
        private static bool HasCaldwellId(string identifiers) {
            foreach (var raw in identifiers.Split(',')) {
                var t = raw.Trim();
                if (t.Length > 2 && t[0] == 'C' && t[1] == ' ') {
                    var rest = t.Substring(2).Trim();
                    if (rest.Length > 0 && rest.All(char.IsDigit)) {
                        return true;
                    }
                }
            }
            return false;
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
            var p = s.TrimStart('+', '-').Split(':');
            if (p.Length < 2 || !TryNum(p[0], out var d) || !TryNum(p[1], out var m)) {
                return false;
            }
            double sec = 0;
            if (p.Length > 2 && !TryNum(p[2], out sec)) {
                return false;
            }
            deg = sign * (d + (m / 60.0) + (sec / 3600.0));
            return deg >= -90 && deg <= 90;
        }
    }
}
