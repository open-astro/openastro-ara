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
using OpenAstroAra.Sequencer;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.Serialization;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Bridges from the stored sequence-body <see cref="JsonElement"/> (per
/// playbook §38.1: NINA-verbatim <c>$type</c> tree with a top-level
/// <c>schemaVersion</c> field added by ARA) into NINA's
/// <see cref="ISequenceContainer"/> object tree via
/// <see cref="SequenceJsonConverter"/>.
///
/// This is the first step toward the real sequence engine — it pulls
/// the JSON apart but doesn't yet drive a run. Subsequent §38k sub-PRs
/// wire the resulting container into an executor that calls
/// <c>ISequenceItem.Execute</c> against the equipment service tree.
///
/// Graceful degradation: unknown <c>$type</c> strings (instructions /
/// containers / conditions / triggers ARA doesn't ship) round-trip
/// through <c>UnknownSequenceContainer</c> / <c>UnknownSequenceItem</c>
/// rather than throw — the same fallback NINA uses for plugin types it
/// can't resolve. The §38.5 validator + the §38.4 import flow flag
/// these to the user as "lossy translation" warnings.
/// </summary>
public sealed class SequenceBodyDeserializer {

    private readonly SequenceJsonConverter _converter;
    private readonly ILogger<SequenceBodyDeserializer>? _logger;

    public SequenceBodyDeserializer(ISequencerFactory factory, ILogger<SequenceBodyDeserializer>? logger = null) {
        _converter = new SequenceJsonConverter(factory);
        _logger = logger;
    }

    /// <summary>
    /// Attempt to deserialize the stored body into an
    /// <see cref="ISequenceContainer"/> root. Returns true on success
    /// (graceful-degradation paths count as success — caller checks
    /// for <see cref="UnknownSequenceContainer"/> via <c>is</c>). Returns
    /// false on malformed JSON or when the body isn't an object.
    /// </summary>
    public bool TryDeserialize(JsonElement body, out ISequenceContainer? container, out string? error) {
        container = null;
        error = null;

        if (body.ValueKind != JsonValueKind.Object) {
            error = $"Sequence body must be a JSON object; got {body.ValueKind}.";
            return false;
        }

        // Pass the raw text through Newtonsoft. The schemaVersion field
        // is harmless on the way through — Newtonsoft ignores object
        // properties that don't map onto the resolved Container type.
        var rawJson = body.GetRawText();

        try {
            container = _converter.Deserialize(rawJson);
            return container is not null;
        } catch (Newtonsoft.Json.JsonException ex) {
            _logger?.LogWarning(ex, "Sequence body failed to deserialize as NINA $type tree");
            error = $"Malformed sequence JSON: {ex.Message}";
            return false;
        } catch (Exception ex) {
            // Unexpected — but don't let the daemon crash on a bad body.
            // The §38.5 validator catches most of these before persist,
            // but historical files might pre-date current validation.
            _logger?.LogError(ex, "Unexpected error deserializing sequence body");
            error = $"Unexpected error: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
