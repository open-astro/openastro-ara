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

    /// <summary>
    /// Hands the guide camera back before guiding starts: the Setup → Smart Focus live-focus loop
    /// borrows it through the guider's polar-align lease, and while that lease is held the guider
    /// answers a guide request with "polar-alignment session in progress" — a sequence's Start
    /// Guiding then failed and the night ran unguided with every dither skipped (2026-10-03).
    /// Program.cs wires this to stop a running live-focus loop; null means nothing to release.
    /// </summary>
    public Func<Task>? ReleaseGuideCameraAsync { get; set; }

    /// <summary>True while this daemon's polar alignment runs — its lease is real and is never pulled
    /// from under it. Program.cs wires it to <see cref="PolarAlignService.IsActive"/>; null means none.</summary>
    public Func<bool>? PolarAlignActive { get; set; }

    /// <summary>The guider reports a lease and no polar alignment in this daemon holds it: a stale lease
    /// (or one the live-focus loop left behind) that would make the guider refuse to guide.</summary>
    internal bool ShouldReleasePaLease(bool? guiderReportsActive) =>
        guiderReportsActive == true && PolarAlignActive?.Invoke() != true;

    // ── Guider ops drive the live device ───────────────────────────────────────────────────────────
    public async Task<bool> StartGuiding(bool forceCalibration, IProgress<ApplicationStatus> progress, CancellationToken token) {
        var guider = MediatorGuider();
        if (guider is null) {
            return false;
        }
        await ReleaseGuideCameraQuietlyAsync().ConfigureAwait(false);
        await ClearPaSessionQuietlyAsync(guider, token).ConfigureAwait(false);
        return await guider.StartGuiding(forceCalibration, progress ?? _noProgress, token).ConfigureAwait(false);
    }

    /// <summary>
    /// The guider's polar-align lease outlives whoever took it: a live-focus loop that died with a daemon
    /// restart left "polar-alignment session in progress" standing with nothing holding it, and every
    /// guide request after that was refused (2026-10-03, twice). Guiding is about to own the guide
    /// camera anyway, so the lease is released — unless this daemon's polar alignment is running and
    /// holds it (<see cref="ShouldReleasePaLease"/>); a refusal here is logged, not fatal.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort lease release before guiding: the guide attempt reports its own outcome.")]
    private async Task ClearPaSessionQuietlyAsync(PHD2Guider guider, CancellationToken token) {
        try {
            var status = await guider.GetPaSessionAsync(token).ConfigureAwait(false);
            if (!ShouldReleasePaLease(status.Active)) {
                if (status.Active == true) {
                    LogPaSessionKept();
                }
                return;
            }
            await guider.SetPaSessionAsync(active: false, timeoutS: null, token).ConfigureAwait(false);
            LogPaSessionCleared();
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            LogPaSessionClearFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Released the guider's polar-align session before guiding (guiding takes the guide camera from whoever held it)")]
    partial void LogPaSessionCleared();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polar alignment is running — its guider session is kept, and the guider will refuse to guide until it ends")]
    partial void LogPaSessionKept();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release the guider's polar-align session before guiding — trying to guide anyway")]
    partial void LogPaSessionClearFailed(Exception ex);

    /// <summary>Runs <see cref="ReleaseGuideCameraAsync"/>; a fault there is logged, never a reason not to guide.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort release of a borrowed camera: the guide attempt itself reports its own outcome.")]
    internal async Task ReleaseGuideCameraQuietlyAsync() {
        if (ReleaseGuideCameraAsync is not { } release) {
            return;
        }
        try {
            await release().ConfigureAwait(false);
        } catch (Exception ex) {
            LogGuideCameraReleaseFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Releasing the guide camera before guiding failed — trying to guide anyway")]
    partial void LogGuideCameraReleaseFailed(Exception ex);

    public Task<bool> StopGuiding(CancellationToken token) =>
        MediatorGuider()?.StopGuiding(token) ?? Task.FromResult(false);

    public Task<bool> Dither(CancellationToken token) =>
        MediatorGuider()?.Dither(_noProgress, token) ?? Task.FromResult(false);

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

    public async Task Disconnect() {
        lock (_gate) {
            if (_disposed) {
                return;
            }
        }
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
