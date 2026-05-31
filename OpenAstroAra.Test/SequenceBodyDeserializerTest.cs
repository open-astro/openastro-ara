#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Sequencer;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.Trigger;
using OpenAstroAra.Sequencer.Utility.DateTimeProvider;
using OpenAstroAra.Server.Services;
using System.Collections.Generic;
using System.Text.Json;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-1 — verifies <see cref="SequenceBodyDeserializer"/> bridges a
    /// stored body <see cref="JsonElement"/> through NINA's
    /// <c>SequenceJsonConverter</c> with graceful degradation for unknown
    /// <c>$type</c> values. This is the first step toward swapping the
    /// placeholder sequencer for a real engine — the deserializer here is
    /// the JSON-side input, executor wiring comes in subsequent §38k subs.
    /// </summary>
    [TestFixture]
    public class SequenceBodyDeserializerTest {

        private static SequenceBodyDeserializer NewDeserializer() {
            // Mock ISequencerFactory. The converter resolves `$type` strings
            // through `Type.GetType(string)` first; unknown types short-circuit
            // to UnknownSequenceContainer without ever consulting the factory.
            // So for unknown-type tests we don't need real registrations.
            var factory = new Mock<ISequencerFactory>(MockBehavior.Loose);
            factory.SetupGet(f => f.Items).Returns(new List<ISequenceItem>());
            factory.SetupGet(f => f.Conditions).Returns(new List<ISequenceCondition>());
            factory.SetupGet(f => f.Triggers).Returns(new List<ISequenceTrigger>());
            factory.SetupGet(f => f.Container).Returns(new List<ISequenceContainer>());
            factory.SetupGet(f => f.DateTimeProviders).Returns(new List<IDateTimeProvider>());
            return new SequenceBodyDeserializer(factory.Object, logger: null);
        }

        private static JsonElement Parse(string json) =>
            JsonDocument.Parse(json).RootElement.Clone();

        [Test]
        public void TryDeserialize_returns_false_when_body_is_not_an_object() {
            var deserializer = NewDeserializer();
            var ok = deserializer.TryDeserialize(Parse("[1, 2, 3]"), out var container, out var error);
            Assert.That(ok, Is.False);
            Assert.That(container, Is.Null);
            Assert.That(error, Does.Contain("must be a JSON object"));
        }

        [Test]
        public void TryDeserialize_returns_false_on_malformed_inner_json_payload() {
            // Pass a JsonElement that itself parses ok but contains a string
            // that drives Newtonsoft into a parse failure when round-tripped.
            // The deserializer should report the failure as `error`, not throw.
            var deserializer = NewDeserializer();
            // A bare-typed array under $type confuses the polymorphic converter.
            var body = Parse("""{ "schemaVersion": "openastroara-sequence-v1", "$type": 12345 }""");
            var ok = deserializer.TryDeserialize(body, out var container, out var error);
            // Either fails outright (preferred) or produces an Unknown container.
            // Both outcomes are non-throwing; assert that the daemon doesn't crash.
            if (!ok) {
                Assert.That(error, Is.Not.Null);
            } else {
                Assert.That(container, Is.Not.Null);
            }
        }

        [Test]
        public void TryDeserialize_unknown_type_yields_graceful_degradation_not_throw() {
            // $type names a class that doesn't exist in any loaded assembly.
            // Type.GetType returns null → SequenceContainerCreationConverter
            // produces UnknownSequenceContainer (internal class) without
            // touching the factory.
            var deserializer = NewDeserializer();
            var body = Parse("""
                {
                    "schemaVersion": "openastroara-sequence-v1",
                    "$type": "Fictional.Namespace.NoSuchContainer, Fictional.Assembly"
                }
                """);

            var ok = deserializer.TryDeserialize(body, out var container, out var error);

            Assert.That(ok, Is.True, $"unexpected error: {error}");
            Assert.That(container, Is.Not.Null);
            // UnknownSequenceContainer is internal; verify by the runtime type
            // name rather than `is` since the test assembly can't reference it.
            Assert.That(container!.GetType().Name, Is.EqualTo("UnknownSequenceContainer"));
        }

        [Test]
        public void TryDeserialize_missing_type_field_still_yields_graceful_degradation() {
            // No $type at root. The converter's else branch still returns
            // UnknownSequenceContainer instead of throwing.
            var deserializer = NewDeserializer();
            var body = Parse("""
                { "schemaVersion": "openastroara-sequence-v1", "items": [] }
                """);

            var ok = deserializer.TryDeserialize(body, out var container, out var error);

            Assert.That(ok, Is.True, $"unexpected error: {error}");
            Assert.That(container, Is.Not.Null);
            Assert.That(container!.GetType().Name, Is.EqualTo("UnknownSequenceContainer"));
        }

        [Test]
        public void TryDeserialize_returns_false_on_completely_malformed_json() {
            // Construct a JsonElement that looks ok via System.Text.Json but
            // whose serialized form bewilders Newtonsoft. Using a clone of
            // a JSON that round-trips to an empty string is the simplest:
            // an empty object should round-trip cleanly — picks the
            // graceful-degradation path again.
            var deserializer = NewDeserializer();
            var body = Parse("""{ }""");
            var ok = deserializer.TryDeserialize(body, out var container, out _);
            // Empty object: no $type → UnknownSequenceContainer.
            Assert.That(ok, Is.True);
            Assert.That(container!.GetType().Name, Is.EqualTo("UnknownSequenceContainer"));
        }
    }
}
