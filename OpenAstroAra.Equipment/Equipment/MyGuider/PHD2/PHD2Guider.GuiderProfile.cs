#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Text;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    /// <summary>
    /// §63.4 (guider-e-3) — maps an ARA profile to its 1:1 PHD2 profile by name. ARA owns a dedicated
    /// PHD2 profile per ARA profile (<c>ara-&lt;slug&gt;</c>) so each rig keeps its own guider tuning,
    /// calibration, and dark library; switching ARA profiles selects (or, on first connect, creates) the
    /// matching PHD2 profile via <see cref="Phd2SetProfileByName"/> / <see cref="Phd2CreateProfile"/>.
    /// This file carries only the pure name mapping (guider-e-3a); the connect-path orchestration that
    /// drives the RPCs is guider-e-3b.
    /// </summary>
    public sealed partial class PHD2Guider {

        /// <summary>
        /// Derive the PHD2 profile name for an ARA profile per §63.4 (<c>ara-&lt;slug&gt;</c>). The slug is a
        /// deterministic lowercase a-z/0-9 form of the ARA profile name: runs of any other character collapse
        /// to a single hyphen, leading/trailing hyphens are trimmed. Examples (§63.4):
        /// <c>"C14 on CEM120" → "ara-c14-on-cem120"</c>, <c>"RedCat on HEQ5" → "ara-redcat-on-heq5"</c>. A
        /// name that slugs to empty (null / whitespace / all-punctuation / non-ASCII-only) falls back to
        /// <c>"ara-default"</c>. Non-ASCII letters are not transliterated — they collapse to hyphens like any
        /// other separator (the slug is an internal PHD2 identifier, not user-facing text).
        /// </summary>
        /// <remarks>
        /// Pure + socket-free so it's unit-testable without a live guider. Two ARA names that differ only in
        /// punctuation/case can slug to the same PHD2 name (e.g. <c>"C-14"</c> and <c>"C 14"</c> → <c>ara-c-14</c>);
        /// disambiguating that collision is deferred to the guider-e-3b wiring (tracked in PORT_TODO).
        /// </remarks>
        public static string AraGuiderProfileName(string? araProfileName) {
            var sb = new StringBuilder();
            var lastWasHyphen = true; // start true so leading separators don't emit a leading hyphen
            foreach (var ch in araProfileName ?? string.Empty) {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) {
                    sb.Append(ch);
                    lastWasHyphen = false;
                } else if (ch >= 'A' && ch <= 'Z') {
                    // Lowercase ASCII inline (avoids ToLowerInvariant / CA1308): the slug must be lowercase
                    // to match the §63.4 convention, and this is an identifier format, not a security fold.
                    sb.Append((char)(ch - 'A' + 'a'));
                    lastWasHyphen = false;
                } else if (!lastWasHyphen) {
                    sb.Append('-');
                    lastWasHyphen = true;
                }
            }
            if (sb.Length > 0 && sb[^1] == '-') {
                sb.Length--; // trim the trailing hyphen a separator-terminated name leaves behind
            }
            return sb.Length == 0 ? "ara-default" : "ara-" + sb.ToString();
        }
    }
}
