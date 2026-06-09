// Headless server stub: the device-event members in this file satisfy the
// equipment mediator interfaces but are never raised server-side (the Flutter
// client drives state over REST/WS), so CS0067 "event is never used" is
// expected here and intentionally suppressed for the whole file.
#pragma warning disable CS0067

#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Interfaces;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyGuider;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;

namespace OpenAstroAra.Server.Services.Equipment;

/// <summary>
/// §38k-11 — headless no-op stub for <see cref="IGuiderMediator"/>.
/// Same pattern as <see cref="HeadlessSafetyMonitorMediator"/> and
/// <see cref="HeadlessTelescopeMediator"/>: every operation returns a
/// "not connected" / "didn't succeed" sentinel; events declared but
/// never fired.
///
/// Used by the daemon's <see cref="HeadlessSequencerFactory"/> to
/// construct guider-aware telescope instructions like <c>ParkScope</c>
/// (which calls <c>guiderMediator.StopGuiding</c> before parking).
/// Real PHD2 wiring lands when §63 PHD2 lifecycle integration lands.
/// </summary>
public sealed class HeadlessGuiderMediator : IGuiderMediator {

    private static readonly GuiderInfo DisconnectedInfo = new() {
        Connected = false,
    };

    // IDeviceMediator surface
    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(IGuiderConsumer consumer) { }
    public void RemoveConsumer(IGuiderConsumer consumer) { }
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public void Broadcast(GuiderInfo deviceInfo) { }
    public GuiderInfo GetInfo() => DisconnectedInfo;
    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }
    public IDevice GetDevice() =>
        throw new NotSupportedException(
            "Headless guider stub has no backing IDevice; real Alpaca-backed wiring swaps in at the DI registration point once §14e Alpaca simulator pinning lands.");

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;

    // IGuiderMediator additions
    public Task<bool> Dither(CancellationToken token) => Task.FromResult(false);
    public Guid StartRMSRecording() => Guid.Empty;
    public RMS GetRMSRecording(Guid handle) => new RMS();
    public RMS StopRMSRecording(Guid handle) => new RMS();
    public Task<bool> StartGuiding(bool forceCalibration, IProgress<ApplicationStatus> progress, CancellationToken token) =>
        Task.FromResult(false);
    public Task<bool> StopGuiding(CancellationToken token) => Task.FromResult(false);
    public Task<bool> AutoSelectGuideStar(CancellationToken token) => Task.FromResult(false);
    public Task<bool> ClearCalibration(CancellationToken token) => Task.FromResult(false);
    public Task<bool> SetShiftRate(SiderealShiftTrackingRate shiftTrackingRate, CancellationToken ct) =>
        Task.FromResult(false);
    public Task<bool> StopShifting(CancellationToken ct) => Task.FromResult(false);
    public LockPosition GetLockPosition() => new LockPosition(0f, 0f);

    public event Func<object, EventArgs, Task>? Dithered;
    public event EventHandler<IGuideStep>? GuideEvent;
    public event Func<object, EventArgs, Task>? GuidingStarted;
    public event Func<object, EventArgs, Task>? GuidingStopped;
}