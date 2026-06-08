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

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) {
            throw new NotImplementedException();
        }

        protected Type? GetType(string typeString) {
            var t = Type.GetType(typeString);
            if (t != null) return t;

            // Two distinct migrations live here:
            //
            // 1. NINA → OpenAstroAra namespace+assembly rename (post-0.5l/0.5g
            //    project rename). A NINA-original sequence body has
            //    "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer";
            //    we need to swap to
            //    "OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer".
            //    The previous logic only swapped a ", NINA" assembly suffix,
            //    leaving the namespace on the class side as NINA.*. That
            //    yielded no Type.GetType hit and all NINA imports fell to
            //    UnknownSequence* — the bug §38k-6 closes.
            // 2. Pre-module-split → post-split (when N.I.N.A. used a single
            //    "NINA" assembly). The class side keeps its short namespace
            //    (e.g. "NINA.Sequencer.X") but the assembly token is ", NINA";
            //    after the split that assembly became OpenAstroAra.Sequencer /
            //    .Core / .Astrometry depending on the type. Kept as the
            //    second-pass fallback below.
            t = Type.GetType(NinaTypeRemapper.RemapNamespace(typeString));
            if (t != null) return t;

            // Pre-split assembly migration fallbacks. Only kicks in when the
            // namespace remap above didn't find a match, so legacy "single-
            // NINA-assembly" bodies still resolve.
            t = Type.GetType(typeString.Replace(", NINA", ", OpenAstroAra.Sequencer"));
            if (t != null) return t;
            t = Type.GetType(typeString.Replace(", NINA", ", OpenAstroAra.Core"));
            if (t != null) return t;
            t = Type.GetType(typeString.Replace(", NINA", ", OpenAstroAra.Astrometry"));
            return t;
        }
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
                .Replace("NINA.Sequencer", "OpenAstroAra.Sequencer")
                .Replace("NINA.Astrometry", "OpenAstroAra.Astrometry")
                .Replace("NINA.Core", "OpenAstroAra.Core");
        }
    }
}