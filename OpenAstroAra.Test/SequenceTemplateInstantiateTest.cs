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
        public void InstantiateAsync_throws_KeyNotFound_for_unknown_template() {
            var sequenceService = new Mock<ISequenceService>();
            var svc = new PlaceholderSequenceTemplateService(sequenceService.Object);

            var request = new TemplateInstantiateRequestDto(NewSequenceName: "x", Parameters: null);

            Assert.ThrowsAsync<KeyNotFoundException>(() =>
                svc.InstantiateAsync("nonexistent-template", request, CancellationToken.None));
        }
    }
}
