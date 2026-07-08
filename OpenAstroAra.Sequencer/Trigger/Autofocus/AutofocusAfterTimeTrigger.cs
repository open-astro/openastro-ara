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
using OpenAstroAra.Sequencer.Utility;
using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Trigger.Autofocus {

    /// <summary>
    /// §59.5 "time interval" trigger — fires its <c>TriggerRunner</c> (a <c>RunAutofocus</c>)
    /// before the next LIGHT exposure once <see cref="Amount"/> minutes have passed since the
    /// last completed autofocus (from <see cref="IImageHistory"/>), or since the block started
    /// when the session has no autofocus yet. The long-term drift catch-all of the trigger
    /// family.
    ///
    /// Headless re-port of NINA's AutofocusAfterTimeTrigger: the <c>IImageHistoryVM</c> is
    /// replaced by the <see cref="IImageHistory"/> seam and the runner content deserializes
    /// from the plan JSON (the factory's <c>RunAutofocus</c> prototype carries the §59 sweep
    /// executor). <see cref="Amount"/> keeps NINA's class default (30) for import fidelity;
    /// the playbook's 90-minute recommendation is a profile-level default applied when ARA
    /// builds plans.
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceTrigger_AutofocusAfterTimeTrigger_Name")]
    [ExportMetadata("Description", "Lbl_SequenceTrigger_AutofocusAfterTimeTrigger_Description")]
    [ExportMetadata("Icon", "AutoFocusAfterTimeSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Focuser")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class AutofocusAfterTimeTrigger : SequenceTrigger {

        [ImportingConstructor]
        public AutofocusAfterTimeTrigger(IImageHistory? history = null, IAutofocusConditionGate? conditionGate = null) : base() {
            this.history = history;
            this.conditionGate = conditionGate;
            Amount = 30;
        }

        private AutofocusAfterTimeTrigger(AutofocusAfterTimeTrigger cloneMe) : this(cloneMe.history, cloneMe.conditionGate) {
            CopyMetaData(cloneMe);
        }

        private readonly IImageHistory? history;
        private readonly IAutofocusConditionGate? conditionGate;
        private DateTime initialTime;
        private bool initialized;

        public override object Clone() {
            return new AutofocusAfterTimeTrigger(this) {
                Amount = Amount,
                TriggerRunner = (SequentialContainer)(TriggerRunner?.Clone() ?? new SequentialContainer()),
            };
        }

        private double amount;

        [JsonProperty]
        public double Amount {
            get => amount;
            set {
                amount = value;
                RaisePropertyChanged();
            }
        }

        private double elapsed;

        public double Elapsed {
            get => elapsed;
            private set {
                elapsed = value;
                RaisePropertyChanged();
            }
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            // Clone tolerates a null TriggerRunner (a malformed import), but executing one must
            // fail loudly — silently running nothing would skip the autofocus this trigger exists
            // to guarantee (same contract as AutofocusAfterExposures).
            if (TriggerRunner == null) {
                throw new SequenceEntityFailedException("Autofocus-after-time has no trigger runner to execute.");
            }
            await TriggerRunner.Run(progress, token);
        }

        public override void SequenceBlockInitialize() {
            // First block start anchors the no-autofocus-yet baseline; re-entries keep it so a
            // container loop doesn't push the deadline out forever.
            if (!initialized) {
                initialTime = DateTime.UtcNow;
                initialized = true;
            }
        }

        public override bool ShouldTrigger(ISequenceItem? previousItem, ISequenceItem? nextItem) {
            if (nextItem is not IExposureItem exposureItem) { return false; }
            if (exposureItem.ImageType != "LIGHT") { return false; }

            var afPoints = history?.AutofocusPoints;
            var lastAF = afPoints is { Count: > 0 } ? afPoints[^1] : null;
            var reference = lastAF?.Time.UtcDateTime ?? initialTime;
            if (reference == default) {
                // Neither an autofocus record nor a block start yet — nothing to measure from.
                return false;
            }
            var since = DateTime.UtcNow - reference;
            Elapsed = Math.Round(since.TotalMinutes, 2);
            var shouldTrigger = since >= TimeSpan.FromMinutes(Amount);

            if (shouldTrigger && IsVetoedByImminentMeridianFlip(nextItem)) {
                Logger.Warning("Autofocus should be triggered, however the meridian flip is too close to be executed");
                shouldTrigger = false;
            }
            // §59.9 — elapsed time only grows, so a deferred fire retries on the next check.
            if (shouldTrigger && conditionGate?.DeferralReason() is { } reason) {
                // Debug, not Info: this fires on EVERY ShouldTrigger call while deferred (per item
                // transition), and the gate already logs + notifies once per episode.
                Logger.Debug($"Autofocus deferred — {reason}. Will run when conditions recover.");
                shouldTrigger = false;
            }
            return shouldTrigger;
        }

        public override string ToString() {
            return $"Trigger: {nameof(AutofocusAfterTimeTrigger)}, Amount: {Amount}";
        }
    }
}
