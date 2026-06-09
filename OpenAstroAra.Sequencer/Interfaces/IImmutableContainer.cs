#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Diagnostics.CodeAnalysis;

namespace OpenAstroAra.Sequencer.Container {

    [SuppressMessage("Design", "CA1040:Avoid empty interfaces",
        Justification = "Marker interface: IImmutableContainer identifies containers that validate their own children (used via 'is IImmutableContainer' type-identity checks, e.g. in Sequencer.Validate). CA1040 documents that suppression is appropriate for marker interfaces.")]
    public interface IImmutableContainer {
    }
}