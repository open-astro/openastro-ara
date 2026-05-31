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
using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §38.6 / §38.7 template service. Three hardcoded built-in templates
/// give the picker something to render out-of-box; additional user/.deb-
/// shipped templates can be dropped as JSON files into
/// <c>{profileDir}/sequences/templates/</c> per playbook §38.2 and the
/// service merges them with the built-ins.
///
/// Instantiate substitutes <c>{{token}}</c> placeholders in the template
/// Body via <see cref="SequenceTemplateVariables.Substitute"/> and
/// creates a fresh <see cref="SequenceDto"/> via the existing
/// <see cref="ISequenceService"/> so the new sequence shows up in the
/// list immediately.
/// </summary>
public sealed class PlaceholderSequenceTemplateService : ISequenceTemplateService {
    private readonly ISequenceService _sequences;
    private readonly string? _templatesDir;
    private readonly ILogger<PlaceholderSequenceTemplateService>? _logger;
    private static readonly JsonDocument _emptyBody = JsonDocument.Parse("{}");

    public PlaceholderSequenceTemplateService(ISequenceService sequences) {
        _sequences = sequences;
    }

    public PlaceholderSequenceTemplateService(
            ISequenceService sequences,
            string profileDir,
            ILogger<PlaceholderSequenceTemplateService>? logger = null) {
        _sequences = sequences;
        _templatesDir = Path.Combine(profileDir, "sequences", FileSequenceService.TemplatesDirName);
        _logger = logger;
    }

    private static JsonElement TemplateBody(string targetTokenName, string filterSet, int framesPerFilter, int integrationMinutes) {
        // §38.6 placeholder bodies — exercise the {{token}} substitution path
        // end-to-end without prescribing the full §38.1 sequence schema yet.
        // Real template authoring lands when the sequencer renderer is in place.
        var json = $$"""
            {
              "schemaVersion": "openastroara-sequence-v1",
              "target": "{{"{{"}}{{targetTokenName}}{{"}}"}}",
              "filterSet": "{{filterSet}}",
              "framesPerFilter": {{framesPerFilter}},
              "integrationMinutes": {{integrationMinutes}}
            }
            """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static readonly SequenceTemplateDto[] BuiltIns = new[] {
        new SequenceTemplateDto(
            Name: "single-target-lrgb",
            Category: "single-target",
            Description: "One target, 4 filters (L/R/G/B), default per-filter framecount + dithering.",
            IsBuiltIn: true,
            Body: TemplateBody("target_name", "LRGB", 30, 120)),
        new SequenceTemplateDto(
            Name: "single-target-narrowband",
            Category: "single-target",
            Description: "One target, 3 narrowband filters (Ha/OIII/SII), longer per-filter exposures.",
            IsBuiltIn: true,
            Body: TemplateBody("target_name", "SHO", 20, 180)),
        new SequenceTemplateDto(
            Name: "all-night-dso-roster",
            Category: "multi-target",
            Description: "Cycle through a target list; rotate when altitude crosses §35.4 limit.",
            IsBuiltIn: true,
            Body: TemplateBody("target_name", "LRGB", 60, 240)),
    };

    public Task<IReadOnlyList<SequenceTemplateDto>> ListAsync(CancellationToken ct) {
        // Built-ins first; disk templates (§38.7) merged on top with the same
        // name overriding the built-in (lets the .deb ship updated templates
        // without code changes).
        if (_templatesDir is null) return Task.FromResult<IReadOnlyList<SequenceTemplateDto>>(BuiltIns);

        var byName = BuiltIns.ToDictionary(t => t.Name, t => t, StringComparer.Ordinal);
        foreach (var disk in LoadDiskTemplates()) {
            byName[disk.Name] = disk;
        }
        return Task.FromResult<IReadOnlyList<SequenceTemplateDto>>(byName.Values.ToList());
    }

    private IEnumerable<SequenceTemplateDto> LoadDiskTemplates() {
        if (_templatesDir is null || !Directory.Exists(_templatesDir)) yield break;
        foreach (var path in Directory.EnumerateFiles(_templatesDir, "*.json")) {
            SequenceTemplateDto? dto = null;
            try {
                var json = File.ReadAllText(path);
                dto = JsonSerializer.Deserialize(json, AraJsonSerializerContext.Default.SequenceTemplateDto);
            } catch (Exception ex) {
                _logger?.LogWarning(ex, "Skipping invalid sequence template at {Path}", path);
            }
            if (dto is not null) yield return dto;
        }
    }

    public Task<SequenceDto> InstantiateAsync(string templateName, TemplateInstantiateRequestDto request, CancellationToken ct) {
        // Look in disk templates first (§38.7 override behavior) then fall back to built-ins.
        SequenceTemplateDto? template = null;
        if (_templatesDir is not null) {
            template = LoadDiskTemplates().FirstOrDefault(t => t.Name == templateName);
        }
        template ??= BuiltIns.FirstOrDefault(t => t.Name == templateName);
        if (template is null) {
            // Endpoint catches null returns and 404s. ISequenceTemplateService's
            // contract is non-nullable, so synthesize a "not found" SequenceDto
            // here — endpoint converts to 404 via existence-check.
            throw new KeyNotFoundException($"Template '{templateName}' not found.");
        }

        // Phase 38d — §38.6 template-variable substitution. The template Body is
        // serialized to text, {{token}} placeholders are swapped for the
        // request's Parameters values via SequenceTemplateVariables.Substitute,
        // then the result is parsed back to a JsonElement. Unknown tokens are
        // preserved literally so §38.5 validation can flag them.
        var substitutedBody = SubstituteTemplateBody(template.Body, request.Parameters);

        // Reuse the §38 create path so the new sequence shows up in
        // /api/v1/sequences immediately.
        return _sequences.CreateAsync(
            new SequenceCreateRequestDto(
                Name: request.NewSequenceName,
                Description: $"Instantiated from template '{templateName}'",
                Body: substitutedBody,
                TemplateOrigin: templateName),
            idempotencyKey: null, ct);
    }

    private static JsonElement SubstituteTemplateBody(JsonElement templateBody, JsonElement? parameters) {
        var rawText = templateBody.GetRawText();
        if (parameters is null || !parameters.Value.ValueKind.Equals(JsonValueKind.Object)) {
            // No parameters supplied; return template body unchanged.
            using var doc = JsonDocument.Parse(rawText);
            return doc.RootElement.Clone();
        }
        // Flatten Parameters top-level string/number properties into a
        // dict for the substitutor. Numbers are stringified via raw text
        // (preserves precision better than ToString on the parsed double).
        var values = new Dictionary<string, string>();
        foreach (var prop in parameters.Value.EnumerateObject()) {
            values[prop.Name] = prop.Value.ValueKind switch {
                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => string.Empty,
                _ => prop.Value.GetRawText(),
            };
        }
        var (result, _) = OpenAstroAra.Core.Utility.SequenceTemplateVariables.Substitute(rawText, values);
        try {
            using var doc = JsonDocument.Parse(result);
            return doc.RootElement.Clone();
        } catch (JsonException) {
            // Substitution produced invalid JSON (e.g. an unquoted brace inside
            // a string field). Fall back to the original body so the caller
            // can still create the sequence; §38.5 validation flags the
            // unresolved placeholders during the next save.
            using var doc = JsonDocument.Parse(rawText);
            return doc.RootElement.Clone();
        }
    }

    public bool TemplateExists(string templateName) {
        if (BuiltIns.Any(t => t.Name == templateName)) return true;
        if (_templatesDir is null) return false;
        return LoadDiskTemplates().Any(t => t.Name == templateName);
    }
}

/// <summary>
/// §38.4 NINA import service. Persists the raw upload under
/// <c>{profileDir}/sequences/imported/from-nina-YYYY-MM-DD/{newName}.json</c>
/// for audit + manual recovery, then creates a fresh library sequence
/// with <c>schemaVersion</c> backfilled if the source omitted it.
///
/// Phase 38h covers the simple-import path. Full §38.4 still needs:
///   - Equipment-ID remapping against the Alpaca-discovered device set
///   - Unsupported-instruction wrapping in SkippedInstruction placeholders
///   - NINA container/instruction type-name normalization
/// All three need real device state + the full §38.1 schema knowledge,
/// so they arrive once the §38 orchestrator is wired.
/// </summary>
public sealed class PlaceholderSequenceImportService : ISequenceImportService {
    private readonly ISequenceService _sequences;
    private readonly string? _importedDir;
    private readonly ILogger<PlaceholderSequenceImportService>? _logger;

    public PlaceholderSequenceImportService(ISequenceService sequences) {
        _sequences = sequences;
    }

    public PlaceholderSequenceImportService(
            ISequenceService sequences,
            string profileDir,
            ILogger<PlaceholderSequenceImportService>? logger = null) {
        _sequences = sequences;
        _importedDir = Path.Combine(profileDir, "sequences", FileSequenceService.ImportedDirName);
        _logger = logger;
    }

    public async Task<SequenceImportResultDto> ImportAsync(SequenceImportRequestDto request, CancellationToken ct) {
        var warnings = new List<string>();
        var body = request.NinaSequenceFile;

        // §38.4 step 3: ensure schemaVersion is present. NINA exports don't
        // include it; ARA-saved exports do. Detect missing + backfill so
        // the §38.5 validator accepts the imported sequence on first save.
        if (body.ValueKind == JsonValueKind.Object &&
            !body.TryGetProperty(SequenceSchemaValidator.SchemaVersionField, out _)) {
            body = BackfillSchemaVersion(body);
            warnings.Add($"schemaVersion was missing; backfilled to '{SequenceSchemaValidator.SchemaVersion}'.");
        }

        // §38.4 step 6: persist the raw upload under imported/from-nina-YYYY-MM-DD/
        // for audit + recovery. Best-effort; failures log but don't abort the
        // import — the in-library copy is what the user actually works with.
        TryPersistOriginal(request.NewName, request.NinaSequenceFile);

        var created = await _sequences.CreateAsync(
            new SequenceCreateRequestDto(
                Name: request.NewName,
                Description: "Imported from NINA",
                Body: body,
                TemplateOrigin: null),
            idempotencyKey: null, ct);

        return new SequenceImportResultDto(
            CreatedSequenceId: created.Id,
            Name: created.Name,
            Warnings: warnings.ToArray(),
            DroppedInstructionTypes: Array.Empty<string>(),
            LossyTranslation: false);
    }

    private static JsonElement BackfillSchemaVersion(JsonElement body) {
        // Re-emit the object with schemaVersion prepended. Preserves all
        // original fields and their order otherwise.
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) {
            w.WriteStartObject();
            w.WriteString(SequenceSchemaValidator.SchemaVersionField, SequenceSchemaValidator.SchemaVersion);
            foreach (var prop in body.EnumerateObject()) {
                if (prop.NameEquals(SequenceSchemaValidator.SchemaVersionField)) continue;
                prop.WriteTo(w);
            }
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }

    private void TryPersistOriginal(string name, JsonElement original) {
        if (_importedDir is null) return;
        try {
            var dateBucket = $"from-nina-{DateTime.UtcNow:yyyy-MM-dd}";
            var bucketDir = Path.Combine(_importedDir, dateBucket);
            Directory.CreateDirectory(bucketDir);

            var safeName = OpenAstroAra.Core.Utility.FilenameTemplateSanitizer.SanitizeComponent(name);
            if (string.IsNullOrEmpty(safeName)) safeName = "unnamed";
            var filename = $"{safeName}-{DateTime.UtcNow:HHmmss}.json";
            var path = Path.Combine(bucketDir, filename);
            File.WriteAllText(path, original.GetRawText());
        } catch (Exception ex) {
            _logger?.LogWarning(ex, "Failed to persist NINA import original for '{Name}'", name);
        }
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
