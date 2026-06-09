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

using OpenAstroAra.Equipment.Equipment.MyWeatherData;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;

namespace OpenAstroAra.Server.Services.Equipment;

/// <summary>
/// §38k-20 — headless no-op stub for <see cref="IWeatherDataMediator"/>.
/// Same pattern as the §38k-9 … §38k-18 stubs. <see cref="IWeatherDataMediator"/>
/// adds nothing beyond the <c>IDeviceMediator</c> base. Completes the
/// device-mediator stub set together with <see cref="HeadlessFlatDeviceMediator"/>;
/// both are dependencies of the disconnect instruction prototypes
/// (<c>DisconnectAllEquipment</c>, <c>DisconnectEquipment</c>).
/// </summary>
public sealed class HeadlessWeatherDataMediator : IWeatherDataMediator {

    private static readonly WeatherDataInfo DisconnectedInfo = new() {
        Connected = false,
    };

    // IDeviceMediator surface — IWeatherDataMediator declares no additional members.
    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(IWeatherDataConsumer consumer) { }
    public void RemoveConsumer(IWeatherDataConsumer consumer) { }
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public void Broadcast(WeatherDataInfo deviceInfo) { }
    public WeatherDataInfo GetInfo() => DisconnectedInfo;
    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }
    public IDevice GetDevice() =>
        throw new NotSupportedException(
            "Headless weather-data stub has no backing IDevice; real Alpaca-backed wiring swaps in at the DI registration point once §14e Alpaca simulator pinning lands.");

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;
}
