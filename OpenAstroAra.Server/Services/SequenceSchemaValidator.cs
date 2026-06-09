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
/// §38.5 server-side validation for sequence JSON bodies. Run on
/// <c>POST /api/v1/sequences</c> and <c>PATCH /api/v1/sequences/{id}</c>
/// before persisting.
///
/// Implemented rules:
/// <list type="bullet">
///   <item>Body must be a JSON object (not null, array, or scalar).</item>
///   <item>Body must declare <c>"schemaVersion": "openastroara-sequence-v1"</c>.</item>
///   <item>Body must contain at least one capturable instruction reachable
///     from the root (§38.5 "capturable-instruction reachability").</item>
/// </list>
///
/// Still deferred (needs device + profile state):
/// <list type="bullet">
///   <item>Equipment-ID resolution against Alpaca-discovered device set.</item>
///   <item>Filter-name lookup against active profile's filter wheel slots.</item>
///   <item>Infinite-loop detection (LoopContainer with no terminating
///     condition).</item>
///   <item>Equipment slot uses match capability (e.g., RunAutofocus needs
///     a focuser slot filled).</item>
/// </list>
/// </summary>
public static class SequenceSchemaValidator {

    public const string SchemaVersion = "openastroara-sequence-v1";
    public const string SchemaVersionField = "schemaVersion";

    /// <summary>
    /// Run §38.5 validation. Returns <c>(true, null)</c> on success;
    /// <c>(false, reason)</c> on failure (caller maps to 422).
    ///
    /// <paramref name="requireCapturableInstruction"/> defaults to true. The
    /// only legitimate exception is the §38.6 template instantiate path
    /// before parameter substitution — a raw template may be a stub body
    /// without any concrete instructions yet. Other call sites should keep
    /// the default.
    /// </summary>
    public static (bool Valid, string? Reason) Validate(JsonElement body, bool requireCapturableInstruction = true) {
        if (body.ValueKind != JsonValueKind.Object) {
            return (false, $"sequence body must be a JSON object (received {body.ValueKind})");
        }

        if (!body.TryGetProperty(SchemaVersionField, out var sv)) {
            return (false, $"sequence body missing required field '{SchemaVersionField}'");
        }

        if (sv.ValueKind != JsonValueKind.String) {
            return (false, $"'{SchemaVersionField}' must be a string (received {sv.ValueKind})");
        }

        var actual = sv.GetString();
        if (!string.Equals(actual, SchemaVersion, System.StringComparison.Ordinal)) {
            return (false,
                $"unrecognized schema version '{actual}'; expected '{SchemaVersion}'");
        }

        if (requireCapturableInstruction) {
            var stats = SequenceBodyInspector.Inspect(body);
            if (stats.InstructionCount == 0) {
                return (false,
                    "sequence body must contain at least one capturable instruction reachable from root");
            }
        }

        return (true, null);
    }
}