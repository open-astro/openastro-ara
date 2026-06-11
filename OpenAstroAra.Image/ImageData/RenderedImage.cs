#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Image.ImageData {

    public class RenderedImage : BaseINPC, IRenderedImage {
        private protected readonly IProfileService profileService;
        private protected readonly IStarDetection starDetection;
        private protected readonly IStarAnnotator starAnnotator;

        public IImageData RawImageData { get; private set; }

        private byte[]? image;

        public byte[] Image {
            get => this.image ?? OriginalImage;
            private set {
                this.image = value;
                RaisePropertyChanged();
            }
        }

        public byte[] OriginalImage { get; private set; }

        public RenderedImage(byte[] image, IImageData rawImageData, IProfileService profileService, IStarDetection starDetection, IStarAnnotator starAnnotator) {
            this.OriginalImage = image;
            this.RawImageData = rawImageData;
            this.profileService = profileService;
            this.starDetection = starDetection;
            this.starAnnotator = starAnnotator;
        }

        public static async Task<IRenderedImage> FromBitmapSource(byte[] source, IExposureDataFactory exposureDataFactory, IProfileService profileService, IStarDetection starDetection, IStarAnnotator starAnnotator, bool calculateStatistics = false) {
            var exposureData = await exposureDataFactory.CreateImageArrayExposureDataFromBitmapSource(source);
            var rawImageData = await exposureData.ToImageData();
            return Create(source, rawImageData, profileService, starDetection, starAnnotator, calculateStatistics: calculateStatistics);
        }

        public static RenderedImage Create(byte[] source, IImageData rawImageData, IProfileService profileService, IStarDetection starDetection, IStarAnnotator starAnnotator, bool calculateStatistics = false) {
            return new RenderedImage(source, rawImageData, profileService, starDetection, starAnnotator);
        }

        // ReRender / Debayer / Stretch / DetectStars / GetThumbnail /
        // UpdateAnalysis bodies removed — they used the deleted WPF-coupled
        // ImageUtility + DebayeredImage + WPF BitmapSource transforms.
        // Replacements arrive when OpenCvSharp4 wiring lands per playbook
        // §line-2105.

        public virtual IRenderedImage ReRender() =>
            // §2105: re-render the display buffer from the raw frame (RenderBitmapSource → a fresh
            // RenderedImage). Used to refresh the rendered view from the source pixels after analysis.
            // SYNCHRONOUS + CPU-bound (~50-200ms on a full-res frame, the Stretcher.Apply cost) — the
            // inherited IRenderedImage contract is sync, so callers on a UI/event thread must offload.
            // Note this re-renders from raw and so does NOT preserve a stretch applied via Stretch().
            RawImageData.RenderImage();

        public IDebayeredImage Debayer(bool saveColorChannels = false, bool saveLumChannel = false, SensorType bayerPattern = SensorType.RGGB) {
            // §2105: full-resolution bilinear debayer of the raw CFA mosaic into LRGB planes. The
            // inherited grayscale Image (mono render) is reused; colour lives in DebayeredData. The
            // inherited IRenderedImage.Debayer is synchronous + CPU-bound (~50-200ms full-res) — offload
            // from a UI/event thread. Caller is responsible for only calling this on a Bayered frame.
            var pattern = ToBayerPattern(bayerPattern);
            var w = RawImageData.Properties.Width;
            var h = RawImageData.Properties.Height;
            var (r, g, b) = OpenAstroAra.Stretch.Debayer.Bilinear(RawImageData.Data.FlatArray, w, h, pattern);
            var data = new LRGBArrays(Luminance(r, g, b), r, g, b);
            return new DebayeredImage(Image, RawImageData, profileService, starDetection, starAnnotator,
                data, saveColorChannels, saveLumChannel, bayerPattern);
        }

        // Map ASCOM SensorType → the §65 BayerPattern. Bayer-mosaic types only; exotic CFAs throw.
        private static OpenAstroAra.Stretch.BayerPattern ToBayerPattern(SensorType s) => s switch {
            SensorType.RGGB => OpenAstroAra.Stretch.BayerPattern.RGGB,
            SensorType.BGGR => OpenAstroAra.Stretch.BayerPattern.BGGR,
            SensorType.GBRG => OpenAstroAra.Stretch.BayerPattern.GBRG,
            SensorType.GRBG => OpenAstroAra.Stretch.BayerPattern.GRBG,
            _ => throw new NotSupportedException(
                $"Bilinear debayer supports RGGB/BGGR/GBRG/GRBG; got {s} (CMYG/LRGB/etc. unsupported)."),
        };

        // Rec.601 luma from the debayered planes.
        private static ushort[] Luminance(ushort[] r, ushort[] g, ushort[] b) {
            var lum = new ushort[r.Length];
            for (int i = 0; i < r.Length; i++) {
                lum[i] = (ushort)(0.299 * r[i] + 0.587 * g[i] + 0.114 * b[i]);
            }
            return lum;
        }

        public virtual Task<IRenderedImage> Stretch(double factor, double blackClipping, bool unlinked) {
            // §2105: re-stretch the raw 16-bit frame with explicit STF knobs via the §65 pipeline —
            // factor = target background (0..1 the median maps to), blackClipping = shadow clip in
            // σ_MAD below the median (NINA passes it negative, hence Abs). `unlinked` (per-channel
            // colour) only applies once Debayer() lands; this grayscale render is inherently linked.
            // Offloaded — Stf is CPU-bound (~50-200ms on a full-res frame).
            var pixels = RawImageData.Data.FlatArray;
            return Task.Run<IRenderedImage>(() => Create(
                OpenAstroAra.Stretch.Stretcher.Stf(pixels, factor, Math.Abs(blackClipping)),
                RawImageData, profileService, starDetection, starAnnotator));
        }

        public Task<IRenderedImage> DetectStars(
                bool annotateImage,
                StarSensitivity sensitivity,
                NoiseReduction noiseReduction,
                IProgress<ApplicationStatus>? progress = default(Progress<ApplicationStatus>),
                CancellationToken cancelToken = default) {
            // §2105: headless star detection + HFR on the raw 16-bit frame via the dependency-free
            // StarDetector (the §26 decision ruled out OpenCvSharp4). Sensitivity selects the k·σ
            // threshold; lower k ⇒ more stars. NoiseReduction != None turns on a 3×3 median pre-filter.
            // Offloaded — the flood-fill is CPU-bound (~50-300ms full-res). On-image annotation is not
            // yet wired (it needs the §2105 annotator); annotateImage is honoured as a no-op for now.
            if (annotateImage) {
                // The §2105 on-image annotator (overlay drawing) isn't wired yet — make the no-op visible
                // so a future §59 autofocus caller that asks for annotation sees why it gets a plain frame.
                Logger.Debug("DetectStars(annotateImage: true): star annotation not yet implemented; returning the un-annotated frame.");
            }
            var parameters = new StarDetectionParams {
                Sensitivity = SensitivityToSigma(sensitivity),
                NoiseReduction = (int)noiseReduction,
            };
            var pixels = RawImageData.Data.FlatArray;
            var width = RawImageData.Properties.Width;
            var height = RawImageData.Properties.Height;
            return Task.Run<IRenderedImage>(() => {
                var result = StarDetector.Detect(pixels, width, height, parameters, cancelToken);
                UpdateAnalysis(parameters, result);
                return this;
            }, cancelToken);
        }

        // StarSensitivity → the background-threshold multiplier k (median + k·σ_MAD). A higher level
        // pushes the threshold down toward the noise floor, recovering fainter stars.
        private static double SensitivityToSigma(StarSensitivity sensitivity) => sensitivity switch {
            StarSensitivity.High => 5.0,
            StarSensitivity.Highest => 3.0,
            _ => 8.0, // Normal
        };

        public Task<byte[]> GetThumbnail() {
            // §2105: 320px-max JPEG thumbnail of the rendered 8-bit grayscale buffer via the §65 encoder.
            // Snapshot the buffer + dims at call time, then offload — resize + JPEG encode is ~50-200ms
            // on a full-res frame, too long to run synchronously on the caller's thread.
            var buffer = Image;
            var width = RawImageData.Properties.Width;
            var height = RawImageData.Properties.Height;
            // EncodeThumbnail needs one grayscale byte/pixel. Make that invariant explicit so a future
            // Stretch()/annotation path that swaps Image for a multi-channel buffer fails loudly here
            // rather than deep inside the Task.Run.
            System.Diagnostics.Debug.Assert(buffer.Length == width * height,
                "GetThumbnail expects a 1-byte/pixel grayscale render buffer");
            return Task.Run(() => OpenAstroAra.Stretch.JpegEncoder.EncodeThumbnail(buffer, width, height));
        }

        public void UpdateAnalysis(StarDetectionParams p, StarDetectionResult result) {
            // §2105: publish a detection result onto the raw frame's analysis so HFR/StarCount flow
            // into the FITS pattern keys (BaseImageData stamps HFR/StarCount from here) and any INPC
            // observers. Mirrors NINA's UpdateAnalysis contract.
            var analysis = RawImageData.StarDetectionAnalysis;
            analysis.HFR = result.AverageHFR;
            analysis.HFRStDev = result.HFRStdDev;
            analysis.DetectedStars = result.DetectedStars;
            analysis.StarList = result.StarList;
        }
    }
}