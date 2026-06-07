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
    public class FilenameTemplateValidatorTest {

        [Test]
        public void Validate_accepts_template_with_FRAMENR() {
            // Per §38.6.1, $$FRAMENR$$ satisfies the uniqueness requirement.
            var result = FilenameTemplateValidator.Validate(
                "$$IMAGETYPE$$/$$DATE$$/$$TARGETNAME$$_$$FILTER$$_$$FRAMENR$$");
            Assert.That(result.Valid, Is.True);
            Assert.That(result.Code, Is.Null);
            Assert.That(result.UnknownTokens, Is.Empty);
        }

        [Test]
        public void Validate_accepts_template_with_DATETIME() {
            // $$DATETIME$$ also satisfies uniqueness.
            var result = FilenameTemplateValidator.Validate(
                "$$IMAGETYPE$$/$$TARGETNAME$$_$$DATETIME$$");
            Assert.That(result.Valid, Is.True);
        }

        [Test]
        public void Validate_rejects_unknown_token() {
            var result = FilenameTemplateValidator.Validate(
                "$$FRAMENR$$_$$BADTOKEN$$");
            Assert.That(result.Valid, Is.False);
            Assert.That(result.Code, Is.EqualTo(FilenameTemplateValidator.CodeUnknownToken));
            Assert.That(result.UnknownTokens, Has.Count.EqualTo(1));
            Assert.That(result.UnknownTokens[0], Is.EqualTo("$$BADTOKEN$$"));
        }

        [Test]
        public void Validate_reports_multiple_unknown_tokens() {
            var result = FilenameTemplateValidator.Validate(
                "$$FRAMENR$$_$$BAD1$$_$$BAD2$$");
            Assert.That(result.Valid, Is.False);
            Assert.That(result.Code, Is.EqualTo(FilenameTemplateValidator.CodeUnknownToken));
            Assert.That(result.UnknownTokens, Is.EquivalentTo(new[] { "$$BAD1$$", "$$BAD2$$" }));
        }

        [Test]
        public void Validate_rejects_template_without_uniqueness_token() {
            // $$TARGETNAME$$ + $$FILTER$$ alone don't make filenames unique
            // across sequential frames.
            var result = FilenameTemplateValidator.Validate(
                "$$IMAGETYPE$$/$$TARGETNAME$$_$$FILTER$$");
            Assert.That(result.Valid, Is.False);
            Assert.That(result.Code, Is.EqualTo(FilenameTemplateValidator.CodeLacksUniqueness));
            Assert.That(result.UnknownTokens, Is.Empty);
        }

        [Test]
        public void Validate_rejects_empty_template() {
            var result = FilenameTemplateValidator.Validate(string.Empty);
            Assert.That(result.Valid, Is.False);
            Assert.That(result.Code, Is.EqualTo(FilenameTemplateValidator.CodeLacksUniqueness));
        }

        [Test]
        public void Validate_unknown_token_takes_precedence_over_missing_uniqueness() {
            // Both errors apply; the §38.6.1 ordering says unknown-token wins.
            var result = FilenameTemplateValidator.Validate(
                "$$TARGETNAME$$_$$BADTOKEN$$");
            Assert.That(result.Valid, Is.False);
            Assert.That(result.Code, Is.EqualTo(FilenameTemplateValidator.CodeUnknownToken));
        }

        [Test]
        public void Validate_handles_typical_default_template() {
            // The default per InMemoryProfileStore.cs:50 + FileProfileStore.cs:164
            // ($$DATEMINUS12$$\$$IMAGETYPE$$\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s).
            // Has $$DATETIME$$ so passes uniqueness.
            var result = FilenameTemplateValidator.Validate(
                @"$$DATEMINUS12$$\$$IMAGETYPE$$\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s");
            Assert.That(result.Valid, Is.True);
        }
    }
}