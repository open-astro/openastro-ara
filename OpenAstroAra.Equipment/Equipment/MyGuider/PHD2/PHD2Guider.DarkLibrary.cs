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
        internal const int CalibrationMinFrameCount = 1;
        internal const int CalibrationMaxFrameCount = 50;
        internal const int CalibrationMinExposureMs = 1;
        internal const int CalibrationMaxExposureMs = 600000;
        // The daemon stores notes as free text; cap them so a client can't push an unbounded string through the
        // RPC. 500 chars is generous for a human-entered build note.
        internal const int CalibrationMaxNotesLength = 500;

        // A calibration build (dark library OR defect-map darks) captures frame_count frames per exposure, so it
        // can run for a long time. Worst realistic case: 50 frames × ~5 matched exposure steps × a long per-frame
        // exposure can push past an hour (50 × 5 × 20 s ≈ 83 min). The socket ReceiveTimeout must clear that
        // ceiling, or it would fire mid-capture while the daemon keeps running, leaving the build in an unknown
        // state — so use a generous 2 h bound. (The service layer may compute a tighter, count-derived bound.)
        // NOTE: SendMessage is bounded only by this socket ReceiveTimeout — it takes no CancellationToken. So the
        // CancellationToken on the build methods guards only the *entry* (pre-dispatch); once the send starts the
        // build is effectively uninterruptible and ct.Cancel() will NOT abort it before the timeout. The service
        // layer must treat a dispatched build as run-to-completion-or-timeout, not cancellable.
        private const int CalibrationBuildTimeoutMs = 120 * 60 * 1000;

        /// <summary>
        /// Validates the §63.6 build parameters at the send site and builds the wire request. Throws
        /// <see cref="ArgumentOutOfRangeException"/> for an out-of-range frame count or exposure bound, and
        /// <see cref="ArgumentException"/> for a contradictory <c>min &gt; max</c> pair — surfaced before the
        /// socket so the caller gets a clear error rather than the daemon's opaque <c>-32602</c>.
        /// </summary>
        public static Phd2BuildDarkLibrary BuildDarkLibraryRequest(
            int frameCount, int? minExposureMs, int? maxExposureMs, bool clearExisting, string? notes, bool loadAfter) {
            if (frameCount is < CalibrationMinFrameCount or > CalibrationMaxFrameCount) {
                throw new ArgumentOutOfRangeException(nameof(frameCount), frameCount,
                    $"frame_count must be {CalibrationMinFrameCount}..{CalibrationMaxFrameCount}.");
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
                    Notes = ValidateNotes(notes, nameof(notes)),
                    LoadAfter = loadAfter,
                },
            };
        }

        /// <summary>
        /// §63.6 — validates the defect-map (bad-pixel) build parameters at the send site and builds the wire
        /// request. Throws <see cref="ArgumentOutOfRangeException"/> for an out-of-range frame count or exposure,
        /// and <see cref="ArgumentException"/> for over-long notes — surfaced before the socket so the caller gets
        /// a clear error rather than the daemon's opaque <c>-32602</c>. Shares the daemon calibration limits +
        /// notes rule with <see cref="BuildDarkLibraryRequest"/>.
        /// </summary>
        public static Phd2BuildDefectMapDarks BuildDefectMapDarksRequest(
            int exposureMs, int frameCount, string? notes, bool loadAfter) {
            if (frameCount is < CalibrationMinFrameCount or > CalibrationMaxFrameCount) {
                throw new ArgumentOutOfRangeException(nameof(frameCount), frameCount,
                    $"frame_count must be {CalibrationMinFrameCount}..{CalibrationMaxFrameCount}.");
            }
            ValidateExposureBound(exposureMs, nameof(exposureMs));
            return new Phd2BuildDefectMapDarks {
                Parameters = new Phd2BuildDefectMapDarksParameter {
                    ExposureMs = exposureMs,
                    FrameCount = frameCount,
                    Notes = ValidateNotes(notes, nameof(notes)),
                    LoadAfter = loadAfter,
                },
            };
        }

        private static void ValidateExposureBound(int? exposureMs, string paramName) {
            // Message names the caller's parameter so it's precise for both the dark-library bounds
            // (min_exposure_ms / max_exposure_ms) and the defect-map single exposure_ms.
            if (exposureMs is int ms && ms is < CalibrationMinExposureMs or > CalibrationMaxExposureMs) {
                throw new ArgumentOutOfRangeException(paramName, ms,
                    $"{paramName} must be {CalibrationMinExposureMs}..{CalibrationMaxExposureMs} ms.");
            }
        }

        /// <summary>Trim-then-bound a free-text calibration note: whitespace-only → null (so an empty note isn't
        /// sent), and a real note over <see cref="CalibrationMaxNotesLength"/> chars throws so a client can't push
        /// an unbounded string through the RPC. Shared by the dark-library + defect-map builders; the caller's
        /// <paramref name="paramName"/> is reported on the exception so it stays accurate per call site.</summary>
        private static string? ValidateNotes(string? notes, string paramName) {
            var trimmed = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            if (trimmed is not null && trimmed.Length > CalibrationMaxNotesLength) {
                throw new ArgumentException(
                    $"{paramName} must be at most {CalibrationMaxNotesLength} characters.", paramName);
            }
            return trimmed;
        }

        /// <summary>
        /// §63.6 — build a dark-frame library for the active profile (blocking; requires a connected guider
        /// with a connected camera and no active capture). Returns the daemon's result (written path, frame
        /// count, captured exposure count + durations) on success; throws on a transport/protocol error.
        /// </summary>
        /// <remarks>
        /// WARNING: <paramref name="ct"/> is honored only at entry (before dispatch). The underlying RPC send
        /// is bounded by a fixed socket timeout and takes no cancellation token, so cancelling after the build
        /// starts has NO effect — the call runs to completion or the ~2 h timeout. The e-4b-2 service layer must
        /// NOT wire a shutdown/timeout token here expecting it to abort an in-flight build.
        /// The <c>Connected</c> guard is not synchronized with the send, so a disconnect in the race window
        /// surfaces as a transport exception rather than <see cref="InvalidOperationException"/> — callers
        /// should expect either.
        /// </remarks>
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
            var response = await SendMessage<Phd2BuildDarkLibraryResponse>(request, CalibrationBuildTimeoutMs);
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

        /// <summary>
        /// §63.6 — capture a master dark and build a defect (bad-pixel) map for the active profile (blocking;
        /// requires a connected guider with a connected camera and no active capture). Returns the daemon's
        /// result (written path, flagged-defect count, exposure + frame count) on success; throws on error.
        /// </summary>
        /// <remarks>
        /// WARNING: <paramref name="ct"/> is honored only at entry (before dispatch) — the underlying RPC send is
        /// bounded by a fixed socket timeout and takes no cancellation token, so cancelling after the build starts
        /// has NO effect (runs to completion or the ~2 h timeout). The <c>Connected</c> guard is not synchronized
        /// with the send, so a disconnect in the race window surfaces as a transport exception rather than
        /// <see cref="InvalidOperationException"/> — callers should expect either. (Same contract as
        /// <see cref="BuildDarkLibraryAsync"/>.)
        /// </remarks>
        public async Task<Phd2BuildDefectMapDarksResult> BuildDefectMapDarksAsync(
            int exposureMs, int frameCount, string? notes, bool loadAfter, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            // Validate before the socket so a bad request never reaches the daemon.
            var request = BuildDefectMapDarksRequest(exposureMs, frameCount, notes, loadAfter);
            Logger.Info(
                $"Phd2 - Building defect map (exposureMs={exposureMs}, frames={frameCount}, loadAfter={loadAfter}).");
            var response = await SendMessage<Phd2BuildDefectMapDarksResponse>(request, CalibrationBuildTimeoutMs);
            if (response.error != null) {
                throw new GuiderRpcException("build_defect_map_darks", response.error.code, response.error.message);
            }
            if (response.result is null) {
                throw new GuiderRpcException("build_defect_map_darks", 0, "missing result payload");
            }
            Logger.Info(
                $"Phd2 - Defect map built at '{response.result.DefectMapPath}' ({response.result.DefectCount} defects).");
            return response.result;
        }

        /// <summary>§63.6 — read the calibration-files status (dark-library / defect-map existence, load state,
        /// paths, auto-load flags, loaded dark stats). Requires a connected guider; throws on RPC error.</summary>
        public async Task<Phd2CalibrationFilesStatus> GetCalibrationFilesStatusAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            // A status read is a quick query, so the default 60 s SendMessage timeout is fine here (unlike the
            // long-running build above, which overrides it with CalibrationBuildTimeoutMs).
            return await SendCalibrationStatusRpcAsync(new Phd2GetCalibrationFilesStatus(), "get_calibration_files_status");
        }

        /// <summary>§63.8 — poll the progress of an in-flight dark-library / defect-map build (all zero/false
        /// when idle). A quick query; because <see cref="SendMessage{T}"/> opens its own short-lived
        /// connection, this is safe to call CONCURRENTLY with the blocking build RPC — the daemon yields
        /// between frames, so a caller can drive a progress bar while <see cref="BuildDarkLibraryAsync"/> is
        /// still awaiting. Requires a connected guider; throws on RPC error.</summary>
        public async Task<Phd2DarkBuildProgress> GetDarkBuildProgressAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            var response = await SendMessage<Phd2GetDarkBuildProgressResponse>(new Phd2GetDarkBuildProgress());
            if (response.error != null) {
                throw new GuiderRpcException("get_dark_build_progress", response.error.code, response.error.message);
            }
            if (response.result is null) {
                throw new GuiderRpcException("get_dark_build_progress", 0, "missing result payload");
            }
            return response.result;
        }

        /// <summary>§63.6 — enable/disable dark subtraction for the active profile (enabling needs a connected
        /// camera). Returns the updated calibration-files status. Requires a connected guider; throws on RPC error
        /// (e.g. the daemon's "camera not connected" when enabling).</summary>
        public Task<Phd2CalibrationFilesStatus> SetDarkLibraryEnabledAsync(bool enabled, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            return SendCalibrationStatusRpcAsync(
                new Phd2SetDarkLibraryEnabled { Parameters = new() { Enabled = enabled } }, "set_dark_library_enabled");
        }

        /// <summary>§63.6 — enable/disable bad-pixel (defect-map) correction for the active profile (enabling needs
        /// a connected camera). Returns the updated calibration-files status. Requires a connected guider; throws
        /// on RPC error.</summary>
        public Task<Phd2CalibrationFilesStatus> SetDefectMapEnabledAsync(bool enabled, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            return SendCalibrationStatusRpcAsync(
                new Phd2SetDefectMapEnabled { Parameters = new() { Enabled = enabled } }, "set_defect_map_enabled");
        }

        // Shared send-and-unwrap for the three RPCs that return the calibration-files status object
        // (get_calibration_files_status, set_dark_library_enabled, set_defect_map_enabled): one error/empty-result
        // contract, one place.
        private async Task<Phd2CalibrationFilesStatus> SendCalibrationStatusRpcAsync(Phd2Method msg, string method) {
            var response = await SendMessage<Phd2CalibrationFilesStatusResponse>(msg);
            if (response.error != null) {
                throw new GuiderRpcException(method, response.error.code, response.error.message);
            }
            if (response.result is null) {
                throw new GuiderRpcException(method, 0, "missing result payload");
            }
            return response.result;
        }
    }

    /// <summary>A guider JSON-RPC call returned an error (or an empty result). Carries the daemon's method,
    /// error code, and message so the service layer can map it to a client-visible failure.</summary>
    // CA1032 (standard exception ctors) is suppressed rather than satisfied with boilerplate: a GuiderRpcException
    // is meaningless without its RPC context (method + code), so the parameterless / message-only / inner-only
    // ctors would only manufacture instances with empty RpcMethod and Code 0. Legacy binary serialization (the
    // other reason CA1032 wants them) is obsolete in .NET 10, so the single context-carrying ctor is the whole API.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors",
        Justification = "This exception is meaningless without its RPC method + code; the standard ctors would only produce context-less instances. Binary serialization is obsolete in .NET 10.")]
    public sealed class GuiderRpcException : Exception {
        public string RpcMethod { get; }
        public int Code { get; }

        public GuiderRpcException(string rpcMethod, int code, string? message)
            : base($"{rpcMethod} failed (code {code}): {message}") {
            RpcMethod = rpcMethod;
            Code = code;
        }
    }
}
