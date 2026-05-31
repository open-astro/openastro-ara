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
/// Phase 38e covers the structural baseline only:
/// <list type="bullet">
///   <item>Body must be a JSON object (not null, array, or scalar).</item>
///   <item>Body must declare <c>"schemaVersion": "openastroara-sequence-v1"</c>.</item>
/// </list>
///
/// Equipment-ID resolution, filter-name lookup, capturable-instruction
/// reachability, infinite-loop detection (§38.5 rest of list) need device
/// + profile state and arrive once the §38 orchestrator is wired.
/// </summary>
public static class SequenceSchemaValidator {

    public const string SchemaVersion = "openastroara-sequence-v1";
    public const string SchemaVersionField = "schemaVersion";

    /// <summary>
    /// Run §38.5 validation. Returns <c>(true, null)</c> on success;
    /// <c>(false, reason)</c> on failure (caller maps to 422).
    /// </summary>
    public static (bool Valid, string? Reason) Validate(JsonElement body) {
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

        return (true, null);
    }
}
