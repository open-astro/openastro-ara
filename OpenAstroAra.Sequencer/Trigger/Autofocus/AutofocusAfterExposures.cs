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
using OpenAstroAra.Core.Utility;
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
        public AutofocusAfterExposures(IAutofocusConditionGate? conditionGate = null) : base() {
            this.conditionGate = conditionGate;
        }

        private AutofocusAfterExposures(AutofocusAfterExposures cloneMe) : base(cloneMe) {
            AfterExposures = cloneMe.AfterExposures;
            conditionGate = cloneMe.conditionGate;
        }

        private readonly IAutofocusConditionGate? conditionGate;

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
            deferredPending = false;
        }

        public override object Clone() {
            return new AutofocusAfterExposures(this) {
                // Null-safe: a malformed import could deserialize TriggerRunner as null (the base
                // [JsonProperty] setter would overwrite the ctor's instance), so don't NPE on Clone.
                TriggerRunner = (SequentialContainer)(TriggerRunner?.Clone() ?? new SequentialContainer()),
            };
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            // Clone tolerates a null TriggerRunner (a malformed import), but executing one must fail
            // loudly — silently running nothing would skip the autofocus, the exact hazard this
            // family avoids (RunAutofocus.Execute throws for the same reason).
            if (TriggerRunner == null) {
                throw new SequenceEntityFailedException("Autofocus-after-exposures has no trigger runner to execute.");
            }
            await TriggerRunner.Run(progress, token);
        }

        // §59.9 — this trigger's condition is EDGE-based (fires exactly on the Nth exposure),
        // so a conditions deferral must be latched or the owed autofocus would silently slip
        // to exposure 2N. The pending flag re-arms the fire on every subsequent check until
        // the sky recovers.
        private bool deferredPending;

        public override bool ShouldTrigger(ISequenceItem? previousItem, ISequenceItem? nextItem) {
            // Only an exposure (IExposureItem) advances the tally, and the trigger fires *on* the
            // exposure that completes a group of N. Capture AfterExposures once so a concurrent write
            // to 0 can't turn the modulo into a DivideByZeroException; Interlocked keeps it atomic.
            // The tally counts exposures *taken*, independent of whether the fired autofocus then
            // succeeds — the cadence is exposure-driven, and a failed autofocus surfaces as a
            // SequenceEntityFailedException through the run-engine rather than rewinding the count.
            var after = AfterExposures;
            var due = false;
            if (previousItem is IExposureItem && after > 0) {
                var count = Interlocked.Increment(ref exposureCount);
                due = count % after == 0;
            }
            if (!due && !deferredPending) {
                return false;
            }
            if (conditionGate?.DeferralReason() is { } reason) {
                deferredPending = true;
                // Debug, not Info: this fires on EVERY ShouldTrigger call while deferred (per item
                // transition), and the gate already logs + notifies once per episode.
                Logger.Debug($"Autofocus deferred — {reason}. Will run when conditions recover.");
                return false;
            }
            deferredPending = false;
            return true;
        }
    }
}
