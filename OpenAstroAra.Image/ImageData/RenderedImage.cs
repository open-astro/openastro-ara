#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enum;
using System;
using System.Threading;
using System.Threading.Tasks;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Core.Utility;

namespace OpenAstroAra.Image.ImageData {

    public class RenderedImage : BaseINPC, IRenderedImage {
        protected readonly IProfileService profileService;
        protected readonly IStarDetection starDetection;
        protected readonly IStarAnnotator starAnnotator;

        public IImageData RawImageData { get; private set; }

        private byte[] image;

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
            throw new NotImplementedException("ReRender pending OpenCvSharp4 wiring.");

        public IDebayeredImage Debayer(bool saveColorChannels = false, bool saveLumChannel = false, SensorType bayerPattern = SensorType.RGGB) =>
            throw new NotImplementedException("Debayer pending OpenCvSharp4 wiring.");

        public virtual Task<IRenderedImage> Stretch(double factor, double blackClipping, bool unlinked) =>
            throw new NotImplementedException("Stretch pending OpenCvSharp4 wiring; use OpenAstroAra.Stretch headless pipeline.");

        public Task<IRenderedImage> DetectStars(
                bool annotateImage,
                StarSensitivityEnum sensitivity,
                NoiseReductionEnum noiseReduction,
                CancellationToken cancelToken = default,
                IProgress<ApplicationStatus> progress = default(Progress<ApplicationStatus>)) =>
            throw new NotImplementedException("DetectStars pending OpenCvSharp4 wiring.");

        public Task<byte[]> GetThumbnail() =>
            throw new NotImplementedException("GetThumbnail pending OpenCvSharp4 wiring.");

        public void UpdateAnalysis(StarDetectionParams p, StarDetectionResult result) =>
            throw new NotImplementedException("UpdateAnalysis pending OpenCvSharp4 wiring.");
    }
}