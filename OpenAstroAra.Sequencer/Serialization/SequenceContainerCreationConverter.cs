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
using System;
using System.Diagnostics.CodeAnalysis;

namespace OpenAstroAra.Sequencer.Serialization {

    public class SequenceContainerCreationConverter : JsonCreationConverter<ISequenceContainer> {
        private ISequencerFactory factory;

        public SequenceContainerCreationConverter(ISequencerFactory factory) {
            this.factory = factory;
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Factory-instantiation recovery boundary: a reflective GetContainer<T> invoke may surface any exception (TargetInvocationException, argument/cast/type-load faults from arbitrary container constructors). All are logged and replaced with an UnknownSequenceContainer placeholder so one bad entity cannot fail the whole sequence load. CA1031 sanctions general catches at such recover-and-continue boundaries.")]
        public override ISequenceContainer Create(Type objectType, JObject jObject) {
            if (jObject.TryGetValue("$type", out var token)) {
                var t = GetType(token.ToString());
                if (t == null) {
                    return new UnknownSequenceContainer(token?.ToString() ?? string.Empty);
                }
                try {
                    var method = factory.GetType().GetMethod(nameof(factory.GetContainer))!.MakeGenericMethod(new Type[] { t });
                    var obj = method.Invoke(factory, null);
                    if (obj == null) {
                        Logger.Error($"Encountered unknown sequence container: {token?.ToString()}");
                        return new UnknownSequenceContainer(token?.ToString() ?? string.Empty);
                    }
                    return (ISequenceContainer)obj;
                } catch (Exception e) {
                    Logger.Error($"Encountered unknown sequence container: {token?.ToString()}", e);
                    return new UnknownSequenceContainer(token?.ToString() ?? string.Empty);
                }
            } else {
                return new UnknownSequenceContainer(token?.ToString() ?? string.Empty);
            }
        }
    }
}