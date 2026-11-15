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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Integration test for the §38.6 template instantiate flow: feeds a
    /// <c>{{token}}</c>-containing template Body through
    /// <see cref="PlaceholderSequenceTemplateService.InstantiateAsync"/> and
    /// verifies the substitution path replaces placeholders with values from
    /// the request's <c>Parameters</c> JsonElement before the new sequence is
    /// created via <see cref="ISequenceService.CreateAsync"/>.
    /// </summary>
    [TestFixture]
    public class SequenceTemplateInstantiateTest {

        [Test]
        public async Task InstantiateAsync_substitutes_target_name_from_parameters() {
            var sequenceService = new Mock<ISequenceService>();
            SequenceCreateRequestDto? captured = null;
            sequenceService.Setup(s => s.CreateAsync(It.IsAny<SequenceCreateRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback<SequenceCreateRequestDto, string?, CancellationToken>((req, _, _) => captured = req)
                .ReturnsAsync((SequenceCreateRequestDto req, string? _, CancellationToken _) =>
                    new SequenceDto(Guid.NewGuid(), req.Name, req.Description,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, req.Body, req.TemplateOrigin));

            var svc = new PlaceholderSequenceTemplateService(sequenceService.Object);

            var parameters = JsonDocument.Parse("""{ "target_name": "M31" }""").RootElement;
            var request = new TemplateInstantiateRequestDto(NewSequenceName: "Andromeda Tonight", Parameters: parameters);

            var result = await svc.InstantiateAsync("single-target-lrgb", request, CancellationToken.None);

            Assert.That(result.Name, Is.EqualTo("Andromeda Tonight"));
            Assert.That(captured, Is.Not.Null);
            // The Body that CreateAsync receives should have target_name substituted.
            var bodyText = captured!.Body.GetRawText();
            Assert.That(bodyText, Does.Contain("\"target\": \"M31\""));
            Assert.That(bodyText, Does.Not.Contain("{{target_name}}"));
        }

        [Test]
        public async Task InstantiateAsync_passes_through_when_no_parameters() {
            var sequenceService = new Mock<ISequenceService>();
            SequenceCreateRequestDto? captured = null;
            sequenceService.Setup(s => s.CreateAsync(It.IsAny<SequenceCreateRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback<SequenceCreateRequestDto, string?, CancellationToken>((req, _, _) => captured = req)
                .ReturnsAsync((SequenceCreateRequestDto req, string? _, CancellationToken _) =>
                    new SequenceDto(Guid.NewGuid(), req.Name, req.Description,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, req.Body, req.TemplateOrigin));

            var svc = new PlaceholderSequenceTemplateService(sequenceService.Object);

            var request = new TemplateInstantiateRequestDto(NewSequenceName: "Untitled", Parameters: null);

            await svc.InstantiateAsync("single-target-lrgb", request, CancellationToken.None);

            // Unknown tokens preserved literally when no parameters supplied.
            Assert.That(captured, Is.Not.Null);
            Assert.That(captured!.Body.GetRawText(), Does.Contain("{{target_name}}"));
        }

        [Test]
        public async Task InstantiateAsync_throws_KeyNotFound_for_unknown_template() {
            var sequenceService = new Mock<ISequenceService>();
            var svc = new PlaceholderSequenceTemplateService(sequenceService.Object);

            var request = new TemplateInstantiateRequestDto(NewSequenceName: "x", Parameters: null);

            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                svc.InstantiateAsync("nonexistent-template", request, CancellationToken.None));
        }

        // ↑ara#1859: a template body is never schema-checked on instantiate, so a disk
        // template with no schemaVersion becomes a stored sequence the engine cannot load.
        // Target behaviour: the service validates the substituted body with
        // SequenceSchemaValidator (requireCapturableInstruction: false) and throws
        // ArgumentException, which the endpoint maps to 422. No sequence is created.
        [Test]
        public async Task InstantiateAsync_refuses_a_disk_template_body_without_schemaVersion() {
            var profileDir = Path.Combine(Path.GetTempPath(), "ara-tpl-" + Guid.NewGuid().ToString("N"));
            try {
                var templatesDir = Path.Combine(profileDir, "sequences", FileSequenceService.TemplatesDirName);
                Directory.CreateDirectory(templatesDir);
                await File.WriteAllTextAsync(Path.Combine(templatesDir, "no-schema.json"), """
                    { "name": "no-schema", "category": "single-target", "description": "d",
                      "isBuiltIn": false, "body": { "target": "{{target_name}}" } }
                    """, CancellationToken.None);
                var sequenceService = new Mock<ISequenceService>();
                var svc = new PlaceholderSequenceTemplateService(sequenceService.Object, profileDir, seedStarterTemplates: false);
                Assert.That(svc.TemplateExists("no-schema"), Is.True, "fixture template must load from disk");

                var request = new TemplateInstantiateRequestDto(NewSequenceName: "x", Parameters: null);

                await Assert.ThrowsAsync<ArgumentException>(() =>
                    svc.InstantiateAsync("no-schema", request, CancellationToken.None));
                sequenceService.Verify(s => s.CreateAsync(It.IsAny<SequenceCreateRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
            } finally {
                Directory.Delete(profileDir, recursive: true);
            }
        }

        // ADR 0018 rule 1: the engine must be able to load every stored body.
        [TestCase("single-target-lrgb")]
        [TestCase("single-target-narrowband")]
        [TestCase("all-night-dso-roster")]
        public async Task InstantiateAsync_built_in_body_loads_with_SequenceBodyDeserializer(string templateName) {
            var sequenceService = new Mock<ISequenceService>();
            SequenceCreateRequestDto? captured = null;
            sequenceService.Setup(s => s.CreateAsync(It.IsAny<SequenceCreateRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback<SequenceCreateRequestDto, string?, CancellationToken>((req, _, _) => captured = req)
                .ReturnsAsync((SequenceCreateRequestDto req, string? _, CancellationToken _) =>
                    new SequenceDto(Guid.NewGuid(), req.Name, req.Description,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, req.Body, req.TemplateOrigin));
            var svc = new PlaceholderSequenceTemplateService(sequenceService.Object);
            var parameters = JsonDocument.Parse("""{ "target_name": "M31" }""").RootElement;

            await svc.InstantiateAsync(templateName, new TemplateInstantiateRequestDto("t", parameters), CancellationToken.None);

            var deserializer = new SequenceBodyDeserializer(HeadlessSequencerFactory.WithDefaults(), logger: null);
            var ok = deserializer.TryDeserialize(captured!.Body, out var container, out var error);
            Assert.That(ok, Is.True, $"engine could not load the instantiated body: {error}");
            Assert.That(container, Is.Not.Null);
        }
    }
}