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
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.Trigger;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Serialization {

    public class SequenceJsonConverter {
        private ISequencerFactory factory;

        private List<JsonConverter> converters;

        public SequenceJsonConverter(ISequencerFactory factory) {
            this.factory = factory;
            var c = new SequenceContainerCreationConverter(factory);
            this.converters = new List<JsonConverter>() {
                c,
                new SequenceItemCreationConverter(factory, c),
                new SequenceConditionCreationConverter(factory),
                new SequenceTriggerCreationConverter(factory),
                new SequenceDateTimeProviderCreationConverter(factory.DateTimeProviders)
            };
        }

        [SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "Serialize is part of the public SequenceJsonConverter instance API, paired with the instance Deserialize and resolved via DI; all call sites invoke it on an instance. Converting only Serialize to static would be a breaking change and leave an inconsistent half-static converter. CA1822 sanctions suppression where marking static is a breaking change.")]
        [SuppressMessage("Security", "CA2326:Do not use TypeNameHandling values other than None",
            Justification = "TypeNameHandling.All is required to emit the polymorphic $type metadata the sequence file format depends on, and is used here on the serialization path only. Deserialization (Deserialize) never sets TypeNameHandling; it resolves types through controlled custom JsonCreationConverters with an explicit namespace remap, so no untrusted type is instantiated from the $type token. Sequence JSON is read from the user's own local profile folder. CA2326 documents that serialization-only use is safe.")]
        public string Serialize(ISequenceContainer container) {
            var json = JsonConvert.SerializeObject(container, Formatting.Indented, new JsonSerializerSettings {
                TypeNameHandling = TypeNameHandling.All,
                PreserveReferencesHandling = PreserveReferencesHandling.All
            });
            return json;
        }

        public ISequenceContainer Deserialize(string sequenceJSON) {
            var container = JsonConvert.DeserializeObject<ISequenceContainer>(sequenceJSON, new JsonSerializerSettings() {
                Converters = converters
            });

            return container ?? throw new InvalidOperationException("Failed to deserialize sequence container");
        }
    }
}