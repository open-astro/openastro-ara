#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.Trigger;
using System;
using System.Diagnostics.CodeAnalysis;

namespace OpenAstroAra.Sequencer.Serialization {

    public abstract class JsonCreationConverter<T> : JsonConverter {

        /// <summary>
        /// Create an instance of objectType, based properties in the JSON object
        /// </summary>
        /// <param name="objectType">type of object expected</param>
        /// <param name="jObject">
        /// contents of JSON object that will be deserialized
        /// </param>
        /// <returns></returns>
        public abstract T Create(Type objectType, JObject jObject);

        public override bool CanConvert(Type objectType) {
            return typeof(T).IsAssignableFrom(objectType);
        }

        public override bool CanWrite => false;

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Per-entity deserialization recovery boundary: any failure while resolving/creating/populating a single sequence entity (JSON, reflection, type-load, or cast faults) is logged and replaced with an Unknown* placeholder so one corrupt entity cannot fail the entire sequence load. CA1031 documents that catching general exceptions is acceptable at such recover-and-continue boundaries.")]
        public override object? ReadJson(JsonReader reader,
                                        Type objectType,
                                         object? existingValue,
                                         JsonSerializer serializer) {
            if (reader.TokenType == JsonToken.Null) return null;

            // Load JObject from stream
            JObject jObject = JObject.Load(reader);
            T? target = default(T);
            try {

                if (jObject != null) {
                    if (jObject["$ref"] != null) {
                        string? id = (jObject["$ref"] as JValue)?.Value as string;
                        target = (T)serializer.ReferenceResolver!.ResolveReference(serializer, id!);
                    } else {
                        // Create target object based on JObject
                        target = Create(objectType, jObject);

                        // Populate the object properties
                        serializer.Populate(jObject.CreateReader(), target!);
                    }
                }
            } catch (Exception ex) {
                Logger.Error("Failed to deserialize sequence entity", ex);
                var unknownEntityName = "";
                if (jObject.TryGetValue("$type", out var token)) {
                    unknownEntityName = token?.ToString() ?? "";
                }
                switch (objectType) {
                    case ISequenceTrigger:
                        return new UnknownSequenceTrigger(unknownEntityName);
                    case ISequenceCondition:
                        return new UnknownSequenceCondition(unknownEntityName);
                    default:
                        return new UnknownSequenceItem(unknownEntityName);
                }

            }

            return target;
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) {
            throw new NotImplementedException();
        }

        protected Type? GetType(string typeString) => NinaTypeRemapper.ResolveType(typeString);
    }

    /// <summary>
    /// Static helper for the §38k-6 NINA → OpenAstroAra namespace+assembly
    /// remap used by <see cref="JsonCreationConverter{T}.GetType"/>. Lifted
    /// out of the generic class so tests can exercise it without having to
    /// pick a concrete <c>T</c>.
    /// </summary>
    public static class NinaTypeRemapper {
        /// <summary>
        /// Remap a NINA-namespace AQTN to the OpenAstroAra namespaces. Handles
        /// the three top-level rename pairs from §0.5g (NINA.Core →
        /// OpenAstroAra.Core), §0.5h (NINA.Astrometry → OpenAstroAra.Astrometry),
        /// and §0.5l (NINA.Sequencer → OpenAstroAra.Sequencer). Matches against
        /// both the class side (left of the comma) and the assembly side
        /// (right of the comma).
        /// </summary>
        public static string RemapNamespace(string typeString) {
            return typeString
                .Replace("NINA.Sequencer", "OpenAstroAra.Sequencer", StringComparison.Ordinal)
                .Replace("NINA.Astrometry", "OpenAstroAra.Astrometry", StringComparison.Ordinal)
                .Replace("NINA.Core", "OpenAstroAra.Core", StringComparison.Ordinal);
        }

        /// <summary>
        /// Resolve a (possibly NINA-original) assembly-qualified type name to the live
        /// OpenAstroAra type, or <c>null</c> if it isn't a type this build knows. Two migrations:
        /// (1) the §38k-6 NINA→OpenAstroAra namespace+assembly rename (a NINA body has
        /// "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer"); (2) the pre-module-split
        /// single-"NINA"-assembly form where only the <c>, NINA</c> assembly token is rewritten.
        /// </summary>
        public static Type? ResolveType(string typeString) {
            var t = Type.GetType(typeString);
            if (t != null) return t;
            t = Type.GetType(RemapNamespace(typeString));
            if (t != null) return t;
            // Pre-split assembly-migration fallbacks for legacy single-NINA-assembly bodies.
            t = Type.GetType(typeString.Replace(", NINA", ", OpenAstroAra.Sequencer", StringComparison.Ordinal));
            if (t != null) return t;
            t = Type.GetType(typeString.Replace(", NINA", ", OpenAstroAra.Core", StringComparison.Ordinal));
            if (t != null) return t;
            t = Type.GetType(typeString.Replace(", NINA", ", OpenAstroAra.Astrometry", StringComparison.Ordinal));
            return t;
        }

        /// <summary>
        /// The canonical <c>"{FullName}, {AssemblyShortName}"</c> form of a sequence <c>$type</c>
        /// when it resolves to a real type in this build; otherwise the original string unchanged.
        /// Used to normalize an imported NINA body to OpenAstroAra type names **losslessly** — a
        /// <c>$type</c> that doesn't resolve (an unsupported/unknown instruction) is left exactly as
        /// it was, so its node keeps its original identity and all of its data.
        /// </summary>
        public static string Canonicalize(string typeString) {
            var resolved = ResolveType(typeString);
            return resolved?.FullName is { } fullName
                ? $"{fullName}, {resolved.Assembly.GetName().Name}"
                : typeString;
        }
    }
}