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

namespace OpenAstroAra.Sequencer.Trigger.Guider {

    /// <summary>
    /// NINA's "Dither after exposures" trigger — fires its <see cref="SequenceTrigger.TriggerRunner"/>
    /// (which holds a <c>Dither</c> instruction) once every <see cref="AfterExposures"/> exposures
    /// taken within the parent container. The most common trigger in real plans (one per Smart
    /// Exposure block).
    ///
    /// Ported as a first-class type so NINA exports round-trip: <see cref="AfterExposures"/> and the
    /// nested <c>TriggerRunner</c> deserialize intact instead of degrading to an
    /// <see cref="UnknownSequenceTrigger"/>.
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceTrigger_Guider_DitherAfterExposures_Name")]
    [ExportMetadata("Description", "Lbl_SequenceTrigger_Guider_DitherAfterExposures_Description")]
    [ExportMetadata("Icon", "DitherSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Guider")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class DitherAfterExposures : SequenceTrigger {

        [ImportingConstructor]
        public DitherAfterExposures() : base() {
        }

        private DitherAfterExposures(DitherAfterExposures cloneMe) : base(cloneMe) {
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
            // The parent block calls this each time it (re)starts executing (SequentialStrategy
            // .InitializeBlock), so zero the exposure tally here — otherwise a reset + replay would
            // carry the previous run's count and fire the dither off-cadence.
            Interlocked.Exchange(ref exposureCount, 0);
        }

        public override object Clone() {
            return new DitherAfterExposures(this) {
                TriggerRunner = (SequentialContainer)TriggerRunner.Clone(),
            };
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            await TriggerRunner.Run(progress, token);
        }

        public override bool ShouldTrigger(ISequenceItem? previousItem, ISequenceItem? nextItem) {
            // RunTriggersAfter calls this once after each completed item. Only an exposure
            // (TakeExposure implements IExposureItem) advances the tally, and the dither fires
            // *on* the exposure that completes a group of N — never on a following non-exposure
            // item, which would otherwise re-fire while the count still sat on a multiple.
            // Interlocked keeps the read-modify-write atomic in case a parallel container ever
            // evaluates triggers off the imaging thread.
            // Capture AfterExposures once: re-reading it for the modulo would risk a
            // DivideByZeroException if another thread set it to 0 between the guard and the modulo.
            var after = AfterExposures;
            if (previousItem is not IExposureItem || after <= 0) {
                return false;
            }
            var count = Interlocked.Increment(ref exposureCount);
            return count % after == 0;
        }
    }
}
