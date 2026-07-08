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
using OpenAstroAra.Core.Locale;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.Utility;
using OpenAstroAra.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Trigger.Autofocus {

    /// <summary>
    /// §59.5 "sensor temp Δ" trigger — fires its <c>TriggerRunner</c> (a <c>RunAutofocus</c>)
    /// before the next LIGHT exposure once the focuser temperature has drifted
    /// <see cref="Amount"/> °C from the last completed autofocus (via <see cref="IImageHistory"/>,
    /// which records the temperature at each run), or from the temperature at trigger
    /// initialization when the session has no autofocus yet. Temperature is the dominant focus
    /// drift driver, so this is the workhorse of the trigger family.
    ///
    /// Headless re-port of NINA's AutofocusAfterTemperatureChangeTrigger: reads the live
    /// temperature from <see cref="IFocuserMediator"/> and the reference point from the
    /// <see cref="IImageHistory"/> seam; the runner content deserializes from the plan JSON.
    /// <see cref="Amount"/> keeps NINA's class default (5) for import fidelity; the playbook's
    /// 1.5 °C recommendation is a profile-level default applied when ARA builds plans.
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceTrigger_AutofocusAfterTemperatureChangeTrigger_Name")]
    [ExportMetadata("Description", "Lbl_SequenceTrigger_AutofocusAfterTemperatureChangeTrigger_Description")]
    [ExportMetadata("Icon", "AutoFocusAfterTemperatureSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Focuser")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class AutofocusAfterTemperatureChangeTrigger : SequenceTrigger, IValidatable {

        [ImportingConstructor]
        public AutofocusAfterTemperatureChangeTrigger(IFocuserMediator? focuserMediator = null, IImageHistory? history = null) : base() {
            this.focuserMediator = focuserMediator;
            this.history = history;
            Amount = 5;
        }

        private AutofocusAfterTemperatureChangeTrigger(AutofocusAfterTemperatureChangeTrigger cloneMe) : this(cloneMe.focuserMediator, cloneMe.history) {
            CopyMetaData(cloneMe);
        }

        private readonly IFocuserMediator? focuserMediator;
        private readonly IImageHistory? history;
        private double initialTemperature = double.NaN;

        public override object Clone() {
            return new AutofocusAfterTemperatureChangeTrigger(this) {
                Amount = Amount,
                TriggerRunner = (SequentialContainer)(TriggerRunner?.Clone() ?? new SequentialContainer()),
            };
        }

        private IList<string> issues = new List<string>();

        public IList<string> Issues {
            get => issues;
            set {
                issues = ImmutableList.CreateRange(value);
                RaisePropertyChanged();
            }
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

        private double deltaT;

        public double DeltaT {
            get => deltaT;
            set {
                deltaT = value;
                RaisePropertyChanged();
            }
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            // Same loud-fail contract as the family: a malformed import clones fine but must not
            // silently skip the autofocus it promised.
            if (TriggerRunner == null) {
                throw new SequenceEntityFailedException("Autofocus-after-temperature-change has no trigger runner to execute.");
            }
            await TriggerRunner.Run(progress, token);
        }

        public override void Initialize() {
            initialTemperature = focuserMediator?.GetInfo()?.Temperature ?? double.NaN;
        }

        public override bool ShouldTrigger(ISequenceItem? previousItem, ISequenceItem? nextItem) {
            if (nextItem is not IExposureItem exposureItem) { return false; }
            if (exposureItem.ImageType != "LIGHT") { return false; }

            var currentTemperature = focuserMediator?.GetInfo()?.Temperature ?? double.NaN;
            if (double.IsNaN(currentTemperature)) {
                // No focuser (or one without a temperature sensor) — the delta is unmeasurable.
                return false;
            }

            var lastAF = history?.AutofocusPoints.LastOrDefault(p => !double.IsNaN(p.Temperature));
            if (lastAF == null && double.IsNaN(initialTemperature)) {
                // Late-arriving sensor: anchor the baseline on the first reading we ever get.
                initialTemperature = currentTemperature;
            }

            var reference = lastAF?.Temperature ?? initialTemperature;
            DeltaT = Math.Round(Math.Abs(reference - currentTemperature), 2);
            var shouldTrigger = Math.Abs(reference - currentTemperature) >= Amount;

            if (shouldTrigger && Parent is { } parent && ItemUtility.IsTooCloseToMeridianFlip(parent, TriggerAndNextDuration(nextItem))) {
                Logger.Warning("Autofocus should be triggered, however the meridian flip is too close to be executed");
                shouldTrigger = false;
            }
            return shouldTrigger;
        }

        private TimeSpan TriggerAndNextDuration(ISequenceItem? nextItem) {
            return (TriggerRunner?.GetItemsSnapshot().FirstOrDefault()?.GetEstimatedDuration() ?? TimeSpan.Zero)
                + (nextItem?.GetEstimatedDuration() ?? TimeSpan.Zero);
        }

        public override string ToString() {
            return $"Trigger: {nameof(AutofocusAfterTemperatureChangeTrigger)}, Amount: {Amount}°";
        }

        public bool Validate() {
            var i = new List<string>();
            // Only the equipment this trigger READS — a missing focuser makes the temperature
            // delta unmeasurable. Camera readiness is RunAutofocus's concern at execution.
            if (focuserMediator?.GetInfo()?.Connected != true) {
                i.Add(Loc.Instance["LblFocuserNotConnected"]);
            }
            Issues = i;
            return i.Count == 0;
        }
    }
}
