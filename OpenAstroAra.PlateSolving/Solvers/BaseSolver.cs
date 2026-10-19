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
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.PlateSolving.Interfaces;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.PlateSolving.Solvers {

    internal abstract class BaseSolver : IPlateSolver {
        // #1136 — the frame the solver reads is written once per solve; on an SBC booting from an SD
        // card that write is a measurable slice of a hinted solve, so the working directory prefers the
        // host's RAM-backed /dev/shm when it is there and writable. Failed frames still land on disk.
        protected static readonly string WORKING_DIRECTORY = ResolveWorkingDirectory();
        protected static readonly string FAILED_DIRECTORY = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "PlateSolver", "Failed");

        /// <summary>Candidate RAM-backed root, overridable for tests; null disables the tmpfs preference.</summary>
        internal static string? SharedMemoryRoot { get; set; } = OperatingSystem.IsLinux() ? "/dev/shm" : null;

        internal static string ResolveWorkingDirectory() {
            var disk = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "PlateSolver");
            var shm = SharedMemoryRoot;
            if (shm is null || !Directory.Exists(shm)) {
                return disk;
            }
            var candidate = Path.Combine(shm, "openastroara", "PlateSolver");
            try {
                // /dev/shm is shared between every local user; keep the frames private to the service.
                if (OperatingSystem.IsWindows()) {
                    Directory.CreateDirectory(candidate);
                } else {
                    const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                    Directory.CreateDirectory(Path.Combine(shm, "openastroara"), ownerOnly);
                    Directory.CreateDirectory(candidate, ownerOnly);
                }
                var probe = Path.Combine(candidate, $".probe-{Environment.ProcessId}");
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);
                return candidate;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return disk;
            }
        }
        protected static readonly string FAILED_FILENAME =
            $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.{Environment.ProcessId}";

        static BaseSolver() {
            CreateOrCleanupDirectory(WORKING_DIRECTORY);
            CreateOrCleanupDirectory(FAILED_DIRECTORY);
        }

        private static void CreateOrCleanupDirectory(string path) {
            if (!Directory.Exists(path)) {
                try {
                    Directory.CreateDirectory(path);
                } catch (IOException ex) {
                    Logger.Error(ex);
                } catch (UnauthorizedAccessException ex) {
                    Logger.Error(ex);
                }
            } else {
                CoreUtil.DirectoryCleanup(path, TimeSpan.FromDays(-7));
            }

        }

        public async Task<PlateSolveResult> SolveAsync(IImageData source, PlateSolveParameter parameter, IProgress<ApplicationStatus>? progress, CancellationToken canceltoken) {
            EnsureSolverValid(parameter);
            var imageProperties = PlateSolveImageProperties.Create(parameter, source);
            return await SolveAsyncImpl(source, parameter, imageProperties, progress, canceltoken);
        }

        protected abstract Task<PlateSolveResult> SolveAsyncImpl(
            IImageData source,
            PlateSolveParameter parameter,
            PlateSolveImageProperties imageProperties,
            IProgress<ApplicationStatus>? progress,
            CancellationToken canceltoken);

        protected virtual void EnsureSolverValid(PlateSolveParameter parameter) {
        }
    }
}