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
using System.Text.RegularExpressions;

namespace OpenAstroAra.Core.Utility {

    /// <summary>
    /// The planning filter kind a filter NAME names, as the daemon's lowercase wire token
    /// (<c>l</c>/<c>r</c>/<c>g</c>/<c>b</c>/<c>osc</c>/<c>ha</c>/<c>oiii</c>/<c>sii</c>/<c>duo</c>/<c>tri</c>),
    /// or null when the name names no kind. Mirrors the client's <c>FilterSetNotifier.recognizedKind</c>
    /// (filter_set_state.dart) token for token: a sequence saved with a planning-set name ('Hα') must
    /// resolve to the wheel slot the client would pick for it ('Ha'). Covers AlpacaBridge's filter-name
    /// presets and their aliases (Luminance/L/Lum, Red/R, Ha/H/Hα/H-alpha, OIII/O/O3, SII/S/S2 …) plus
    /// custom names that embed them ('Antlia Ha 3nm'). Sloan (U' G' R' I' Z'), Clear, Dark, UV, IR and
    /// "Filter N" have no planning kind, so they stay unrecognised rather than passing as L or R.
    /// </summary>
    public static class FilterNameKind {
        private static readonly Regex SloanName = new(@"^[ugriz]\s*['′’]", RegexOptions.Compiled);
        private static readonly Regex LabelToken = new("[a-zα]+|[0-9]+", RegexOptions.Compiled);

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
            Justification = "The tokens are matched against the daemon's lowercase wire vocabulary ('ha', 'oiii' …) and mirror the client's lowercase recogniser; uppercase would read differently for no gain.")]
        public static string? Recognized(string? label) {
            var l = (label ?? string.Empty).Trim().ToLowerInvariant();
            if (l.Length == 0 || SloanName.IsMatch(l)) {
                return null;
            }
            // Multi-band product names first — they embed line-filter substrings ('l-enhance' contains
            // "ha", 'l-extreme' contains "tre"), so a naive Contains("ha") would misfile them as Hα.
            if (l.Contains("extreme", StringComparison.Ordinal) || l.Contains("duo", StringComparison.Ordinal) || l.Contains("dual", StringComparison.Ordinal)) {
                return "duo";
            }
            if (l.Contains("enhance", StringComparison.Ordinal) || l.Contains("tri", StringComparison.Ordinal)) {
                return "tri";
            }
            // Line filters match whole label tokens (letter and digit runs, so 'Ha7nm' → ha, 7, nm) or
            // two adjacent tokens joined ('H-alpha' → halpha, 'O-III' → oiii, 'S 2' → s2), so substrings
            // inside ordinary words can't trigger them.
            var parts = new List<string>();
            foreach (Match m in LabelToken.Matches(l)) {
                parts.Add(m.Value);
            }
            var tokens = new HashSet<string>(parts, StringComparer.Ordinal);
            for (var i = 0; i + 1 < parts.Count; i++) {
                tokens.Add(parts[i] + parts[i + 1]);
            }
            // AlpacaBridge's one-letter codes (H, O, S) only as the whole label — as a token they'd
            // catch 'UHC-S'.
            if (l == "h" || tokens.Contains("ha") || tokens.Contains("hα") || tokens.Contains("halpha")) {
                return "ha";
            }
            if (l == "o" || tokens.Contains("oiii") || tokens.Contains("o3")) {
                return "oiii";
            }
            if (l == "s" || tokens.Contains("sii") || tokens.Contains("s2")) {
                return "sii";
            }
            if (tokens.Contains("r") || l.StartsWith("red", StringComparison.Ordinal)) {
                return "r";
            }
            if (tokens.Contains("g") || l.StartsWith("green", StringComparison.Ordinal)) {
                return "g";
            }
            if (tokens.Contains("b") || l.StartsWith("blue", StringComparison.Ordinal)) {
                return "b";
            }
            if (l.Contains("osc", StringComparison.Ordinal) || l.Contains("color", StringComparison.Ordinal) || l.Contains("colour", StringComparison.Ordinal)) {
                return "osc";
            }
            if (tokens.Contains("l") || l.StartsWith("lum", StringComparison.Ordinal)) {
                return "l";
            }
            return null;
        }

        /// <summary>The one entry of <paramref name="candidates"/> whose name is of the same recognised
        /// kind as <paramref name="name"/>, or null when the name has no kind, no candidate shares it,
        /// or more than one does (two Hα slots: nothing to choose between).</summary>
        public static T? SingleOfSameKind<T>(string? name, IEnumerable<T> candidates, Func<T, string?> nameOf) where T : class {
            var kind = Recognized(name);
            if (kind is null) {
                return null;
            }
            T? match = null;
            foreach (var candidate in candidates) {
                if (Recognized(nameOf(candidate)) != kind) {
                    continue;
                }
                if (match is not null) {
                    return null;
                }
                match = candidate;
            }
            return match;
        }
    }
}
