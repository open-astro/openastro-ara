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
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using System;

namespace OpenAstroAra.Sequencer.Serialization {

    public class SequenceConditionCreationConverter : JsonCreationConverter<ISequenceCondition> {
        private ISequencerFactory factory;

        public SequenceConditionCreationConverter(ISequencerFactory factory) {
            this.factory = factory;
        }

        public override ISequenceCondition Create(Type objectType, JObject jObject) {
            if (jObject.TryGetValue("$type", out var token)) {
                var t = GetType(token.ToString());
                if (t == null) {
                    return new UnknownSequenceCondition(token?.ToString() ?? string.Empty);
                }
                try {
                    var method = factory.GetType().GetMethod(nameof(factory.GetCondition))!.MakeGenericMethod(new Type[] { t });
                    var obj = method.Invoke(factory, null);
                    if (obj == null) {
                        Logger.Error($"Encountered unknown sequence condition: {token?.ToString()}");
                        return new UnknownSequenceCondition(token?.ToString() ?? string.Empty);
                    }
                    return (ISequenceCondition)obj;
                } catch (Exception e) {
                    Logger.Error($"Encountered unknown sequence condition: {token?.ToString()}", e);
                    return new UnknownSequenceCondition(token?.ToString() ?? string.Empty);
                }
            } else {
                return new UnknownSequenceCondition(token?.ToString() ?? string.Empty);
            }
        }
    }
}