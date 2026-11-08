#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
    public Task<OperationAcceptedDto> PushGuiderProfileAsync(string? idempotencyKey, CancellationToken ct, bool refuseUnservedSelection = true) {
        var guider = RequireConnectedGuider();
        return PushAsync(guider, idempotencyKey, refuseUnservedSelection, ct);
    }

    /// <summary>Tuning-only push — the runtime-safe subset, equipment left connected (guiding continues).
    /// No dark-library invalidation check: the camera selection is never part of this push.</summary>
    public Task<OperationAcceptedDto> PushGuiderTuningAsync(string? idempotencyKey, CancellationToken ct) {
        var guider = RequireConnectedGuider();
        return PushTuningAsync(guider, idempotencyKey, ct);
    }

    /// <summary>#1592 — see <see cref="IGuiderService.ApplyGuiderMultiStarAsync"/>.</summary>
    public async Task ApplyGuiderMultiStarAsync(CancellationToken ct) {
        var guider = RequireConnectedGuider();
        // Between targets the guider is stopped (slew, centre), so its state alone cannot tell a run is on.
        if (_sequencerResolver?.Invoke() is { } sequencer && (await sequencer.ActiveRunIdsAsync(ct).ConfigureAwait(false)).Count > 0) {
            throw new OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.GuiderBusyException(
                "A sequence is running; applying multi-star would release the guider's equipment under it. Apply it when the run has ended.");
        }
        await guider.ApplyMultiStarFromProfileAsync(ct).ConfigureAwait(false);
    }

    private async Task<OperationAcceptedDto> PushTuningAsync(
            OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PHD2Guider guider, string? idempotencyKey, CancellationToken ct) {
        var methods = await guider.RepushGuiderTuningAsync(ct).ConfigureAwait(false);
        var payload = new JsonObject {
            ["methods"] = new JsonArray(methods.Select(m => (JsonNode)JsonValue.Create(m)).ToArray()),
            ["scope"] = "tuning",
            ["ara_profile_id"] = _activeProfileIdResolver?.Invoke()?.ToString(),
        };
        await EmitCalibrationEventAsync(WsEventCatalog.GuiderProfilePushed, payload).ConfigureAwait(false);
        return Accepted("guider.profile.push", idempotencyKey);
    }

    private async Task<OperationAcceptedDto> PushAsync(
            OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PHD2Guider guider, string? idempotencyKey,
            bool refuseUnservedSelection, CancellationToken ct) {
        // Capture the selection BEFORE the push so the §63.17 invalidation compare is against what this push
        // actually sent (a concurrent settings edit mid-push shouldn't skew the decision).
        var settings = _profileService.ActiveProfile.GuiderSettings;
        // #1306 — the explicit push refuses, saying why, rather than send a selection that can only leave
        // the daemon on localhost:0 with its equipment disconnected (the 422 reaches the client's Apply
        // button). The §63.4 profile-switch follow is exempt (review r2): a profile saved before this rule
        // must still get its guider twin and tuning, with the guider's own Alpaca server left as it is.
        if (refuseUnservedSelection && await DescribeSelectionProblemAsync(settings, ct).ConfigureAwait(false) is { } reason) {
            // #1665 — the refused selection is not sent, but the twin still follows, so calibration and
            // darks never land in the previous profile's twin.
            MarkOutOfStep(reason);
            await guider.SelectActiveAraProfileTwinAsync(ct).ConfigureAwait(false);
            throw new GuiderSelectionInvalidException(reason);
        }
        var camera = settings.GuiderCamera;
        var cameraId = settings.GuiderCameraId;
        IReadOnlyList<string> methods;
        try {
            methods = await guider.RepushGuiderEngineConfigAsync(ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            MarkOutOfStep(ex.Message);
            throw;
        }
        if (refuseUnservedSelection) {
            lock (_gate) {
                _outOfStepReason = null;
            }
        } else {
            // A select's push skipped the checks, so passing proves nothing: the selection is judged here.
            await RecheckSelectionAsync(ct).ConfigureAwait(false);
        }
        var payload = new JsonObject {
            ["methods"] = new JsonArray(methods.Select(m => (JsonNode)JsonValue.Create(m)).ToArray()),
            ["ara_profile_id"] = _activeProfileIdResolver?.Invoke()?.ToString(),
        };
        await EmitCalibrationEventAsync(WsEventCatalog.GuiderProfilePushed, payload).ConfigureAwait(false);
        await EmitDarkLibraryInvalidationIfCameraChangedAsync(guider, camera, cameraId, ct).ConfigureAwait(false);
        return Accepted("guider.profile.push", idempotencyKey);
    }

    // #1665 — why the guider does not hold the active profile's guider settings, with the fix. Null when it
    // does, or when that is not known yet (no push or connect has decided). Guarded by _gate.
    private string? _outOfStepReason;

    internal string? OutOfStepReason {
        get {
            lock (_gate) {
                return _outOfStepReason;
            }
        }
    }

    private void MarkOutOfStep(string reason) {
        var message = $"The Guider does not have this profile's guider settings: {reason.TrimEnd('.')}. "
            + "Fix it in Guider settings and press Apply, or reconnect the Guider.";
        lock (_gate) {
            _outOfStepReason = message;
        }
        LogOutOfStep(message);
    }

    /// <summary>#1665 — refuse an operation that would capture with another profile's Guide Camera.</summary>
    internal void ThrowIfOutOfStep(string operation) {
        if (OutOfStepReason is { } reason) {
            LogRefusedOutOfStep(operation, reason);
            throw new GuiderOutOfStepException(reason);
        }
    }

    // #1665 — the connect-time push and a select's push send the selection unchecked, so it is judged here.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Check boundary after a push that already landed: it must never turn a good connect or push into a failure; the state is left as it was and the fault is logged.")]
    private async Task RecheckSelectionAsync(CancellationToken ct) {
        try {
            var reason = await DescribeSelectionProblemAsync(_profileService.ActiveProfile.GuiderSettings, ct).ConfigureAwait(false);
            if (reason is not null) {
                MarkOutOfStep(reason);
                return;
            }
            lock (_gate) {
                _outOfStepReason = null;
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogSelectionRecheckFailed(ex);
        }
    }

    private async Task<string?> DescribeSelectionProblemAsync(IGuiderSettings settings, CancellationToken ct) =>
        OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PHD2Guider.DescribeUnservedAlpacaSelection(settings)
        // #1308 — re-resolve the pinned guide camera against the server's configureddevices: a
        // renumbered or vanished camera, or one that is the main imaging camera, is refused.
        ?? await DescribeCameraPinProblemAsync(settings, ct).ConfigureAwait(false);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guider is out of step with the active profile (#1665): {Reason}")]
    private partial void LogOutOfStep(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Operation} refused, guider out of step with the active profile (#1665): {Reason}")]
    private partial void LogRefusedOutOfStep(string operation, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guider selection check failed; out-of-step state left as it was (#1665)")]
    private partial void LogSelectionRecheckFailed(Exception exception);

    private async Task<string?> DescribeCameraPinProblemAsync(IGuiderSettings settings, CancellationToken ct) {
        if (GuiderCameraPinCheck.CameraEndpoint(settings) is not { } ep) {
            return null;
        }
        var devices = _alpaca is null
            ? new Dictionary<string, AlpacaConfiguredDevice>()
            : await _alpaca.GetConfiguredDevicesAsync(ep.Host, ep.Port, ct).ConfigureAwait(false);
        if (devices.Count == 0) {
            LogPinCheckSkipped(ep.Host, ep.Port);
        }
        return GuiderCameraPinCheck.Describe(settings, devices, _mainCameraResolver?.Invoke());
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Guide-camera pin check skipped: {Host}:{Port} answered no configured devices, so the stored device number is pushed as is (#1308)")]
    private partial void LogPinCheckSkipped(string host, int port);

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

/// <summary>#1665 — the guider does not hold the active profile's guider settings, so an operation that
/// would capture with its Guide Camera is refused. The message says why and how to fix it.</summary>
public class GuiderOutOfStepException : InvalidOperationException {
    public GuiderOutOfStepException() { }
    public GuiderOutOfStepException(string message) : base(message) { }
    public GuiderOutOfStepException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>#1306 — the stored guider equipment selection cannot be pushed as it stands (a bare Alpaca
/// choice with no server). The endpoint maps it to <c>422 Unprocessable Entity</c>; the message says
/// which slot and what to set.</summary>
public class GuiderSelectionInvalidException : Exception {
    public GuiderSelectionInvalidException() { }
    public GuiderSelectionInvalidException(string message) : base(message) { }
    public GuiderSelectionInvalidException(string message, Exception innerException) : base(message, innerException) { }
}
