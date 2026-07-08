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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    // §45 (polar-align-a) — drive the guider's polar-alignment capture surface from the connected guider.
    // The wire shapes landed with PHD2Methods.PolarAlign.cs (§45 DTO slices); this adds the RPC invocation
    // + send-site validation, the DarkLibrary precedent (PHD2Guider.DarkLibrary.cs) applied to the loop's
    // four RPCs: capture_single_frame (fire-and-forget solver frame), get_star_centroids (live track),
    // set_pa_session / get_pa_session (the single-client guide-camera lease). ARA owns the routine — this
    // is only the guider-client half; the PolarAlignService state machine (solve + slew + geometry) is the
    // follow-up slice. capture_single_frame acks 0 immediately and announces completion + the saved-FITS
    // path on the SingleFrameComplete event (raised from ProcessEvent), so its ack is fast even for a long
    // exposure — the default SendMessage timeout is fine.
    public sealed partial class PHD2Guider {

        // Daemon contracts (openastro-guider API_REFERENCE.md): max_stars 1..50; the PA-session lease
        // timeout 10..3600 s; gain is the daemon's normalized 0..100 scale (not e-/ADU).
        internal const int CentroidsMinStars = 1;
        internal const int CentroidsMaxStars = 50;
        internal const int PaSessionMinTimeoutSeconds = 10;
        internal const int PaSessionMaxTimeoutSeconds = 3600;
        internal const int CaptureGainMin = 0;
        internal const int CaptureGainMax = 100;

        /// <summary>Raised when the daemon finishes a <c>capture_single_frame</c> (the RPC itself acks
        /// immediately). <see cref="SingleFrameCompleteEventArgs.Path"/> is the saved-FITS location ARA
        /// hands to its plate solver, present only when the capture requested a save.</summary>
        public event EventHandler<SingleFrameCompleteEventArgs>? SingleFrameComplete;

        private void RaiseSingleFrameComplete(bool success, string? error, string? path) =>
            SingleFrameComplete?.Invoke(this, new SingleFrameCompleteEventArgs(success, error, path));

        /// <summary>
        /// Validates the §45 solver-frame parameters at the send site and builds the wire request. A saved
        /// frame (<paramref name="save"/> true, or any <paramref name="path"/> given) requires a non-empty
        /// ABSOLUTE path — the daemon rejects a relative path, and the solver needs a known location to read.
        /// Throws <see cref="ArgumentOutOfRangeException"/> for a non-positive exposure/binning or an
        /// out-of-range gain, and <see cref="ArgumentException"/> for a save without an absolute path or a
        /// malformed subframe — surfaced before the socket so the caller gets a clear error rather than the
        /// daemon's opaque <c>-32602</c>.
        /// </summary>
        public static Phd2CaptureSolverFrame BuildCaptureSolverFrameRequest(
            int? exposureMs, int? binning, int? gain, IReadOnlyList<int>? subframe, string? path, bool save) {
            if (exposureMs is int exp && exp < 1) {
                throw new ArgumentOutOfRangeException(nameof(exposureMs), exp, "exposure must be a positive number of milliseconds.");
            }
            if (binning is int bin && bin < 1) {
                throw new ArgumentOutOfRangeException(nameof(binning), bin, "binning must be >= 1.");
            }
            if (gain is int g && g is < CaptureGainMin or > CaptureGainMax) {
                throw new ArgumentOutOfRangeException(nameof(gain), g, $"gain must be {CaptureGainMin}..{CaptureGainMax}.");
            }
            if (subframe is not null && subframe.Count != 4) {
                throw new ArgumentException("subframe must be [x, y, width, height].", nameof(subframe));
            }
            // A path implies a save; a save needs an absolute path the daemon can write and ARA can read.
            var effectiveSave = save || path is not null;
            if (effectiveSave) {
                if (string.IsNullOrWhiteSpace(path)) {
                    throw new ArgumentException("a saved frame requires a path.", nameof(path));
                }
                if (!System.IO.Path.IsPathRooted(path)) {
                    throw new ArgumentException($"path must be absolute: '{path}'.", nameof(path));
                }
            }
            return new Phd2CaptureSolverFrame {
                Parameters = new Phd2CaptureSolverFrameParameter {
                    ExposureMs = exposureMs,
                    Binning = binning,
                    Gain = gain,
                    Subframe = subframe,
                    Path = effectiveSave ? path : null,
                    Save = effectiveSave ? true : null,
                },
            };
        }

        /// <summary>
        /// §45 — take one frame outside the guiding loop (camera connected, no active capture; typically
        /// under a PA-session lease). The RPC acks as soon as the daemon starts the exposure; the finished
        /// frame (with its saved-FITS path when <paramref name="save"/>/<paramref name="path"/> was set)
        /// arrives asynchronously on <see cref="SingleFrameComplete"/>. Throws on a transport/protocol error
        /// or an invalid parameter (validated before the socket).
        /// </summary>
        /// <remarks><paramref name="ct"/> is honored only at entry — the RPC send is bounded by the socket
        /// timeout and takes no cancellation token, but the ack is immediate so that window is small.</remarks>
        public async Task CaptureSolverFrameAsync(
            int? exposureMs, int? binning, int? gain, IReadOnlyList<int>? subframe, string? path, bool save,
            CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            var request = BuildCaptureSolverFrameRequest(exposureMs, binning, gain, subframe, path, save);
            Logger.Info($"Phd2 - capture_single_frame (exposureMs={exposureMs}, binning={binning}, save={save || path is not null}).");
            var response = await SendMessage<GenericPhdMethodResponse>(request);
            if (response.error != null) {
                throw new GuiderRpcException("capture_single_frame", response.error.code, response.error.message);
            }
        }

        /// <summary>
        /// §45 — detect stars on the daemon's CURRENT frame and return their sub-pixel centroids without
        /// selecting a star or touching guider state. <paramref name="maxStars"/> is daemon-clamped to
        /// 1..50 (validated here first). The daemon fails the call ("no stars found") when the frame is
        /// starless or a subframe — surfaced as a <see cref="GuiderRpcException"/>. Throws on any error.
        /// </summary>
        public async Task<IReadOnlyList<Phd2StarCentroid>> GetStarCentroidsAsync(
            IReadOnlyList<int>? roi, int? maxStars, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            if (roi is not null && roi.Count != 4) {
                throw new ArgumentException("roi must be [x, y, width, height].", nameof(roi));
            }
            if (maxStars is int ms && ms is < CentroidsMinStars or > CentroidsMaxStars) {
                throw new ArgumentOutOfRangeException(nameof(maxStars), ms,
                    $"max_stars must be {CentroidsMinStars}..{CentroidsMaxStars}.");
            }
            var request = new Phd2GetStarCentroids { Parameters = new Phd2GetStarCentroidsParameter { Roi = roi, MaxStars = maxStars } };
            var response = await SendMessage<Phd2GetStarCentroidsResponse>(request);
            if (response.error != null) {
                throw new GuiderRpcException("get_star_centroids", response.error.code, response.error.message);
            }
            return response.result ?? Array.Empty<Phd2StarCentroid>();
        }

        /// <summary>
        /// §45 — start/renew (<paramref name="active"/> true) or end (false) the polar-alignment session
        /// lease that keeps the single-client guide camera ARA's for the routine. The lease auto-expires
        /// (10..3600 s, daemon default 600) so a crashed orchestrator can't wedge the daemon — renew by
        /// calling again. Returns the resulting session status. Throws on error (e.g. the daemon's rejection
        /// when starting while calibrating/guiding).
        /// </summary>
        public Task<Phd2PaSessionStatus> SetPaSessionAsync(bool active, int? timeoutS, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            if (timeoutS is int t && t is < PaSessionMinTimeoutSeconds or > PaSessionMaxTimeoutSeconds) {
                throw new ArgumentOutOfRangeException(nameof(timeoutS), t,
                    $"timeout_s must be {PaSessionMinTimeoutSeconds}..{PaSessionMaxTimeoutSeconds}.");
            }
            var request = new Phd2SetPaSession { Parameters = new Phd2SetPaSessionParameter { Active = active, TimeoutS = timeoutS } };
            return SendPaSessionRpcAsync(request, "set_pa_session");
        }

        /// <summary>§45 — read the polar-alignment session lease status (active + seconds until it expires).
        /// Requires a connected guider; throws on RPC error.</summary>
        public Task<Phd2PaSessionStatus> GetPaSessionAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            return SendPaSessionRpcAsync(new Phd2GetPaSession(), "get_pa_session");
        }

        // Shared send-and-unwrap for the two RPCs that return the PA-session status object
        // (set_pa_session, get_pa_session): one error/empty-result contract, one place.
        private async Task<Phd2PaSessionStatus> SendPaSessionRpcAsync(Phd2Method msg, string method) {
            var response = await SendMessage<Phd2PaSessionResponse>(msg);
            if (response.error != null) {
                throw new GuiderRpcException(method, response.error.code, response.error.message);
            }
            if (response.result is null) {
                throw new GuiderRpcException(method, 0, "missing result payload");
            }
            return response.result;
        }
    }

    /// <summary>The daemon finished a <c>capture_single_frame</c>. <see cref="Success"/> is always set;
    /// <see cref="Error"/> carries the failure reason on a failed capture; <see cref="Path"/> is the
    /// saved-FITS location when the capture requested a save (the file ARA's plate solver reads).</summary>
    public sealed class SingleFrameCompleteEventArgs(bool success, string? error, string? path) : EventArgs {
        public bool Success { get; } = success;
        public string? Error { get; } = error;
        public string? Path { get; } = path;
    }
}
