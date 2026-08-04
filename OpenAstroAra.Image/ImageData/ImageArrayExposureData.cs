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
using OpenAstroAra.Image.FileFormat.Raster;
using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Image.Interfaces;
using System;
using System.Collections.Generic;
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

        public override Task<IImageData> ToImageData(IProgress<ApplicationStatus>? progress = default, CancellationToken cancelToken = default) {
            return Task.FromResult<IImageData>(
                imageDataFactory.CreateBaseImageData(
                    imageArray: this.imageArray,
                    width: this.Width,
                    height: this.Height,
                    bitDepth: this.BitDepth,
                    isBayered: this.IsBayered,
                    metaData: this.MetaData));
        }

        public static async Task<ImageArrayExposureData> FromBitmapSource(byte[] source,
                IImageDataFactory imageDataFactory, CancellationToken cancellationToken = default) {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(imageDataFactory);
            var decoded = await new RasterImageDecoder().DecodeBufferAsync(source,
                expectedFormat: null, ImageLoadLimits.Default, cancellationToken).ConfigureAwait(false);
            var metadata = new ImageMetaData();
            if (decoded.Format == RasterImageFormat.Tiff
                && decoded.Metadata.TryGetValue("TIFFIMAGEDESCRIPTION", out var description)) {
                TiffMetadataCodec.TryDecode(description, decoded.Width, decoded.Height, out metadata);
            }
            var headers = new List<IGenericMetaDataHeader>(metadata.GenericHeaders);
            foreach (var pair in decoded.Metadata) {
                if (string.Equals(pair.Key, "TIFFIMAGEDESCRIPTION", StringComparison.Ordinal)) continue;
                headers.Add(new StringMetaDataHeader(pair.Key, pair.Value));
            }
            headers.Add(new IntMetaDataHeader("RASTERSOURCEBITDEPTH", decoded.SourceBitDepth));
            if (decoded.IsPreviewOnly) {
                headers.Add(new StringMetaDataHeader("RASTERPREVIEWONLY", "true"));
            }
            metadata.GenericHeaders = headers;
            var cfaPattern = RasterMetadata.ApplyColorModel(metadata,
                hasColorPlanes: decoded.ColorData is not null);
            return new ImageArrayExposureData(decoded.BorrowLuminancePlane(), decoded.Width,
                decoded.Height, bitDepth: 16, isBayered: cfaPattern is not null,
                metadata, imageDataFactory);
        }
    }
}