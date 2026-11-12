#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using OpenAstroAra.Core.Model;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.FlatDevice {

    /// <summary>
    /// Turn the flat panel off and close its motorised cover, then wait for it to read closed, so bias and
    /// darks run unattended behind it instead of after a lens-cap pause. Unlike <see cref="OpenFlatPanelCover"/>,
    /// which carries on because a Wait for User follows it, this step FAILS when no panel or motorised cover is
    /// connected, nothing is wired, or the cover never reads closed: no one is asked, so darks behind an open
    /// cover would be ruined without a word.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class CloseFlatPanelCover : SequenceItem, IEstimatesOwnDuration {

        public CloseFlatPanelCover(IFlatCaptureExecutor? flatCaptureExecutor = null) {
            this.flatCaptureExecutor = flatCaptureExecutor;
        }

        private readonly IFlatCaptureExecutor? flatCaptureExecutor;

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (flatCaptureExecutor is null) {
                throw new SequenceEntityFailedException("Flat panel control is not wired for sequence execution on this daemon.");
            }
            progress?.Report(new ApplicationStatus() { Status = "Closing the flat panel cover" });
            if (!await flatCaptureExecutor.CloseCoverAsync(token)) {
                throw new SequenceEntityFailedException(
                    "The flat panel cover did not close: connect a flat panel with a motorised cover, or cover the scope by hand before bias and darks.");
            }
        }

        public override TimeSpan GetEstimatedDuration() => TimeSpan.FromSeconds(30);

        public override object Clone() {
            return new CloseFlatPanelCover(flatCaptureExecutor) {
                Icon = Icon,
                Name = Name,
                Category = Category,
                Description = Description,
            };
        }

        public override string ToString() => $"Item: {nameof(CloseFlatPanelCover)}";
    }
}
