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
using OpenAstroAra.Core.Model;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Autofocus {

    /// <summary>
    /// NINA's "Run Autofocus" — runs an autofocus routine on the focuser. Common in real plans
    /// (one per target, plus filter changes). No serialized fields beyond the base.
    ///
    /// Ported as a first-class type so NINA exports round-trip instead of degrading to an
    /// <see cref="UnknownSequenceItem"/>. EXECUTION runs the real §59.8 V-curve sweep through
    /// <see cref="IAutofocusExecutor"/> (probe positions → HFR → curve fit → move to best); a
    /// failed or unwired sweep fails this step loudly rather than silently skipping focus
    /// (which would quietly ruin the subsequent frames).
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceItem_Autofocus_RunAutofocus_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_Autofocus_RunAutofocus_Description")]
    [ExportMetadata("Icon", "AutoFocusSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Focuser")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class RunAutofocus : SequenceItem {

        [ImportingConstructor]
        public RunAutofocus(IAutofocusExecutor? autofocusExecutor = null) {
            this.autofocusExecutor = autofocusExecutor;
        }

        private readonly IAutofocusExecutor? autofocusExecutor;

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (autofocusExecutor is null) {
                throw new SequenceEntityFailedException("Autofocus is not wired for sequence execution on this daemon.");
            }
            var ok = await autofocusExecutor.RunAutofocusAsync(progress, token);
            if (!ok) {
                // Continuing out of focus would quietly ruin every subsequent frame.
                throw new SequenceEntityFailedException("Autofocus sweep failed — see the daemon log (probe quality, curve fit, or focuser fault).");
            }
        }

        public override object Clone() {
            return new RunAutofocus(autofocusExecutor) {
                Icon = Icon,
                Name = Name,
                Category = Category,
                Description = Description,
            };
        }
    }
}
