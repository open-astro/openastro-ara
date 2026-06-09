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

namespace OpenAstroAra.Test.Utility {

    [TestFixture]
    public class FilenameTemplateSanitizerTest {

        [Test]
        public void SanitizeComponent_replaces_each_illegal_char_with_underscore() {
            // Per playbook §38.6.1: / \ : * ? " < > | all illegal.
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("a/b"), Is.EqualTo("a_b"));
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("a\\b"), Is.EqualTo("a_b"));
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("a:b"), Is.EqualTo("a_b"));
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("a*b"), Is.EqualTo("a_b"));
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("a?b"), Is.EqualTo("a_b"));
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("a\"b"), Is.EqualTo("a_b"));
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("a<b"), Is.EqualTo("a_b"));
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("a>b"), Is.EqualTo("a_b"));
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("a|b"), Is.EqualTo("a_b"));
        }

        [Test]
        public void SanitizeComponent_collapses_consecutive_underscores() {
            // "M27 / Dumbbell" → §38.6.1 example: "M27___Dumbbell" but
            // consecutive `_` collapse → "M27_Dumbbell".
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("M27 / Dumbbell"),
                Is.EqualTo("M27 _ Dumbbell"));
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("a///b"), Is.EqualTo("a_b"));
        }

        [Test]
        public void SanitizeComponent_strips_outer_whitespace_and_control_chars() {
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("  hello  "), Is.EqualTo("hello"));
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("\0hello"), Is.EqualTo("_hello"));
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("hithere"), Is.EqualTo("hi_there"));
        }

        [Test]
        public void SanitizeComponent_keeps_safe_chars() {
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("NGC_7000_LIGHT_120s_0001"),
                Is.EqualTo("NGC_7000_LIGHT_120s_0001"));
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("2026-05-31_22-15-00"),
                Is.EqualTo("2026-05-31_22-15-00"));
            // Spaces kept (per playbook §38.6.1 example "NGC 7000" → "NGC_7000" comment
            // notes "space kept; some users prefer underscores everywhere").
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("NGC 7000"),
                Is.EqualTo("NGC 7000"));
        }

        [Test]
        public void SanitizeComponent_handles_non_ascii_replacement() {
            // Non-printable Unicode → underscore. "Hα" → "H_" per playbook example.
            // (Alpha 'α' is a Letter, so it IS printable + stays. The example was
            // about a specific Unicode-control or formatting char.)
            // Test true control char:
            Assert.That(FilenameTemplateSanitizer.SanitizeComponent("Ha\u200ETest"),
                Is.EqualTo("Ha_Test"));  // U+200E LEFT-TO-RIGHT MARK is Format
        }

        [Test]
        public void SanitizePath_preserves_separators_between_components() {
            var input = "captures/NGC 7000/2026-05-31/LIGHT_H<a>_120s_0001.fits";
            var result = FilenameTemplateSanitizer.SanitizePath(input);
            // `<a>` → `_a_` then consecutive-underscore collapse leaves `_a_`.
            Assert.That(result, Is.EqualTo("captures/NGC 7000/2026-05-31/LIGHT_H_a_120s_0001.fits"));
        }

        [Test]
        public void ResolveEmpty_returns_placeholder_for_null_or_whitespace() {
            Assert.That(FilenameTemplateSanitizer.ResolveEmpty(null,
                EmptyTokenPlaceholders.SensorTemp), Is.EqualTo("noTemp"));
            Assert.That(FilenameTemplateSanitizer.ResolveEmpty("",
                EmptyTokenPlaceholders.Filter), Is.EqualTo("noFilter"));
            Assert.That(FilenameTemplateSanitizer.ResolveEmpty("   ",
                EmptyTokenPlaceholders.Gain), Is.EqualTo("noGain"));
        }

        [Test]
        public void ResolveEmpty_returns_value_when_non_empty() {
            Assert.That(FilenameTemplateSanitizer.ResolveEmpty("Ha",
                EmptyTokenPlaceholders.Filter), Is.EqualTo("Ha"));
            Assert.That(FilenameTemplateSanitizer.ResolveEmpty("-10.5",
                EmptyTokenPlaceholders.SensorTemp), Is.EqualTo("-10.5"));
        }

        [Test]
        public void ApplyLengthCap_no_change_for_short_paths() {
            var path = "captures/NGC_7000/2026-05-31_22-15-00_Ha_120s_0001.fits";
            var (result, truncated) = FilenameTemplateSanitizer.ApplyLengthCap(path, "NGC_7000");
            Assert.That(result, Is.EqualTo(path));
            Assert.That(truncated, Is.False);
        }

        [Test]
        public void ApplyLengthCap_truncates_target_name_first_for_long_paths() {
            // Per playbook §38.6.1 example: 250-char path with verbose target name
            // collapses to ~190 chars by truncating the target name.
            var longTarget = "Andromeda_Galaxy_M31_NGC224_Bortle4_Backyard_2026_VeryDetailed_LongTargetName_With_Lots_Of_Extra_Padding_To_Force_Truncation_Beyond_Limit";
            var path = $"/var/lib/openastroara/media/captures/{longTarget}/2026-05-31_22-15-00_Ha_120s_0001.fits";
            Assume.That(path.Length, Is.GreaterThan(FilenameTemplateSanitizer.MaxPathLength));

            var (result, truncated) = FilenameTemplateSanitizer.ApplyLengthCap(path, longTarget);

            Assert.That(truncated, Is.True);
            Assert.That(result.Length, Is.LessThanOrEqualTo(FilenameTemplateSanitizer.MaxPathLength));
            // Frame number + datetime + extension preserved
            Assert.That(result, Does.EndWith("_0001.fits"));
            Assert.That(result, Does.Contain("2026-05-31_22-15-00"));
            // Target name was truncated with trailing _ sentinel
            Assert.That(result, Does.Not.Contain(longTarget));
            Assert.That(result, Does.Contain("Andromeda_Galaxy_M31"));
        }
    }
}