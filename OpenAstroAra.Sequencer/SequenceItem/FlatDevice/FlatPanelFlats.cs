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
    /// §48.3 — auto-exposure flat set (the headless re-imagining of NINA's flat-panel flats).
    /// Captures one SET of flats for the current filter/focus context: the executor lights the
    /// panel, probes exposures until the frame mean hits <see cref="TargetAdu"/> within
    /// <see cref="TargetAduTolerancePct"/>, then takes <see cref="FrameCount"/> saved FLAT frames
    /// at the converged exposure. Per-filter iteration stays the sequence's concern — the §39.5
    /// matching-flats generator emits one of these per filter block (SwitchFilter →
    /// MoveFocuserAbsolute → FlatPanelFlats), and a hand-built plan composes it the same way.
    /// A missing executor or a failed set fails this step loudly — silently continuing would
    /// produce a calibration library the user believes exists and doesn't.
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceItem_FlatDevice_FlatPanelFlats_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_FlatDevice_FlatPanelFlats_Description")]
    [ExportMetadata("Icon", "FlatWizardSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_FlatDevice")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class FlatPanelFlats : SequenceItem {

        [ImportingConstructor]
        public FlatPanelFlats(IFlatCaptureExecutor? flatCaptureExecutor = null) {
            this.flatCaptureExecutor = flatCaptureExecutor;
        }

        private readonly IFlatCaptureExecutor? flatCaptureExecutor;

        // §48.7 defaults. Setters keep the TakeExposure RaisePropertyChanged idiom so a future
        // editor binding stays live.

        private double targetAdu = 30000;

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

        private int frameCount = 30;

        [JsonProperty]
        public int FrameCount {
            get => frameCount;
            set { frameCount = value; RaisePropertyChanged(); }
        }

        private int brightness = 50;

        [JsonProperty]
        public int Brightness {
            get => brightness;
            set { brightness = value; RaisePropertyChanged(); }
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
                throw new SequenceEntityFailedException("Flat capture is not wired for sequence execution on this daemon.");
            }
            // Guard the request here (not just in the executor) so a hand-edited plan fails with
            // a message naming the field instead of a generic capture failure.
            if (TargetAdu <= 0 || FrameCount < 1 || MinExposureSec <= 0 || MaxExposureSec < MinExposureSec) {
                throw new SequenceEntityFailedException(
                    $"Flat set is misconfigured: target ADU {TargetAdu}, frame count {FrameCount}, exposure bounds [{MinExposureSec}, {MaxExposureSec}].");
            }
            var request = new FlatSetRequest(
                TargetAdu, TargetAduTolerancePct, FrameCount, Brightness,
                MinExposureSec, MaxExposureSec, Gain, Offset);
            var ok = await flatCaptureExecutor.CaptureFlatSetAsync(request, progress, token);
            if (!ok) {
                throw new SequenceEntityFailedException(
                    "Flat set failed — the exposure probe could not reach the target ADU or the capture path is unavailable (see the daemon log).");
            }
        }

        // Probe budget (executor-bounded iterations) + the saved frames at whatever exposure
        // converges — the midpoint of the bounds is the honest a-priori guess.
        public override TimeSpan GetEstimatedDuration() {
            var perFrame = (MinExposureSec + MaxExposureSec) / 2;
            return TimeSpan.FromSeconds(30 + (FrameCount * Math.Max(1, perFrame)));
        }

        public override object Clone() {
            return new FlatPanelFlats(flatCaptureExecutor) {
                Icon = Icon,
                Name = Name,
                Category = Category,
                Description = Description,
                TargetAdu = TargetAdu,
                TargetAduTolerancePct = TargetAduTolerancePct,
                FrameCount = FrameCount,
                Brightness = Brightness,
                MinExposureSec = MinExposureSec,
                MaxExposureSec = MaxExposureSec,
                Gain = Gain,
                Offset = Offset,
            };
        }

        public override string ToString() {
            return $"Item: {nameof(FlatPanelFlats)}, TargetAdu: {TargetAdu} ±{TargetAduTolerancePct}%, Frames: {FrameCount}, Brightness: {Brightness}";
        }
    }
}
