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
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Equipment.MyFilterWheel;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;

namespace OpenAstroAra.Server.Services.Equipment;

/// <summary>
/// §38k-15 — headless no-op stub for <see cref="IFilterWheelMediator"/>.
/// Same pattern as the §38k-9 … §38k-13 stubs. DI-registered to complete the
/// equipment-mediator stub surface; the filter-change instruction
/// (<c>SwitchFilter</c>) is NOT yet registered because it also depends on
/// <c>IProfileService</c> (for the configured filter list), which the headless
/// daemon does not yet wire — that lands in a follow-up §38k sub-PR.
/// </summary>
public sealed class HeadlessFilterWheelMediator : IFilterWheelMediator {

    private static readonly FilterWheelInfo DisconnectedInfo = new() {
        Connected = false,
    };

    // IDeviceMediator surface
    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(IFilterWheelConsumer consumer) { }
    public void RemoveConsumer(IFilterWheelConsumer consumer) { }
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public void Broadcast(FilterWheelInfo deviceInfo) { }
    public FilterWheelInfo GetInfo() => DisconnectedInfo;
    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }
    public IDevice GetDevice() =>
        throw new NotSupportedException(
            "Headless filter-wheel stub has no backing IDevice; real Alpaca-backed wiring swaps in at the DI registration point once §14e Alpaca simulator pinning lands.");

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;

    // IFilterWheelMediator addition — a no-op "change" echoes the requested
    // filter back as the now-current filter (nothing physically moves with no
    // driver attached). Real impls return the wheel's reported position.
    public Task<FilterInfo> ChangeFilter(FilterInfo inputFilter, IProgress<ApplicationStatus>? progress = null, CancellationToken token = default) =>
        Task.FromResult(inputFilter);

    public event Func<object, FilterChangedEventArgs, Task>? FilterChanged;
}
