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
using OpenAstroAra.Core.Enum;
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
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace OpenAstroAra.Image.ImageData {

    public partial class BaseImageData : IImageData {
        protected readonly IProfileService profileService;
        protected readonly IStarDetection starDetection;
        protected readonly IStarAnnotator starAnnotator;

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
            // StarDetectionAnalysis populated when OpenCvSharp4-backed
            // IStarDetection lands per playbook §line-2105.
            StarDetectionAnalysis = null!;
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
            // ImageUtility.CreateSourceFromArray (WPF BitmapSource pipeline)
            // pending OpenCvSharp4 replacement per playbook §line-2105.
            throw new NotImplementedException("RenderBitmapSource pending OpenCvSharp4 wiring.");
        }

        public void SetImageStatistics(IImageStatistics imageStatistics) {
            Statistics = new Nito.AsyncEx.AsyncLazy<IImageStatistics>(() => Task.FromResult(imageStatistics));
        }

        #region "Save"

        [Obsolete]
        /// <summary>
        ///  Saves file to application temp path
        /// </summary>
        /// <param name="fileType"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        public async Task<string> PrepareSave(FileSaveInfo fileSaveInfo, CancellationToken cancelToken = default) {
            var actualPath = string.Empty;
            try {
                using (MyStopWatch.Measure()) {
                    // Reference: https://devblogs.microsoft.com/premier-developer/the-danger-of-taskcompletionsourcet-class/
                    var cancelTaskSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (cancelToken.Register(() => cancelTaskSource.SetCanceled())) {
                        var saveTask = SaveToDiskAsync(fileSaveInfo, Guid.NewGuid().ToString(), cancelToken, false);
                        await Task.WhenAny(cancelTaskSource.Task, saveTask);
                        cancelToken.ThrowIfCancellationRequested();
                        actualPath = saveTask.Result;
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

        [Obsolete]
        /// <summary>
        /// Renames and moves file to destination according to pattern
        /// </summary>
        /// <param name="file"></param>
        /// <param name="pattern"></param>
        /// <param name="customPatterns"></param>
        /// <returns></returns>        
        public string FinalizeSave(string file, string pattern, IList<ImagePattern> customPatterns) {
            try {
                if (pattern.Contains(ImagePatternKeys.SensorTemp) && double.IsNaN(MetaData.Camera.Temperature) && !string.IsNullOrEmpty(Data.RAWType)) {
                    string sensorTemp = GetSensorTempFromExifTool(file);
                    pattern = pattern.Replace(ImagePatternKeys.SensorTemp, sensorTemp);
                }

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

        private string GetSensorTempFromExifTool(string file) {
            string tempString = string.Empty;
            try {
                string EXIFTOOLLOCATION = Path.Combine(CoreUtil.APPLICATIONDIRECTORY, "Utility", "ExifTool", "exiftool.exe");
                var sb = new StringBuilder();

                System.Diagnostics.Process process = new System.Diagnostics.Process();
                System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo();
                startInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                startInfo.FileName = EXIFTOOLLOCATION;
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.RedirectStandardInput = true;
                startInfo.CreateNoWindow = true;
                startInfo.Arguments = $"-CameraTemperature \"{file}\"";
                process.StartInfo = startInfo;
                process.EnableRaisingEvents = true;

                process.OutputDataReceived += (sender, e) => {
                    sb.AppendLine(e.Data);
                };

                process.ErrorDataReceived += (sender, e) => {
                    sb.AppendLine(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                process.WaitForExit();

                Logger.Trace(sb.ToString());

                // remove whitespace and format
                tempString = sb.ToString().Replace(" ", "");
                tempString = tempString.Substring(tempString.IndexOf(':') + 1).ToLower().Trim();

                if (!Regex.IsMatch(tempString, "^[0-9]{1,4}[cCfFkK]$")) {
                    Logger.Error($"Value returned by EXIF Tool is no valid temperature: {tempString}");
                    tempString = string.Empty;
                }
            } catch (Exception ex) {
                Logger.Error(ex);
            }

            return tempString;
        }

        public ImagePatterns GetImagePatterns() {
            var p = new ImagePatterns();
            var metadata = MetaData;
            p.Set(ImagePatternKeys.Filter, metadata.FilterWheel.Filter);
            p.Set(ImagePatternKeys.ExposureTime, metadata.Image.ExposureTime);
            p.Set(ImagePatternKeys.ApplicationStartDate, CoreUtil.ApplicationStartDate.ToString("yyyy-MM-dd"));
            p.Set(ImagePatternKeys.Date, metadata.Image.ExposureStart.ToLocalTime().ToString("yyyy-MM-dd"));

            // ExposureStart is initialized to DateTime.MinValue, and we cannot subtract time from that. Only evaluate
            // the $$DATEMINUS12$$ pattern if the time is at least 12 hours on from DateTime.MinValue.
            if (metadata.Image.ExposureStart > DateTime.MinValue.AddHours(12)) {
                p.Set(ImagePatternKeys.DateMinus12, metadata.Image.ExposureStart.ToLocalTime().AddHours(-12).ToString("yyyy-MM-dd"));
            }

            p.Set(ImagePatternKeys.DateUtc, metadata.Image.ExposureStart.ToUniversalTime().ToString("yyyy-MM-dd"));
            p.Set(ImagePatternKeys.Time, metadata.Image.ExposureStart.ToLocalTime().ToString("HH-mm-ss"));
            p.Set(ImagePatternKeys.TimeUtc, metadata.Image.ExposureStart.ToUniversalTime().ToString("HH-mm-ss"));
            p.Set(ImagePatternKeys.DateTime, metadata.Image.ExposureStart.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss"));
            p.Set(ImagePatternKeys.MJD, metadata.Image.ExposureStart.ToMJD(), precision: 8);
            p.Set(ImagePatternKeys.FrameNr, metadata.Image.ExposureNumber.ToString("0000"));
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


        public async Task<string> SaveToDisk(FileSaveInfo fileSaveInfo, CancellationToken token, bool forceFileType, IList<ImagePattern> customPatterns) {
            if (customPatterns == null) { customPatterns = new List<ImagePattern>(); }
            var pattern = fileSaveInfo.FilePattern;
            string actualPath = string.Empty;
            try {
                if (pattern.Contains(ImagePatternKeys.SensorTemp) && double.IsNaN(MetaData.Camera.Temperature) && !string.IsNullOrEmpty(Data.RAWType)) {
                    // For DSLRs we need to retrieve the temperature after the file is written. Hence we replace the pattern with this special placeholder
                    pattern = pattern.Replace(ImagePatternKeys.SensorTemp, "$$DSLR_SENSORTEMP$$");
                }

                var imagePatterns = GetImagePatterns();
                foreach (var cp in customPatterns) {
                    imagePatterns.Add(cp);
                }
                var fileName = imagePatterns.GetImageFileString(pattern);

                actualPath = await SaveToDiskAsync(fileSaveInfo, fileName, token, forceFileType);

                if (pattern.Contains("$$DSLR_SENSORTEMP$$") && double.IsNaN(MetaData.Camera.Temperature) && !string.IsNullOrEmpty(Data.RAWType)) {
                    // Extract the temperature from the EXIF info for DSLRs
                    actualPath = ExtractDSLRTemperatureAndMoveFile(actualPath);
                }
                Logger.Info($"Saved image to {actualPath}");
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                Logger.Error(ex);
                throw;
            }
            return actualPath;
        }

        private string ExtractDSLRTemperatureAndMoveFile(string actualPath) {
            var oldPath = actualPath;
            try {
                string sensorTemp = GetSensorTempFromExifTool(actualPath);
                actualPath = actualPath.Replace("$$DSLR_SENSORTEMP$$", sensorTemp.ToString(CultureInfo.InvariantCulture));

                // Create a folder in case this pattern was set up to be a folder
                var fi = new FileInfo(actualPath);
                if (fi.Directory != null && !fi.Directory.Exists) {
                    fi.Directory.Create();
                }

                File.Move(oldPath, actualPath);
            } catch (Exception ex) {
                // In case of any error point to the original file
                actualPath = oldPath;
                Logger.Error(ex);

            }
            return actualPath;
        }


        public Task<string> SaveToDisk(FileSaveInfo fileSaveInfo, CancellationToken token, bool forceFileType = false) {
            return SaveToDisk(fileSaveInfo, token, forceFileType, new List<ImagePattern>());
        }

        private Task<string> SaveToDiskAsync(FileSaveInfo fileSaveInfo, string fileName, CancellationToken cancelToken, bool forceFileType = false) {
            return Task.Run(() => {
                string path = string.Empty;
                fileSaveInfo.FilePath = Path.Combine(fileSaveInfo.FilePath, fileName);

                if (!forceFileType && Data.RAWData != null) {
                    fileSaveInfo.FileType = FileType.RAW;
                    path = SaveRAW(fileSaveInfo.FilePath);
                } else {
                    switch (fileSaveInfo.FileType) {
                        case FileType.FITS:
                            path = SaveFits(fileSaveInfo);
                            break;

                        case FileType.XISF:
                            path = SaveXisf(fileSaveInfo);
                            break;

                        case FileType.TIFF:
                        default:
                            path = SaveTiff(fileSaveInfo);
                            break;
                    }
                }

                return path;
            }, cancelToken);
        }

        private string SaveRAW(string path) {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
            IImageArray data = Data;
            if (data.RAWData == null) {
                throw new InvalidOperationException("Image has no RAW data to save.");
            }
            string uniquePath = CoreUtil.GetUniqueFilePath(path + "." + data.RAWType);
            File.WriteAllBytes(uniquePath, data.RAWData);
            return uniquePath;
        }

        private string SaveTiff(FileSaveInfo fileSaveInfo) {
            // SaveTiff pending OpenCvSharp4 / SkiaSharp TIFF encoder per
            // playbook §line-2105 — the WPF TiffBitmapEncoder pipeline was
            // deleted in the net10.0 conversion. The headless daemon's
            // primary capture path is FITS via OpenAstroAra.Fits; TIFF export
            // arrives in a follow-up.
            throw new NotImplementedException("SaveTiff pending OpenCvSharp4 wiring.");
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

                // CFitsio treats paranthesis for special logic and are thus not allowed
                fileSaveInfo.FilePath = fileSaveInfo.FilePath.Replace("(", "_").Replace(")", "_").Replace("[", "_").Replace("]", "_");
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
                img.AddAttachedImageInt(Data.FlatArrayInt, fileSaveInfo);
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
        /// <param name="rawConverter">Which type of raw converter to use, when image is in RAW format</param>
        /// <param name="ct">Token to cancel operation</param>
        /// <returns></returns>
        public static Task<IImageData> FromFile(string path, int bitDepth, bool isBayered, object? rawConverter, IImageDataFactory imageDataFactory, CancellationToken ct = default) {
            return Task.Run(async () => {
                if (!File.Exists(path)) {
                    throw new FileNotFoundException();
                }
                switch (Path.GetExtension(path).ToLower()) {
                    case ".xisf":
                        return await XISF.Load(new Uri(path), isBayered, imageDataFactory, ct);

                    case ".fit":
                    case ".fits":
                    case ".fts":
                    case ".fz":
                        return await FITS.Load(new Uri(path), isBayered, imageDataFactory, ct);

                    default:
                        // Non-FITS / non-XISF formats (gif/tiff/jpg/png/cr2/etc.)
                        // pending OpenCvSharp4 + libraw integration per playbook
                        // §line-2105 — the previous WPF BitmapDecoder + DCRaw
                        // pipeline was deleted in the net10.0 conversion.
                        throw new NotSupportedException($"File format {Path.GetExtension(path)} pending OpenCvSharp4 wiring.");
                }
            }, ct);
        }

        public static bool FileIsSupported(string path) {
            if (!File.Exists(path)) {
                throw new FileNotFoundException();
            }

            // Until OpenCvSharp4 lands, only FITS + XISF are supported headless.
            var supportedExtensions = new Regex(@".*\.(xisf|fits?|fz|fts)", RegexOptions.IgnoreCase);
            return supportedExtensions.IsMatch(path);
        }

        // BitmapToImageArray (WPF BitmapDecoder + FormatConvertedBitmap) deleted;
        // replacement lands with OpenCvSharp4 wiring per playbook §line-2105.

        #endregion "Load"
    }

    public class ImageDataFactory : IImageDataFactory {
        protected readonly IProfileService profileService;
        protected readonly IPluggableBehaviorSelector<IStarDetection> starDetectionSelector;
        protected readonly IPluggableBehaviorSelector<IStarAnnotator> starAnnotatorSelector;

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

        public Task<IImageData> CreateFromFile(string path, int bitDepth, bool isBayered, RawConverter rawConverter, CancellationToken ct = default) {
            // RawConverterFactory deleted in the net10.0 conversion; the
            // rawConverter param is ignored until libraw replaces DCRaw per
            // playbook §line-2105.
            return BaseImageData.FromFile(path, bitDepth, isBayered, null, this, ct);
        }
    }
}