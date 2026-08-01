#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace OpenAstroAra.Core.Guiding;

public sealed record MountKnowledgeEntry(
    IReadOnlyList<string> ManufacturerPatterns,
    IReadOnlyList<string> ModelPatterns,
    string DeclaredDriveType,
    IReadOnlyList<double> ExpectedPeriodicPeriodsSeconds,
    GuidingMountBehaviorClass PriorBehaviorClass,
    double PriorConfidence);

public static class MountKnowledgeBase {
    private const string ResourceName = "OpenAstroAra.Core.Guiding.MountKnowledgeBase.json";
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly IReadOnlyList<MountKnowledgeEntry> Entries = LoadEntries();

    public static MountKnowledgeEntry? Find(string manufacturer, string model) =>
        Entries.FirstOrDefault(entry => Matches(entry.ModelPatterns, model)
            && (string.IsNullOrWhiteSpace(manufacturer)
                || Matches(entry.ManufacturerPatterns, manufacturer)
                || Matches(entry.ManufacturerPatterns, model)));

    private static bool Matches(IReadOnlyList<string> patterns, string value) =>
        patterns.Any(pattern => !string.IsNullOrWhiteSpace(pattern)
            && value.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    private static MountKnowledgeEntry[] LoadEntries() {
        using var stream = typeof(MountKnowledgeBase).GetTypeInfo().Assembly
            .GetManifestResourceStream(ResourceName);
        if (stream is null) return Array.Empty<MountKnowledgeEntry>();
        var document = JsonSerializer.Deserialize<KnowledgeDocument>(stream, JsonOptions);
        return document?.Entries?.ToArray() ?? Array.Empty<MountKnowledgeEntry>();
    }

    [SuppressMessage("Performance", "CA1812", Justification = "System.Text.Json creates this DTO when loading the embedded knowledge resource.")]
    private sealed class KnowledgeDocument {
        public int SchemaVersion { get; set; }
        public List<MountKnowledgeEntry>? Entries { get; set; }
    }
}
