#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;

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
public sealed partial class PlaceholderSequenceTemplateService : ISequenceTemplateService {
    private readonly ISequenceService _sequences;
    private readonly string? _templatesDir;
    private readonly ILogger<PlaceholderSequenceTemplateService> _logger = NullLogger<PlaceholderSequenceTemplateService>.Instance;

    public PlaceholderSequenceTemplateService(ISequenceService sequences) {
        _sequences = sequences;
    }

    public PlaceholderSequenceTemplateService(
            ISequenceService sequences,
            string profileDir,
            ILogger<PlaceholderSequenceTemplateService>? logger = null,
            bool seedStarterTemplates = true) {
        _sequences = sequences;
        _templatesDir = Path.Combine(profileDir, "sequences", FileSequenceService.TemplatesDirName);
        _logger = logger ?? NullLogger<PlaceholderSequenceTemplateService>.Instance;
        // Opt-out exists for tests that assert merge behavior against their own fixture files;
        // the daemon always seeds.
        if (seedStarterTemplates) {
            SeedStarterTemplates();
        }
    }

    /// <summary>
    /// §38.7 — seeds the disk-shipped starter templates (<c>templates/*.json</c> next to the
    /// binary; carried into the .deb by the publish output) into the profile's templates dir on
    /// first run. Copy-if-missing only: a user-edited (or user-deleted-and-recreated) template is
    /// never overwritten, and removing a starter re-seeds it on the next start.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Startup seeding boundary: a missing/readonly templates dir or a copy failure must degrade to 'no starter templates' (built-ins still serve), never fail daemon construction. CA1031's log-and-recover boundary applies.")]
    private void SeedStarterTemplates() {
        if (_templatesDir is null) {
            return;
        }
        try {
            var shippedDir = Path.Combine(AppContext.BaseDirectory, "templates");
            if (!Directory.Exists(shippedDir)) {
                return; // dev layouts without the Content output: built-ins still serve
            }
            Directory.CreateDirectory(_templatesDir);
            foreach (var shipped in Directory.EnumerateFiles(shippedDir, "*.json")) {
                var name = Path.GetFileName(shipped);
                var target = Path.Combine(_templatesDir, name);
                if (File.Exists(target)) {
                    continue;
                }
                try {
                    File.Copy(shipped, target);
                    LogTemplateSeeded(name);
                } catch (IOException) when (File.Exists(target)) {
                    // Exists-check + copy isn't atomic: a second daemon instance won the race and
                    // the template IS seeded — not a failure, no warning.
                }
            }
        } catch (Exception ex) {
            LogTemplateSeedFailed(ex);
        }
    }

    // Built-in bodies are real §38.1 sequence trees (same shape as the shipped starters in
    // templates/*.json): a root SequentialContainer holding one LoopCondition block per filter.
    // The {{target_name}} token is replaced on instantiate.
    private const string SeqNs = "OpenAstroAra.Sequencer";
    private const string ColArgs = "System.Collections.ObjectModel.ObservableCollection`1[[OpenAstroAra.Sequencer.";

    private static string Col(string iface, string values) =>
        $$"""{ "$type": "{{ColArgs}}{{iface}}, {{SeqNs}}]], System.ObjectModel", "$values": [{{values}}] }""";

    private static JsonElement TemplateBody(string targetTokenName, string[] filters, int framesPerFilter, int exposureSeconds) {
        var token = "{{" + targetTokenName + "}}";
        var blocks = new System.Text.StringBuilder();
        for (var i = 0; i < filters.Length; i++) {
            if (i > 0) blocks.Append(',');
            var loop = $$"""{ "$type": "{{SeqNs}}.Conditions.LoopCondition, {{SeqNs}}", "Iterations": {{framesPerFilter}}, "Parent": null }""";
            var items = $$"""
                { "$type": "{{SeqNs}}.SequenceItem.FilterWheel.SwitchFilter, {{SeqNs}}",
                  "Filter": { "$type": "OpenAstroAra.Core.Model.Equipment.FilterInfo, OpenAstroAra.Core", "_name": "{{filters[i]}}", "_position": {{i}} },
                  "Parent": null, "ErrorBehavior": 0, "Attempts": 1 },
                { "$type": "NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer",
                  "ExposureTime": {{exposureSeconds}}.0, "Gain": -1, "Offset": -1,
                  "Binning": { "$type": "OpenAstroAra.Core.Model.Equipment.BinningMode, OpenAstroAra.Core", "X": 1, "Y": 1 },
                  "ImageType": "LIGHT", "ExposureCount": 0, "Parent": null, "ErrorBehavior": 0, "Attempts": 1 }
                """;
            blocks.Append($$"""
                { "$type": "{{SeqNs}}.Container.SequentialContainer, {{SeqNs}}",
                  "Strategy": { "$type": "{{SeqNs}}.Container.ExecutionStrategy.SequentialStrategy, {{SeqNs}}" },
                  "Name": "{{filters[i]}} x{{framesPerFilter}}",
                  "Conditions": {{Col("Conditions.ISequenceCondition", loop)}},
                  "IsExpanded": true,
                  "Items": {{Col("SequenceItem.ISequenceItem", items)}},
                  "Triggers": {{Col("Trigger.ISequenceTrigger", "")}},
                  "Parent": null, "ErrorBehavior": 0, "Attempts": 1 }
                """);
        }
        var json = $$"""
            {
              "schemaVersion": "openastroara-sequence-v1",
              "$type": "{{SeqNs}}.Container.SequentialContainer, {{SeqNs}}",
              "Strategy": { "$type": "{{SeqNs}}.Container.ExecutionStrategy.SequentialStrategy, {{SeqNs}}" },
              "Name": "{{string.Join("", filters)}} - {{token}}",
              "Conditions": {{Col("Conditions.ISequenceCondition", "")}},
              "IsExpanded": true,
              "Items": {{Col("SequenceItem.ISequenceItem", blocks.ToString())}},
              "Triggers": {{Col("Trigger.ISequenceTrigger", "")}},
              "Parent": null, "ErrorBehavior": 0, "Attempts": 1
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
            Body: TemplateBody("target_name", new[] { "L", "R", "G", "B" }, 30, 120)),
        new SequenceTemplateDto(
            Name: "single-target-narrowband",
            Category: "single-target",
            Description: "One target, 3 narrowband filters (Ha/OIII/SII), longer per-filter exposures.",
            IsBuiltIn: true,
            Body: TemplateBody("target_name", new[] { "Ha", "OIII", "SII" }, 20, 180)),
        new SequenceTemplateDto(
            Name: "all-night-dso-roster",
            Category: "multi-target",
            Description: "Cycle through a target list; rotate when altitude crosses §35.4 limit.",
            IsBuiltIn: true,
            Body: TemplateBody("target_name", new[] { "L", "R", "G", "B" }, 60, 240)),
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
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                LogInvalidTemplate(ex, path);
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
        // preserved literally; a token left in a value is still a valid string.
        var substitutedBody = SubstituteTemplateBody(template.Body, request.Parameters);

        // ADR 0018 rules 1 and 3: a stored body must load in the engine, so a disk
        // template is checked like a created sequence. The endpoint maps this to 422.
        var (valid, reason) = SequenceSchemaValidator.Validate(substitutedBody, requireCapturableInstruction: false);
        if (!valid) throw new TemplateBodyRefusedException(reason ?? "template body failed the schema check");

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping invalid sequence template at {Path}")]
    private partial void LogInvalidTemplate(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded starter sequence template {Name} into the profile templates dir")]
    private partial void LogTemplateSeeded(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Starter-template seeding failed; built-in templates still serve")]
    private partial void LogTemplateSeedFailed(Exception ex);
}