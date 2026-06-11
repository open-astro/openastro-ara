#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Profile.Interfaces;

namespace OpenAstroAra.Image.ImageData {

    /// <summary>
    /// §2105: a <see cref="RenderedImage"/> with full-resolution debayered colour planes attached
    /// (<see cref="DebayeredData"/> = Lum/R/G/B). The inherited grayscale <c>Image</c> is the
    /// luminance/mono render; colour consumers read <see cref="DebayeredData"/>.
    /// </summary>
    public class DebayeredImage : RenderedImage, IDebayeredImage {

        public LRGBArrays DebayeredData { get; }
        public bool SaveColorChannels { get; }
        public bool SaveLumChannel { get; }
        public SensorType BayerPattern { get; }

        public DebayeredImage(
            byte[] image, IImageData rawImageData, IProfileService profileService,
            IStarDetection starDetection, IStarAnnotator starAnnotator,
            LRGBArrays debayeredData, bool saveColorChannels, bool saveLumChannel, SensorType bayerPattern)
            : base(image, rawImageData, profileService, starDetection, starAnnotator) {
            DebayeredData = debayeredData;
            SaveColorChannels = saveColorChannels;
            SaveLumChannel = saveLumChannel;
            BayerPattern = bayerPattern;
        }
    }
}
