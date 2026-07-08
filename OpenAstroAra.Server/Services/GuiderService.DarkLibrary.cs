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

// §63.6 (guider-e-4b-2 / e-4c-b-2) — surface the guider-client calibration builds (dark library + defect map)
// over the §6 REST/WS surface. Both build_dark_library and build_defect_map_darks are BLOCKING RPCs (they
// answer only when the whole capture stack is done), so this follows the §60.5 202-Accepted pattern: validate
// synchronously, dispatch the build on a background task, and report start/finish over the §60.9 WS stream.
// §63.8: the daemon yields between frames, so a SEPARATE poll of get_dark_build_progress drives a granular
// progress bar during the build — run concurrently here (PHD2Guider.SendMessage opens its own connection per
// call, so the poll never contends with the in-flight build). The two builds share one in-flight gate because
// the daemon can only run a single calibration capture at a time (one camera, "no active capture" precondition).
public sealed partial class GuiderService {

    public const string DarkLibraryStartedEvent = "guider.dark_library.started";
    public const string DarkLibraryProgressEvent = "guider.dark_library.progress";
    public const string DarkLibraryCompleteEvent = "guider.dark_library.complete";
    public const string DarkLibraryFailedEvent = "guider.dark_library.failed";

    // §63.8 progress poll cadence — 1 s is plenty for a multi-minute build's progress bar and keeps the poll
    // load trivial (one short-lived RPC connection per tick).
    private const int BuildProgressPollIntervalMs = 1000;

    // A calibration build (dark library OR defect-map darks) is a single blocking RPC on the daemon, so two
    // concurrent builds are undefined behavior there (and would emit ambiguous paired started/complete events) —
    // this serializes ALL calibration builds under _gate. A second build with the SAME non-null idempotency key
    // AND the same operation type as the in-flight one is an idempotent no-op (202, the §60.5 at-least-once
    // contract); any other concurrent build (dark or defect-map) is rejected with 409. The operation type is
    // tracked too so a same-key request to the *other* build endpoint doesn't falsely re-accept (it would emit
    // the in-flight op's WS events, never the requested op's — a client would wait forever).
    private bool _calibrationBuildInProgress;
    private string? _calibrationBuildKey;
    private string? _calibrationBuildOp;

    private void ReleaseCalibrationGateLocked() {
        _calibrationBuildInProgress = false;
        _calibrationBuildKey = null;
        _calibrationBuildOp = null;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "If Task.Run fails to even queue the work (e.g. OutOfMemoryException creating a thread), the background task's finally — which releases the calibration-build gate — never runs, so the gate would stick true forever (every later build → 409). Release it here and rethrow so the failure still surfaces; the catch is a release-and-rethrow boundary, not a swallow.")]
    private void DispatchCalibrationBuild(Func<Task> build) {
        try {
            _ = Task.Run(build, _shutdownCts.Token);
        } catch (Exception) {
            lock (_gate) {
                ReleaseCalibrationGateLocked();
            }
            throw;
        }
    }

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
        const string op = "guider.dark_library.build";
        lock (_gate) {
            switch (ResolveBuildAdmission(_calibrationBuildInProgress, _calibrationBuildKey, _calibrationBuildOp, idempotencyKey, op)) {
                case BuildAdmission.IdempotentAccept:
                    // Retry of the in-flight build with the same key + op → re-accept (§60.5 at-least-once).
                    return Task.FromResult(Accepted(op, idempotencyKey));
                case BuildAdmission.Reject:
                    throw new CalibrationBuildInProgressException();
                default: // Start
                    _calibrationBuildInProgress = true;
                    _calibrationBuildKey = idempotencyKey;
                    _calibrationBuildOp = op;
                    break;
            }
        }
        // Capture the token NOW (struct, safe to hold past CTS dispose) — evaluating
        // _shutdownCts.Token inside the deferred lambda would race a concurrent Dispose
        // and throw ObjectDisposedException in the background task, leaking the gate.
        var darkToken = _shutdownCts.Token;
        DispatchCalibrationBuild(() => BuildDarkLibraryInBackground(guider, rpcRequest, darkToken));
        return Task.FromResult(Accepted(op, idempotencyKey));
    }

    internal enum BuildAdmission { Start, IdempotentAccept, Reject }

    /// <summary>Pure decision for the concurrent-build gate (so the guard logic is unit-testable without a live
    /// guider): no build in flight → Start; a build in flight with the same non-null key AND the same operation
    /// type → IdempotentAccept; any other build while one is in flight → Reject (409). A null request key never
    /// matches an in-flight key, and a same-key request for a *different* operation is rejected (not re-accepted)
    /// so the caller never gets a 202 for an op whose WS events will never fire.</summary>
    internal static BuildAdmission ResolveBuildAdmission(
            bool inProgress, string? inFlightKey, string? inFlightOp, string? requestKey, string requestOp) {
        if (!inProgress) {
            return BuildAdmission.Start;
        }
        if (requestKey is not null && requestKey == inFlightKey && requestOp == inFlightOp) {
            return BuildAdmission.IdempotentAccept;
        }
        return BuildAdmission.Reject;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background build boundary: guider.BuildDarkLibraryAsync can throw arbitrary socket/IO/protocol exceptions (and GuiderRpcException) over a 2-hour blocking RPC. The 202 already returned, so any escape must surface as the failed WS event and be contained — it must not become an unobserved task exception. Log-and-recover.")]
    private async Task BuildDarkLibraryInBackground(PHD2Guider guider, Phd2BuildDarkLibrary rpcRequest, CancellationToken ct) {
        var p = rpcRequest.Parameters!;
        // §63.8: cancel the progress poll the moment the build settles (complete OR failed), independently of the
        // build's own cancellation — a linked source so shutdown still tears both down together.
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pollTask = Task.CompletedTask;
        try {
            LogDarkLibraryBuildStarted(p.FrameCount, p.ClearExisting);
            await EmitCalibrationEventAsync(DarkLibraryStartedEvent, new JsonObject {
                ["frame_count"] = p.FrameCount,
                ["clear_existing"] = p.ClearExisting,
                ["load_after"] = p.LoadAfter,
            });
            pollTask = PollBuildProgressAsync(guider, DarkLibraryProgressEvent, pollCts.Token);
            var result = await guider.BuildDarkLibraryAsync(
                p.FrameCount, p.MinExposureMs, p.MaxExposureMs, p.ClearExisting, p.Notes, p.LoadAfter,
                ct).ConfigureAwait(false);
            var exposures = new JsonArray();
            if (result.ExposuresMs is not null) {
                foreach (var ms in result.ExposuresMs) {
                    exposures.Add(ms);
                }
            }
            await EmitCalibrationEventAsync(DarkLibraryCompleteEvent, new JsonObject {
                ["profile_id"] = result.ProfileId,
                ["dark_library_path"] = result.DarkLibraryPath,
                ["frame_count"] = result.FrameCount,
                ["exposure_count"] = result.ExposureCount,
                ["exposures_ms"] = exposures,
            });
        } catch (Exception ex) {
            LogDarkLibraryBuildFailed(ex);
            await EmitCalibrationEventAsync(DarkLibraryFailedEvent, new JsonObject {
                ["error"] = ex.Message,
            });
        } finally {
            // Stop the progress poll and let it unwind before releasing the gate (PollBuildProgressAsync swallows
            // its own cancellation, so this await never throws).
            await pollCts.CancelAsync().ConfigureAwait(false);
            await pollTask.ConfigureAwait(false);
            // Release the single-build gate whether the build completed or failed.
            lock (_gate) {
                ReleaseCalibrationGateLocked();
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
        } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or GuiderRpcException) {
            // Raced with a disconnect/dispose after we captured the guider outside the gate — the guider went
            // away mid-read. A drop during the RPC surfaces as a transport error (GuiderRpcException) per
            // PHD2Guider.BuildDarkLibraryAsync's remarks, not always InvalidOperationException. Treat all of
            // these as the disconnected contract (null) rather than surfacing a 500.
            return null;
        }
        return MapStatus(status);
    }

    /// <summary>§63.6 — enable/disable dark subtraction; returns the updated status. Throws
    /// InvalidOperationException (→ 409) when disconnected and GuiderRpcException (→ 422) when the daemon rejects
    /// the toggle (e.g. enabling with no camera connected).</summary>
    // Non-async outer so RequireConnectedGuider's "not connected" (InvalidOperationException) surfaces
    // synchronously — same contract as the other guide ops (and the endpoint maps it to 409 either way).
    public Task<CalibrationFilesStatusDto> SetDarkLibraryEnabledAsync(bool enabled, CancellationToken ct) {
        var guider = RequireConnectedGuider();
        return MapStatusAsync(guider.SetDarkLibraryEnabledAsync(enabled, ct));
    }

    /// <summary>§63.6 — enable/disable bad-pixel (defect-map) correction; returns the updated status. Same error
    /// contract as <see cref="SetDarkLibraryEnabledAsync"/>.</summary>
    public Task<CalibrationFilesStatusDto> SetDefectMapEnabledAsync(bool enabled, CancellationToken ct) {
        var guider = RequireConnectedGuider();
        return MapStatusAsync(guider.SetDefectMapEnabledAsync(enabled, ct));
    }

    private static async Task<CalibrationFilesStatusDto> MapStatusAsync(Task<Phd2CalibrationFilesStatus> rpc) =>
        MapStatus(await rpc.ConfigureAwait(false));

    private static CalibrationFilesStatusDto MapStatus(Phd2CalibrationFilesStatus status) =>
        new(ProfileId: status.ProfileId,
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

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort: a failed publish from a custom IWsBroadcaster (e.g. SocketException) must not abort the background build or surface as an unobserved task exception. CA1031's log-and-recover boundary applies.")]
    private async Task EmitCalibrationEventAsync(string eventType, JsonObject payload) {
        if (_ws is null) {
            return;
        }
        try {
            // ToJsonString()+Parse (not JsonSerializer.SerializeToElement) is deliberate: SerializeToElement on a
            // JsonObject takes the reflection path (IL2026/IL3050), which the AOT-published Server's
            // warnings=errors gate rejects. The string round-trip is the AOT-safe way to build a JsonElement and
            // matches SequencerService.EmitAsync. Beyond the start/complete events it also carries the §63.8
            // progress ticks (once per second), so the extra allocation stays negligible.
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            // best-effort; a failed publish must not affect the build, but log it so a silently-dropped event is
            // visible in production rather than invisible.
            LogCalibrationWsPublishFailed(eventType, ex);
        }
    }

    // §63.8 — while a build blocks on its single RPC, poll get_dark_build_progress on its OWN short-lived
    // connection (PHD2Guider.SendMessage dials a fresh TcpClient per call, so this never contends with the
    // in-flight build) and forward each active tick as a progress WS event. The loop ends when the caller cancels
    // its linked token (build finished / failed / shutdown). A single poll fault is swallowed per-iteration and
    // polling resumes next tick — a dropped progress frame is cosmetic, and the build's own path reports real
    // faults; the loop never disturbs the build or escapes as an unobserved task exception.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Progress polling is best-effort telemetry running alongside the build: a poll RPC can throw arbitrary socket/IO/protocol exceptions (or the guider can drop mid-build), and none of that must disturb the build or surface as an unobserved task exception. Each fault is contained to its tick so the next tick can recover; cancellation (build done) ends the loop cleanly.")]
    private async Task PollBuildProgressAsync(PHD2Guider guider, string progressEvent, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                await Task.Delay(BuildProgressPollIntervalMs, ct).ConfigureAwait(false);
                var progress = await guider.GetDarkBuildProgressAsync(ct).ConfigureAwait(false);
                if (progress.Active) {
                    await EmitCalibrationEventAsync(progressEvent, new JsonObject {
                        ["exposure_index"] = progress.ExposureIndex,
                        ["exposure_count"] = progress.ExposureCount,
                        ["exposure_ms"] = progress.ExposureMs,
                        ["frame"] = progress.Frame,
                        ["frame_count"] = progress.FrameCount,
                    });
                }
            } catch (OperationCanceledException) {
                // Linked token cancelled → the build finished (or shutdown). Normal loop termination.
                return;
            } catch (Exception) {
                // Transient poll fault (e.g. a socket hiccup or a same-instant disconnect the build will report):
                // skip this tick and keep polling. The Task.Delay at the top bounds the retry to one per second.
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dark-library build started (frames={FrameCount}, clearExisting={ClearExisting})")]
    partial void LogDarkLibraryBuildStarted(int frameCount, bool clearExisting);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dark-library build failed")]
    partial void LogDarkLibraryBuildFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Calibration WS event '{EventType}' failed to publish")]
    partial void LogCalibrationWsPublishFailed(string eventType, Exception ex);

    // ── §63.6 guider-e-4c-b-2: defect-map (bad-pixel) build — mirrors the dark-library build above, sharing the
    // single calibration-build gate + the calibration WS-emit helper. ──

    public const string DefectMapStartedEvent = "guider.defect_map.started";
    public const string DefectMapProgressEvent = "guider.defect_map.progress";
    public const string DefectMapCompleteEvent = "guider.defect_map.complete";
    public const string DefectMapFailedEvent = "guider.defect_map.failed";

    public Task<OperationAcceptedDto> BuildDefectMapDarksAsync(
            BuildDefectMapDarksRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        // Validate synchronously so a bad request is a 400 now, not a background failure later.
        var rpcRequest = PHD2Guider.BuildDefectMapDarksRequest(
            request.ExposureMs, request.FrameCount, request.Notes, request.LoadAfter);
        // Reject if no guider is connected (→ 409) before accepting a 202 we can't honor.
        var guider = RequireConnectedGuider();
        const string op = "guider.defect_map.build";
        lock (_gate) {
            switch (ResolveBuildAdmission(_calibrationBuildInProgress, _calibrationBuildKey, _calibrationBuildOp, idempotencyKey, op)) {
                case BuildAdmission.IdempotentAccept:
                    return Task.FromResult(Accepted(op, idempotencyKey));
                case BuildAdmission.Reject:
                    // Shared gate: a dark-library build in flight also rejects a defect-map build (one capture).
                    throw new CalibrationBuildInProgressException();
                default: // Start
                    _calibrationBuildInProgress = true;
                    _calibrationBuildKey = idempotencyKey;
                    _calibrationBuildOp = op;
                    break;
            }
        }
        // Capture the token NOW (see BuildDarkLibraryAsync) so the deferred lambda can't
        // race Dispose and throw ObjectDisposedException reading _shutdownCts.Token.
        var defectToken = _shutdownCts.Token;
        DispatchCalibrationBuild(() => BuildDefectMapDarksInBackground(guider, rpcRequest, defectToken));
        return Task.FromResult(Accepted(op, idempotencyKey));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background build boundary: guider.BuildDefectMapDarksAsync can throw arbitrary socket/IO/protocol exceptions (and GuiderRpcException) over a long blocking RPC. The 202 already returned, so any escape must surface as the failed WS event and be contained — it must not become an unobserved task exception. Log-and-recover.")]
    private async Task BuildDefectMapDarksInBackground(PHD2Guider guider, Phd2BuildDefectMapDarks rpcRequest, CancellationToken ct) {
        var p = rpcRequest.Parameters!;
        // §63.8: poll defect-map build progress (get_dark_build_progress covers both build types) on its own
        // connection; the linked source cancels it the instant the build settles.
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pollTask = Task.CompletedTask;
        try {
            LogDefectMapBuildStarted(p.ExposureMs, p.FrameCount);
            await EmitCalibrationEventAsync(DefectMapStartedEvent, new JsonObject {
                ["exposure_ms"] = p.ExposureMs,
                ["frame_count"] = p.FrameCount,
                ["load_after"] = p.LoadAfter,
            });
            pollTask = PollBuildProgressAsync(guider, DefectMapProgressEvent, pollCts.Token);
            var result = await guider.BuildDefectMapDarksAsync(
                p.ExposureMs, p.FrameCount, p.Notes, p.LoadAfter, ct).ConfigureAwait(false);
            await EmitCalibrationEventAsync(DefectMapCompleteEvent, new JsonObject {
                ["profile_id"] = result.ProfileId,
                ["defect_map_path"] = result.DefectMapPath,
                ["defect_count"] = result.DefectCount,
                ["exposure_ms"] = result.ExposureMs,
                ["frame_count"] = result.FrameCount,
            });
        } catch (Exception ex) {
            LogDefectMapBuildFailed(ex);
            await EmitCalibrationEventAsync(DefectMapFailedEvent, new JsonObject {
                ["error"] = ex.Message,
            });
        } finally {
            // Stop the progress poll and let it unwind before releasing the gate (the await never throws —
            // PollBuildProgressAsync swallows its own cancellation).
            await pollCts.CancelAsync().ConfigureAwait(false);
            await pollTask.ConfigureAwait(false);
            lock (_gate) {
                ReleaseCalibrationGateLocked();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Defect-map build started (exposureMs={ExposureMs}, frames={FrameCount})")]
    partial void LogDefectMapBuildStarted(int exposureMs, int frameCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Defect-map build failed")]
    partial void LogDefectMapBuildFailed(Exception ex);
}

/// <summary>The shared calibration-build gate rejected a second concurrent build (e-4b-2 #384 r5
/// follow-up). Derives from <see cref="InvalidOperationException"/> so every existing catch keeps
/// working, but lets the endpoint give this case a distinct problem-detail <c>type</c> — the
/// client needs to tell "another build is running, wait" apart from "guider not connected,
/// connect it" (both were an indistinguishable 409 before).</summary>
public sealed class CalibrationBuildInProgressException : InvalidOperationException {
    public CalibrationBuildInProgressException() : base("a calibration build is already in progress") { }
    public CalibrationBuildInProgressException(string message) : base(message) { }
    public CalibrationBuildInProgressException(string message, Exception innerException) : base(message, innerException) { }
}
