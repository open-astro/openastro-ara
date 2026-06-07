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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Lightweight structural read of a §38.1 sequence body. Walks the JSON
/// DOM and counts instruction nodes + target containers without
/// instantiating the library's full <c>ISequenceRootContainer</c> graph
/// (which depends on the §38 orchestrator + a constructed
/// <c>ISequencerFactory</c>).
///
/// Used by <see cref="FileSequenceService.ListAsync"/> to populate the
/// <see cref="OpenAstroAra.Server.Contracts.SequenceListItemDto.InstructionCount"/>
/// + <c>TargetCount</c> fields so the WILMA sequence list shows real
/// values instead of zeros.
///
/// Detection rules (best-effort against NINA's verbatim schema):
/// <list type="bullet">
///   <item><b>Instruction</b>: any object whose <c>$type</c> ends with
///     <c>".SequenceItem.&lt;Something&gt;, NINA.Sequencer"</c> or starts
///     with the <c>"OpenAstroAra.Sequencer.SequenceItem."</c> namespace
///     path. Heuristic — robust enough to count without parsing the full
///     polymorphic graph.</item>
///   <item><b>Target</b>: any object whose <c>$type</c> ends with
///     <c>"DeepSkyObjectContainer, NINA.Sequencer"</c> or its
///     <c>OpenAstroAra.Sequencer</c> equivalent.</item>
/// </list>
/// </summary>
public static class SequenceBodyInspector {

    public sealed record Stats(int InstructionCount, int TargetCount);

    public static Stats Inspect(JsonElement body) {
        if (body.ValueKind != JsonValueKind.Object) return new Stats(0, 0);
        var instructions = 0;
        var targets = 0;
        Walk(body, ref instructions, ref targets);
        return new Stats(instructions, targets);
    }

    private static void Walk(JsonElement node, ref int instructions, ref int targets) {
        switch (node.ValueKind) {
            case JsonValueKind.Object:
                if (node.TryGetProperty("$type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String) {
                    var typeName = typeProp.GetString() ?? string.Empty;
                    if (LooksLikeInstruction(typeName)) instructions++;
                    if (LooksLikeTarget(typeName)) targets++;
                }
                foreach (var prop in node.EnumerateObject()) {
                    Walk(prop.Value, ref instructions, ref targets);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray()) {
                    Walk(item, ref instructions, ref targets);
                }
                break;
        }
    }

    private static bool LooksLikeInstruction(string typeName) =>
        typeName.Contains(".SequenceItem.", System.StringComparison.Ordinal) &&
        !typeName.Contains(".SequenceItem.Container.", System.StringComparison.Ordinal);

    private static bool LooksLikeTarget(string typeName) =>
        typeName.Contains("DeepSkyObjectContainer", System.StringComparison.Ordinal);
}