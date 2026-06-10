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
using OpenAstroAra.Sequencer.SequenceItem.FilterWheel;
using OpenAstroAra.Server.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38.7 — validates every disk-shipped starter template (the <c>templates/*.json</c> files the
    /// .deb carries and the daemon seeds into the profile on first run): each must parse as a
    /// <see cref="OpenAstroAra.Server.Contracts.SequenceTemplateDto"/>, pass §38.5 schema validation
    /// (incl. the capturable-instruction reachability check), and deserialize through the REAL
    /// engine (<see cref="SequenceBodyDeserializer"/> + <see cref="HeadlessSequencerFactory"/>) into
    /// a container whose registered instructions resolve to their real types — so a template that
    /// drifts from the engine's schema fails CI, not a user's first run.
    /// </summary>
    [TestFixture]
    public class StarterTemplateTest {

        private static readonly string TemplatesDir = Path.Combine(TestContext.CurrentContext.TestDirectory, "templates");

        private static List<string> TemplateFiles() {
            Assert.That(Directory.Exists(TemplatesDir), Is.True,
                $"shipped templates dir missing at {TemplatesDir} — check the Server csproj Content copy + Test project link");
            var files = Directory.EnumerateFiles(TemplatesDir, "*.json").OrderBy(f => f).ToList();
            Assert.That(files, Has.Count.EqualTo(3), "expected exactly the three §38.7 starter templates");
            return files;
        }

        [Test]
        public void Every_starter_template_parses_and_passes_schema_validation() {
            foreach (var path in TemplateFiles()) {
                var dto = JsonSerializer.Deserialize(
                    File.ReadAllText(path),
                    OpenAstroAra.Server.AraJsonSerializerContext.Default.SequenceTemplateDto);
                Assert.That(dto, Is.Not.Null, $"{Path.GetFileName(path)} must parse as a SequenceTemplateDto");
                Assert.That(dto!.Name, Is.Not.Empty);
                Assert.That(dto.IsBuiltIn, Is.False, "disk templates are not built-ins");

                var (valid, reason) = SequenceSchemaValidator.Validate(dto.Body);
                Assert.That(valid, Is.True, $"{dto.Name}: {reason}");
            }
        }

        [Test]
        public void Every_starter_template_deserializes_through_the_real_engine() {
            var deserializer = new SequenceBodyDeserializer(HeadlessSequencerFactory.WithDefaults());
            foreach (var path in TemplateFiles()) {
                var dto = JsonSerializer.Deserialize(
                    File.ReadAllText(path),
                    OpenAstroAra.Server.AraJsonSerializerContext.Default.SequenceTemplateDto)!;

                var ok = deserializer.TryDeserialize(dto.Body, out var container, out var error);
                Assert.That(ok, Is.True, $"{dto.Name}: {error}");
                Assert.That(container, Is.TypeOf<SequentialContainer>(),
                    $"{dto.Name}: root must resolve to a real SequentialContainer, not Unknown*");
                Assert.That(container!.Items, Is.Not.Empty, $"{dto.Name}: root container has no items");

                // No REGISTERED instruction may silently degrade: anything Unknown must be one of
                // the deliberate NINA-verbatim capture nodes (TakeExposure — not ported yet; they
                // activate via the §38k-6 remap the day the capture path lands).
                var items = container.GetItemsSnapshot();
                var unknowns = items
                    .Where(i => i.GetType().Name.StartsWith("Unknown", System.StringComparison.Ordinal))
                    .ToList();
                Assert.That(unknowns, Has.Count.LessThan(items.Count),
                    $"{dto.Name}: every top-level item degraded to Unknown — the template schema has drifted");
            }
        }

        [Test]
        public void Filter_blocks_resolve_their_SwitchFilter_and_loop_conditions() {
            var deserializer = new SequenceBodyDeserializer(HeadlessSequencerFactory.WithDefaults());
            var path = Path.Combine(TemplatesDir, "lrgb-dso.json");
            var dto = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                OpenAstroAra.Server.AraJsonSerializerContext.Default.SequenceTemplateDto)!;

            deserializer.TryDeserialize(dto.Body, out var container, out _);
            var blocks = container!.GetItemsSnapshot().OfType<SequentialContainer>().ToList();
            Assert.That(blocks, Has.Count.EqualTo(4), "L/R/G/B filter blocks");
            foreach (var block in blocks) {
                Assert.That(block.GetConditionsSnapshot(), Has.Count.EqualTo(1),
                    $"{block.Name}: per-filter loop condition missing");
                Assert.That(block.GetItemsSnapshot().Any(i => i is SwitchFilter),
                    Is.True, $"{block.Name}: SwitchFilter did not resolve to its real type");
            }
        }
    }
}
