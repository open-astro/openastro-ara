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

        public IDebayeredImage Debayer(bool saveColorChannels = false, bool saveLumChannel = false, SensorType bayerPattern = SensorType.RGGB) =>
            throw new NotImplementedException("Debayer pending OpenCvSharp4 wiring.");

        public virtual Task<IRenderedImage> Stretch(double factor, double blackClipping, bool unlinked) =>
            throw new NotImplementedException("Stretch pending OpenCvSharp4 wiring; use OpenAstroAra.Stretch headless pipeline.");

        public Task<IRenderedImage> DetectStars(
                bool annotateImage,
                StarSensitivity sensitivity,
                NoiseReduction noiseReduction,
                IProgress<ApplicationStatus>? progress = default(Progress<ApplicationStatus>),
                CancellationToken cancelToken = default) =>
            throw new NotImplementedException("DetectStars pending OpenCvSharp4 wiring.");

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

        public void UpdateAnalysis(StarDetectionParams p, StarDetectionResult result) =>
            throw new NotImplementedException("UpdateAnalysis pending OpenCvSharp4 wiring.");
    }
}