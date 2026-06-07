#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json.Linq;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using System;
using System.Diagnostics;

namespace OpenAstroAra.Sequencer.Serialization {

    public class SequenceItemCreationConverter : JsonCreationConverter<ISequenceItem> {
        private ISequencerFactory factory;
        private SequenceContainerCreationConverter sequenceContainerCreationConverter;

        public SequenceItemCreationConverter(ISequencerFactory factory, SequenceContainerCreationConverter sequenceContainerCreationConverter) {
            this.factory = factory;
            this.sequenceContainerCreationConverter = sequenceContainerCreationConverter;
        }

        public override ISequenceItem Create(Type objectType, JObject jObject) {
            if (jObject.SelectToken("Strategy.$type") != null) {
                return sequenceContainerCreationConverter.Create(objectType, jObject);
            }

            if (jObject.TryGetValue("ImageType", out var value)) {
                if (value.Value<string>() == "DARKFLAT") {
                    // Migration of values prior to 3.0
                    jObject["ImageType"] = new JValue("DARK");
                }
            }

            if (jObject.TryGetValue("$type", out var token)) {
                token = PluginMergeMigration(token?.ToString());
                var t = GetType(token?.ToString());
                if (t == null) {
                    return new UnknownSequenceItem(token?.ToString());
                }
                try {
                    var method = factory.GetType().GetMethod(nameof(factory.GetItem)).MakeGenericMethod(new Type[] { t });
                    var obj = method.Invoke(factory, null);
                    if (obj == null) {
                        Logger.Error($"Encountered unknown sequence item: {token?.ToString()}");
                        return new UnknownSequenceItem(token?.ToString());
                    }
                    return (ISequenceItem)obj;
                } catch (Exception e) {
                    Logger.Error($"Encountered unknown sequence item: {token?.ToString()}", e);
                    return new UnknownSequenceItem(token?.ToString());
                }
            } else {
                return new UnknownSequenceItem(token?.ToString());
            }
        }

        private string PluginMergeMigration(string token) => token switch {
            "NINA.Plugins.Connector.Instructions.ConnectAllEquipment, NINA.Plugins.Connector" => "OpenAstroAra.Sequencer.SequenceItem.Connect.ConnectAllEquipment, OpenAstroAra.Sequencer",
            "NINA.Plugins.Connector.Instructions.ConnectEquipment, NINA.Plugins.Connector" => "OpenAstroAra.Sequencer.SequenceItem.Connect.ConnectEquipment, OpenAstroAra.Sequencer",
            "NINA.Plugins.Connector.Instructions.DisconnectEquipment, NINA.Plugins.Connector" => "OpenAstroAra.Sequencer.SequenceItem.Connect.DisconnectEquipment, OpenAstroAra.Sequencer",
            "NINA.Plugins.Connector.Instructions.DisconnectAllEquipment, NINA.Plugins.Connector" => "OpenAstroAra.Sequencer.SequenceItem.Connect.DisconnectAllEquipment, OpenAstroAra.Sequencer",
            "NINA.Plugins.Connector.Instructions.SwitchProfile, NINA.Plugins.Connector" => "OpenAstroAra.Sequencer.SequenceItem.Connect.SwitchProfile, OpenAstroAra.Sequencer",
            _ => token
        };
    }
}