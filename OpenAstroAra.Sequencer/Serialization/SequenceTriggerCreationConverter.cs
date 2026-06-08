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
using OpenAstroAra.Sequencer.Trigger;
using System;

namespace OpenAstroAra.Sequencer.Serialization {

    public class SequenceTriggerCreationConverter : JsonCreationConverter<ISequenceTrigger> {
        private ISequencerFactory factory;

        public SequenceTriggerCreationConverter(ISequencerFactory factory) {
            this.factory = factory;
        }

        public override ISequenceTrigger Create(Type objectType, JObject jObject) {
            if (jObject.TryGetValue("$type", out var token)) {
                token = PluginMergeMigration(token?.ToString() ?? string.Empty);
                var t = GetType(token?.ToString() ?? string.Empty);
                if (t == null) {
                    return new UnknownSequenceTrigger(token?.ToString() ?? string.Empty);
                }
                try {
                    var method = factory.GetType().GetMethod(nameof(factory.GetTrigger))!.MakeGenericMethod(new Type[] { t });
                    var obj = method.Invoke(factory, null);
                    if (obj == null) {
                        Logger.Error($"Encountered unknown sequence trigger: {token?.ToString()}");
                        return new UnknownSequenceTrigger(token?.ToString() ?? string.Empty);
                    }
                    return (ISequenceTrigger)obj;
                } catch (Exception e) {
                    Logger.Error($"Encountered unknown sequence trigger: {token?.ToString()}", e);
                    return new UnknownSequenceTrigger(token?.ToString() ?? string.Empty);
                }
            } else {
                return new UnknownSequenceTrigger(token?.ToString() ?? string.Empty);
            }
        }

        private string PluginMergeMigration(string token) => token switch {
            "NINA.Plugins.Connector.Instructions.ReconnectOnDownloadFailure, NINA.Plugins.Connector" => "OpenAstroAra.Sequencer.Trigger.Connect.ReconnectOnDownloadFailure, OpenAstroAra.Sequencer",
            "NINA.Plugins.Connector.Instructions.ReconnectTrigger, NINA.Plugins.Connector" => "OpenAstroAra.Sequencer.Trigger.Connect.ReconnectTrigger, OpenAstroAra.Sequencer",
            _ => token
        };
    }
}