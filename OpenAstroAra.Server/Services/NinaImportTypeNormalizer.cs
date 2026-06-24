#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).
*/

#endregion "copyright"

using OpenAstroAra.Sequencer.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §38.4 — rewrites every <c>$type</c> token in an imported NINA sequence body to its canonical
    /// OpenAstroAra form, so a stored import uses the same type names the editor emits and the
    /// client catalog (keyed on <c>OpenAstroAra.Sequencer.*</c>) matches. Without this, an imported
    /// sequence keeps its original <c>NINA.Sequencer.*</c> names and every node renders as a generic
    /// fallback in the editor.
    ///
    /// **Lossless by construction:** only the <c>$type</c> string is swapped, and only when it
    /// resolves to a real type in this build (<see cref="NinaTypeRemapper.Canonicalize"/>). An
    /// unsupported/unknown <c>$type</c> is left exactly as-is, and no node is ever deserialized, so
    /// every other property and child is preserved verbatim — an unknown instruction keeps its
    /// identity and all of its data.
    /// </summary>
    public static class NinaImportTypeNormalizer {

        public static NormalizationResult Normalize(JsonElement body) {
            var node = JsonNode.Parse(body.GetRawText());
            if (node is null) {
                return new NormalizationResult(body, Array.Empty<string>());
            }
            var unsupported = new SortedSet<string>(StringComparer.Ordinal);
            NormalizeNode(node, unsupported);
            using var doc = JsonDocument.Parse(node.ToJsonString());
            return new NormalizationResult(doc.RootElement.Clone(), unsupported.ToList());
        }

        private static void NormalizeNode(JsonNode node, ISet<string> unsupported) {
            switch (node) {
                case JsonObject obj:
                    if (obj.TryGetPropertyValue("$type", out var typeNode) &&
                        typeNode is JsonValue value && value.TryGetValue<string>(out var typeString)) {
                        var canonical = NinaTypeRemapper.Canonicalize(typeString);
                        if (!string.Equals(canonical, typeString, StringComparison.Ordinal)) {
                            obj["$type"] = canonical;
                        } else if (canonical.StartsWith("NINA.Sequencer.", StringComparison.Ordinal)) {
                            // Still a NINA sequencer type after the pass ⇒ this build has no port of
                            // it. The node is preserved as-is; record the short name so the import
                            // result can tell the user which instructions weren't recognized.
                            unsupported.Add(ShortName(canonical));
                        }
                    }
                    foreach (var property in obj) {
                        if (property.Value is not null) {
                            NormalizeNode(property.Value, unsupported);
                        }
                    }
                    break;
                case JsonArray array:
                    foreach (var item in array) {
                        if (item is not null) {
                            NormalizeNode(item, unsupported);
                        }
                    }
                    break;
            }
        }

        private static string ShortName(string typeString) {
            // Strip any generic arguments first ('[' onward) so a comma inside the bracketed type
            // params can't truncate the name, then drop the assembly suffix at the top-level comma.
            var classSide = typeString.Split('[')[0].Split(',')[0];
            var lastDot = classSide.LastIndexOf('.');
            return lastDot >= 0 ? classSide[(lastDot + 1)..] : classSide;
        }
    }

    /// <summary>The normalized body plus the short names of any NINA instruction types this build
    /// doesn't yet support (left untouched in the body, reported to the user).</summary>
    public sealed record NormalizationResult(JsonElement Body, IReadOnlyList<string> UnsupportedTypes);
}
