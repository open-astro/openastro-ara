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
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Trigger.Autofocus {

    /// <summary>
    /// NINA's "Autofocus after exposures" trigger — fires its <see cref="SequenceTrigger.TriggerRunner"/>
    /// (which holds a <c>RunAutofocus</c>) once every <see cref="AfterExposures"/> exposures taken
    /// within the parent container. The autofocus sibling of <c>DitherAfterExposures</c>.
    ///
    /// Ported as a first-class type so NINA exports round-trip: <see cref="AfterExposures"/> and the
    /// nested <c>TriggerRunner</c> deserialize intact instead of degrading to an
    /// <see cref="UnknownSequenceTrigger"/>.
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceTrigger_Autofocus_AutofocusAfterExposures_Name")]
    [ExportMetadata("Description", "Lbl_SequenceTrigger_Autofocus_AutofocusAfterExposures_Description")]
    [ExportMetadata("Icon", "AutoFocusSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Focuser")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class AutofocusAfterExposures : SequenceTrigger {

        [ImportingConstructor]
        public AutofocusAfterExposures() : base() {
        }

        private AutofocusAfterExposures(AutofocusAfterExposures cloneMe) : base(cloneMe) {
            AfterExposures = cloneMe.AfterExposures;
        }

        private int afterExposures = 1;

        [JsonProperty]
        public int AfterExposures {
            get => afterExposures;
            set {
                afterExposures = value;
                RaisePropertyChanged();
            }
        }

        private int exposureCount;

        public override void SequenceBlockInitialize() {
            // Zero the tally each time the parent block (re)starts (SequentialStrategy.InitializeBlock)
            // so a reset + replay doesn't carry the previous run's count and fire off-cadence.
            Interlocked.Exchange(ref exposureCount, 0);
        }

        public override object Clone() {
            return new AutofocusAfterExposures(this) {
                TriggerRunner = (SequentialContainer)TriggerRunner.Clone(),
            };
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            await TriggerRunner.Run(progress, token);
        }

        public override bool ShouldTrigger(ISequenceItem? previousItem, ISequenceItem? nextItem) {
            // Only an exposure (IExposureItem) advances the tally, and the trigger fires *on* the
            // exposure that completes a group of N. Capture AfterExposures once so a concurrent write
            // to 0 can't turn the modulo into a DivideByZeroException; Interlocked keeps it atomic.
            var after = AfterExposures;
            if (previousItem is not IExposureItem || after <= 0) {
                return false;
            }
            var count = Interlocked.Increment(ref exposureCount);
            return count % after == 0;
        }
    }
}
