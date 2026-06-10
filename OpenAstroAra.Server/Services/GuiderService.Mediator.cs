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
/// connection state; the IDeviceMediator lifecycle members stay inert (connection is REST-driven).
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

    // ── IDeviceMediator lifecycle — inert; connection is REST-driven ────────────────────────────────
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());
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
}
