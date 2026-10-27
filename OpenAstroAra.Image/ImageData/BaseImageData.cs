#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Interfaces;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Image.FileFormat;
using OpenAstroAra.Image.FileFormat.FITS;
using OpenAstroAra.Image.FileFormat.XISF;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace OpenAstroAra.Image.ImageData {

    public partial class BaseImageData : IImageData {
        private protected readonly IProfileService profileService;
        private protected readonly IStarDetection starDetection;
        private protected readonly IStarAnnotator starAnnotator;

        public BaseImageData(ushort[] input, int width, int height, int bitDepth, bool isBayered, ImageMetaData metaData, IProfileService profileService, IStarDetection starDetection, IStarAnnotator starAnnotator)
            : this(
                  imageArray: new ImageArray(flatArray: input),
                  width: width,
                  height: height,
                  bitDepth: bitDepth,
                  isBayered: isBayered,
                  metaData: metaData,
                  profileService: profileService,
                  starDetection: starDetection,
                  starAnnotator: starAnnotator) {
        }

        public BaseImageData(IImageArray imageArray, int width, int height, int bitDepth, bool isBayered, ImageMetaData metaData, IProfileService profileService, IStarDetection starDetection, IStarAnnotator starAnnotator) {
            Data = imageArray;
            MetaData = metaData;
            Properties = new ImageProperties(width: width, height: height, bitDepth: bitDepth, isBayered: isBayered, gain: metaData.Camera.Gain, offset: metaData.Camera.Offset);
            // Start from a default StarDetectionAnalysis whose sentinels (HFR = NaN,
            // DetectedStars = -1) make GetImagePatterns() take the "no star data"
            // branch until detection runs — assigning null! here would NRE on any
            // FinalizeSave() before then.
            StarDetectionAnalysis = new StarDetectionAnalysis();
            Statistics = new Nito.AsyncEx.AsyncLazy<IImageStatistics>(async () => await Task.Run(() => ImageStatistics.Create(this)));
            this.profileService = profileService;
            this.starDetection = starDetection;
            this.starAnnotator = starAnnotator;
        }

        public IImageArray Data { get; private set; }

        public ImageProperties Properties { get; private set; }

        public ImageMetaData MetaData { get; private set; }

        public Nito.AsyncEx.AsyncLazy<IImageStatistics> Statistics { get; private set; }

        public IStarDetectionAnalysis StarDetectionAnalysis { get; set; }

        public IRenderedImage RenderImage() {
            return RenderedImage.Create(RenderBitmapSource(), this, profileService, starDetection, starAnnotator);
        }

        public byte[] RenderBitmapSource() {
            // §2105: render the raw 16-bit frame to an 8-bit display buffer (one byte/pixel, row-major)
            // via the §65 SkiaSharp-era Stretcher — an auto-STF stretch so the result is viewable
            // straight out of the camera. Re-stretch with explicit params is RenderedImage.Stretch().
            //
            // #1147: a Bayered (OSC) frame whose metadata names a Bayer mosaic renders as the debayered
            // LUMINANCE (bilinear debayer → Rec.601 luma), not the grey CFA checkerboard — the one-byte
            // display buffer is mono by contract, so luminance is the honest OSC display; colour planes
            // come from RenderedImage.Debayer(). A Bayered frame with no usable pattern (Monochrome /
            // CMYG / LRGB in the metadata) still renders the raw mosaic rather than guessing RGGB.
            var pattern = MetaData?.Camera?.SensorType ?? SensorType.Monochrome;
            if (Properties.IsBayered && RenderedImage.IsSupportedBayer(pattern)) {
                var (r, g, b) = OpenAstroAra.Stretch.Debayer.Bilinear(
                    Data.FlatArray, Properties.Width, Properties.Height, RenderedImage.ToBayerPattern(pattern));
                return OpenAstroAra.Stretch.Stretcher.Apply(
                    OpenAstroAra.Stretch.StretchAlgorithm.AutoStf, RenderedImage.Luminance(r, g, b));
            }
            return OpenAstroAra.Stretch.Stretcher.Apply(
                OpenAstroAra.Stretch.StretchAlgorithm.AutoStf, Data.FlatArray);
        }

        public void SetImageStatistics(IImageStatistics statistics) {
            Statistics = new Nito.AsyncEx.AsyncLazy<IImageStatistics>(() => Task.FromResult(statistics));
        }

        #region "Save"

        [Obsolete("Legacy two-step save API; use SaveToDisk instead.")]
        /// <summary>
        ///  Saves file to application temp path
        /// </summary>
        /// <param name="fileType"></param>
        /// <param name="cancelToken"></param>
        /// <returns></returns>
        public async Task<string> PrepareSave(FileSaveInfo fileSaveInfo, CancellationToken cancelToken = default) {
            var actualPath = string.Empty;
            try {
                using (MyStopWatch.Measure()) {
                    // Reference: https://devblogs.microsoft.com/premier-developer/the-danger-of-taskcompletionsourcet-class/
                    var cancelTaskSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (cancelToken.Register(() => cancelTaskSource.SetCanceled())) {
                        var saveTask = SaveToDiskAsync(fileSaveInfo, Guid.NewGuid().ToString(), cancelToken);
                        await Task.WhenAny(cancelTaskSource.Task, saveTask);
                        cancelToken.ThrowIfCancellationRequested();
                        actualPath = await saveTask;
                    }

                    Logger.Debug($"Saved temporary image at {actualPath}");
                }
            } catch (OperationCanceledException) {
                throw;
            } catch (AggregateException ae) {
                Logger.Error(ae);
                throw ae.InnerException ?? ae;
            } catch (Exception ex) {
                Logger.Error(ex);
                throw;
            } finally {
            }
            return actualPath;
        }

        [Obsolete("Legacy two-step save API; use SaveToDisk instead.")]
        /// <summary>
        /// Renames and moves file to destination according to pattern
        /// </summary>
        /// <param name="file"></param>
        /// <param name="pattern"></param>
        /// <param name="customPatterns"></param>
        /// <returns></returns>        
        public string FinalizeSave(string file, string pattern, IList<ImagePattern> customPatterns) {
            try {
                var imagePatterns = GetImagePatterns();
                foreach (var cp in customPatterns) {
                    imagePatterns.Add(cp);
                }

                var fileName = imagePatterns.GetImageFileString(pattern);
                var extension = GetFileExtensionsRegex().Match(file).Value;
                var targetPath = Path.GetDirectoryName(file) ?? string.Empty;
                var newFileName = CoreUtil.GetUniqueFilePath(Path.Combine(targetPath, $"{fileName}{extension}"));

                var fi = new FileInfo(newFileName);
                if (fi.Directory != null && !fi.Directory.Exists) {
                    fi.Directory.Create();
                }

                var fileinfo = new FileInfo(file);

                Logger.Info($"Finalize image and moving it to {newFileName}");
                fileinfo.MoveTo(newFileName);

                return newFileName;
            } catch (Exception ex) {
                Logger.Error(ex);
                throw;
            } finally {
            }
        }

        [GeneratedRegex(@"(?:(?:\.\w+)?\.\w+$)")]
        private static partial Regex GetFileExtensionsRegex();

        public ImagePatterns GetImagePatterns() {
            var p = new ImagePatterns();
            var metadata = MetaData;
            p.Set(ImagePatternKeys.Filter, metadata.FilterWheel.Filter);
            p.Set(ImagePatternKeys.ExposureTime, metadata.Image.ExposureTime);
            p.Set(ImagePatternKeys.ApplicationStartDate, CoreUtil.ApplicationStartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            p.Set(ImagePatternKeys.Date, metadata.Image.ExposureStart.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            // ExposureStart is initialized to DateTime.MinValue, and we cannot subtract time from that. Only evaluate
            // the $$DATEMINUS12$$ pattern if the time is at least 12 hours on from DateTime.MinValue.
            if (metadata.Image.ExposureStart > DateTime.MinValue.AddHours(12)) {
                p.Set(ImagePatternKeys.DateMinus12, metadata.Image.ExposureStart.ToLocalTime().AddHours(-12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            p.Set(ImagePatternKeys.DateUtc, metadata.Image.ExposureStart.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            p.Set(ImagePatternKeys.Time, metadata.Image.ExposureStart.ToLocalTime().ToString("HH-mm-ss", CultureInfo.InvariantCulture));
            p.Set(ImagePatternKeys.TimeUtc, metadata.Image.ExposureStart.ToUniversalTime().ToString("HH-mm-ss", CultureInfo.InvariantCulture));
            p.Set(ImagePatternKeys.DateTime, metadata.Image.ExposureStart.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture));
            p.Set(ImagePatternKeys.MJD, metadata.Image.ExposureStart.ToMJD(), precision: 8);
            p.Set(ImagePatternKeys.FrameNr, metadata.Image.ExposureNumber.ToString("0000", CultureInfo.InvariantCulture));
            p.Set(ImagePatternKeys.ImageType, metadata.Image.ImageType);
            p.Set(ImagePatternKeys.TargetName, metadata.Target.Name);

            if (metadata.Image.RecordedRMS != null) {
                p.Set(ImagePatternKeys.RMS, metadata.Image.RecordedRMS.Total);
                p.Set(ImagePatternKeys.RMSArcSec, metadata.Image.RecordedRMS.Total * metadata.Image.RecordedRMS.Scale);
                p.Set(ImagePatternKeys.PeakRA, metadata.Image.RecordedRMS.PeakRA);
                p.Set(ImagePatternKeys.PeakRAArcSec, metadata.Image.RecordedRMS.PeakRA * metadata.Image.RecordedRMS.Scale);
                p.Set(ImagePatternKeys.PeakDec, metadata.Image.RecordedRMS.PeakDec);
                p.Set(ImagePatternKeys.PeakDecArcSec, metadata.Image.RecordedRMS.PeakDec * metadata.Image.RecordedRMS.Scale);
            }

            if (metadata.Focuser.Position.HasValue) {
                p.Set(ImagePatternKeys.FocuserPosition, metadata.Focuser.Position.Value);
            }

            if (!double.IsNaN(metadata.Focuser.Temperature)) {
                p.Set(ImagePatternKeys.FocuserTemp, metadata.Focuser.Temperature);
            }

            if (string.IsNullOrEmpty(metadata.Camera.Binning)) {
                p.Set(ImagePatternKeys.Binning, "1x1");
            } else {
                p.Set(ImagePatternKeys.Binning, metadata.Camera.Binning);
            }

            if (!double.IsNaN(metadata.Camera.Temperature)) {
                p.Set(ImagePatternKeys.SensorTemp, metadata.Camera.Temperature);
            }

            if (!double.IsNaN(metadata.Camera.SetPoint)) {
                p.Set(ImagePatternKeys.TemperatureSetPoint, metadata.Camera.SetPoint);
            }

            if (metadata.Camera.Gain >= 0) {
                p.Set(ImagePatternKeys.Gain, metadata.Camera.Gain);
            }

            if (metadata.Camera.Offset >= 0) {
                p.Set(ImagePatternKeys.Offset, metadata.Camera.Offset);
            }

            if (metadata.Camera.USBLimit >= 0) {
                p.Set(ImagePatternKeys.USBLimit, metadata.Camera.USBLimit);
            }

            if (!double.IsNaN(StarDetectionAnalysis.HFR)) {
                p.Set(ImagePatternKeys.HFR, StarDetectionAnalysis.HFR);
            }

            if (!double.IsNaN(metadata.WeatherData.SkyQuality)) {
                p.Set(ImagePatternKeys.SQM, metadata.WeatherData.SkyQuality);
            }

            if (!string.IsNullOrEmpty(metadata.Camera.ReadoutModeName)) {
                p.Set(ImagePatternKeys.ReadoutMode, metadata.Camera.ReadoutModeName);
            }

            if (!string.IsNullOrEmpty(metadata.Camera.Name)) {
                p.Set(ImagePatternKeys.Camera, metadata.Camera.Name);
            }

            if (!string.IsNullOrEmpty(metadata.Telescope.Name)) {
                p.Set(ImagePatternKeys.Telescope, metadata.Telescope.Name);
            }

            if (!double.IsNaN(metadata.Rotator.MechanicalPosition)) {
                p.Set(ImagePatternKeys.RotatorAngle, metadata.Rotator.MechanicalPosition);
            }

            if (StarDetectionAnalysis.DetectedStars >= 0) {
                p.Set(ImagePatternKeys.StarCount, StarDetectionAnalysis.DetectedStars);
            }

            p.Set(ImagePatternKeys.SequenceTitle, metadata.Sequence.Title);

            return p;
        }


        public async Task<string> SaveToDisk(FileSaveInfo fileSaveInfo, IList<ImagePattern> customPatterns, CancellationToken cancelToken) {
            if (customPatterns == null) { customPatterns = new List<ImagePattern>(); }
            var pattern = fileSaveInfo.FilePattern;
            string actualPath = string.Empty;
            try {
                var imagePatterns = GetImagePatterns();
                foreach (var cp in customPatterns) {
                    imagePatterns.Add(cp);
                }
                var fileName = imagePatterns.GetImageFileString(pattern);

                actualPath = await SaveToDiskAsync(fileSaveInfo, fileName, cancelToken);
                Logger.Info($"Saved image to {actualPath}");
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                Logger.Error(ex);
                throw;
            }
            return actualPath;
        }

        public Task<string> SaveToDisk(FileSaveInfo fileSaveInfo, CancellationToken cancelToken = default) {
            return SaveToDisk(fileSaveInfo, new List<ImagePattern>(), cancelToken);
        }

        private Task<string> SaveToDiskAsync(FileSaveInfo fileSaveInfo, string fileName, CancellationToken cancelToken) {
            return Task.Run(() => {
                fileSaveInfo.FilePath = Path.Combine(fileSaveInfo.FilePath, fileName);

                // Ara saves FITS and XISF only; older profiles may still carry TIFF or RAW.
                return fileSaveInfo.FileType switch {
                    FileType.FITS => SaveFits(fileSaveInfo),
                    FileType.XISF => SaveXisf(fileSaveInfo),
                    _ => throw new NotSupportedException($"Saving {fileSaveInfo.FileType} files is not supported; Ara saves FITS or XISF."),
                };
            }, cancelToken);
        }

        private static CfitsioNative.COMPRESSION GetFITSCompression(FITSCompressionType fITSCompressionTypeEnum) {
            return fITSCompressionTypeEnum switch {
                FITSCompressionType.NONE => CfitsioNative.COMPRESSION.NOCOMPRESS,
                FITSCompressionType.RICE => CfitsioNative.COMPRESSION.RICE_1,
                FITSCompressionType.PLIO => CfitsioNative.COMPRESSION.PLIO_1,
                FITSCompressionType.HCOMPRESS => CfitsioNative.COMPRESSION.HCOMPRESS_1,
                FITSCompressionType.GZIP1 => CfitsioNative.COMPRESSION.GZIP_1,
                FITSCompressionType.GZIP2 => CfitsioNative.COMPRESSION.GZIP_2,
                _ => CfitsioNative.COMPRESSION.NOCOMPRESS,
            };
        }

        private string SaveFits(FileSaveInfo fileSaveInfo) {
            string extension = ".fits";

            if (fileSaveInfo.FITSUseLegacyWriter) {
                Directory.CreateDirectory(Path.GetDirectoryName(fileSaveInfo.FilePath) ?? string.Empty);
                var uniquePath = CoreUtil.GetUniqueFilePath(fileSaveInfo.FilePath + fileSaveInfo.GetExtension(extension));
                FITS f = new FITS(
                    Data.FlatArray,
                    Properties.Width,
                    Properties.Height
                );

                f.PopulateHeaderCards(MetaData);

                using (FileStream fs = new FileStream(uniquePath, FileMode.Create)) {
                    f.Write(fs);
                }
                return uniquePath;
            } else {
                if (fileSaveInfo.FITSAddFzExtension && fileSaveInfo.FITSCompressionType != FITSCompressionType.NONE) {
                    extension += ".fz";
                }

                // Filename policy kept from NINA: brackets become "_". CFITSIO no longer needs it
                // (fits_create_file binds the disk-file entry point ffdkinit, which takes the name
                // literally), so this only keeps existing folder names stable.
                fileSaveInfo.FilePath = fileSaveInfo.FilePath.Replace("(", "_", StringComparison.Ordinal).Replace(")", "_", StringComparison.Ordinal).Replace("[", "_", StringComparison.Ordinal).Replace("]", "_", StringComparison.Ordinal);
                Directory.CreateDirectory(Path.GetDirectoryName(fileSaveInfo.FilePath) ?? string.Empty);

                var uniquePath = CoreUtil.GetUniqueFilePath(fileSaveInfo.FilePath + fileSaveInfo.GetExtension(extension), "{0}_{1}");

                var compression = GetFITSCompression(fileSaveInfo.FITSCompressionType);

                CFitsioFITS? f = null;
                try {
                    if (Data.FlatArrayInt != null) {
                        f = new CFitsioFITS(uniquePath, Data.FlatArrayInt, Properties.Width, Properties.Height, compression);
                    } else {
                        f = new CFitsioFITS(uniquePath, Data.FlatArray, Properties.Width, Properties.Height, compression);
                    }
                    f.PopulateHeaderCards(MetaData);
                } finally {
                    f?.Close();
                }
                return uniquePath;
            }
        }

        private string SaveXisf(FileSaveInfo fileSaveInfo) {
            XISFHeader header = new XISFHeader();

            var sampleFormat = Data.FlatArrayInt != null ? XISFSampleFormat.UInt32 : XISFSampleFormat.UInt16;
            header.AddImageMetaData(Properties, MetaData.Image.ImageType, sampleFormat);

            header.Populate(MetaData);

            XISF img = new XISF(header);

            if (Data.FlatArrayInt != null) {
                img.AddAttachedImageInt(Data.FlatArrayInt, Properties.BitDepth, fileSaveInfo);
            } else {
                img.AddAttachedImage(Data.FlatArray, fileSaveInfo);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fileSaveInfo.FilePath) ?? string.Empty);
            string uniquePath = CoreUtil.GetUniqueFilePath(fileSaveInfo.FilePath + fileSaveInfo.GetExtension(".xisf"));

            using (FileStream fs = new FileStream(uniquePath, FileMode.Create)) {
                img.Save(fs);
            }

            return uniquePath;
        }

        #endregion "Save"

        #region "Load"

        /// <summary>
        /// Loads an image from a given file path
        /// </summary>
        /// <param name="path">File Path to image</param>
        /// <param name="bitDepth">bit depth of each pixel</param>
        /// <param name="isBayered">Flag to indicate if the image is bayer matrix encoded</param>
        /// <param name="ct">Token to cancel operation</param>
        /// <returns></returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Lowercasing ASCII file-format tokens (XISF codec/checksum names, file extensions, EXIF tags) to match lowercase identifiers; not a security decision.")]
        public static Task<IImageData> FromFile(string path, int bitDepth, bool isBayered, IImageDataFactory imageDataFactory, CancellationToken ct = default) {
            return Task.Run(async () => {
                if (!File.Exists(path)) {
                    throw new FileNotFoundException();
                }
                switch (Path.GetExtension(path).ToLowerInvariant()) {
                    case ".xisf":
                        return await XISF.Load(new Uri(path), isBayered, imageDataFactory, ct);

                    case ".fit":
                    case ".fits":
                    case ".fts":
                    case ".fz":
                        return await FITS.Load(new Uri(path), isBayered, imageDataFactory, ct);

                    default:
                        // Ara loads FITS and XISF only. DSLR RAW is decoded by
                        // AlpacaBridge, which hands Ara a plain ImageArray.
                        throw new NotSupportedException($"File format {Path.GetExtension(path)} is not supported; Ara loads FITS or XISF.");
                }
            }, ct);
        }

        public static bool FileIsSupported(string path) {
            if (!File.Exists(path)) {
                throw new FileNotFoundException();
            }

            // FITS and XISF only; see FromFile.
            var supportedExtensions = new Regex(@".*\.(xisf|fits?|fz|fts)", RegexOptions.IgnoreCase);
            return supportedExtensions.IsMatch(path);
        }

        #endregion "Load"
    }

    public class ImageDataFactory : IImageDataFactory {
        private protected readonly IProfileService profileService;
        private protected readonly IPluggableBehaviorSelector<IStarDetection> starDetectionSelector;
        private protected readonly IPluggableBehaviorSelector<IStarAnnotator> starAnnotatorSelector;

        public ImageDataFactory(IProfileService profileService, IPluggableBehaviorSelector<IStarDetection> starDetectionSelector, IPluggableBehaviorSelector<IStarAnnotator> starAnnotatorSelector) {
            this.profileService = profileService;
            this.starDetectionSelector = starDetectionSelector;
            this.starAnnotatorSelector = starAnnotatorSelector;
        }

        public BaseImageData CreateBaseImageData(ushort[] input, int width, int height, int bitDepth, bool isBayered, ImageMetaData metaData) {
            return new BaseImageData(input, width, height, bitDepth, isBayered, metaData, this.profileService, this.starDetectionSelector.GetBehavior(), this.starAnnotatorSelector.GetBehavior());
        }

        public BaseImageData CreateBaseImageData(IImageArray imageArray, int width, int height, int bitDepth, bool isBayered, ImageMetaData metaData) {
            return new BaseImageData(imageArray, width, height, bitDepth, isBayered, metaData, this.profileService, this.starDetectionSelector.GetBehavior(), this.starAnnotatorSelector.GetBehavior());
        }

        public Task<IImageData> CreateFromFile(string path, int bitDepth, bool isBayered, CancellationToken ct = default) {
            return BaseImageData.FromFile(path, bitDepth, isBayered, this, ct);
        }
    }
}