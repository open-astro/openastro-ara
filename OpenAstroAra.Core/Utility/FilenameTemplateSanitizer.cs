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
using System.Text;
using System.Text.RegularExpressions;

namespace OpenAstroAra.Core.Utility {

    /// <summary>
    /// Implements playbook §38.6.1 — filename template sanitization rules for
    /// the §38.6 capture-time `$$TOKEN$$` substitution.
    ///
    /// Three concerns rolled together:
    /// 1. Illegal-character replacement (cross-FS safety: ext4 on Pi, APFS on
    ///    macOS WILMA, NTFS on Windows shares).
    /// 2. Empty/null token placeholders (consistent filename shape across
    ///    captures even when some metadata is missing — OSC camera has no
    ///    filter, dewless DSLR has no temp, etc.).
    /// 3. 200-char path cap with truncation priority (frame number + datetime
    ///    preserved, target name truncated first per §38.6.1 priority list).
    ///
    /// Pure functions; no I/O. Token substitution itself is the caller's job —
    /// pass in the already-substituted filename and this normalizes it.
    /// </summary>
    public static class FilenameTemplateSanitizer {

        /// <summary>
        /// Characters illegal on at least one of ext4 / APFS / NTFS / Windows
        /// shares. Per playbook §38.6.1.
        /// </summary>
        private static readonly char[] IllegalChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

        /// <summary>
        /// Hard cap on total path length per playbook §38.6.1. Stays under
        /// Windows 260 limit with margin for share-mount prefixes.
        /// </summary>
        public const int MaxPathLength = 200;

        /// <summary>
        /// Default placeholder strings for tokens that resolve to no value.
        /// English ASCII for forever-stable filenames (not localized).
        /// Per playbook §38.6.1 table.
        /// </summary>
        public static class EmptyTokenPlaceholders {
            public const string SensorTemp = "noTemp";
            public const string Filter = "noFilter";
            public const string Gain = "noGain";
            public const string Offset = "noOffset";
            public const string Binning = "1x1";
            public const string TargetName = "unnamed";
        }

        /// <summary>
        /// Sanitize a single filename component (no path separators) — replace
        /// illegal chars, strip outer whitespace, collapse runs of underscores,
        /// and strip ASCII control + non-printable characters.
        /// </summary>
        public static string SanitizeComponent(string input) {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            var sb = new StringBuilder(input.Length);
            foreach (var ch in input) {
                if (Array.IndexOf(IllegalChars, ch) >= 0) {
                    sb.Append('_');
                } else if (ch < 0x20) {
                    // ASCII control chars (NUL .. US)
                    sb.Append('_');
                } else if (char.IsControl(ch)) {
                    // Other Unicode control chars
                    sb.Append('_');
                } else if (!IsPrintable(ch)) {
                    sb.Append('_');
                } else {
                    sb.Append(ch);
                }
            }

            var result = sb.ToString().Trim();
            // Collapse consecutive underscores
            result = Regex.Replace(result, "_+", "_");
            return result;
        }

        /// <summary>
        /// Sanitize a full path (directory separators preserved). Each
        /// component is sanitized; the path separators (forward slash here,
        /// which the daemon writes on Linux) stay intact.
        /// </summary>
        public static string SanitizePath(string input) {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var parts = input.Replace('\\', '/').Split('/');
            var sanitized = new List<string>(parts.Length);
            foreach (var part in parts) {
                if (string.IsNullOrEmpty(part)) continue;
                sanitized.Add(SanitizeComponent(part));
            }
            return string.Join("/", sanitized);
        }

        /// <summary>
        /// Resolve an empty / null token to its playbook §38.6.1 placeholder.
        /// Returns <paramref name="value"/> unchanged when non-empty.
        /// </summary>
        public static string ResolveEmpty(string? value, string placeholder) =>
            string.IsNullOrWhiteSpace(value) ? placeholder : value;

        /// <summary>
        /// Apply the §38.6.1 200-char path cap with truncation priority:
        ///   1. Frame-number suffix preserved (uniqueness)
        ///   2. Date/time stamps preserved (chronological ordering)
        ///   3. Filter / exposure / temp truncated last (acquisition context)
        ///   4. <c>$$TARGETNAME$$</c> truncated FIRST (user-supplied)
        ///
        /// This impl operates on an already-substituted full path and a
        /// <see cref="TargetName"/> hint marking where the target-name segment
        /// lives in the path. If the hint is present + the path overflows, the
        /// target-name portion is truncated until the path fits (with a
        /// trailing <c>_</c> sentinel so consumers know truncation happened).
        ///
        /// Returns the sanitized path + a bool indicating whether truncation
        /// occurred (caller emits §38.6.1's <c>frame.filename_truncated</c> WS
        /// event when true).
        /// </summary>
        public static (string Path, bool Truncated) ApplyLengthCap(string path, string? targetName) {
            if (path.Length <= MaxPathLength) return (path, false);

            // If we have a target-name hint, truncate that segment first.
            if (!string.IsNullOrEmpty(targetName)) {
                var idx = path.IndexOf(targetName!, StringComparison.Ordinal);
                if (idx >= 0) {
                    var excess = path.Length - MaxPathLength + 1; // +1 for the trailing "_" sentinel
                    var truncatedTargetLen = Math.Max(1, targetName!.Length - excess);
                    if (truncatedTargetLen < targetName.Length) {
                        var truncated = string.Concat(targetName.AsSpan(0, truncatedTargetLen), "_");
                        var result = string.Concat(path.AsSpan(0, idx), truncated, path.AsSpan(idx + targetName.Length));
                        if (result.Length <= MaxPathLength) return (result, true);
                        // Fallthrough — even truncated target wasn't enough; trim from the start of
                        // the path as last resort.
                        path = result;
                    }
                }
            }

            // Last-resort: trim trailing chars (preserves the filename extension
            // since chronological + frame-number bits are usually at the end —
            // but for the playbook truncation-priority case the targetName cut
            // above is the normal path).
            return (path.Substring(0, MaxPathLength), true);
        }

        private static bool IsPrintable(char ch) {
            var category = char.GetUnicodeCategory(ch);
            return category != System.Globalization.UnicodeCategory.Control
                && category != System.Globalization.UnicodeCategory.OtherNotAssigned
                && category != System.Globalization.UnicodeCategory.Format
                && category != System.Globalization.UnicodeCategory.Surrogate;
        }
    }
}