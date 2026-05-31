#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Model;
using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Image.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;
namespace OpenAstroAra.Image.ImageData {

    public class ImageArrayExposureData : BaseExposureData {
        private readonly IImageArray imageArray;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsBayered { get; private set; }

        public ImageArrayExposureData(
            ushort[] input,
            int width,
            int height,
            int bitDepth,
            bool isBayered,
            ImageMetaData metaData,
            IImageDataFactory imageDataFactory)
            : base(bitDepth, metaData, imageDataFactory) {
            this.imageArray = new ImageArray(input);
            this.Width = width;
            this.Height = height;
            this.IsBayered = isBayered;
        }
        public ImageArrayExposureData(
            int[] input,
            int width,
            int height,
            int bitDepth,
            bool isBayered,
            ImageMetaData metaData,
            IImageDataFactory imageDataFactory)
            : base(bitDepth, metaData, imageDataFactory) {
            this.imageArray = new ImageArrayInt(input);
            this.Width = width;
            this.Height = height;
            this.IsBayered = isBayered;
        }

        public override Task<IImageData> ToImageData(IProgress<ApplicationStatus> progress = default, CancellationToken cancelToken = default) {
            return Task.FromResult<IImageData>(
                imageDataFactory.CreateBaseImageData(
                    imageArray: this.imageArray,
                    width: this.Width,
                    height: this.Height,
                    bitDepth: this.BitDepth,
                    isBayered: this.IsBayered,
                    metaData: this.MetaData));
        }

        // FromBitmapSource / ArrayFromSource / ArrayFrom8BitSource /
        // ArrayFrom16BitSource removed — the WPF BitmapSource pixel-format
        // matrix is replaced by OpenCvSharp4 Mat conversions per playbook
        // §line-2105. The headless daemon's capture path uses raw ushort[]
        // arrays directly (via the §72 CFITSIO P/Invoke + the §65 stretch
        // pipeline), so this no-longer-used WPF entry point is stubbed.
        public static Task<ImageArrayExposureData> FromBitmapSource(byte[] source, IImageDataFactory imageDataFactory) =>
            throw new NotImplementedException("FromBitmapSource pending OpenCvSharp4 wiring.");
    }
}