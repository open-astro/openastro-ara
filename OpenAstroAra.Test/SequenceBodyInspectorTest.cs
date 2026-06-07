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

    /// <summary>
    /// Verifies the lightweight §38.1-body inspector counts instructions +
    /// targets without instantiating the library's full sequencer graph.
    /// </summary>
    [TestFixture]
    public class SequenceBodyInspectorTest {

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        [Test]
        public void Inspect_returns_zero_for_empty_object() {
            var body = Parse("{}");
            var stats = SequenceBodyInspector.Inspect(body);
            Assert.That(stats.InstructionCount, Is.EqualTo(0));
            Assert.That(stats.TargetCount, Is.EqualTo(0));
        }

        [Test]
        public void Inspect_returns_zero_for_non_object_body() {
            Assert.That(SequenceBodyInspector.Inspect(Parse("[]")).InstructionCount, Is.EqualTo(0));
            Assert.That(SequenceBodyInspector.Inspect(Parse("\"string\"")).InstructionCount, Is.EqualTo(0));
            Assert.That(SequenceBodyInspector.Inspect(Parse("42")).TargetCount, Is.EqualTo(0));
        }

        [Test]
        public void Inspect_counts_NINA_instruction_types() {
            var body = Parse("""
                {
                    "schemaVersion": "openastroara-sequence-v1",
                    "$type": "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer",
                    "items": [
                        { "$type": "NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer" },
                        { "$type": "NINA.Sequencer.SequenceItem.Telescope.SlewScopeToRaDec, NINA.Sequencer" },
                        { "$type": "NINA.Sequencer.SequenceItem.Camera.CoolCamera, NINA.Sequencer" }
                    ]
                }
                """);
            var stats = SequenceBodyInspector.Inspect(body);
            Assert.That(stats.InstructionCount, Is.EqualTo(3));
            Assert.That(stats.TargetCount, Is.EqualTo(0));
        }

        [Test]
        public void Inspect_counts_DeepSkyObjectContainer_as_target() {
            var body = Parse("""
                {
                    "$type": "NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer",
                    "items": [
                        {
                            "$type": "NINA.Sequencer.Container.DeepSkyObjectContainer, NINA.Sequencer",
                            "name": "M31",
                            "items": [
                                { "$type": "NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer" }
                            ]
                        },
                        {
                            "$type": "NINA.Sequencer.Container.DeepSkyObjectContainer, NINA.Sequencer",
                            "name": "M42",
                            "items": [
                                { "$type": "NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer" },
                                { "$type": "NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer" }
                            ]
                        }
                    ]
                }
                """);
            var stats = SequenceBodyInspector.Inspect(body);
            Assert.That(stats.TargetCount, Is.EqualTo(2));
            Assert.That(stats.InstructionCount, Is.EqualTo(3));
        }

        [Test]
        public void Inspect_recognizes_OpenAstroAra_namespace_variants() {
            // Library project is renamed OpenAstroAra.Sequencer; the inspector
            // matches both NINA-namespaced and ARA-namespaced $type values.
            var body = Parse("""
                {
                    "$type": "OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure, OpenAstroAra.Sequencer"
                }
                """);
            var stats = SequenceBodyInspector.Inspect(body);
            Assert.That(stats.InstructionCount, Is.EqualTo(1));
        }

        [Test]
        public void Inspect_does_not_count_container_types_as_instructions() {
            // Containers nest items but aren't themselves instructions.
            var body = Parse("""
                {
                    "$type": "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer"
                }
                """);
            var stats = SequenceBodyInspector.Inspect(body);
            Assert.That(stats.InstructionCount, Is.EqualTo(0));
        }
    }
}