#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Core.Utility;
using System.Collections.Generic;

namespace OpenAstroAra.Test.Utility {

    [TestFixture]
    public class SequenceTemplateVariablesTest {

        [Test]
        public void Substitute_replaces_known_tokens() {
            var input = "Slew to {{target_name}} at RA {{target_ra}} Dec {{target_dec}}.";
            var values = new Dictionary<string, string> {
                ["target_name"] = "M31",
                ["target_ra"] = "00:42:44",
                ["target_dec"] = "+41:16:09",
            };
            var (result, unknown) = SequenceTemplateVariables.Substitute(input, values);
            Assert.That(result, Is.EqualTo("Slew to M31 at RA 00:42:44 Dec +41:16:09."));
            Assert.That(unknown, Is.Empty);
        }

        [Test]
        public void Substitute_preserves_unknown_tokens_and_lists_them() {
            var input = "{{target_name}} for {{integration_minutes}} min";
            var values = new Dictionary<string, string> { ["target_name"] = "NGC7000" };
            var (result, unknown) = SequenceTemplateVariables.Substitute(input, values);
            Assert.That(result, Is.EqualTo("NGC7000 for {{integration_minutes}} min"));
            Assert.That(unknown, Is.EquivalentTo(new[] { "integration_minutes" }));
        }

        [Test]
        public void Substitute_handles_whitespace_around_token() {
            // Mustache convention: {{ name }} works the same as {{name}}.
            var input = "{{ target_name }} and {{target_ra}}.";
            var values = new Dictionary<string, string> {
                ["target_name"] = "M42",
                ["target_ra"] = "05:35:17",
            };
            var (result, _) = SequenceTemplateVariables.Substitute(input, values);
            Assert.That(result, Is.EqualTo("M42 and 05:35:17."));
        }

        [Test]
        public void Substitute_works_on_raw_JSON_text() {
            var template = @"{
              ""schemaVersion"": ""openastroara-sequence-v1"",
              ""target"": ""{{target_name}}"",
              ""ra"": ""{{target_ra}}"",
              ""dec"": ""{{target_dec}}""
            }";
            var values = new Dictionary<string, string> {
                ["target_name"] = "M31",
                ["target_ra"] = "00:42:44",
                ["target_dec"] = "+41:16:09",
            };
            var (result, unknown) = SequenceTemplateVariables.Substitute(template, values);
            Assert.That(result, Does.Contain("\"target\": \"M31\""));
            Assert.That(result, Does.Contain("\"ra\": \"00:42:44\""));
            Assert.That(result, Does.Contain("\"dec\": \"+41:16:09\""));
            Assert.That(unknown, Is.Empty);
        }

        [Test]
        public void Substitute_returns_input_unchanged_when_no_tokens() {
            var input = @"{""no"": ""tokens here""}";
            var (result, unknown) = SequenceTemplateVariables.Substitute(input, new Dictionary<string, string>());
            Assert.That(result, Is.EqualTo(input));
            Assert.That(unknown, Is.Empty);
        }

        [Test]
        public void Substitute_handles_empty_template() {
            var (result, unknown) = SequenceTemplateVariables.Substitute(string.Empty, new Dictionary<string, string>());
            Assert.That(result, Is.EqualTo(string.Empty));
            Assert.That(unknown, Is.Empty);
        }

        [Test]
        public void Substitute_does_not_match_single_brace_or_malformed() {
            // {single} (one brace) and {{1bad}} (token starting with digit) shouldn't match.
            var input = "{single} {{1bad}} {{target_name}}";
            var values = new Dictionary<string, string> { ["target_name"] = "M27" };
            var (result, unknown) = SequenceTemplateVariables.Substitute(input, values);
            Assert.That(result, Is.EqualTo("{single} {{1bad}} M27"));
            Assert.That(unknown, Is.Empty);
        }

        [Test]
        public void EscapeForJsonString_escapes_quote_backslash_and_newline() {
            Assert.That(SequenceTemplateVariables.EscapeForJsonString("hello \"world\""),
                Is.EqualTo("hello \\\"world\\\""));
            Assert.That(SequenceTemplateVariables.EscapeForJsonString("a\\b"),
                Is.EqualTo("a\\\\b"));
            Assert.That(SequenceTemplateVariables.EscapeForJsonString("line1\nline2"),
                Is.EqualTo("line1\\nline2"));
            Assert.That(SequenceTemplateVariables.EscapeForJsonString("\t\b\f\r"),
                Is.EqualTo("\\t\\b\\f\\r"));
        }

        [Test]
        public void EscapeForJsonString_escapes_control_chars_as_unicode() {
            var input = "";
            var expected = "\\u0001\\u0002";
            Assert.That(SequenceTemplateVariables.EscapeForJsonString(input), Is.EqualTo(expected));
        }

        [Test]
        public void TokenNames_All_contains_canonical_set() {
            Assert.That(SequenceTemplateVariables.TokenNames.All, Has.Count.EqualTo(7));
            Assert.That(SequenceTemplateVariables.TokenNames.All, Does.Contain("target_name"));
            Assert.That(SequenceTemplateVariables.TokenNames.All, Does.Contain("target_ra"));
            Assert.That(SequenceTemplateVariables.TokenNames.All, Does.Contain("target_dec"));
            Assert.That(SequenceTemplateVariables.TokenNames.All, Does.Contain("target_rotation"));
            Assert.That(SequenceTemplateVariables.TokenNames.All, Does.Contain("integration_minutes"));
            Assert.That(SequenceTemplateVariables.TokenNames.All, Does.Contain("frames_per_filter"));
            Assert.That(SequenceTemplateVariables.TokenNames.All, Does.Contain("filter_set"));
        }
    }
}
