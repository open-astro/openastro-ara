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

using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyFlatDevice;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;

namespace OpenAstroAra.Server.Services.Equipment;

/// <summary>
/// §38k-19 — headless no-op stub for <see cref="IFlatDeviceMediator"/>.
/// Same pattern as the §38k-9 … §38k-18 stubs. Completes the device-mediator
/// stub set together with <see cref="HeadlessWeatherDataMediator"/>; both are
/// dependencies of the disconnect instruction prototypes
/// (<c>DisconnectAllEquipment</c>, <c>DisconnectEquipment</c>). There are no
/// flat-device sequence instructions in the tree, so this stub has no
/// instruction of its own.
/// </summary>
public sealed class HeadlessFlatDeviceMediator : IFlatDeviceMediator {

    private static readonly FlatDeviceInfo DisconnectedInfo = new() {
        Connected = false,
    };

    // IDeviceMediator surface
    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(IFlatDeviceConsumer consumer) { }
    public void RemoveConsumer(IFlatDeviceConsumer consumer) { }
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public void Broadcast(FlatDeviceInfo deviceInfo) { }
    public FlatDeviceInfo GetInfo() => DisconnectedInfo;
    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }
    public IDevice GetDevice() =>
        throw new NotSupportedException(
            "Headless flat-device stub has no backing IDevice; real Alpaca-backed wiring swaps in at the DI registration point once §14e Alpaca simulator pinning lands.");

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;

    // IFlatDeviceMediator additions — cover/brightness ops are no-ops with no
    // driver attached.
    public Task SetBrightness(int brightness, IProgress<ApplicationStatus> progress, CancellationToken token) => Task.CompletedTask;
    public Task CloseCover(IProgress<ApplicationStatus> progress, CancellationToken token) => Task.CompletedTask;
    public Task ToggleLight(bool onOff, IProgress<ApplicationStatus> progress, CancellationToken token) => Task.CompletedTask;
    public Task OpenCover(IProgress<ApplicationStatus> progress, CancellationToken token) => Task.CompletedTask;

    public event Func<object, EventArgs, Task>? Opened;
    public event Func<object, EventArgs, Task>? Closed;
    public event Func<object, FlatDeviceBrightnessChangedEventArgs, Task>? BrightnessChanged;
    public event Func<object, EventArgs, Task>? LightToggled;
}
