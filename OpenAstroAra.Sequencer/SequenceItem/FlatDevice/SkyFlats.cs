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

namespace OpenAstroAra.Sequencer.SequenceItem.FlatDevice {

    /// <summary>
    /// §48.4 — twilight sky-flat set (the headless re-imagining of NINA's sky flats). Captures one
    /// SET of flats off the twilight sky for the current filter/focus context: the executor
    /// re-probes the sky before EVERY saved frame (twilight brightness drifts minute to minute),
    /// rescales the exposure toward <see cref="TargetAdu"/>, and bails honestly when the sky is
    /// too bright even at the minimum exposure (<see cref="StopAtMaxAdu"/>) or too dark even at
    /// the maximum (<see cref="StopAtMinAdu"/>). Twilight TIMING and pointing are the sequence's
    /// concern — the §39.5 generator composes [WaitForSunAltitude → SlewScopeToAltAz → per-filter
    /// SkyFlats], and a hand-built plan does the same. No flat panel is involved.
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceItem_FlatDevice_SkyFlats_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_FlatDevice_SkyFlats_Description")]
    [ExportMetadata("Icon", "FlatWizardSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_FlatDevice")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SkyFlats : SequenceItem {

        [ImportingConstructor]
        public SkyFlats(IFlatCaptureExecutor? flatCaptureExecutor = null) {
            this.flatCaptureExecutor = flatCaptureExecutor;
        }

        private readonly IFlatCaptureExecutor? flatCaptureExecutor;

        // §48.7 sky_flat defaults.

        private double targetAdu = 25000;

        [JsonProperty]
        public double TargetAdu {
            get => targetAdu;
            set { targetAdu = value; RaisePropertyChanged(); }
        }

        private double targetAduTolerancePct = 5;

        [JsonProperty]
        public double TargetAduTolerancePct {
            get => targetAduTolerancePct;
            set { targetAduTolerancePct = value; RaisePropertyChanged(); }
        }

        private int frameCount = 20;

        [JsonProperty]
        public int FrameCount {
            get => frameCount;
            set { frameCount = value; RaisePropertyChanged(); }
        }

        private double minExposureSec = 0.01;

        [JsonProperty]
        public double MinExposureSec {
            get => minExposureSec;
            set { minExposureSec = value; RaisePropertyChanged(); }
        }

        private double maxExposureSec = 10;

        [JsonProperty]
        public double MaxExposureSec {
            get => maxExposureSec;
            set { maxExposureSec = value; RaisePropertyChanged(); }
        }

        private double stopAtMaxAdu = 50000;

        [JsonProperty]
        public double StopAtMaxAdu {
            get => stopAtMaxAdu;
            set { stopAtMaxAdu = value; RaisePropertyChanged(); }
        }

        private double stopAtMinAdu = 5000;

        [JsonProperty]
        public double StopAtMinAdu {
            get => stopAtMinAdu;
            set { stopAtMinAdu = value; RaisePropertyChanged(); }
        }

        private int gain = -1;

        [JsonProperty]
        public int Gain {
            get => gain;
            set { gain = value; RaisePropertyChanged(); }
        }

        private int offset = -1;

        [JsonProperty]
        public int Offset {
            get => offset;
            set { offset = value; RaisePropertyChanged(); }
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (flatCaptureExecutor is null) {
                throw new SequenceEntityFailedException("Sky-flat capture is not wired for sequence execution on this daemon.");
            }
            if (TargetAdu <= 0 || FrameCount < 1 || MinExposureSec <= 0 || MaxExposureSec < MinExposureSec
                || StopAtMinAdu < 0 || StopAtMaxAdu <= TargetAdu || StopAtMinAdu >= TargetAdu) {
                throw new SequenceEntityFailedException(
                    $"Sky-flat set is misconfigured: target ADU {TargetAdu} must sit between stop bounds [{StopAtMinAdu}, {StopAtMaxAdu}], frame count {FrameCount}, exposure bounds [{MinExposureSec}, {MaxExposureSec}].");
            }
            var request = new SkyFlatSetRequest(
                TargetAdu, TargetAduTolerancePct, FrameCount,
                MinExposureSec, MaxExposureSec, StopAtMaxAdu, StopAtMinAdu, Gain, Offset);
            var ok = await flatCaptureExecutor.CaptureSkyFlatSetAsync(request, progress, token);
            if (!ok) {
                throw new SequenceEntityFailedException(
                    "Sky-flat set failed — the sky left the usable brightness window or the capture path is unavailable (see the daemon log).");
            }
        }

        // Twilight sets re-probe before every frame, so budget roughly two exposures per saved
        // frame at the bounds' midpoint.
        public override TimeSpan GetEstimatedDuration() {
            var perFrame = (MinExposureSec + MaxExposureSec) / 2;
            return TimeSpan.FromSeconds(FrameCount * Math.Max(2, 2 * perFrame));
        }

        public override object Clone() {
            return new SkyFlats(flatCaptureExecutor) {
                Icon = Icon,
                Name = Name,
                Category = Category,
                Description = Description,
                TargetAdu = TargetAdu,
                TargetAduTolerancePct = TargetAduTolerancePct,
                FrameCount = FrameCount,
                MinExposureSec = MinExposureSec,
                MaxExposureSec = MaxExposureSec,
                StopAtMaxAdu = StopAtMaxAdu,
                StopAtMinAdu = StopAtMinAdu,
                Gain = Gain,
                Offset = Offset,
            };
        }

        public override string ToString() {
            return $"Item: {nameof(SkyFlats)}, TargetAdu: {TargetAdu} ±{TargetAduTolerancePct}%, Frames: {FrameCount}, Stop: [{StopAtMinAdu}, {StopAtMaxAdu}]";
        }
    }
}
