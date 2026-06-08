#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Utility;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;

namespace OpenAstroAra.Profile {

    [Serializable()]
    [DataContract]
    public abstract class Settings : SerializableINPC, ISettings {

        [SuppressMessage("Usage", "CA2214:Do not call overridable methods in constructors",
            Justification = "Intentional settings-defaults pattern: each concrete settings type overrides SetDefaultValues to initialize only its own DataMember fields to defaults before deserialization populates them. Derived constructors are empty, so there is no partially-constructed-state hazard.")]
        protected Settings() {
            SetDefaultValues();
        }

        protected abstract void SetDefaultValues();
    }
}