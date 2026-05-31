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
using OpenAstroAra.Server.Services;
using System.Text.Json;

namespace OpenAstroAra.Test {

    [TestFixture]
    public class SequenceSchemaValidatorTest {

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        [Test]
        public void Validate_accepts_minimal_v1_body_with_opt_out() {
            // §38.5 capturable-instruction check is the strict default; for
            // unit tests of the schemaVersion gate we opt out so a body
            // with no instructions can still pass.
            var body = Parse("""{ "schemaVersion": "openastroara-sequence-v1" }""");
            var (valid, reason) = SequenceSchemaValidator.Validate(body, requireCapturableInstruction: false);
            Assert.That(valid, Is.True);
            Assert.That(reason, Is.Null);
        }

        [Test]
        public void Validate_accepts_v1_body_with_real_instruction() {
            // Strict-mode default. Body has a TakeExposure instruction so the
            // §38.5 capturable-instruction check passes.
            var body = Parse("""
                {
                    "schemaVersion": "openastroara-sequence-v1",
                    "name": "test",
                    "items": [
                        { "$type": "NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer" }
                    ]
                }
                """);
            var (valid, reason) = SequenceSchemaValidator.Validate(body);
            Assert.That(valid, Is.True);
            Assert.That(reason, Is.Null);
        }

        [Test]
        public void Validate_rejects_body_without_capturable_instruction() {
            var body = Parse("""
                {
                    "schemaVersion": "openastroara-sequence-v1",
                    "items": []
                }
                """);
            var (valid, reason) = SequenceSchemaValidator.Validate(body);
            Assert.That(valid, Is.False);
            Assert.That(reason, Does.Contain("at least one capturable instruction"));
        }

        [Test]
        public void Validate_rejects_missing_schema_version() {
            var body = Parse("""{ "name": "no version here" }""");
            var (valid, reason) = SequenceSchemaValidator.Validate(body);
            Assert.That(valid, Is.False);
            Assert.That(reason, Does.Contain("schemaVersion"));
        }

        [Test]
        public void Validate_rejects_unrecognized_schema_version() {
            var body = Parse("""{ "schemaVersion": "openastroara-sequence-v2" }""");
            var (valid, reason) = SequenceSchemaValidator.Validate(body);
            Assert.That(valid, Is.False);
            Assert.That(reason, Does.Contain("unrecognized"));
            Assert.That(reason, Does.Contain("openastroara-sequence-v1"));
        }

        [Test]
        public void Validate_rejects_non_object_body() {
            var body = Parse("[1, 2, 3]");
            var (valid, reason) = SequenceSchemaValidator.Validate(body);
            Assert.That(valid, Is.False);
            Assert.That(reason, Does.Contain("must be a JSON object"));
        }

        [Test]
        public void Validate_rejects_schema_version_with_wrong_type() {
            var body = Parse("""{ "schemaVersion": 1 }""");
            var (valid, reason) = SequenceSchemaValidator.Validate(body);
            Assert.That(valid, Is.False);
            Assert.That(reason, Does.Contain("must be a string"));
        }
    }
}
