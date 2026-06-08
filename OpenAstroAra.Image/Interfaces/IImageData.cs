#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enum;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Image.FileFormat;
using OpenAstroAra.Image.ImageData;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace OpenAstroAra.Image.Interfaces {

    public interface IImageData {
        IImageArray Data { get; }

        ImageProperties Properties { get; }

        Nito.AsyncEx.AsyncLazy<IImageStatistics> Statistics { get; }

        void SetImageStatistics(IImageStatistics statistics);

        IStarDetectionAnalysis StarDetectionAnalysis { get; set; }

        ImageMetaData MetaData { get; }

        IRenderedImage RenderImage();

        byte[] RenderBitmapSource();

        ImagePatterns GetImagePatterns();

        Task<string> SaveToDisk(FileSaveInfo fileSaveInfo, CancellationToken cancelToken = default, bool forceFileType = false);
        Task<string> SaveToDisk(FileSaveInfo fileSaveInfo, CancellationToken token, bool forceFileType, IList<ImagePattern> customPatterns);

        [Obsolete]
        Task<string> PrepareSave(FileSaveInfo fileSaveInfo, CancellationToken cancelToken = default);
        [Obsolete]
        string FinalizeSave(string file, string pattern, IList<ImagePattern> customPatterns);
    }

    public interface IImageDataFactory {

        BaseImageData CreateBaseImageData(ushort[] input, int width, int height, int bitDepth, bool isBayered, ImageMetaData metaData);

        BaseImageData CreateBaseImageData(IImageArray imageArray, int width, int height, int bitDepth, bool isBayered, ImageMetaData metaData);

        Task<IImageData> CreateFromFile(string path, int bitDepth, bool isBayered, RawConverter rawConverter, CancellationToken ct = default);
    }
}