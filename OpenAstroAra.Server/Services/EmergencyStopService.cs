// Copyright (c) OpenAstroTech contributors.
// Derived from N.I.N.A. — Nighttime Imaging 'N' Astronomy (MPL-2.0).
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §35.3 emergency stop — the one-button "make everything stop moving NOW"
/// path behind <c>POST /api/v1/server/emergency-stop</c>. Rungs, in order:
/// abort active sequence runs FIRST (so a still-running instruction can't
/// start a fresh exposure or slew behind the stop), then abort the in-flight
/// exposure, stop guiding, park the mount, and switch the flat panel's light
/// off. Every rung is best-effort — a dead device must never block the
/// remaining rungs — and the result reports honestly what each rung did.
/// The <c>safety.emergency_stop</c> WS event fires up-front so WILMA can
/// show the stop banner while the daemon works.
/// </summary>
public sealed partial class EmergencyStopService {
    private enum RungOutcome { Done, Failed, NotAvailable }

    private readonly ICameraService? _camera;
    private readonly Func<ISequencerService?>? _sequencerResolver;
    private readonly IGuiderService? _guider;
    private readonly ITelescopeService? _telescope;
    private readonly IFlatDeviceService? _flatDevice;
    private readonly INotificationService? _notifications;
    private readonly IWsBroadcaster? _ws;
    private readonly ILogger<EmergencyStopService> _logger;

    // Single-flight: a panicked double-press must not queue a second full
    // pass (each rung is idempotent, but a second park/abort volley just adds
    // noise while the first is mid-flight). 0 = idle, 1 = a stop is running.
    private int _running;

    public EmergencyStopService(
            ICameraService? camera = null,
            Func<ISequencerService?>? sequencerResolver = null,
            IGuiderService? guider = null,
            ITelescopeService? telescope = null,
            IFlatDeviceService? flatDevice = null,
            INotificationService? notifications = null,
            IWsBroadcaster? ws = null,
            ILogger<EmergencyStopService>? logger = null) {
        _camera = camera;
        _sequencerResolver = sequencerResolver;
        _guider = guider;
        _telescope = telescope;
        _flatDevice = flatDevice;
        _notifications = notifications;
        _ws = ws;
        _logger = logger ?? NullLogger<EmergencyStopService>.Instance;
    }

    /// <summary>
    /// Runs the full stop ladder. Deliberately takes no CancellationToken:
    /// the HTTP caller disconnecting must not cancel a half-finished stop.
    /// </summary>
    public async Task<EmergencyStopResultDto> ExecuteAsync() {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) {
            LogAlreadyRunning();
            return new EmergencyStopResultDto(
                AlreadyInProgress: true,
                RunsAborted: 0,
                ExposureAborted: false,
                GuidingStopped: false,
                ParkRequested: false,
                FlatPanelLightOff: false,
                FailedRungs: Array.Empty<string>());
        }
        try {
            return await ExecuteCoreAsync().ConfigureAwait(false);
        } finally {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<EmergencyStopResultDto> ExecuteCoreAsync() {
        LogTriggered();
        // Fires BEFORE the rungs so the client can alarm while the daemon works
        // — same contract as the §35.4 safety.unsafe event.
        await PublishAsync(WsEventCatalog.SafetyEmergencyStop, new JsonObject()).ConfigureAwait(false);

        // Failed = attempted but faulted; a device that isn't connected at all
        // is NOT a failure (a rig without a flat panel must not yell about it).
        var failedRungs = new List<string>();

        var (runsAborted, abortRunsOutcome) = await AbortRunsQuietlyAsync().ConfigureAwait(false);
        if (abortRunsOutcome is RungOutcome.Failed) { failedRungs.Add("abort_runs"); }
        var exposureOutcome = await RungAsync("abort exposure",
            _camera is null ? null : ct => _camera.AbortExposureAsync(ct)).ConfigureAwait(false);
        if (exposureOutcome is RungOutcome.Failed) { failedRungs.Add("abort_exposure"); }
        var guidingOutcome = await RungAsync("stop guiding",
            _guider is null ? null : async ct => await _guider.StopGuidingAsync(null, ct).ConfigureAwait(false)).ConfigureAwait(false);
        if (guidingOutcome is RungOutcome.Failed) { failedRungs.Add("stop_guiding"); }
        var parkOutcome = await RungAsync("park mount",
            _telescope is null ? null : async ct => await _telescope.ParkAsync(new ParkRequestDto("emergency_stop"), null, ct).ConfigureAwait(false)).ConfigureAwait(false);
        if (parkOutcome is RungOutcome.Failed) { failedRungs.Add("park"); }
        var flatOutcome = await RungAsync("flat panel light off",
            _flatDevice is null ? null : async ct => await _flatDevice.ApplyFlatPanelAsync(new FlatPanelRequestDto(LightOn: false), null, ct).ConfigureAwait(false)).ConfigureAwait(false);
        if (flatOutcome is RungOutcome.Failed) { failedRungs.Add("flat_panel_light_off"); }

        var exposureAborted = exposureOutcome is RungOutcome.Done;
        var guidingStopped = guidingOutcome is RungOutcome.Done;
        var parkRequested = parkOutcome is RungOutcome.Done;
        var flatLightOff = flatOutcome is RungOutcome.Done;

        LogCompleted(runsAborted, exposureAborted, guidingStopped, parkRequested, flatLightOff);

        var failedRungsJson = new JsonArray();
        foreach (var rung in failedRungs) { failedRungsJson.Add(rung); }
        await PublishAsync(WsEventCatalog.SafetyActionTaken, new JsonObject {
            ["action"] = "emergency_stop",
            ["runs_aborted"] = runsAborted,
            ["exposure_aborted"] = exposureAborted,
            ["guiding_stopped"] = guidingStopped,
            ["park_requested"] = parkRequested,
            ["flat_panel_light_off"] = flatLightOff,
            ["failed_rungs"] = failedRungsJson,
        }).ConfigureAwait(false);

        await NotifyQuietlyAsync(
            "Emergency stop executed",
            (runsAborted > 0 ? $"{runsAborted} running sequence(s) aborted, " : "No sequence was running, ")
            + (exposureAborted ? "the in-flight exposure was aborted, " : string.Empty)
            + (guidingStopped ? "guiding was stopped, " : string.Empty)
            + (parkRequested ? "the mount was told to park" : "no connected mount was parked")
            + (flatLightOff ? ", and the flat panel light was switched off" : string.Empty) + "."
            + (failedRungs.Count > 0
                ? $" ATTENTION: {failedRungs.Count} step(s) FAILED ({string.Join(", ", failedRungs)}) — check the rig manually."
                : string.Empty)).ConfigureAwait(false);

        return new EmergencyStopResultDto(
            AlreadyInProgress: false,
            RunsAborted: runsAborted,
            ExposureAborted: exposureAborted,
            GuidingStopped: guidingStopped,
            ParkRequested: parkRequested,
            FlatPanelLightOff: flatLightOff,
            FailedRungs: failedRungs);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort rung: the run abort must never block the hardware rungs behind it; logged, never rethrown.")]
    private async Task<(int Aborted, RungOutcome Outcome)> AbortRunsQuietlyAsync() {
        try {
            var sequencer = _sequencerResolver?.Invoke();
            if (sequencer is null) {
                return (0, RungOutcome.NotAvailable);
            }
            return (await sequencer.AbortActiveRunsAsync(CancellationToken.None).ConfigureAwait(false), RungOutcome.Done);
        } catch (Exception ex) {
            LogRungFailed("abort runs", ex);
            return (0, RungOutcome.Failed);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort rung: a dead device must never block the remaining rungs; the result DTO carries the honest false, logged, never rethrown.")]
    private async Task<RungOutcome> RungAsync(string rung, Func<CancellationToken, Task>? action) {
        if (action is null) {
            return RungOutcome.NotAvailable;
        }
        try {
            // CancellationToken.None deliberately: the caller disconnecting
            // must not cancel a half-finished stop.
            await action(CancellationToken.None).ConfigureAwait(false);
            return RungOutcome.Done;
        } catch (Exception ex) {
            LogRungFailed(rung, ex);
            return RungOutcome.Failed;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The event is advisory; a WS fault must not fail the stop itself. Logged, never rethrown.")]
    private async Task PublishAsync(string eventType, JsonObject payload) {
        if (_ws is null) {
            return;
        }
        try {
            // ToJsonString()+Parse (not JsonSerializer.SerializeToElement) is the
            // AOT-safe JsonElement construction — matches GuiderService/SequencerService.
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogWsPublishFailed(eventType, ex);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The notification is advisory; a store fault must not fail the stop itself. Logged, never rethrown.")]
    private async Task NotifyQuietlyAsync(string title, string message) {
        if (_notifications is null) {
            return;
        }
        try {
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: DateTimeOffset.UtcNow,
                Severity: NotificationSeverity.Critical,
                Category: NotificationCategory.Safety,
                Title: title,
                Message: message,
                Read: false,
                Dismissed: false,
                DismissedUtc: null,
                Payload: null,
                RelatedEntityType: null,
                RelatedEntityId: null), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogRungFailed("notify", ex);
        }
    }

    [LoggerMessage(EventId = 3531, Level = LogLevel.Critical, Message = "§35.3 emergency stop triggered")]
    private partial void LogTriggered();

    [LoggerMessage(EventId = 3532, Level = LogLevel.Information, Message = "§35.3 emergency stop already in progress — second trigger ignored")]
    private partial void LogAlreadyRunning();

    [LoggerMessage(EventId = 3533, Level = LogLevel.Information,
        Message = "§35.3 emergency stop completed: runsAborted={RunsAborted} exposureAborted={ExposureAborted} guidingStopped={GuidingStopped} parkRequested={ParkRequested} flatLightOff={FlatLightOff}")]
    private partial void LogCompleted(int runsAborted, bool exposureAborted, bool guidingStopped, bool parkRequested, bool flatLightOff);

    [LoggerMessage(EventId = 3534, Level = LogLevel.Warning, Message = "§35.3 emergency stop rung '{Rung}' failed")]
    private partial void LogRungFailed(string rung, Exception exception);

    [LoggerMessage(EventId = 3535, Level = LogLevel.Warning, Message = "§35.3 WS publish of {EventType} failed")]
    private partial void LogWsPublishFailed(string eventType, Exception exception);
}
