#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Core.Utility;

namespace OpenAstroAra.Profile.Interfaces {

    public interface IFilterWheelSettings : ISettings {
        // CA2227: get-only at the interface; the concrete settings type keeps a
        // setter for DataContract deserialization. Callers mutate via Add/Remove.
        ObserveAllCollection<FilterInfo> FilterWheelFilters { get; }
        string Id { get; set; }
        string LastDeviceName { get; set; }
        bool DisableGuidingOnFilterChange { get; set; }
        bool Unidirectional { get; set; }
    }
}