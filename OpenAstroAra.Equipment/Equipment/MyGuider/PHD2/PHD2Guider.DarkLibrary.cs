#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Utility;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    // §63.6 (guider-e-4b) — drive the guider's dark-library / calibration-files surface from the connected
    // guider. The wire shapes were locked in guider-e-4a (PHD2Methods.DarkLibrary.cs); this adds the actual
    // RPC invocation + send-site validation. NOTE: build_dark_library is a BLOCKING RPC — the guider runs the
    // whole capture stack and only answers when the library is written; it emits NO dark-library-progress
    // event on the §63.2 event stream. So the service layer (e-4b-2) runs this on a background task and reports
    // started/complete, NOT a granular progress bar (the §63.8 progress-stream premise does not hold here).
    public sealed partial class PHD2Guider {

        // Daemon contract (doc/jsonrpc_api.md): frame_count 1..50; exposure bounds 1..600000 ms.
        internal const int DarkLibraryMinFrameCount = 1;
        internal const int DarkLibraryMaxFrameCount = 50;
        internal const int DarkLibraryMinExposureMs = 1;
        internal const int DarkLibraryMaxExposureMs = 600000;

        // A dark-library build captures frame_count frames at each matched exposure, so it can run for minutes.
        // The default 60 s SendMessage timeout would abort it mid-capture; give the blocking RPC room to finish.
        private const int BuildDarkLibraryTimeoutMs = 30 * 60 * 1000;

        /// <summary>
        /// Validates the §63.6 build parameters at the send site and builds the wire request. Throws
        /// <see cref="ArgumentOutOfRangeException"/> for an out-of-range frame count or exposure bound, and
        /// <see cref="ArgumentException"/> for a contradictory <c>min &gt; max</c> pair — surfaced before the
        /// socket so the caller gets a clear error rather than the daemon's opaque <c>-32602</c>.
        /// </summary>
        public static Phd2BuildDarkLibrary BuildDarkLibraryRequest(
            int frameCount, int? minExposureMs, int? maxExposureMs, bool clearExisting, string? notes, bool loadAfter) {
            if (frameCount is < DarkLibraryMinFrameCount or > DarkLibraryMaxFrameCount) {
                throw new ArgumentOutOfRangeException(nameof(frameCount), frameCount,
                    $"frame_count must be {DarkLibraryMinFrameCount}..{DarkLibraryMaxFrameCount}.");
            }
            ValidateExposureBound(minExposureMs, nameof(minExposureMs));
            ValidateExposureBound(maxExposureMs, nameof(maxExposureMs));
            if (minExposureMs is int min && maxExposureMs is int max && min > max) {
                throw new ArgumentException(
                    $"min_exposure_ms ({min}) must not exceed max_exposure_ms ({max}).", nameof(minExposureMs));
            }
            return new Phd2BuildDarkLibrary {
                Parameters = new Phd2BuildDarkLibraryParameter {
                    FrameCount = frameCount,
                    MinExposureMs = minExposureMs,
                    MaxExposureMs = maxExposureMs,
                    ClearExisting = clearExisting,
                    Notes = string.IsNullOrWhiteSpace(notes) ? null : notes,
                    LoadAfter = loadAfter,
                },
            };
        }

        private static void ValidateExposureBound(int? exposureMs, string paramName) {
            if (exposureMs is int ms && ms is < DarkLibraryMinExposureMs or > DarkLibraryMaxExposureMs) {
                throw new ArgumentOutOfRangeException(paramName, ms,
                    $"exposure bounds must be {DarkLibraryMinExposureMs}..{DarkLibraryMaxExposureMs} ms.");
            }
        }

        /// <summary>
        /// §63.6 — build a dark-frame library for the active profile (blocking; requires a connected guider
        /// with a connected camera and no active capture). Returns the daemon's result (written path, frame
        /// count, captured exposure count + durations) on success; throws on a transport/protocol error.
        /// </summary>
        public async Task<Phd2BuildDarkLibraryResult> BuildDarkLibraryAsync(
            int frameCount, int? minExposureMs, int? maxExposureMs, bool clearExisting, string? notes, bool loadAfter,
            CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            // Validate before the socket so a bad request never reaches the daemon.
            var request = BuildDarkLibraryRequest(frameCount, minExposureMs, maxExposureMs, clearExisting, notes, loadAfter);
            Logger.Info(
                $"Phd2 - Building dark library (frames={frameCount}, clearExisting={clearExisting}, loadAfter={loadAfter}).");
            var response = await SendMessage<Phd2BuildDarkLibraryResponse>(request, BuildDarkLibraryTimeoutMs);
            if (response.error != null) {
                throw new GuiderRpcException("build_dark_library", response.error.code, response.error.message);
            }
            if (response.result is null) {
                throw new GuiderRpcException("build_dark_library", 0, "missing result payload");
            }
            Logger.Info(
                $"Phd2 - Dark library built at '{response.result.DarkLibraryPath}' ({response.result.ExposureCount} exposures).");
            return response.result;
        }

        /// <summary>§63.6 — read the calibration-files status (dark-library / defect-map existence, load state,
        /// paths, auto-load flags, loaded dark stats). Requires a connected guider; throws on RPC error.</summary>
        public async Task<Phd2CalibrationFilesStatus> GetCalibrationFilesStatusAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            var response = await SendMessage<Phd2CalibrationFilesStatusResponse>(new Phd2GetCalibrationFilesStatus());
            if (response.error != null) {
                throw new GuiderRpcException("get_calibration_files_status", response.error.code, response.error.message);
            }
            if (response.result is null) {
                throw new GuiderRpcException("get_calibration_files_status", 0, "missing result payload");
            }
            return response.result;
        }
    }

    /// <summary>A guider JSON-RPC call returned an error (or an empty result). Carries the daemon's method,
    /// error code, and message so the service layer can map it to a client-visible failure.</summary>
    public sealed class GuiderRpcException : Exception {
        public string RpcMethod { get; }
        public int Code { get; }

        public GuiderRpcException(string rpcMethod, int code, string? message)
            : base($"{rpcMethod} failed (code {code}): {message}") {
            RpcMethod = rpcMethod;
            Code = code;
        }

        public GuiderRpcException() { RpcMethod = string.Empty; }

        public GuiderRpcException(string message) : base(message) { RpcMethod = string.Empty; }

        public GuiderRpcException(string message, Exception innerException)
            : base(message, innerException) { RpcMethod = string.Empty; }
    }
}
