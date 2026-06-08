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

using OpenAstroAra.Equipment.Equipment.MyFocuser;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OxyPlot;

namespace OpenAstroAra.Server.Services.Equipment;

/// <summary>
/// §38k-12 — headless no-op stub for <see cref="IFocuserMediator"/>.
/// Same pattern as the §38k-9 / §38k-10 / §38k-11 stubs. Used by the
/// daemon's <see cref="HeadlessSequencerFactory"/> to construct
/// focuser-bound instruction prototypes (<c>MoveFocuserAbsolute</c>,
/// <c>MoveFocuserRelative</c>, <c>MoveFocuserByTemperature</c>).
/// </summary>
public sealed class HeadlessFocuserMediator : IFocuserMediator {

    private static readonly FocuserInfo DisconnectedInfo = new() {
        Connected = false,
    };

    // IDeviceMediator surface
    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(IFocuserConsumer consumer) { }
    public void RemoveConsumer(IFocuserConsumer consumer) { }
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public void Broadcast(FocuserInfo deviceInfo) { }
    public FocuserInfo GetInfo() => DisconnectedInfo;
    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }
    public IDevice GetDevice() => null!;

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;

    // IFocuserMediator additions — focuser-position ops return the
    // current position the focuser claims to be at; with no real driver
    // attached we report 0. Real impls return the device's reported
    // post-move position.
    public void ToggleTempComp(bool tempComp) { }
    public Task<int> MoveFocuser(int position, CancellationToken ct) => Task.FromResult(0);
    public Task<int> MoveFocuserRelative(int position, CancellationToken ct) => Task.FromResult(0);
    public Task<int> MoveFocuserByTemperatureRelative(double temperature, double Slope, CancellationToken ct) =>
        Task.FromResult(0);
    public void BroadcastSuccessfulAutoFocusRun(AutoFocusInfo info) { }
    public void BroadcastNewAutoFocusPoint(DataPoint dataPoint) { }
    public void BroadcastUserFocused(FocuserInfo info) { }
    public void BroadcastAutoFocusRunStarting() { }
}