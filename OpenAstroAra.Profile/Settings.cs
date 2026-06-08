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
using System.Runtime.Serialization;

namespace OpenAstroAra.Profile {

    [Serializable()]
    [DataContract]
    public abstract class Settings : SerializableINPC, ISettings {

        // CA2214: the base constructor intentionally does NOT call the overridable
        // SetDefaultValues(). Each concrete (sealed) settings type calls SetDefaultValues()
        // from its own constructor instead, so the virtual method is never invoked while the
        // object is still partially constructed.
        protected abstract void SetDefaultValues();
    }
}