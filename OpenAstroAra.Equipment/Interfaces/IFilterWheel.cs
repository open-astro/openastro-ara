#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Core.Utility;
using System.Collections;

using System.Collections.Generic;

namespace OpenAstroAra.Equipment.Interfaces {

    public interface IFilterWheel : IDevice {
        IReadOnlyList<int> FocusOffsets { get; }
        IReadOnlyList<string> Names { get; }
        short Position { get; set; }
        AsyncObservableCollection<FilterInfo> Filters { get; }
    }
}