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
using OpenAstroAra.Equipment.Interfaces.Mediator;
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
    /// §59.5 "HFR drift" trigger — fires its <c>TriggerRunner</c> (a <c>RunAutofocus</c>)
    /// before the next LIGHT exposure when the extrapolated HFR trend of the last
    /// <see cref="SampleSize"/> frames sits more than <see cref="Amount"/>% above the session's
    /// best HFR since the last autofocus. Catches the drift the time-interval trigger missed.
    ///
    /// Headless re-port of NINA's AutofocusAfterHFRIncreaseTrigger: per-frame HFR points come
    /// from the <see cref="IImageHistory"/> seam (only LIGHT frames with a positive HFR count),
    /// the current filter from <see cref="IFilterWheelMediator"/> (points are per-filter — a
    /// filter change resets what "the trend" means), and the trend itself is a plain
    /// least-squares line (NINA used Accord's SimpleLinearRegression; two-variable OLS is
    /// small enough to carry inline and keeps Accord out of this project).
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceTrigger_AutofocusAfterHFRIncreaseTrigger_Name")]
    [ExportMetadata("Description", "Lbl_SequenceTrigger_AutofocusAfterHFRIncreaseTrigger_Description")]
    [ExportMetadata("Icon", "AutoFocusAfterHFRSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Focuser")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class AutofocusAfterHFRIncreaseTrigger : SequenceTrigger {

        [ImportingConstructor]
        public AutofocusAfterHFRIncreaseTrigger(IImageHistory? history = null, IFilterWheelMediator? filterWheelMediator = null) : base() {
            this.history = history;
            this.filterWheelMediator = filterWheelMediator;
            Amount = 5;
            SampleSize = 10;
        }

        private AutofocusAfterHFRIncreaseTrigger(AutofocusAfterHFRIncreaseTrigger cloneMe) : this(cloneMe.history, cloneMe.filterWheelMediator) {
            CopyMetaData(cloneMe);
        }

        private readonly IImageHistory? history;
        private readonly IFilterWheelMediator? filterWheelMediator;

        public override object Clone() {
            return new AutofocusAfterHFRIncreaseTrigger(this) {
                Amount = Amount,
                SampleSize = SampleSize,
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

        private int sampleSize = 3;

        [JsonProperty]
        public int SampleSize {
            get => sampleSize;
            set {
                // A regression over fewer than 3 points is noise; silently keeping the old value
                // (rather than throwing) matches NINA so imports with a bad value stay loadable.
                if (value >= 3) {
                    sampleSize = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double originalHfr;

        public double OriginalHFR {
            get => originalHfr;
            private set {
                originalHfr = value;
                RaisePropertyChanged();
            }
        }

        private double hfrTrendPercentage;

        public double HFRTrendPercentage {
            get => hfrTrendPercentage;
            private set {
                hfrTrendPercentage = value;
                RaisePropertyChanged();
            }
        }

        private double hfrTrend;

        public double HFRTrend {
            get => hfrTrend;
            private set {
                hfrTrend = value;
                RaisePropertyChanged();
            }
        }

        private string filter = string.Empty;

        public string Filter {
            get => filter;
            private set {
                filter = value;
                RaisePropertyChanged();
            }
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            // Same loud-fail contract as the family: a malformed import clones fine but must not
            // silently skip the autofocus it promised.
            if (TriggerRunner == null) {
                throw new SequenceEntityFailedException("Autofocus-after-HFR-increase has no trigger runner to execute.");
            }
            await TriggerRunner.Run(progress, token);
        }

        public override bool ShouldTrigger(ISequenceItem? previousItem, ISequenceItem? nextItem) {
            if (nextItem is not IExposureItem exposureItem) { return false; }
            if (exposureItem.ImageType != "LIGHT") { return false; }
            if (history == null) { return false; }

            var afPoints = history.AutofocusPoints;
            var lastAF = afPoints is { Count: > 0 } ? afPoints[^1] : null;
            var points = history.ImagePoints
                .Where(x => x.Type == "LIGHT" && !double.IsNaN(x.Hfr) && x.Hfr > 0);
            if (lastAF != null) {
                points = points.Where(x => x.Id > lastAF.Id);
            }

            // ALWAYS scope to the current filter — a mono rig (no wheel) records null-filter
            // points and matches null==null; a wheel transiently reporting nothing matches
            // no points and the trigger stays quiet, instead of mixing filters into one
            // trend where a bandpass-driven HFR difference could spuriously fire (or mask)
            // an autofocus.
            var fwInfo = filterWheelMediator?.GetInfo();
            var currentFilter = fwInfo is { Connected: true, SelectedFilter: not null }
                ? fwInfo.SelectedFilter.Name
                : null;
            Filter = currentFilter ?? string.Empty;
            points = points.Where(x => x.Filter == currentFilter);

            var imageHistory = points.ToList();
            if (imageHistory.Count == 0) {
                OriginalHFR = 0;
                return false;
            }

            OriginalHFR = imageHistory.Min(x => x.Hfr);

            var samples = imageHistory.Skip(Math.Max(0, imageHistory.Count - SampleSize)).ToList();
            if (samples.Count < 3) {
                HFRTrendPercentage = 0;
                HFRTrend = 0;
                return false;
            }

            // Least-squares line over (1..n, HFR) — the smoothed "HFR now" is the line's value
            // at the newest sample, so a single bad frame can't fire the trigger on its own.
            HFRTrend = PredictLast(samples.Select(x => x.Hfr).ToList());
            HFRTrendPercentage = Math.Round(((HFRTrend / OriginalHFR) - 1.0) * 100.0, 2);

            var shouldTrigger = false;
            if (HFRTrendPercentage > Amount) {
                Logger.Info($"Autofocus after HFR change should be triggered, as current HFR trend is {HFRTrendPercentage}% higher compared to threshold of {Amount}%");
                shouldTrigger = true;
            }

            if (shouldTrigger && IsVetoedByImminentMeridianFlip(nextItem)) {
                Logger.Warning("Autofocus should be triggered, however the meridian flip is too close to be executed");
                shouldTrigger = false;
            }
            return shouldTrigger;
        }

        private static double PredictLast(System.Collections.Generic.List<double> values) {
            var n = values.Count;
            var meanX = (n + 1) / 2.0;
            var meanY = values.Average();
            double covXY = 0, varX = 0;
            for (var i = 0; i < n; i++) {
                var dx = (i + 1) - meanX;
                covXY += dx * (values[i] - meanY);
                varX += dx * dx;
            }
            // varX is 0 only for n < 2, which the caller's minimum-3-samples gate excludes.
            var slope = covXY / varX;
            var intercept = meanY - (slope * meanX);
            return intercept + (slope * n);
        }

        public override string ToString() {
            return $"Trigger: {nameof(AutofocusAfterHFRIncreaseTrigger)}, Amount: {Amount}";
        }
    }
}
