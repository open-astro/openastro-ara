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
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

// §63.6 (guider-e-4b-2) — surface the guider-client dark-library build (guider-e-4b-1) over the §6 REST/WS
// surface. build_dark_library is a BLOCKING RPC with no progress events (see PHD2Guider.DarkLibrary.cs), so this
// follows the §60.5 202-Accepted pattern: validate synchronously, dispatch the build on a background task, and
// report start/finish over the §60.9 WS stream — there is no granular progress bar to forward.
public sealed partial class GuiderService {

    public const string DarkLibraryStartedEvent = "guider.dark_library.started";
    public const string DarkLibraryCompleteEvent = "guider.dark_library.complete";
    public const string DarkLibraryFailedEvent = "guider.dark_library.failed";

    // build_dark_library is a single blocking RPC on the daemon, so two concurrent builds are undefined behavior
    // there (and would emit ambiguous paired started/complete events) — this serializes them under _gate. A
    // second build with the SAME non-null idempotency key as the in-flight one is an idempotent no-op (202, the
    // §60.5 at-least-once contract); any other concurrent build is rejected with 409.
    private bool _darkLibraryBuildInProgress;
    private string? _darkLibraryBuildKey;

    public Task<OperationAcceptedDto> BuildDarkLibraryAsync(
            BuildDarkLibraryRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        // Validate the request synchronously so a bad request is a 4xx now, not a background failure later. The
        // static builder throws ArgumentOutOfRangeException / ArgumentException, which the endpoint maps to 400.
        var rpcRequest = PHD2Guider.BuildDarkLibraryRequest(
            request.FrameCount, request.MinExposureMs, request.MaxExposureMs,
            request.ClearExisting, request.Notes, request.LoadAfter);
        // Reject up front if no guider is connected (throws InvalidOperationException → 409) — same as the other
        // guide ops; do NOT accept a 202 we can't honor.
        var guider = RequireConnectedGuider();
        lock (_gate) {
            if (_darkLibraryBuildInProgress) {
                // Idempotent retry of the in-flight build → re-accept; a different build → 409.
                if (idempotencyKey is not null && idempotencyKey == _darkLibraryBuildKey) {
                    return Task.FromResult(Accepted("guider.dark_library.build", idempotencyKey));
                }
                throw new InvalidOperationException("a dark-library build is already in progress");
            }
            _darkLibraryBuildInProgress = true;
            _darkLibraryBuildKey = idempotencyKey;
        }
        _ = Task.Run(() => BuildDarkLibraryInBackground(guider, rpcRequest), CancellationToken.None);
        return Task.FromResult(Accepted("guider.dark_library.build", idempotencyKey));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background build boundary: guider.BuildDarkLibraryAsync can throw arbitrary socket/IO/protocol exceptions (and GuiderRpcException) over a 2-hour blocking RPC. The 202 already returned, so any escape must surface as the failed WS event and be contained — it must not become an unobserved task exception. Log-and-recover.")]
    private async Task BuildDarkLibraryInBackground(PHD2Guider guider, Phd2BuildDarkLibrary rpcRequest) {
        var p = rpcRequest.Parameters!;
        try {
            LogDarkLibraryBuildStarted(p.FrameCount, p.ClearExisting);
            await EmitDarkLibraryEventAsync(DarkLibraryStartedEvent, new JsonObject {
                ["frame_count"] = p.FrameCount,
                ["clear_existing"] = p.ClearExisting,
                ["load_after"] = p.LoadAfter,
            });
            var result = await guider.BuildDarkLibraryAsync(
                p.FrameCount, p.MinExposureMs, p.MaxExposureMs, p.ClearExisting, p.Notes, p.LoadAfter,
                CancellationToken.None).ConfigureAwait(false);
            var exposures = new JsonArray();
            if (result.ExposuresMs is not null) {
                foreach (var ms in result.ExposuresMs) {
                    exposures.Add(ms);
                }
            }
            await EmitDarkLibraryEventAsync(DarkLibraryCompleteEvent, new JsonObject {
                ["profile_id"] = result.ProfileId,
                ["dark_library_path"] = result.DarkLibraryPath,
                ["frame_count"] = result.FrameCount,
                ["exposure_count"] = result.ExposureCount,
                ["exposures_ms"] = exposures,
            });
        } catch (Exception ex) {
            LogDarkLibraryBuildFailed(ex);
            await EmitDarkLibraryEventAsync(DarkLibraryFailedEvent, new JsonObject {
                ["error"] = ex.Message,
            });
        } finally {
            // Release the single-build gate whether the build completed or failed.
            lock (_gate) {
                _darkLibraryBuildInProgress = false;
                _darkLibraryBuildKey = null;
            }
        }
    }

    public async Task<CalibrationFilesStatusDto?> GetCalibrationFilesStatusAsync(CancellationToken ct) {
        PHD2Guider? guider;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Read the status only while genuinely connected; otherwise the contract returns null (not an error).
            guider = _state == EquipmentConnectionState.Connected ? _guider : null;
        }
        if (guider is null) {
            return null;
        }
        Phd2CalibrationFilesStatus status;
        try {
            status = await guider.GetCalibrationFilesStatusAsync(ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) {
            // Raced with a disconnect/dispose after we captured the guider outside the gate — the guider went
            // away mid-read. Treat it as the disconnected contract (null) rather than surfacing an error.
            return null;
        }
        return new CalibrationFilesStatusDto(
            ProfileId: status.ProfileId,
            DarkLibraryPath: status.DarkLibraryPath,
            DefectMapPath: status.DefectMapPath,
            DarkLibraryExists: status.DarkLibraryExists,
            DefectMapExists: status.DefectMapExists,
            DarkLibraryCompatible: status.DarkLibraryCompatible,
            DefectMapCompatible: status.DefectMapCompatible,
            DarkLibraryLoaded: status.DarkLibraryLoaded,
            DefectMapLoaded: status.DefectMapLoaded,
            AutoLoadDarks: status.AutoLoadDarks,
            AutoLoadDefectMap: status.AutoLoadDefectMap,
            DarkCountLoaded: status.DarkCountLoaded,
            DarkMinExposureSecondsLoaded: status.DarkMinExposureSecondsLoaded,
            DarkMaxExposureSecondsLoaded: status.DarkMaxExposureSecondsLoaded);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort: a failed publish from a custom IWsBroadcaster (e.g. SocketException) must not abort the background build or surface as an unobserved task exception. CA1031's log-and-recover boundary applies.")]
    private async Task EmitDarkLibraryEventAsync(string eventType, JsonObject payload) {
        if (_ws is null) {
            return;
        }
        try {
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            // best-effort; a failed publish must not affect the build, but log it so a silently-dropped event is
            // visible in production rather than invisible.
            LogDarkLibraryWsPublishFailed(eventType, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dark-library build started (frames={FrameCount}, clearExisting={ClearExisting})")]
    partial void LogDarkLibraryBuildStarted(int frameCount, bool clearExisting);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dark-library build failed")]
    partial void LogDarkLibraryBuildFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dark-library WS event '{EventType}' failed to publish")]
    partial void LogDarkLibraryWsPublishFailed(string eventType, Exception ex);
}
