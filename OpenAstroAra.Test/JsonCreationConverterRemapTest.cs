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
using OpenAstroAra.Sequencer.Serialization;
using OpenAstroAra.Server.Services;
using System.Text.Json;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-6 — the inherited <c>JsonCreationConverter.GetType()</c> previously
    /// only swapped the assembly suffix on a NINA-namespaced AQTN, leaving the
    /// class side as <c>NINA.Sequencer.X</c>. That meant a real NINA-imported
    /// sequence body always fell to <c>UnknownSequence*</c> because no type
    /// named <c>NINA.Sequencer.X</c> exists in any OpenAstroAra assembly.
    /// This test fixture pins the corrected remap behavior.
    /// </summary>
    [TestFixture]
    public class JsonCreationConverterRemapTest {

        // §0.5l NINA.Sequencer → OpenAstroAra.Sequencer

        [Test]
        public void RemapNinaNamespace_NINA_Sequencer_class_and_assembly() {
            var input = "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer";
            var remapped = NinaTypeRemapper.RemapNamespace(input);
            Assert.That(remapped, Is.EqualTo(
                "OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer"));
        }

        // §0.5g NINA.Core → OpenAstroAra.Core

        [Test]
        public void RemapNinaNamespace_NINA_Core_class_and_assembly() {
            var input = "NINA.Core.Model.Equipment.FilterInfo, NINA.Core";
            var remapped = NinaTypeRemapper.RemapNamespace(input);
            Assert.That(remapped, Is.EqualTo(
                "OpenAstroAra.Core.Model.Equipment.FilterInfo, OpenAstroAra.Core"));
        }

        // §0.5h NINA.Astrometry → OpenAstroAra.Astrometry

        [Test]
        public void RemapNinaNamespace_NINA_Astrometry_class_and_assembly() {
            var input = "NINA.Astrometry.Coordinates, NINA.Astrometry";
            var remapped = NinaTypeRemapper.RemapNamespace(input);
            Assert.That(remapped, Is.EqualTo(
                "OpenAstroAra.Astrometry.Coordinates, OpenAstroAra.Astrometry"));
        }

        // Untouched OpenAstroAra AQTN should round-trip unchanged.

        [Test]
        public void RemapNinaNamespace_already_remapped_string_is_idempotent() {
            var input = "OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer";
            var remapped = NinaTypeRemapper.RemapNamespace(input);
            Assert.That(remapped, Is.EqualTo(input));
        }

        // End-to-end: a NINA-original $type body now deserializes to a real
        // SequentialContainer instead of UnknownSequenceContainer.

        [Test]
        public void Deserializer_resolves_NINA_namespaced_SequentialContainer() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var deserializer = new SequenceBodyDeserializer(factory, logger: null);

            // Exactly the shape a NINA-original sequence body has at the root.
            var body = JsonDocument.Parse("""
                {
                    "schemaVersion": "openastroara-sequence-v1",
                    "$type": "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer"
                }
                """).RootElement.Clone();

            var ok = deserializer.TryDeserialize(body, out var container, out var error);

            Assert.That(ok, Is.True, $"unexpected error: {error}");
            Assert.That(container, Is.Not.Null);
            // Before §38k-6: this would be UnknownSequenceContainer because
            // the inherited remap left "NINA.Sequencer.Container.SequentialContainer"
            // on the class side. After §38k-6: real SequentialContainer.
            Assert.That(container!.GetType().Name, Is.EqualTo("SequentialContainer"));
        }

        [Test]
        public void Deserializer_resolves_NINA_namespaced_SequenceRootContainer() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var deserializer = new SequenceBodyDeserializer(factory, logger: null);

            var body = JsonDocument.Parse("""
                {
                    "schemaVersion": "openastroara-sequence-v1",
                    "$type": "NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer"
                }
                """).RootElement.Clone();

            var ok = deserializer.TryDeserialize(body, out var container, out _);

            Assert.That(ok, Is.True);
            Assert.That(container!.GetType().Name, Is.EqualTo("SequenceRootContainer"));
        }
    }
}