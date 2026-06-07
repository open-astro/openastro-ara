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
    /// Implements playbook §38.6 sequence template variables — Mustache-style
    /// <c>{{token}}</c> substitution applied server-side at
    /// <c>POST /api/v1/sequences/templates/{name}/instantiate</c>.
    ///
    /// Distinct from §38.6.1 filename templates (which use <c>$$TOKEN$$</c>):
    /// filename tokens are applied per-capture as the file is written; sequence
    /// template variables are applied ONCE when a template is instantiated into
    /// the user's library and the resulting sequence then runs with concrete
    /// values baked in.
    ///
    /// Canonical token list (playbook §38.6):
    /// <list type="bullet">
    ///   <item><c>{{target_name}}</c>, <c>{{target_ra}}</c>, <c>{{target_dec}}</c>, <c>{{target_rotation}}</c></item>
    ///   <item><c>{{integration_minutes}}</c>, <c>{{frames_per_filter}}</c></item>
    ///   <item><c>{{filter_set}}</c> — named filter combination from the profile (e.g., "LRGB" or "SHO")</item>
    /// </list>
    ///
    /// Tokens may appear in any string value within the template JSON body —
    /// the substitutor walks the raw JSON text and replaces matches.
    /// Unknown tokens left intact for the caller to surface as warnings.
    /// </summary>
    public static class SequenceTemplateVariables {

        /// <summary>
        /// Canonical token names recognized by the substitutor. Caller may
        /// pass values for any subset; missing tokens are left as <c>{{name}}</c>
        /// in the output so unresolved references surface during validation.
        /// </summary>
        public static class TokenNames {
            public const string TargetName = "target_name";
            public const string TargetRa = "target_ra";
            public const string TargetDec = "target_dec";
            public const string TargetRotation = "target_rotation";
            public const string IntegrationMinutes = "integration_minutes";
            public const string FramesPerFilter = "frames_per_filter";
            public const string FilterSet = "filter_set";

            public static readonly IReadOnlySet<string> All = new HashSet<string> {
                TargetName, TargetRa, TargetDec, TargetRotation,
                IntegrationMinutes, FramesPerFilter, FilterSet,
            };
        }

        // {{token_name}} — letters/digits/underscores between the braces.
        // Whitespace around the token is permitted ({{ token_name }}) and
        // stripped, matching Mustache convention.
        private static readonly Regex TokenPattern = new(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

        /// <summary>
        /// Substitute every <c>{{name}}</c> in <paramref name="template"/> with the
        /// matching value from <paramref name="values"/>. Tokens whose name isn't
        /// in <paramref name="values"/> are returned unchanged. Returns the
        /// substituted text plus the set of unknown tokens encountered (caller
        /// surfaces those as warnings in the §38.6 instantiate response).
        /// </summary>
        public static (string Result, IReadOnlyCollection<string> UnknownTokens) Substitute(
                string template, IReadOnlyDictionary<string, string> values) {
            if (string.IsNullOrEmpty(template)) return (template ?? string.Empty, Array.Empty<string>());

            var unknown = new HashSet<string>(StringComparer.Ordinal);
            var result = TokenPattern.Replace(template, match => {
                var name = match.Groups[1].Value;
                if (values.TryGetValue(name, out var value)) {
                    return value;
                }
                // Unknown token — preserve the literal placeholder so validation
                // can flag it. Add to the unknown set for the caller.
                unknown.Add(name);
                return match.Value;
            });
            return (result, unknown);
        }

        /// <summary>
        /// Quote a raw value for safe embedding in a JSON string literal. The
        /// substitutor caller passes already-escaped values when the token sits
        /// inside a JSON string; this helper handles the common case of a value
        /// that might contain quotes / backslashes / control chars.
        /// </summary>
        public static string EscapeForJsonString(string value) {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length + 2);
            foreach (var ch in value) {
                switch (ch) {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (ch < 0x20) sb.Append($"\\u{(int)ch:X4}");
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}