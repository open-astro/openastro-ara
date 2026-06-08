#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Astrometry {

    // TODO(port): Phase 0.5p-followup / Phase 15 sweep — the original DatabaseInteraction
    // was EF6-backed access to NINA.sqlite (BrightStars / Constellations / DSOs / earth
    // rotation parameters etc.). Phase 0.5p deleted the underlying schema types per §56
    // NINA-DB-greenfield without porting the consumers. Daemon DB design (per §56) is
    // future work. For now this class preserves the two methods callers actually need:
    //   - GetUt1Utc: returns 0 (no IERS earth-rotation table available; SOFA topocentric
    //     transforms still work, just without sub-second UT1-UTC correction)
    //   - GetDisplayAlias: pure-CPU Levenshtein alias matcher; no DB needed, kept as-is
    // The other former methods (GetConstellations, GetObjectTypes, GetBrightStars,
    // GetConstellationsWithStars, GetConstellationBoundaries, GetCatalogues,
    // GetDeepSkyObjects, GetContext) are removed because their return types referenced
    // deleted schema. Restore them as daemon-DB-backed services in Phase 15+.
    public class DatabaseInteraction {

        public DatabaseInteraction() {
        }

        public DatabaseInteraction(string connectionString) {
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "Instance method by design: a stub today, but slated to read from the per-instance daemon DB connection when the IERS earth-rotation table is restored (Phase 15+).")]
        public Task<double> GetUt1Utc(DateTime date, CancellationToken token) {
            if (token.IsCancellationRequested) {
                return Task.FromCanceled<double>(token);
            }
            return Task.FromResult(0d);
        }

        public static string GetDisplayAlias(string searchName, IReadOnlyList<string> aliases) {
            // No search by name, default to longest
            if (string.IsNullOrEmpty(searchName)) {
                return aliases.Aggregate("", (max, cur) => max.Length > cur.Length ? max : cur);
            }

            string cleanedSearchName = cleanForSearching(searchName);

            var cleanedAliases = aliases
                .Select(name => new {
                    name,
                    cleanName = cleanForSearching(name)
                })
                .OrderByDescending(alias => alias.cleanName.Length)
                .ToList();

            // Do any of them start with what we're typing? If so, go with the longest
            string? result = cleanedAliases
                .Where(alias => alias.cleanName.StartsWith(cleanedSearchName, StringComparison.Ordinal))
                .Select(alias => alias.name)
                .FirstOrDefault();

            if (null != result) {
                return result;
            }

            // None of them start with it, so let's use levenshtein distance, length breaks ties
            return cleanedAliases
                .OrderBy(alias => Fastenshtein.Levenshtein.Distance(cleanedSearchName, alias.cleanName))
                .ThenByDescending(alias => alias.cleanName.Length)
                .Select(alias => alias.name)
                .FirstOrDefault() ?? string.Empty;
        }

        private static string cleanForSearching(string token) {
            return Regex.Replace(token, @"[^\w\-]*", "", RegexOptions.Multiline).ToUpperInvariant();
        }
    }
}