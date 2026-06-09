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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Equipment.Equipment.MyDome;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;

namespace OpenAstroAra.Server.Services.Equipment;

/// <summary>
/// §38k-18 — headless no-op stub for <see cref="IDomeMediator"/>.
/// Same pattern as the §38k-9 … §38k-13 stubs. Backs the dome instruction
/// prototypes (<c>OpenDomeShutter</c>, <c>CloseDomeShutter</c>, <c>ParkDome</c>,
/// <c>FindHomeDome</c>, <c>SlewDomeAzimuth</c>, <c>EnableDomeSynchronization</c>,
/// <c>DisableDomeSynchronization</c>). <c>SynchronizeDome</c> is deferred — it
/// also needs an <c>IDomeFollower</c>, which the headless daemon does not yet
/// wire.
/// </summary>
public sealed class HeadlessDomeMediator : IDomeMediator {

    private static readonly DomeInfo DisconnectedInfo = new() {
        Connected = false,
    };

    // IDeviceMediator surface
    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(IDomeConsumer consumer) { }
    public void RemoveConsumer(IDomeConsumer consumer) { }
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public void Broadcast(DomeInfo deviceInfo) { }
    public DomeInfo GetInfo() => DisconnectedInfo;
    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }
    public IDevice GetDevice() =>
        throw new NotSupportedException(
            "Headless dome stub has no backing IDevice; real Alpaca-backed wiring swaps in at the DI registration point once §14e Alpaca simulator pinning lands.");

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;

    // IDomeMediator additions — every operation reports "didn't succeed" /
    // "not following" off the not-connected stub. Sequence items that invoke
    // these see failure and bail out cleanly.
    public bool IsFollowingScope => false;
    public Task WaitForDomeSynchronization(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<bool> SyncToScopeCoordinates(Coordinates coordinates, PierSide sideOfPier, CancellationToken cancellationToken) =>
        Task.FromResult(false);
    public Task<bool> OpenShutter(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> CloseShutter(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> EnableFollowing(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> DisableFollowing(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> Park(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> FindHome(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SlewToAzimuth(double degrees, CancellationToken cancellationToken) => Task.FromResult(false);

    public event EventHandler<EventArgs>? Synced;
    public event Func<object, EventArgs, Task>? Opened;
    public event Func<object, EventArgs, Task>? Closed;
    public event Func<object, EventArgs, Task>? Parked;
    public event Func<object, EventArgs, Task>? Homed;
    public event Func<object, DomeEventArgs, Task>? Slewed;
}
