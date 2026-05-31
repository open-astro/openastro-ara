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
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Sequencer.Serialization;
using OpenAstroAra.Server.Services;
using System.Linq;
using System.Text.Json;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-5 — end-to-end Serialize → Deserialize round-trip exercising the
    /// real <see cref="SequenceJsonConverter"/> against a <see cref="HeadlessSequencerFactory"/>
    /// populated by <c>WithDefaults()</c>. The previous §38k tests verified
    /// the factory prototype lookup and the unknown-type degradation path
    /// in isolation; this one runs both halves of the pipeline against each
    /// other to validate the engine works for the simplest "container with
    /// one item" sequence shape — the structural minimum for a real run.
    /// </summary>
    [TestFixture]
    public class SequenceJsonRoundTripTest {

        [Test]
        public void Roundtrip_empty_SequentialContainer_yields_SequentialContainer() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var converter = new SequenceJsonConverter(factory);

            var original = new SequentialContainer { Name = "Test sequence" };
            var json = converter.Serialize(original);

            var roundTripped = converter.Deserialize(json);

            Assert.That(roundTripped, Is.Not.Null);
            Assert.That(roundTripped.GetType().Name, Is.EqualTo("SequentialContainer"));
            Assert.That(roundTripped.Name, Is.EqualTo("Test sequence"));
        }

        [Test]
        public void Roundtrip_SequentialContainer_with_Annotation_preserves_child_type() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var converter = new SequenceJsonConverter(factory);

            var original = new SequentialContainer { Name = "Mixed container" };
            original.Items.Add(new Annotation { Name = "First note" });

            var json = converter.Serialize(original);

            var roundTripped = converter.Deserialize(json);

            Assert.That(roundTripped, Is.Not.Null);
            Assert.That(roundTripped.GetType().Name, Is.EqualTo("SequentialContainer"));
            Assert.That(roundTripped.Items, Has.Count.EqualTo(1));
            // The Annotation prototype lookup happens inside the
            // SequenceItemCreationConverter — verifying the resulting type
            // name confirms the factory found it (vs. UnknownSequenceItem).
            Assert.That(roundTripped.Items[0].GetType().Name, Is.EqualTo("Annotation"));
        }

        [Test]
        public void Roundtrip_SequentialContainer_with_WaitForTimeSpan_preserves_Time_property() {
            // WaitForTimeSpan has a Time JsonProperty — verify it survives
            // the round-trip and the prototype lookup didn't reset it back
            // to the default value of 1.
            var factory = HeadlessSequencerFactory.WithDefaults();
            var converter = new SequenceJsonConverter(factory);

            var original = new SequentialContainer { Name = "Timer container" };
            original.Items.Add(new WaitForTimeSpan { Time = 42.5 });

            var json = converter.Serialize(original);

            var roundTripped = converter.Deserialize(json);

            Assert.That(roundTripped.Items, Has.Count.EqualTo(1));
            var timer = roundTripped.Items[0] as WaitForTimeSpan;
            Assert.That(timer, Is.Not.Null);
            Assert.That(timer!.Time, Is.EqualTo(42.5));
        }

        [Test]
        public void Roundtrip_SequentialContainer_with_two_items_preserves_order() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var converter = new SequenceJsonConverter(factory);

            var original = new SequentialContainer();
            original.Items.Add(new Annotation { Name = "step 1" });
            original.Items.Add(new WaitForTimeSpan { Time = 7 });

            var json = converter.Serialize(original);

            var roundTripped = converter.Deserialize(json);

            Assert.That(roundTripped.Items, Has.Count.EqualTo(2));
            Assert.That(roundTripped.Items[0].GetType().Name, Is.EqualTo("Annotation"));
            Assert.That(roundTripped.Items[1].GetType().Name, Is.EqualTo("WaitForTimeSpan"));
        }

        [Test]
        public void Roundtrip_via_SequenceBodyDeserializer_System_Text_Json_bridge() {
            // The daemon stores the body as JsonElement (System.Text.Json),
            // so the full path goes JsonElement -> raw text -> Newtonsoft
            // SequenceJsonConverter. This test exercises the actual
            // SequenceBodyDeserializer rather than calling the converter
            // directly — verifies the System.Text.Json -> Newtonsoft bridge
            // works for a real populated container.
            var factory = HeadlessSequencerFactory.WithDefaults();
            var converter = new SequenceJsonConverter(factory);
            var deserializer = new SequenceBodyDeserializer(factory, logger: null);

            var original = new SequentialContainer { Name = "Bridge test" };
            original.Items.Add(new Annotation { Name = "via STJ" });
            var newtonsoftJson = converter.Serialize(original);

            // Parse with System.Text.Json then feed through the deserializer.
            var body = JsonDocument.Parse(newtonsoftJson).RootElement.Clone();

            var ok = deserializer.TryDeserialize(body, out var container, out var error);

            Assert.That(ok, Is.True, $"unexpected error: {error}");
            Assert.That(container, Is.Not.Null);
            Assert.That(container!.GetType().Name, Is.EqualTo("SequentialContainer"));
            Assert.That(container.Items, Has.Count.EqualTo(1));
            Assert.That(container.Items[0].GetType().Name, Is.EqualTo("Annotation"));
        }
    }
}
