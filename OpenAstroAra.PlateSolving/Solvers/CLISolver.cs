#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Locale;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Core.Utility.Extensions;
using OpenAstroAra.Image.FileFormat;
using OpenAstroAra.Image.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.PlateSolving.Solvers {

    internal abstract class CLISolver : BaseSolver {
        protected string executableLocation;

        public CLISolver(string executableLocation) {
            this.executableLocation = executableLocation;
        }

        protected abstract string GetLocalizedPlateSolverName();

        protected abstract string GetArguments(
            string imageFilePath,
            string outputFilePath,
            PlateSolveParameter parameter,
            PlateSolveImageProperties imageProperties);

        protected abstract PlateSolveResult ReadResult(
            string outputFilePath,
            PlateSolveParameter parameter,
            PlateSolveImageProperties imageProperties);

        protected override async Task<PlateSolveResult> SolveAsyncImpl(
            IImageData source,
            PlateSolveParameter parameter,
            PlateSolveImageProperties imageProperties,
            IProgress<ApplicationStatus>? progress,
            CancellationToken cancelToken) {
            var result = new PlateSolveResult() { Success = false };
            string? imagePath = null, outputPath = null;
            try {
                // Update target coordinates
                if (source.MetaData.Target.Coordinates == null || double.IsNaN(source.MetaData.Target.Coordinates.RA))
                    source.MetaData.Target.Coordinates = source.MetaData.Telescope.Coordinates;
                // Copy Image to local app data
                imagePath = await PrepareAndSaveImage(source, cancelToken);

                progress?.Report(new ApplicationStatus() { Status = Loc.Instance["LblSolving"] });

                outputPath = GetOutputPath(imagePath);

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken)) {
                    cts.CancelAfter(TimeSpan.FromMinutes(10));
                    // Pass the linked token so the 10-minute timeout actually cancels the solve.
                    await StartCLI(imagePath, outputPath, parameter, imageProperties, progress, cts.Token);
                }

                //Extract solution coordinates
                result = ReadResult(outputPath, parameter, imageProperties);
            } catch (OperationCanceledException) {
                if (!cancelToken.IsCancellationRequested) {
                    Logger.Error("Platesolver timed out after 10 minutes");
                }
            } finally {
                progress?.Report(new ApplicationStatus() { Status = string.Empty });

                var filePrefix = FAILED_FILENAME;
                if (!string.IsNullOrWhiteSpace(source?.MetaData?.Target?.Name)) {
                    filePrefix += $".{CoreUtil.ReplaceAllInvalidFilenameChars(source.MetaData.Target.Name)}";
                }
                if (parameter.Coordinates == null) {
                    filePrefix += ".blind";
                }

                if (imagePath != null && File.Exists(imagePath)) {
                    MoveOrDeleteFile(result, imagePath, filePrefix, cancelToken);
                }

                if (outputPath != null && File.Exists(outputPath)) {
                    MoveOrDeleteFile(result, outputPath, filePrefix, cancelToken);
                }

                foreach (var file in GetSideCarFilePaths(imagePath ?? string.Empty)) {
                    MoveOrDeleteFile(result, file, filePrefix, cancelToken);
                }
            }
            return result;
        }

        private static void MoveOrDeleteFile(PlateSolveResult result, string file, string movedFilePrefix, CancellationToken cancelToken) {
            try {
                if (!result.Success && !cancelToken.IsCancellationRequested) {
                    if (File.Exists(file)) {
                        var destination = Path.Combine(FAILED_DIRECTORY, $"{movedFilePrefix}.{Path.GetExtension(file)}");
                        if (File.Exists(destination)) {
                            File.Delete(destination);
                        }
                        File.Move(file, destination);
                    }
                } else {
                    File.Delete(file);
                }
            } catch (IOException ex) {
                Logger.Error(ex);
            } catch (UnauthorizedAccessException ex) {
                Logger.Error(ex);
            }
        }

        protected static async Task<string> PrepareAndSaveImage(IImageData source, CancellationToken cancelToken) {
            FileSaveInfo fileSaveInfo = new FileSaveInfo {
                FilePath = WORKING_DIRECTORY,
                FilePattern = Path.GetRandomFileName(),
                FileType = FileType.FITS
            };

            return await source.SaveToDisk(fileSaveInfo, forceFileType: true, cancelToken: cancelToken);
        }

        protected abstract string GetOutputPath(string imageFilePath);

        /// <summary>
        /// Some solvers create more files than the result output path.
        /// Return a list of paths to those sidecar files be deleted.
        /// </summary>
        /// <param name="imageFilePath"></param>
        /// <returns></returns>
        protected virtual List<string> GetSideCarFilePaths(string imageFilePath) {
            return new List<string>();
        }

        protected async Task StartCLI(string imageFilePath, string outputFilePath, PlateSolveParameter parameter, PlateSolveImageProperties imageProperties, IProgress<ApplicationStatus>? progress, CancellationToken ct) {
            if (executableLocation != "cmd.exe" && !File.Exists(executableLocation)) {
                throw new FileNotFoundException("Platesolver executable not found. Please point to the correct platesolver executable in platsolving options.", executableLocation);
            }

            using var process = new System.Diagnostics.Process();
            System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo();

            startInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal;
            startInfo.FileName = executableLocation;
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.CreateNoWindow = true;
            startInfo.Arguments = GetArguments(imageFilePath, outputFilePath, parameter, imageProperties);
            process.StartInfo = startInfo;
            process.EnableRaisingEvents = true;

            process.OutputDataReceived += (object sender, System.Diagnostics.DataReceivedEventArgs e) => {
                progress?.Report(new ApplicationStatus() { Status = e.Data ?? string.Empty });
                Logger.Debug(e.Data ?? string.Empty);
            };

            process.ErrorDataReceived += (object sender, System.Diagnostics.DataReceivedEventArgs e) => {
                progress?.Report(new ApplicationStatus() { Status = e.Data ?? string.Empty });
                Logger.Error(e.Data ?? string.Empty);
            };
            Logger.Debug($"Starting process '{executableLocation}' with args '{startInfo.Arguments}'");
            process.Start();
            try {
                await process.WaitForExitAsync(ct);
            } catch (OperationCanceledException) {
                // Timeout or caller cancellation: kill the solver (and its children) before propagating.
                try {
                    if (!process.HasExited) {
                        process.Kill(entireProcessTree: true);
                    }
                } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) {
                    Logger.Error(ex);
                }
                throw;
            }
            // §42.2 row 14 — a solver crash used to be indistinguishable from a clean no-solution
            // (success was inferred solely from the sidecar file). Surface the exit code so the
            // generic solve-retry loop's failures are diagnosable; solver subclasses map their
            // documented codes (ASTAP: 1 = clean no-solution, 32/33 = missing star database, ...).
            var exitCode = process.ExitCode;
            if (exitCode != 0) {
                Logger.Warning($"Plate solver '{Path.GetFileName(executableLocation)}' exited with code {exitCode}{DescribeExitCode(exitCode)}");
            }
        }

        /// <summary>Solver-specific meaning of a non-zero exit code, appended to the exit-code
        /// warning (e.g. ASTAP's documented codes). Empty when the code isn't recognized.</summary>
        protected virtual string DescribeExitCode(int exitCode) => string.Empty;
    }
}