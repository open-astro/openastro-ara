#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

// CS0067: the IDeviceMediator events below have no headless subscriber — connection lifecycle and
// guide-step consumption are driven through the REST surface + §60.9 WS stream, not these events.
#pragma warning disable CS0067

using Microsoft.Extensions.Logging;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Interfaces;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyGuider;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §63 guider-c — <see cref="GuiderService"/> also serves the Sequencer's <see cref="IGuiderMediator"/>,
/// replacing <c>HeadlessGuiderMediator</c>. One singleton backs both the REST <c>IGuiderService</c> and
/// this mediator (per §8.1), so the sequencer's <c>StartGuiding</c>/<c>StopGuiding</c>/<c>Dither</c>
/// instructions drive the live PHD2 guider instead of no-op stubs. <c>GetInfo()</c> reports the live
/// connection state. <c>Connect()</c>/<c>Disconnect()</c>/<c>Rescan()</c> back the sequencer's
/// ConnectEquipment/ReconnectTrigger (#1123): Connect waits on an in-flight §63.3 recovery pass before
/// falling back to the REST connect path.
/// </summary>
public sealed partial class GuiderService : IGuiderMediator {

    // The live guider iff connected, else null — guide ops return false when not connected (the
    // sequencer's attempt policy handles false; it does not expect a throw here).
    private PHD2Guider? MediatorGuider() {
        lock (_gate) {
            return !_disposed && _state == EquipmentConnectionState.Connected ? _guider : null;
        }
    }

    public GuiderInfo GetInfo() {
        lock (_gate) {
            var connected = !_disposed && _state == EquipmentConnectionState.Connected && _guider is not null;
            return new GuiderInfo {
                Connected = connected,
                Guiding = connected && IsGuidingState(_guider!.State),
                Name = connected ? "PHD2" : string.Empty,
                DeviceId = connected ? "PHD2_Single" : string.Empty,
                PixelScale = connected ? _guider!.PixelScale : 0,
                // #1123 — StartGuiding(ForceCalibration) validates against this; PHD2 can clear it.
                CanClearCalibration = connected && _guider!.CanClearCalibration,
                // #1228 — PHD2 supports lock-position shifting; nothing validates on it today, but the
                // info should not contradict the device.
                CanSetShiftRate = connected && _guider!.CanSetShiftRate,
            };
        }
    }

    /// <summary>#1364 — the profile's guider-advanced block for every push; handed to each new guider
    /// connection. Program.cs wires it to the profile store; null pushes none.</summary>
    public Func<OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.GuiderAdvancedSettings?>? AdvancedSettings { get; set; }

    // ── Guider ops drive the live device ───────────────────────────────────────────────────────────
    public async Task<bool> StartGuiding(bool forceCalibration, IProgress<ApplicationStatus> progress, CancellationToken token) {
        var guider = MediatorGuider();
        if (guider is null) {
            return false;
        }
        ThrowIfOutOfStep("A run's Start Guiding");
        await Custody.TakeForGuidingAsync(this, token).ConfigureAwait(false);
        return await guider.StartGuiding(forceCalibration, progress ?? _noProgress, token).ConfigureAwait(false);
    }

    internal Func<DateTimeOffset> CalibrationNightClock { get; set; } = () => DateTimeOffset.UtcNow;

    private const string CalibrationFailedEvent = "guider.calibration.failed";

    private DateTimeOffset? _calibratedNight; // guarded by _gate

    public async Task<bool> RecalibrateAndGuide(IProgress<ApplicationStatus> progress, CancellationToken token) {
        if (await StartGuiding(forceCalibration: true, progress, token).ConfigureAwait(false)) {
            await RecordCalibrationOutcomeQuietlyAsync(failed: false).ConfigureAwait(false);
            return true;
        }
        // The guider erased its calibration before it calibrated, so the earlier one has to be put back first.
        var guider = MediatorGuider();
        if (guider is null
            || !await guider.RestoreCalibrationAsync(token).ConfigureAwait(false)
            || !await StartGuiding(forceCalibration: false, progress, token).ConfigureAwait(false)) {
            return false;
        }
        await RecordCalibrationOutcomeQuietlyAsync(failed: true).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> StartGuidingCalibratedTonight(IProgress<ApplicationStatus> progress, CancellationToken token) {
        var night = PreflightChecklistService.NightStart(CalibrationNightClock());
        bool calibrated;
        lock (_gate) {
            calibrated = _calibratedNight == night;
        }
        if (calibrated) {
            return await StartGuiding(forceCalibration: false, progress, token).ConfigureAwait(false);
        }
        var started = await RecalibrateAndGuide(progress, token).ConfigureAwait(false);
        if (started) {
            lock (_gate) {
                _calibratedNight = night;
            }
        }
        return started;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A diagnostics store fault must never undo a guiding start that worked; log-and-recover boundary like the other diagnostic helpers.")]
    private async Task RecordCalibrationOutcomeQuietlyAsync(bool failed) {
        if (failed) {
            await NotifyQuietlyAsync(NotificationSeverity.Warning, "Guider calibration failed",
                "The guider could not calibrate, so it is guiding on its earlier calibration.").ConfigureAwait(false);
        }
        if (_diagnostics is null) {
            return;
        }
        try {
            if (failed) {
                await _diagnostics.CreateEventAsync(
                    new DiagnosticEventDto(Guid.NewGuid(), CalibrationFailedEvent, DiagnosticHealth.Yellow,
                        "The guider could not calibrate, so it is guiding on its earlier calibration.", DateTimeOffset.UtcNow,
                        ClearedUtc: null, AutoActionTaken: true, AutoActionDescription: "Restored the earlier calibration."),
                    recommendedAction: "Check the guide star and the mount's guide output, then calibrate again.",
                    autoCorrectible: false, CancellationToken.None).ConfigureAwait(false);
            } else {
                await _diagnostics.ClearOpenEventsByTypeAsync(CalibrationFailedEvent, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            }
        } catch (Exception ex) {
            LogDiagnosticRecordFailed(ex);
        }
    }

    public DateTimeOffset? GuidingStoppedUtc {
        get {
            lock (_gate) {
                return _guidingStoppedUtc;
            }
        }
    }

    public Task<bool> StopGuiding(CancellationToken token) =>
        MediatorGuider()?.StopGuiding(token) ?? Task.FromResult(false);

    public async Task<bool> Dither(CancellationToken token) {
        var guider = MediatorGuider();
        if (guider is null) {
            // No connected guider: a plain refusal, so the previous dither's verdict can't be read as
            // this one's (the Dither instruction then reports "not connected").
            LastDitherError = "refused";
            return false;
        }
        var sent = await guider.Dither(_noProgress, token).ConfigureAwait(false);
        // #1156 — the sequencer's dither reports its terminal status too (no operation id:
        // nothing was accepted over REST), so the Live tab sees a settle failure as it happens.
        // An abort mid-settle makes Dither() return true with no verdict (WaitForSettling
        // swallows the cancellation) — that is not a settled dither (#1351 r1 note).
        // A refusal carries the guider's own reason when it has one (the dither RPC errored); a plain
        // "refused" means it was not guiding.
        var error = !sent ? (guider.LastSettleError ?? "refused")
            : token.IsCancellationRequested ? "cancelled"
            : guider.LastSettleError;
        // The verdict is set BEFORE the publish so a publish fault can't leave the previous one in place.
        LastDitherError = error;
        await PublishDitherCompleteAsync(operationId: null, ok: sent && error is null, error).ConfigureAwait(false);
        return sent;
    }

    public string? LastDitherError { get; private set; }

    public Task<bool> AutoSelectGuideStar(CancellationToken token) =>
        MediatorGuider()?.AutoSelectGuideStar() ?? Task.FromResult(false);

    public Task<bool> ClearCalibration(CancellationToken token) =>
        MediatorGuider()?.ClearCalibration(token) ?? Task.FromResult(false);

    public Task<bool> SetShiftRate(SiderealShiftTrackingRate shiftTrackingRate, CancellationToken ct) =>
        MediatorGuider()?.SetShiftRate(shiftTrackingRate, ct) ?? Task.FromResult(false);

    public Task<bool> StopShifting(CancellationToken ct) =>
        MediatorGuider()?.StopShifting(ct) ?? Task.FromResult(false);

    // GetLockPosition is sync on the mediator but async on the client; no registered sequence
    // instruction reads it, so return the inert origin rather than a deadlock-prone sync-over-async.
    public LockPosition GetLockPosition() => new(0f, 0f);

    // Mediator-side RMS recording (handle-based) has no PHD2Guider counterpart and no registered
    // headless instruction consumer; the live RMS is exposed via the REST GuiderStateDto instead.
    public Guid StartRMSRecording() => Guid.Empty;
    public RMS GetRMSRecording(Guid handle) => new();
    public RMS StopRMSRecording(Guid handle) => new();

    // ── IDeviceMediator lifecycle (#1123) ──────────────────────────────────────────────────────────

    /// <summary>
    /// The sequencer's connect (ConnectEquipment / ReconnectTrigger). A PHD2 socket drop leaves the
    /// guider in Error with a §63.3 recovery pass already reconnecting in the background, and the
    /// trigger fires in that window. So: wait on the in-flight pass (bounded by the profile's retry
    /// timeout) instead of starting a second one, and only if it ended without reconnecting (a
    /// non-systemd host returns Unsupervised; a failed recovery) fall back to a plain reconnect over
    /// the REST connect path. A pass still running at the deadline is left alone — it keeps owning the
    /// reconnect and its notifications — and the connect reports failure.
    /// </summary>
    public Task<bool> Connect() => Connect(CancellationToken.None);

    /// <summary>
    /// <see cref="Connect()"/> with the sequencer's token (#1228): ConnectEquipment passes its own, so a
    /// Stop/Abort during the wait cancels it instead of waiting out the window. One deadline is fixed at
    /// entry and bounds both the wait on the in-flight pass and the fallback connect, so a pass that ends
    /// without reconnecting cannot start a second full window ("up to the retry timeout" means once).
    /// </summary>
    public async Task<bool> Connect(CancellationToken token) {
        Task? pass;
        lock (_gate) {
            if (_disposed) {
                return false;
            }
            if (_state == EquipmentConnectionState.Connected && _guider is not null) {
                return true;
            }
            pass = _recovering ? _recoveryPassTask : null;
        }
        var (graceSeconds, window) = ReconnectGraceWindow();
        var deadline = DateTimeOffset.UtcNow + window;

        if (pass is not null) {
            LogMediatorAwaitingRecovery(graceSeconds);
            // RunRecoveryAsync contains its own faults, so the pass task never faults; WhenAny just
            // bounds the wait. The timer is cancelled once the pass wins so it does not stay armed.
            using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var timer = Task.Delay(window, timerCts.Token);
            var winner = await Task.WhenAny(pass, timer).ConfigureAwait(false);
            if (winner == timer) {
                token.ThrowIfCancellationRequested();
            } else {
                await timerCts.CancelAsync().ConfigureAwait(false);
            }
            lock (_gate) {
                if (_disposed) {
                    return false;
                }
                if (_state == EquipmentConnectionState.Connected && _guider is not null) {
                    return true;
                }
                if (_recovering) {
                    LogMediatorRecoveryStillRunning(graceSeconds);
                    return false;
                }
            }
        }

        token.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= deadline) {
            // The pass used the whole window: a fallback connect would only run past it in the background.
            return false;
        }
        try {
            // Null host/port keep the profile's target. Never supersede a recovery pass: the mediator
            // isn't the user, and a pass that started after our check must keep running.
            await ConnectCoreAsync(new GuiderConnectRequestDto(null, null), idempotencyKey: null, supersedeRecovery: false)
                .ConfigureAwait(false);
        } catch (ObjectDisposedException) {
            return false;
        }
        return await WaitForConnectSettledAsync(deadline, token).ConfigureAwait(false);
    }

    // Poll the 202-style background connect until it settles (Connected → true; Error/Disconnected →
    // false) or the deadline passes (false; the attempt keeps running and settles on its own).
    private async Task<bool> WaitForConnectSettledAsync(DateTimeOffset deadline, CancellationToken token) {
        while (true) {
            lock (_gate) {
                if (_disposed) {
                    return false;
                }
                if (_state == EquipmentConnectionState.Connected && _guider is not null) {
                    return true;
                }
                if (_state is EquipmentConnectionState.Error or EquipmentConnectionState.Disconnected) {
                    return false;
                }
            }
            if (DateTimeOffset.UtcNow >= deadline) {
                return false;
            }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    /// <summary>Test seam (#1228): runs between <see cref="Disconnect"/>'s disposed check and the disconnect
    /// call, where a concurrent <see cref="Dispose"/> lands in the race the catch below covers.</summary>
    internal Action? BeforeMediatorDisconnect { get; set; }

    public async Task Disconnect() {
        lock (_gate) {
            if (_disposed) {
                return;
            }
        }
        BeforeMediatorDisconnect?.Invoke();
        try {
            await DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
        } catch (ObjectDisposedException) {
            // Dispose() landed between the check above and the call (#1228): disposed is disconnected.
        }
    }

    // ARA drives exactly one guider (PHD2 over §63.5). ConnectEquipment only calls Connect() when the
    // profile's guider id is in this list, so report the configured one; "No_Guider" means none.
    public Task<IList<string>> Rescan() {
        var name = _profileService.ActiveProfile.GuiderSettings.GuiderName;
        IList<string> ids = string.IsNullOrWhiteSpace(name) || name == "No_Guider"
            ? new List<string>()
            : new List<string> { name };
        return Task.FromResult(ids);
    }
    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(IGuiderConsumer consumer) { }
    public void RemoveConsumer(IGuiderConsumer consumer) { }
    public void Broadcast(GuiderInfo deviceInfo) { }
    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }

    public IDevice GetDevice() =>
        throw new NotSupportedException(
            "GuiderService does not expose a raw IDevice; the Sequencer uses GetInfo()/StartGuiding/StopGuiding/Dither and connection is driven through the REST surface.");

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;
    public event Func<object, EventArgs, Task>? Dithered;
    public event EventHandler<IGuideStep>? GuideEvent;
    public event Func<object, EventArgs, Task>? GuidingStarted;
    public event Func<object, EventArgs, Task>? GuidingStopped;

    [LoggerMessage(EventId = 6339, Level = LogLevel.Information, Message = "Sequencer guider connect: waiting up to {GraceSeconds}s for the in-flight §63.3 recovery")]
    private partial void LogMediatorAwaitingRecovery(int graceSeconds);

    [LoggerMessage(EventId = 6340, Level = LogLevel.Warning, Message = "Sequencer guider connect: §63.3 recovery still running after {GraceSeconds}s; reporting failure and leaving recovery in charge")]
    private partial void LogMediatorRecoveryStillRunning(int graceSeconds);
}
