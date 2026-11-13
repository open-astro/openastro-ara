#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Alpaca.Clients;
using ASCOM.Common.DeviceInterfaces;
using OpenAstroAra.Server.Contracts;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace OpenAstroAra.Server.Services;

// ADR 0011 rule 4 — the Alpaca adapter of the Telescope. It holds the Alpaca client, sends the calls the
// service used to send, and is the only place that builds one (ADR 0003). Tests hand the service a fake.

internal interface ITelescopeDevice : IDeviceAdapter {
    bool Slewing { get; }
    double RightAscension { get; }
    double Declination { get; }
    double TargetRightAscension { get; }
    double TargetDeclination { get; }
    DriveRate TrackingRate { get; set; }
    bool Tracking { get; set; }
    bool AtPark { get; }
    bool AtHome { get; }
    bool IsPulseGuiding { get; }
    PointingState SideOfPier { get; set; }
    EquatorialCoordinateType EquatorialSystem { get; }
    bool CanSlewAsync { get; }
    bool CanSlew { get; }
    bool CanSync { get; }
    bool CanPark { get; }
    bool CanUnpark { get; }
    bool CanSetTracking { get; }
    bool CanPulseGuide { get; }
    bool CanFindHome { get; }
    bool CanSetPierSide { get; }
    double FocalLength { get; }
    double ApertureDiameter { get; }
    ITrackingRates TrackingRates { get; }
    IList<string> SupportedActions { get; }
    double SiteLatitude { get; set; }
    double SiteLongitude { get; set; }
    double SiteElevation { get; set; }
    bool CanMoveAxis(TelescopeAxis axis);
    IAxisRates AxisRates(TelescopeAxis axis);
    string Action(string name, string parameters);
    PointingState DestinationSideOfPier(double raHours, double decDegrees);
    void MoveAxis(TelescopeAxis axis, double rate);
    void AbortSlew();
    void Park();
    void Unpark();
    void FindHome();
    void SlewToCoordinatesAsync(double raHours, double decDegrees);
    void SyncToCoordinates(double raHours, double decDegrees);
}

internal sealed class AlpacaTelescopeDevice(AlpacaTelescope client) : ITelescopeDevice {
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The adapter owns the AlpacaTelescope; the lifecycle disposes the adapter.")]
    public static ITelescopeDevice Create(DiscoveredDeviceDto device) {
        var host = AlpacaDeviceAddress.Host(device);
        return new AlpacaTelescopeDevice(new AlpacaTelescope(AlpacaDeviceAddress.Scheme(device),
            host, device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null));
    }

    public void Connect() => AlpacaConnect.Connect(client);
    public bool Connected => client.Connected;
    public void Stop() => client.AbortSlew();
    public void Disconnect() => client.Connected = false;
    public void Dispose() => client.Dispose();
    public bool Slewing => client.Slewing;
    public double RightAscension => client.RightAscension;
    public double Declination => client.Declination;
    public double TargetRightAscension => client.TargetRightAscension;
    public double TargetDeclination => client.TargetDeclination;
    public DriveRate TrackingRate { get => client.TrackingRate; set => client.TrackingRate = value; }
    public bool Tracking { get => client.Tracking; set => client.Tracking = value; }
    public bool AtPark => client.AtPark;
    public bool AtHome => client.AtHome;
    public bool IsPulseGuiding => client.IsPulseGuiding;
    public PointingState SideOfPier { get => client.SideOfPier; set => client.SideOfPier = value; }
    public EquatorialCoordinateType EquatorialSystem => client.EquatorialSystem;
    public bool CanSlewAsync => client.CanSlewAsync;
    public bool CanSlew => client.CanSlew;
    public bool CanSync => client.CanSync;
    public bool CanPark => client.CanPark;
    public bool CanUnpark => client.CanUnpark;
    public bool CanSetTracking => client.CanSetTracking;
    public bool CanPulseGuide => client.CanPulseGuide;
    public bool CanFindHome => client.CanFindHome;
    public bool CanSetPierSide => client.CanSetPierSide;
    public double FocalLength => client.FocalLength;
    public double ApertureDiameter => client.ApertureDiameter;
    public ITrackingRates TrackingRates => client.TrackingRates;
    public IList<string> SupportedActions => client.SupportedActions;
    public double SiteLatitude { get => client.SiteLatitude; set => client.SiteLatitude = value; }
    public double SiteLongitude { get => client.SiteLongitude; set => client.SiteLongitude = value; }
    public double SiteElevation { get => client.SiteElevation; set => client.SiteElevation = value; }
    public bool CanMoveAxis(TelescopeAxis axis) => client.CanMoveAxis(axis);
    public IAxisRates AxisRates(TelescopeAxis axis) => client.AxisRates(axis);
    public string Action(string name, string parameters) => client.Action(name, parameters);
    public PointingState DestinationSideOfPier(double raHours, double decDegrees) => client.DestinationSideOfPier(raHours, decDegrees);
    public void MoveAxis(TelescopeAxis axis, double rate) => client.MoveAxis(axis, rate);
    public void AbortSlew() => client.AbortSlew();
    public void Park() => client.Park();
    public void Unpark() => client.Unpark();
    public void FindHome() => client.FindHome();
    public void SlewToCoordinatesAsync(double raHours, double decDegrees) => client.SlewToCoordinatesAsync(raHours, decDegrees);
    public void SyncToCoordinates(double raHours, double decDegrees) => client.SyncToCoordinates(raHours, decDegrees);
}
