#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

// §63.17 (PR 2) — on-demand profile push: POST /guider/profile/push re-runs the §63.5 engine-config +
// equipment-selection push against the connected daemon so a settings edit takes effect without a full
// reconnect. The push itself is PHD2Guider.RepushGuiderEngineConfigAsync (best-effort per message, with the
// disconnect → reconnect window when a selection/setup message is queued); completion is announced with the
// guider.profile_pushed WS event carrying the attempted RPC method names.
public sealed partial class GuiderService {

    /// <summary>§63.17 — synchronous 202-style op: the push runs to completion before the accept returns
    /// (it's a handful of quick RPCs, seconds at most — not a §60.5 background job), then the
    /// <c>guider.profile_pushed</c> event reports the attempted method names. Disconnected guider throws
    /// InvalidOperationException (→ 409, typed).</summary>
    // Non-async outer so RequireConnectedGuider's "not connected" surfaces synchronously — same contract as
    // the other explicit guide ops (and what the endpoint's 409 mapping relies on).
    public Task<OperationAcceptedDto> PushGuiderProfileAsync(string? idempotencyKey, CancellationToken ct) {
        var guider = RequireConnectedGuider();
        return PushAsync(guider, idempotencyKey, ct);
    }

    private async Task<OperationAcceptedDto> PushAsync(
            OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PHD2Guider guider, string? idempotencyKey, CancellationToken ct) {
        // Capture the selection BEFORE the push so the §63.17 invalidation compare is against what this push
        // actually sent (a concurrent settings edit mid-push shouldn't skew the decision).
        var settings = _profileService.ActiveProfile.GuiderSettings;
        var camera = settings.GuiderCamera;
        var cameraId = settings.GuiderCameraId;
        var methods = await guider.RepushGuiderEngineConfigAsync(ct).ConfigureAwait(false);
        var payload = new JsonObject {
            ["methods"] = new JsonArray(methods.Select(m => (JsonNode)JsonValue.Create(m)).ToArray()),
            ["ara_profile_id"] = _activeProfileIdResolver?.Invoke()?.ToString(),
        };
        await EmitCalibrationEventAsync(WsEventCatalog.GuiderProfilePushed, payload).ConfigureAwait(false);
        await EmitDarkLibraryInvalidationIfCameraChangedAsync(guider, camera, cameraId, ct).ConfigureAwait(false);
        return Accepted("guider.profile.push", idempotencyKey);
    }

    // §63.17 invalidation — the guide camera the last push (this daemon session) selected. Null = unknown
    // (no push observed yet), which never triggers an invalidation: the first push after boot has no baseline
    // to call a "change", and a false invalidation banner would nag users into pointless rebuilds.
    private string? _lastPushedGuiderCamera;
    private string? _lastPushedGuiderCameraId;

    /// <summary>§63.17 — pure decision for the dark-library invalidation: only a push whose camera selection
    /// DIFFERS from the previously-pushed one (case-sensitive; the daemon matches choice strings verbatim)
    /// invalidates, and only when a baseline exists. Unset ("") selections never invalidate.</summary>
    internal static bool GuideCameraChanged(
            string? previousCamera, string? previousCameraId, string? camera, string? cameraId) {
        if (previousCamera is null && previousCameraId is null) {
            return false; // no baseline — first observed push
        }
        var cameraChanged = !string.IsNullOrWhiteSpace(camera) && camera != previousCamera;
        var idChanged = !string.IsNullOrWhiteSpace(cameraId) && cameraId != previousCameraId;
        return cameraChanged || idChanged;
    }

    private async Task EmitDarkLibraryInvalidationIfCameraChangedAsync(
            OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PHD2Guider guider, string? camera, string? cameraId, CancellationToken ct) {
        var changed = GuideCameraChanged(_lastPushedGuiderCamera, _lastPushedGuiderCameraId, camera, cameraId);
        _lastPushedGuiderCamera = camera;
        _lastPushedGuiderCameraId = cameraId;
        if (!changed) {
            return;
        }
        // Only nag when there is actually a library to invalidate — best-effort read; a status hiccup
        // suppresses the banner rather than failing a push that already succeeded.
        var status = await GetCalibrationFilesStatusAsync(ct).ConfigureAwait(false);
        if (status is not { DarkLibraryExists: true } && status is not { DefectMapExists: true }) {
            return;
        }
        await EmitCalibrationEventAsync(WsEventCatalog.GuiderDarkLibraryInvalidated,
            new JsonObject { ["reason"] = "guide_camera_changed" }).ConfigureAwait(false);
    }
}
