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
            // #1188 — both streams are redirected and read asynchronously. Before, stdout was
            // redirected but never read (no BeginOutputReadLine), so the handlers below never fired
            // and a solver writing more than the pipe buffer (64 KB) blocked on write until the solve
            // timeout killed it.
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.CreateNoWindow = true;
            startInfo.Arguments = GetArguments(imageFilePath, outputFilePath, parameter, imageProperties);
            process.StartInfo = startInfo;
            process.EnableRaisingEvents = true;

            // A null line is end-of-stream; the solve result is only read once both have closed.
            var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tail = new Queue<string>();

            process.OutputDataReceived += (object sender, System.Diagnostics.DataReceivedEventArgs e) =>
                ReceiveOutputLine(e.Data, false, stdoutClosed, tail, progress);
            process.ErrorDataReceived += (object sender, System.Diagnostics.DataReceivedEventArgs e) =>
                ReceiveOutputLine(e.Data, true, stderrClosed, tail, progress);
            Logger.Debug($"Starting process '{executableLocation}' with args '{startInfo.Arguments}'");
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try {
                await process.WaitForExitAsync(ct);
                // WaitForExitAsync already waits for the async readers to hit end-of-stream; waiting
                // on our own markers as well keeps "every line was handled before the result is read"
                // explicit rather than an implementation detail of Process.
                await Task.WhenAll(stdoutClosed.Task, stderrClosed.Task).WaitAsync(ct);
            } catch (OperationCanceledException) {
                // Timeout or caller cancellation: kill the solver AND its children before propagating.
                // Unconditionally (#1219): a solver that has exited while a grandchild still holds the
                // pipe is exactly the case the tree kill is for, and Kill on an exited root is a no-op.
                try {
                    process.Kill(entireProcessTree: true);
                } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) {
                    Logger.Error(ex);
                }
                throw;
            } finally {
                // Stop the async readers before the process is disposed (#1219): a reader still
                // attached to a pipe a grandchild holds would otherwise outlive the Process object.
                CancelReadQuietly(process.CancelOutputRead);
                CancelReadQuietly(process.CancelErrorRead);
            }
            // §42.2 row 14 — a solver crash used to be indistinguishable from a clean no-solution
            // (success was inferred solely from the sidecar file). Surface the exit code so the
            // generic solve-retry loop's failures are diagnosable; solver subclasses map their
            // documented codes (ASTAP: 1 = clean no-solution, 32/33 = missing star database, ...).
            // The last lines the solver printed go with it, since per-line output is Debug only.
            var exitCode = process.ExitCode;
            if (exitCode != 0) {
                string lastOutput;
                lock (tail) {
                    LastExitTail = tail.ToArray();
                    lastOutput = tail.Count == 0 ? string.Empty : $"; last output:{Environment.NewLine}{string.Join(Environment.NewLine, tail)}";
                }
                Logger.Warning($"Plate solver '{Path.GetFileName(executableLocation)}' exited with code {exitCode}{DescribeExitCode(exitCode)}{lastOutput}");
            }
        }

        /// <summary>The last <see cref="OutputTailLines"/> lines the solver printed before a non-zero
        /// exit (stderr lines prefixed <c>[stderr]</c>), as carried by the exit warning; null until a
        /// run ends that way. Exposed for subclasses and tests (#1219).</summary>
        protected IReadOnlyList<string>? LastExitTail { get; private set; }

        // CancelOutputRead/CancelErrorRead throw InvalidOperationException when the matching
        // Begin*ReadLine never ran (a Start that failed); nothing to stop then.
        private static void CancelReadQuietly(Action cancel) {
            try {
                cancel();
            } catch (InvalidOperationException) {
                // reader was never started
            }
        }

        /// <summary>How many of the solver's last output lines the non-zero-exit warning carries.</summary>
        private const int OutputTailLines = 20;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Process output callback on a thread-pool reader thread: an exception escaping it (a failing progress sink) is unhandled and process-fatal, so it is logged and the solve carries on.")]
        private void ReceiveOutputLine(string? line, bool stdErr, TaskCompletionSource closed, Queue<string> tail, IProgress<ApplicationStatus>? progress) {
            if (line == null) {
                closed.TrySetResult();
                return;
            }
            if (string.IsNullOrWhiteSpace(line)) {
                return;
            }
            lock (tail) {
                tail.Enqueue(stdErr ? $"[stderr] {line}" : line);
                while (tail.Count > OutputTailLines) {
                    tail.Dequeue();
                }
            }
            try {
                OnSolverOutput(line, stdErr, progress);
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        /// <summary>Solver-specific meaning of a non-zero exit code, appended to the exit-code
        /// warning (e.g. ASTAP's documented codes). Empty when the code isn't recognized.</summary>
        protected virtual string DescribeExitCode(int exitCode) => string.Empty;

        /// <summary>One non-blank line of solver output, stdout or stderr, called on a reader thread
        /// (the two streams can call concurrently). The line becomes the solve's progress status and
        /// is logged at Debug. stderr used to be logged at Error, but CLI solvers print routine
        /// progress and diagnostics there, so a successful solve would have filled the log with
        /// errors; a failing solve surfaces the last lines at Warning with its exit code instead.
        /// Note (#1219): with the file sink at Debug every line is a synchronous log write on the
        /// pipe-drain thread; a verbose solver under DEBUG logging is slowed by its own output.</summary>
        protected virtual void OnSolverOutput(string line, bool stdErr, IProgress<ApplicationStatus>? progress) {
            progress?.Report(new ApplicationStatus() { Status = line });
            Logger.Debug(stdErr ? $"[stderr] {line}" : line);
        }
    }
}