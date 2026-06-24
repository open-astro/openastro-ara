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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Platesolving {

    /// <summary>
    /// NINA's "Center and Rotate" — slew to the target, plate-solve, sync + re-slew to centre, then
    /// rotate the camera to <see cref="PositionAngle"/>. Common in real plans (one per target).
    ///
    /// Ported as a first-class type so NINA exports round-trip (Coordinates / PositionAngle /
    /// Inherited survive import) instead of degrading to an <see cref="UnknownSequenceItem"/>.
    /// Execution is not yet wired into the sequencer run-engine (the §28 CenteringService is
    /// server-side and not exposed to the sequencer); running this step fails loudly rather than
    /// silently skipping the centre/rotate.
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceItem_Platesolving_CenterAndRotate_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_Platesolving_CenterAndRotate_Description")]
    [ExportMetadata("Icon", "PlatesolveAndRotateSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Telescope")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class CenterAndRotate : SequenceItem {

        [ImportingConstructor]
        public CenterAndRotate() {
            Coordinates = new InputCoordinates();
        }

        [JsonProperty]
        public bool Inherited { get; set; }

        [JsonProperty]
        public InputCoordinates Coordinates { get; set; }

        [JsonProperty]
        public double PositionAngle { get; set; }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) =>
            throw new SequenceEntityFailedException("Center-and-rotate is not yet wired for sequence execution.");

        public override object Clone() {
            return new CenterAndRotate {
                Icon = Icon,
                Name = Name,
                Category = Category,
                Description = Description,
                Inherited = Inherited,
                // Null-safe: a malformed/partial import could deserialize Coordinates as null
                // (the [JsonProperty] setter would overwrite the ctor's instance), so don't NPE.
                Coordinates = Coordinates?.Clone() ?? new InputCoordinates(),
                PositionAngle = PositionAngle,
            };
        }
    }
}
