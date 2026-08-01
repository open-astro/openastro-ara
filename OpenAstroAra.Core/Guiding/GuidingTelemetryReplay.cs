#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Core.Guiding;

/// <summary>JSON-lines replay source used by deterministic tests and offline reports.</summary>
public static class GuidingTelemetryReplay {
    public static async IAsyncEnumerable<GuidingTelemetrySample> ReadJsonLinesAsync(
        Stream source, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(source);
        using var reader = new StreamReader(source, leaveOpen: true);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line) {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var sample = JsonSerializer.Deserialize<GuidingTelemetrySample>(line);
            if (sample is not null) yield return sample;
        }
    }

    public static async Task<GuidingTelemetryWindow> ReadWindowAsync(
        Stream source, string sourceName = "replay", CancellationToken ct = default) {
        var samples = new List<GuidingTelemetrySample>();
        await foreach (var sample in ReadJsonLinesAsync(source, ct).ConfigureAwait(false))
            samples.Add(sample);
        var ordered = samples.OrderBy(s => s.TimestampUtc).ToArray();
        var start = ordered.Length == 0 ? DateTimeOffset.UtcNow : ordered[0].TimestampUtc;
        var end = ordered.Length == 0 ? start : ordered[^1].TimestampUtc;
        return new GuidingTelemetryWindow(ordered, sourceName, start, end);
    }

    public static async Task WriteJsonLinesAsync(
        Stream destination, IEnumerable<GuidingTelemetrySample> samples, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(samples);
        await using var writer = new StreamWriter(destination, leaveOpen: true);
        foreach (var sample in samples) {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(JsonSerializer.Serialize(sample)).ConfigureAwait(false);
        }
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }
}
