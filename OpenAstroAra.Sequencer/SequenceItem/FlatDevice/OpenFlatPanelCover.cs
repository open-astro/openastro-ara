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
using OpenAstroAra.Core.Utility;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.FlatDevice {

    /// <summary>
    /// Turn the flat panel off and open its motorised cover, so the user can put a lens cap on
    /// before bias and darks (a panel doesn't always seal out stray light). The generated
    /// calibration run follows it with a Wait for User. With no panel, or a panel without a
    /// motorised cover, the step logs and carries on: the user moves the panel by hand during
    /// that pause.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class OpenFlatPanelCover : SequenceItem, IEstimatesOwnDuration {

        public OpenFlatPanelCover(IFlatCaptureExecutor? flatCaptureExecutor = null) {
            this.flatCaptureExecutor = flatCaptureExecutor;
        }

        private readonly IFlatCaptureExecutor? flatCaptureExecutor;

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (flatCaptureExecutor is null) {
                Logger.Warning("Open flat panel cover - no flat capture wired on this daemon; continuing.");
                return;
            }
            progress?.Report(new ApplicationStatus() { Status = "Opening the flat panel cover" });
            if (!await flatCaptureExecutor.OpenCoverAsync(token)) {
                Logger.Info("Open flat panel cover - nothing opened (no panel or no motorised cover); continuing.");
            }
        }

        public override TimeSpan GetEstimatedDuration() => TimeSpan.FromSeconds(30);

        public override object Clone() {
            return new OpenFlatPanelCover(flatCaptureExecutor) {
                Icon = Icon,
                Name = Name,
                Category = Category,
                Description = Description,
            };
        }

        public override string ToString() => $"Item: {nameof(OpenFlatPanelCover)}";
    }
}
