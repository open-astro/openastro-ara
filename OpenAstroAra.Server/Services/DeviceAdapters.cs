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
using ASCOM.Common.Alpaca;
using ASCOM.Common.DeviceInterfaces;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// ADR 0011 — the seam between a <see cref="DeviceLifecycle{TDevice}"/> and the hardware. Alpaca is
/// the one production adapter (ADR 0003); tests hand the lifecycle a fake. Every member may throw:
/// the lifecycle and the device class own the error handling.
/// </summary>
internal interface IDeviceAdapter : IDisposable {
    /// <summary>Sends <c>Connected = true</c> (see <see cref="AlpacaConnect"/>).</summary>
    void Connect();

    /// <summary>The §42.3 probe: throws on transport death, false on a driver-side disconnect.</summary>
    bool Connected { get; }

    /// <summary>Stops any motion before a disconnect (Halt, AbortSlew).</summary>
    void Stop();

    /// <summary>Sends <c>Connected = false</c>.</summary>
    void Disconnect();
}

internal static class AlpacaDeviceAddress {
    /// <summary>The address to dial: the IP when discovery found one, else the host name.</summary>
    public static string Host(DiscoveredDeviceDto device) {
        var host = string.IsNullOrWhiteSpace(device.IpAddress) ? device.HostName : device.IpAddress;
        return string.IsNullOrWhiteSpace(host)
            ? throw new InvalidOperationException(
                $"discovered device '{device.Name}' carries neither an IP address nor a host name")
            : host;
    }

    public static ServiceType Scheme(DiscoveredDeviceDto device) => device.UseHttps ? ServiceType.Https : ServiceType.Http;
}

internal interface IRotatorDevice : IDeviceAdapter {
    bool IsMoving { get; }
    float MechanicalPosition { get; }
    float Position { get; }
    bool Reverse { get; set; }
    bool CanReverse { get; }
    float StepSize { get; }
    void MoveAbsolute(float position);
    void MoveMechanical(float position);
    void Sync(float position);
    void Halt();
}

internal sealed class AlpacaRotatorDevice(AlpacaRotator client) : IRotatorDevice {
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The adapter owns the AlpacaRotator; the lifecycle disposes the adapter.")]
    public static IRotatorDevice Create(DiscoveredDeviceDto device) {
        var host = AlpacaDeviceAddress.Host(device);
        return new AlpacaRotatorDevice(new AlpacaRotator(AlpacaDeviceAddress.Scheme(device),
            host, device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null));
    }

    public void Connect() => AlpacaConnect.Connect(client);
    public bool Connected => client.Connected;
    public void Stop() => client.Halt();
    public void Disconnect() => client.Connected = false;
    public void Dispose() => client.Dispose();
    public bool IsMoving => client.IsMoving;
    public float MechanicalPosition => client.MechanicalPosition;
    public float Position => client.Position;
    public bool Reverse { get => client.Reverse; set => client.Reverse = value; }
    public bool CanReverse => client.CanReverse;
    public float StepSize => client.StepSize;
    public void MoveAbsolute(float position) => client.MoveAbsolute(position);
    public void MoveMechanical(float position) => client.MoveMechanical(position);
    public void Sync(float position) => client.Sync(position);
    public void Halt() => client.Halt();
}

internal interface IDomeDevice : IDeviceAdapter {
    bool Slewing { get; }
    double Azimuth { get; }
    ShutterState ShutterStatus { get; }
    bool AtHome { get; }
    bool AtPark { get; }
    bool CanSetShutter { get; }
    bool CanSetAzimuth { get; }
    bool CanSyncAzimuth { get; }
    bool CanPark { get; }
    bool CanFindHome { get; }
    bool CanSetPark { get; }
    void SlewToAzimuth(double azimuth);
    void SyncToAzimuth(double azimuth);
    void Park();
    void SetPark();
    void FindHome();
    void OpenShutter();
    void CloseShutter();
    void AbortSlew();
}

internal sealed class AlpacaDomeDevice(AlpacaDome client) : IDomeDevice {
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The adapter owns the AlpacaDome; the lifecycle disposes the adapter.")]
    public static IDomeDevice Create(DiscoveredDeviceDto device) {
        var host = AlpacaDeviceAddress.Host(device);
        return new AlpacaDomeDevice(new AlpacaDome(AlpacaDeviceAddress.Scheme(device),
            host, device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null));
    }

    public void Connect() => AlpacaConnect.Connect(client);
    public bool Connected => client.Connected;
    public void Stop() => client.AbortSlew();
    public void Disconnect() => client.Connected = false;
    public void Dispose() => client.Dispose();
    public bool Slewing => client.Slewing;
    public double Azimuth => client.Azimuth;
    public ShutterState ShutterStatus => client.ShutterStatus;
    public bool AtHome => client.AtHome;
    public bool AtPark => client.AtPark;
    public bool CanSetShutter => client.CanSetShutter;
    public bool CanSetAzimuth => client.CanSetAzimuth;
    public bool CanSyncAzimuth => client.CanSyncAzimuth;
    public bool CanPark => client.CanPark;
    public bool CanFindHome => client.CanFindHome;
    public bool CanSetPark => client.CanSetPark;
    public void SlewToAzimuth(double azimuth) => client.SlewToAzimuth(azimuth);
    public void SyncToAzimuth(double azimuth) => client.SyncToAzimuth(azimuth);
    public void Park() => client.Park();
    public void SetPark() => client.SetPark();
    public void FindHome() => client.FindHome();
    public void OpenShutter() => client.OpenShutter();
    public void CloseShutter() => client.CloseShutter();
    public void AbortSlew() => client.AbortSlew();
}
