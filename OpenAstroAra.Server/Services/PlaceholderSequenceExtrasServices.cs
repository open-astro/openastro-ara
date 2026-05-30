#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Text.Json;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Phase 13.15 — placeholder <see cref="ISequenceTemplateService"/>.
/// Three built-in fixture templates so the §38.6 template picker has
/// real entries to render. Instantiate creates a fresh
/// <see cref="SequenceDto"/> via the existing <see cref="ISequenceService"/>
/// so the new sequence shows up in the list immediately.
/// </summary>
public sealed class PlaceholderSequenceTemplateService : ISequenceTemplateService {
    private readonly ISequenceService _sequences;
    private static readonly JsonDocument _emptyBody = JsonDocument.Parse("{}");

    public PlaceholderSequenceTemplateService(ISequenceService sequences) {
        _sequences = sequences;
    }

    private static readonly SequenceTemplateDto[] BuiltIns = new[] {
        new SequenceTemplateDto(
            Name: "single-target-lrgb",
            Category: "single-target",
            Description: "One target, 4 filters (L/R/G/B), default per-filter framecount + dithering.",
            IsBuiltIn: true,
            Body: _emptyBody.RootElement.Clone()),
        new SequenceTemplateDto(
            Name: "single-target-narrowband",
            Category: "single-target",
            Description: "One target, 3 narrowband filters (Ha/OIII/SII), longer per-filter exposures.",
            IsBuiltIn: true,
            Body: _emptyBody.RootElement.Clone()),
        new SequenceTemplateDto(
            Name: "all-night-dso-roster",
            Category: "multi-target",
            Description: "Cycle through a target list; rotate when altitude crosses §35.4 limit.",
            IsBuiltIn: true,
            Body: _emptyBody.RootElement.Clone()),
    };

    public Task<IReadOnlyList<SequenceTemplateDto>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SequenceTemplateDto>>(BuiltIns);

    public Task<SequenceDto> InstantiateAsync(string templateName, TemplateInstantiateRequestDto request, CancellationToken ct) {
        var template = BuiltIns.FirstOrDefault(t => t.Name == templateName);
        if (template is null) {
            // Endpoint catches null returns and 404s. ISequenceTemplateService's
            // contract is non-nullable, so synthesize a "not found" SequenceDto
            // here — endpoint converts to 404 via existence-check.
            throw new KeyNotFoundException($"Template '{templateName}' not found.");
        }
        // Reuse the §38 create path so the new sequence shows up in
        // /api/v1/sequences immediately.
        return _sequences.CreateAsync(
            new SequenceCreateRequestDto(
                Name: request.NewSequenceName,
                Description: $"Instantiated from template '{templateName}'",
                Body: template.Body,
                TemplateOrigin: templateName),
            idempotencyKey: null, ct);
    }

    public bool TemplateExists(string templateName) => BuiltIns.Any(t => t.Name == templateName);
}

/// <summary>
/// Phase 13.15 — placeholder <see cref="ISequenceImportService"/>.
/// Returns a synthetic import result with one warning and no dropped
/// instructions, mirroring what a real NINA XML translation would look
/// like for a simple sequence. Real translator + warning catalog land
/// in Phase 14 §38.4.
/// </summary>
public sealed class PlaceholderSequenceImportService : ISequenceImportService {
    private readonly ISequenceService _sequences;

    public PlaceholderSequenceImportService(ISequenceService sequences) {
        _sequences = sequences;
    }

    public async Task<SequenceImportResultDto> ImportAsync(SequenceImportRequestDto request, CancellationToken ct) {
        // Create an empty sequence so the import has a real id to point at.
        var created = await _sequences.CreateAsync(
            new SequenceCreateRequestDto(
                Name: request.NewName,
                Description: "Imported from NINA (§38.4 — placeholder)",
                Body: request.NinaSequenceFile,
                TemplateOrigin: null),
            idempotencyKey: null, ct);
        return new SequenceImportResultDto(
            CreatedSequenceId: created.Id,
            Name: created.Name,
            Warnings: new[] {
                "Placeholder import: no real NINA XML translator wired yet (§38.4 lands in Phase 14).",
            },
            DroppedInstructionTypes: Array.Empty<string>(),
            LossyTranslation: false);
    }
}

/// <summary>
/// Phase 13.15 — placeholder <see cref="IAutoFlatsService"/>. Returns
/// 202 OperationAccepted; real impl pauses the sequence run until the
/// user's decision arrives per §48.
/// </summary>
public sealed class PlaceholderAutoFlatsService : IAutoFlatsService {
    public Task<OperationAcceptedDto> ProvideDecisionAsync(Guid sequenceId, AutoFlatsDecisionRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.auto-flats.decide", idempotencyKey));
}
