#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    /// <summary>
    /// #1364 (epic item 1) — the live guide-camera view's RPCs. Each wrapper throws
    /// <see cref="InvalidOperationException"/> when the guider is not connected and
    /// <see cref="GuiderRpcException"/> when the guider refuses, the same contract as the polar-align
    /// wrappers, so the daemon's endpoint layer maps them to 409 / 422 the same way.
    /// </summary>
    public partial class PHD2Guider {

        /// <summary>The live view's bound on each guider RPC: a guider that stops answering costs one
        /// poll, not a minute.</summary>
        public const int LiveRpcTimeoutMs = 5000;

        private void RequireConnectedForLive() {
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
        }

        private static void ThrowOnError(string method, PhdMethodResponse response) {
            if (response.error != null) {
                throw new GuiderRpcException(method, response.error.code, response.error.message);
            }
        }

        /// <summary>The guide camera's frame size in pixels, or null when the camera is not connected to
        /// the guider (the guider answers that with an error, which is not a fault of ours).</summary>
        public async Task<(int Width, int Height)?> GetCameraFrameSizeAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            RequireConnectedForLive();
            var response = await SendMessage<Phd2IntListResponse>(new Phd2GetCameraFrameSize(), LiveRpcTimeoutMs).ConfigureAwait(false);
            if (response.error != null || response.result is not { Count: 2 } size) {
                return null;
            }
            return (size[0], size[1]);
        }

        /// <summary>Half-size of the star search box, in guide pixels.</summary>
        public async Task<int?> GetSearchRegionAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            RequireConnectedForLive();
            var response = await SendMessage<Phd2IntResponse>(new Phd2GetSearchRegion(), LiveRpcTimeoutMs).ConfigureAwait(false);
            return response.error != null ? null : response.result;
        }

        /// <summary>Whether the guider tracks several stars (its multi-star mode).</summary>
        public async Task<bool?> GetMultiStarEnabledAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            RequireConnectedForLive();
            var response = await SendMessage<Phd2GuiderOptionsResponse>(new Phd2GetGuiderOptions(), LiveRpcTimeoutMs).ConfigureAwait(false);
            return response.error != null ? null : response.result?.MultiStarEnabled;
        }

        /// <summary>Lock on the star nearest (<paramref name="x"/>, <paramref name="y"/>) frame pixels —
        /// a tap on the live picture. The guider must be looping or guiding to have an image.</summary>
        public async Task SetLockPositionAsync(double x, double y, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            RequireConnectedForLive();
            // Positional params [x, y, exact]: exact false lets the guider snap to the nearest star,
            // which is what a tap on a picture wants.
            var request = new Phd2SetLockPosition { Parameters = new object[] { x, y, false } };
            var response = await SendMessage<GenericPhdMethodResponse>(request, LiveRpcTimeoutMs).ConfigureAwait(false);
            ThrowOnError("set_lock_position", response);
        }

        public async Task DeselectStarAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            RequireConnectedForLive();
            var response = await SendMessage<GenericPhdMethodResponse>(new Phd2DeselectStar(), LiveRpcTimeoutMs).ConfigureAwait(false);
            ThrowOnError("deselect_star", response);
        }

        /// <summary>Start looping exposures (no guiding) so the live view has frames to show and a star
        /// can be chosen. The guider's camera and mount are connected first when they are not: a guider
        /// whose equipment is disconnected answers <c>loop</c> with "could not start looping" (seen
        /// 2026-10-16 after a session's equipment had been dropped), which tells the user nothing.</summary>
        public async Task LoopAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            RequireConnectedForLive();
            if (!await EnsurePHD2EquipmentConnected().ConfigureAwait(false)) {
                throw new GuiderRpcException("loop", 1, "the guider could not connect its camera and mount — check Settings → Guider");
            }
            var response = await SendMessage<GenericPhdMethodResponse>(new Phd2Loop(), LiveRpcTimeoutMs).ConfigureAwait(false);
            ThrowOnError("loop", response);
        }

        /// <summary>Stop looping (or guiding) — the guider's Stop button.</summary>
        public async Task StopCaptureAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            RequireConnectedForLive();
            var response = await SendMessage<GenericPhdMethodResponse>(new Phd2StopCapture(), LiveRpcTimeoutMs).ConfigureAwait(false);
            ThrowOnError("stop_capture", response);
        }
    }
}
