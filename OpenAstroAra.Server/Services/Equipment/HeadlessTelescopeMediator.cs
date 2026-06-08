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
using OpenAstroAra.Core.Enum;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;

namespace OpenAstroAra.Server.Services.Equipment;

/// <summary>
/// §38k-10 — headless no-op stub for <see cref="ITelescopeMediator"/>.
/// Matches <see cref="HeadlessSafetyMonitorMediator"/>'s pattern: every
/// operation returns a "not connected" / "didn't succeed" sentinel; events
/// are declared but never fired. Used by the daemon's
/// <see cref="HeadlessSequencerFactory"/> to construct telescope-bound
/// sequence-item prototypes (<see cref="OpenAstroAra.Sequencer.SequenceItem.Telescope.SetTracking"/>)
/// in §38k-10. Real Alpaca-backed wiring swaps in at the DI registration
/// point once §14e Alpaca simulator pinning lands.
/// </summary>
public sealed class HeadlessTelescopeMediator : ITelescopeMediator {

    private static readonly TelescopeInfo DisconnectedInfo = new() {
        Connected = false,
    };

    // IDeviceMediator surface
    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(ITelescopeConsumer consumer) { }
    public void RemoveConsumer(ITelescopeConsumer consumer) { }
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public void Broadcast(TelescopeInfo deviceInfo) { }
    public TelescopeInfo GetInfo() => DisconnectedInfo;
    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }
    public IDevice GetDevice() => null!;

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;

    // ITelescopeMediator additions — every operation is a no-op that
    // reports failure. Sequence items that invoke these (Validate /
    // Execute) will see "not connected" and bail out cleanly.

    public void MoveAxis(TelescopeAxes axis, double rate) { }
    public void PulseGuide(GuideDirections direction, int duration) { }
    public Task<bool> Sync(Coordinates coordinates) => Task.FromResult(false);

    public Task<bool> SlewToCoordinatesAsync(Coordinates coords, CancellationToken token) =>
        Task.FromResult(false);

    [Obsolete]
    public Task<bool> SlewToCoordinatesAsync(TopocentricCoordinates coords, CancellationToken token) =>
        Task.FromResult(false);

    public Task<bool> SlewToTopocentricCoordinates(TopocentricCoordinates coords, CancellationToken token) =>
        Task.FromResult(false);

    public Task<bool> MeridianFlip(Coordinates targetCoordinates, CancellationToken token) =>
        Task.FromResult(false);

    public bool SetTrackingEnabled(bool trackingEnabled) => false;
    public bool SetTrackingMode(TrackingMode trackingMode) => false;
    public bool SetCustomTrackingRate(SiderealShiftTrackingRate rate) => false;
    public bool SendToSnapPort(bool start) => false;

    public Coordinates GetCurrentPosition() =>
        new Coordinates(Angle.ByDegree(0), Angle.ByDegree(0), Epoch.J2000);

    public Task<bool> ParkTelescope(IProgress<ApplicationStatus> progress, CancellationToken token) =>
        Task.FromResult(false);

    public Task<bool> UnparkTelescope(IProgress<ApplicationStatus> progress, CancellationToken token) =>
        Task.FromResult(false);

    public Task WaitForSlew(CancellationToken token) => Task.CompletedTask;

    public Task<bool> FindHome(IProgress<ApplicationStatus> progress, CancellationToken token) =>
        Task.FromResult(false);

    public void StopSlew() { }

    public PierSide DestinationSideOfPier(Coordinates coordinates) => PierSide.pierUnknown;

    public event Func<object, BeforeMeridianFlipEventArgs, Task>? BeforeMeridianFlip;
    public Task RaiseBeforeMeridianFlip(BeforeMeridianFlipEventArgs e) => Task.CompletedTask;

    public event Func<object, AfterMeridianFlipEventArgs, Task>? AfterMeridianFlip;
    public Task RaiseAfterMeridianFlip(AfterMeridianFlipEventArgs e) => Task.CompletedTask;

    public event Func<object, EventArgs, Task>? Parked;
    public event Func<object, EventArgs, Task>? Homed;
    public event Func<object, EventArgs, Task>? Unparked;
    public event Func<object, MountSlewedEventArgs, Task>? Slewed;
}