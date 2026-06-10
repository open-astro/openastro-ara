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
using OpenAstroAra.Core.Locale;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Imaging {

    /// <summary>
    /// §14e capture-path PRb — the headless re-port of NINA's <c>TakeExposure</c>. The JSON surface
    /// (<c>ExposureTime</c>/<c>Gain</c>/<c>Offset</c>/<c>Binning</c>/<c>ImageType</c>/
    /// <c>ExposureCount</c>) is NINA-verbatim so imported sequences and the §38.7 starter templates
    /// resolve via the §38k-6 type remap. Execution is ARA-native: the instruction builds a
    /// <see cref="CaptureSequence"/> and hands it to <see cref="IImagingMediator.CaptureImage"/>,
    /// whose server implementation runs the full §14e pipeline (expose → download → §72 FITS →
    /// §28 catalog) — the returned <c>IExposureData</c> is deliberately ignored (the frame is
    /// already persisted server-side; the WPF-era in-memory image pipeline is not ported).
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceItem_Imaging_TakeExposure_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_Imaging_TakeExposure_Description")]
    [ExportMetadata("Icon", "CameraSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Camera")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class TakeExposure : SequenceItem, IValidatable {

        private readonly ICameraMediator cameraMediator;
        private readonly IImagingMediator imagingMediator;

        [ImportingConstructor]
        public TakeExposure(ICameraMediator cameraMediator, IImagingMediator imagingMediator) {
            this.cameraMediator = cameraMediator;
            this.imagingMediator = imagingMediator;
            Binning = new BinningMode(1, 1);
        }

        private TakeExposure(TakeExposure cloneMe) : this(cloneMe.cameraMediator, cloneMe.imagingMediator) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new TakeExposure(this) {
                ExposureTime = ExposureTime,
                Gain = Gain,
                Offset = Offset,
                Binning = Binning is null ? new BinningMode(1, 1) : new BinningMode(Binning.X, Binning.Y),
                ImageType = ImageType,
                ExposureCount = ExposureCount,
            };
        }

        private double exposureTime;

        [JsonProperty]
        public double ExposureTime {
            get => exposureTime;
            set { exposureTime = value; RaisePropertyChanged(); }
        }

        private int gain = -1;

        /// <summary>NINA convention: -1 = use the camera's current/default gain.</summary>
        [JsonProperty]
        public int Gain {
            get => gain;
            set { gain = value; RaisePropertyChanged(); }
        }

        private int offset = -1;

        /// <summary>NINA convention: -1 = use the camera's current/default offset.</summary>
        [JsonProperty]
        public int Offset {
            get => offset;
            set { offset = value; RaisePropertyChanged(); }
        }

        private BinningMode binning = new(1, 1);

        [JsonProperty]
        public BinningMode Binning {
            get => binning;
            set { binning = value; RaisePropertyChanged(); }
        }

        private string imageType = ImageTypes.LIGHT;

        [JsonProperty]
        public string ImageType {
            get => imageType;
            set { imageType = value; RaisePropertyChanged(); }
        }

        private int exposureCount;

        /// <summary>Running counter NINA persists per instruction (incremented per execution).</summary>
        [JsonProperty]
        public int ExposureCount {
            get => Volatile.Read(ref exposureCount);
            set { Volatile.Write(ref exposureCount, value); RaisePropertyChanged(); }
        }

        private IList<string> issues = new List<string>();

        public IList<string> Issues {
            get => issues;
            set { issues = value; RaisePropertyChanged(); }
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var sequence = new CaptureSequence(
                ExposureTime,
                string.IsNullOrWhiteSpace(ImageType) ? ImageTypes.LIGHT : ImageType,
                filterType: null,           // filter changes are SwitchFilter's concern
                binning: Binning ?? new BinningMode(1, 1),
                exposureCount: 1) {
                Gain = Gain,
                Offset = Offset,
            };
            // The capture pipeline persists the frame server-side; the in-memory exposure data is
            // deliberately discarded (see class doc).
            _ = await imagingMediator.CaptureImage(sequence, token, progress, Parent?.Name ?? string.Empty);
            // Atomic: a ParallelContainer can drive concurrent executions, and a lost update would
            // persist a wrong count through the [JsonProperty] backing field.
            Interlocked.Increment(ref exposureCount);
            RaisePropertyChanged(nameof(ExposureCount));
        }

        public bool Validate() {
            var i = new List<string>();
            if (!cameraMediator.GetInfo().Connected) {
                i.Add(Loc.Instance["LblCameraNotConnected"]);
            }
            // A NINA import or hand-edited sequence can carry a non-positive exposure; catch it here
            // so the error stays in the validation layer instead of surfacing as an opaque driver
            // exception from StartExposure(duration <= 0).
            if (ExposureTime <= 0) {
                i.Add(Loc.Instance["LblExposureTimeNotValid"]);
            }
            // An unrecognized ImageType would map to Light yet still be written verbatim into the
            // FITS IMAGETYP header, where plate-solvers and calibration matchers could misfile it.
            // Reject the typo here; blank is fine — Execute defaults it to LIGHT.
            if (!string.IsNullOrWhiteSpace(ImageType) && !IsRecognizedImageType(ImageType)) {
                i.Add(Loc.Instance["LblImageTypeNotValid"]);
            }
            Issues = i;
            return i.Count == 0;
        }

        private static readonly string[] RecognizedImageTypes = {
            ImageTypes.LIGHT, ImageTypes.FLAT, ImageTypes.DARK, ImageTypes.BIAS, ImageTypes.SNAPSHOT, "DARKFLAT",
        };

        private static bool IsRecognizedImageType(string imageType) =>
            Array.Exists(RecognizedImageTypes, t => string.Equals(t, imageType, StringComparison.OrdinalIgnoreCase));

        public override void AfterParentChanged() {
            Validate();
        }

        public override TimeSpan GetEstimatedDuration() {
            // Clamp at zero so a parent container summing durations before Validate() runs can't get
            // a negative total from an invalid (<= 0) exposure.
            return TimeSpan.FromSeconds(Math.Max(0, ExposureTime));
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(TakeExposure)}, ExposureTime: {ExposureTime}, Type: {ImageType}, Binning: {Binning?.Name}, Gain: {Gain}";
        }
    }
}
