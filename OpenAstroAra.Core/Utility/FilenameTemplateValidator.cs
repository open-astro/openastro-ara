#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Model;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace OpenAstroAra.Core.Utility {

    /// <summary>
    /// Implements playbook §38.6.1 "Validation at sequence start" — the
    /// filename template is checked before the first capture so the user
    /// gets a clear 422 instead of unique-filename collisions mid-session.
    ///
    /// Two checks:
    /// <list type="bullet">
    ///   <item><b>Unknown token</b>: every <c>$$TOKEN$$</c> in the template
    ///     must exist in the canonical <see cref="ImagePatternKeys"/> list.</item>
    ///   <item><b>Uniqueness</b>: at least one of <c>$$FRAMENR$$</c> or
    ///     <c>$$DATETIME$$</c> must appear — otherwise sequential frames
    ///     overwrite each other.</item>
    /// </list>
    /// </summary>
    /// <summary>Result of <see cref="FilenameTemplateValidator.Validate"/>.</summary>
    public sealed record ValidationResult(
        bool Valid,
        string? Code,
        IReadOnlyList<string> UnknownTokens);

    public static class FilenameTemplateValidator {

        public const string CodeUnknownToken = "unknown_template_token";
        public const string CodeLacksUniqueness = "template_lacks_uniqueness_token";

        /// <summary>
        /// $$TOKEN$$ pattern. Captures the inner token name so callers can
        /// look it up in the canonical token registry.
        /// </summary>
        private static readonly Regex TokenPattern =
            new(@"\$\$([A-Z][A-Z0-9_]*)\$\$", RegexOptions.Compiled);

        /// <summary>
        /// Run §38.6.1 validation. Returns Valid=true when the template
        /// passes both checks; otherwise Code identifies the first failure
        /// (unknown_template_token wins over template_lacks_uniqueness_token
        /// since unknown tokens are a strictly worse error).
        /// </summary>
        public static ValidationResult Validate(string template) {
            if (string.IsNullOrEmpty(template)) {
                return new ValidationResult(
                    Valid: false,
                    Code: CodeLacksUniqueness,
                    UnknownTokens: System.Array.Empty<string>());
            }

            var canonical = CanonicalTokens();
            var unknown = new List<string>();
            var hasFrameNr = false;
            var hasDateTime = false;

            foreach (Match m in TokenPattern.Matches(template)) {
                var full = m.Value;
                if (!canonical.Contains(full)) {
                    unknown.Add(full);
                }
                if (full == ImagePatternKeys.FrameNr) hasFrameNr = true;
                if (full == ImagePatternKeys.DateTime) hasDateTime = true;
            }

            if (unknown.Count > 0) {
                return new ValidationResult(
                    Valid: false,
                    Code: CodeUnknownToken,
                    UnknownTokens: unknown);
            }

            if (!hasFrameNr && !hasDateTime) {
                return new ValidationResult(
                    Valid: false,
                    Code: CodeLacksUniqueness,
                    UnknownTokens: System.Array.Empty<string>());
            }

            return new ValidationResult(Valid: true, Code: null,
                UnknownTokens: System.Array.Empty<string>());
        }

        /// <summary>
        /// Snapshot of every <c>$$TOKEN$$</c> string declared in
        /// <see cref="ImagePatternKeys"/>. Cached because the field list
        /// is fixed at compile time.
        /// </summary>
        private static readonly HashSet<string> _canonicalTokens = BuildCanonicalTokens();

        private static HashSet<string> CanonicalTokens() => _canonicalTokens;

        private static HashSet<string> BuildCanonicalTokens() {
            var set = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var f in typeof(ImagePatternKeys).GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)) {
                if (f.FieldType == typeof(string)) {
                    var value = (string?)f.GetValue(null);
                    if (!string.IsNullOrEmpty(value)) set.Add(value);
                }
            }
            return set;
        }
    }
}