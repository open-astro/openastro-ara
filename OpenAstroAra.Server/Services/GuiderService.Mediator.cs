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
            };
        }
    }

    // ── Guider ops drive the live device ───────────────────────────────────────────────────────────
    public Task<bool> StartGuiding(bool forceCalibration, IProgress<ApplicationStatus> progress, CancellationToken token) =>
        MediatorGuider()?.StartGuiding(forceCalibration, progress ?? _noProgress, token) ?? Task.FromResult(false);

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
    public async Task<bool> Connect() {
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

        if (pass is not null) {
            LogMediatorAwaitingRecovery(graceSeconds);
            // RunRecoveryAsync contains its own faults, so the pass task never faults; WhenAny just
            // bounds the wait.
            await Task.WhenAny(pass, Task.Delay(window)).ConfigureAwait(false);
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

        try {
            // Null host/port keep the profile's target. Never supersede a recovery pass: the mediator
            // isn't the user, and a pass that started after our check must keep running.
            await ConnectCoreAsync(new GuiderConnectRequestDto(null, null), idempotencyKey: null, supersedeRecovery: false)
                .ConfigureAwait(false);
        } catch (ObjectDisposedException) {
            return false;
        }
        return await WaitForConnectSettledAsync(DateTimeOffset.UtcNow + window).ConfigureAwait(false);
    }

    // Poll the 202-style background connect until it settles (Connected → true; Error/Disconnected →
    // false) or the deadline passes (false; the attempt keeps running and settles on its own).
    private async Task<bool> WaitForConnectSettledAsync(DateTimeOffset deadline) {
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
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    public async Task Disconnect() {
        lock (_gate) {
            if (_disposed) {
                return;
            }
        }
        await DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
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
