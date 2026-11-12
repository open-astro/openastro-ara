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
using System.Diagnostics.CodeAnalysis;

namespace OpenAstroAra.Server.Services;

// ADR 0011 rule 4 — the Alpaca adapters of the six small devices. Each is the one production adapter of
// its device (ADR 0003): it holds the Alpaca client, sends the same calls the services used to send, and
// is the only place that builds one. Tests hand the service a fake from OpenAstroAra.Test instead.

internal interface IFlatDeviceDevice : IDeviceAdapter {
    CoverStatus CoverState { get; }
    CalibratorStatus CalibratorState { get; }
    int Brightness { get; }
    int MaxBrightness { get; }
    void OpenCover();
    void CloseCover();
    void CalibratorOn(int brightness);
    void CalibratorOff();
}

internal sealed class AlpacaFlatDevice(AlpacaCoverCalibrator client) : IFlatDeviceDevice {
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The adapter owns the AlpacaCoverCalibrator; the lifecycle disposes the adapter.")]
    public static IFlatDeviceDevice Create(DiscoveredDeviceDto device) =>
        new AlpacaFlatDevice(new AlpacaCoverCalibrator(AlpacaDeviceAddress.Scheme(device),
            AlpacaDeviceAddress.Host(device), device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null));

    public void Connect() => AlpacaConnect.Connect(client);
    public bool Connected => client.Connected;
    public void Stop() { } // a cover has no halt on this path: the teardown only disconnects
    public void Disconnect() => client.Connected = false;
    public void Dispose() => client.Dispose();
    public CoverStatus CoverState => client.CoverState;
    public CalibratorStatus CalibratorState => client.CalibratorState;
    public int Brightness => client.Brightness;
    public int MaxBrightness => client.MaxBrightness;
    public void OpenCover() => client.OpenCover();
    public void CloseCover() => client.CloseCover();
    public void CalibratorOn(int brightness) => client.CalibratorOn(brightness);
    public void CalibratorOff() => client.CalibratorOff();
}

internal interface IObservingConditionsDevice : IDeviceAdapter {
    double Temperature { get; }
    double Humidity { get; }
    double DewPoint { get; }
    double Pressure { get; }
    double CloudCover { get; }
    double WindSpeed { get; }
    double WindGust { get; }
    double WindDirection { get; }
    double RainRate { get; }
    double SkyQuality { get; }
    double SkyTemperature { get; }
}

internal sealed class AlpacaObservingConditionsDevice(AlpacaObservingConditions client) : IObservingConditionsDevice {
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The adapter owns the AlpacaObservingConditions; the lifecycle disposes the adapter.")]
    public static IObservingConditionsDevice Create(DiscoveredDeviceDto device) =>
        new AlpacaObservingConditionsDevice(new AlpacaObservingConditions(AlpacaDeviceAddress.Scheme(device),
            AlpacaDeviceAddress.Host(device), device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null));

    public void Connect() => AlpacaConnect.Connect(client);
    public bool Connected => client.Connected;
    public void Stop() { }
    public void Disconnect() => client.Connected = false;
    public void Dispose() => client.Dispose();
    public double Temperature => client.Temperature;
    public double Humidity => client.Humidity;
    public double DewPoint => client.DewPoint;
    public double Pressure => client.Pressure;
    public double CloudCover => client.CloudCover;
    public double WindSpeed => client.WindSpeed;
    public double WindGust => client.WindGust;
    public double WindDirection => client.WindDirection;
    public double RainRate => client.RainRate;
    public double SkyQuality => client.SkyQuality;
    public double SkyTemperature => client.SkyTemperature;
}

internal interface ISwitchDevice : IDeviceAdapter {
    short MaxSwitch { get; }
    string GetSwitchName(short id);
    string GetSwitchDescription(short id);
    double GetSwitchValue(short id);
    double MinSwitchValue(short id);
    double MaxSwitchValue(short id);
    double SwitchStep(short id);
    bool CanWrite(short id);
    void SetSwitchValue(short id, double value);
}

internal sealed class AlpacaSwitchDevice(AlpacaSwitch client) : ISwitchDevice {
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The adapter owns the AlpacaSwitch; the lifecycle disposes the adapter.")]
    public static ISwitchDevice Create(DiscoveredDeviceDto device) =>
        new AlpacaSwitchDevice(new AlpacaSwitch(AlpacaDeviceAddress.Scheme(device),
            AlpacaDeviceAddress.Host(device), device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null));

    public void Connect() => AlpacaConnect.Connect(client);
    public bool Connected => client.Connected;
    public void Stop() { }
    public void Disconnect() => client.Connected = false;
    public void Dispose() => client.Dispose();
    public short MaxSwitch => client.MaxSwitch;
    public string GetSwitchName(short id) => client.GetSwitchName(id);
    public string GetSwitchDescription(short id) => client.GetSwitchDescription(id);
    public double GetSwitchValue(short id) => client.GetSwitchValue(id);
    public double MinSwitchValue(short id) => client.MinSwitchValue(id);
    public double MaxSwitchValue(short id) => client.MaxSwitchValue(id);
    public double SwitchStep(short id) => client.SwitchStep(id);
    public bool CanWrite(short id) => client.CanWrite(id);
    public void SetSwitchValue(short id, double value) => client.SetSwitchValue(id, value);
}

internal interface ISafetyMonitorDevice : IDeviceAdapter {
    bool IsSafe { get; }
}

internal sealed class AlpacaSafetyMonitorDevice(AlpacaSafetyMonitor client) : ISafetyMonitorDevice {
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The adapter owns the AlpacaSafetyMonitor; the lifecycle disposes the adapter.")]
    public static ISafetyMonitorDevice Create(DiscoveredDeviceDto device) =>
        new AlpacaSafetyMonitorDevice(new AlpacaSafetyMonitor(AlpacaDeviceAddress.Scheme(device),
            AlpacaDeviceAddress.Host(device), device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null));

    public void Connect() => AlpacaConnect.Connect(client);
    public bool Connected => client.Connected;
    public void Stop() { }
    public void Disconnect() => client.Connected = false;
    public void Dispose() => client.Dispose();
    public bool IsSafe => client.IsSafe;
}

internal interface IFilterWheelDevice : IDeviceAdapter {
    short Position { get; set; }
    string[] Names { get; }
    int[] FocusOffsets { get; }
}

internal sealed class AlpacaFilterWheelDevice(AlpacaFilterWheel client) : IFilterWheelDevice {
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The adapter owns the AlpacaFilterWheel; the lifecycle disposes the adapter.")]
    public static IFilterWheelDevice Create(DiscoveredDeviceDto device) =>
        new AlpacaFilterWheelDevice(new AlpacaFilterWheel(AlpacaDeviceAddress.Scheme(device),
            AlpacaDeviceAddress.Host(device), device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null));

    public void Connect() => AlpacaConnect.Connect(client);
    public bool Connected => client.Connected;
    public void Stop() { } // a wheel has no halt: the teardown only disconnects
    public void Disconnect() => client.Connected = false;
    public void Dispose() => client.Dispose();
    public short Position { get => client.Position; set => client.Position = value; }
    public string[] Names => client.Names;
    public int[] FocusOffsets => client.FocusOffsets;
}

internal interface IFocuserDevice : IDeviceAdapter {
    bool IsMoving { get; }
    int Position { get; }
    double Temperature { get; }
    bool TempComp { get; set; }
    int MaxStep { get; }
    double StepSize { get; }
    bool TempCompAvailable { get; }
    bool Absolute { get; }
    void Move(int position);
    void Halt();
}

internal sealed class AlpacaFocuserDevice(AlpacaFocuser client) : IFocuserDevice {
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The adapter owns the AlpacaFocuser; the lifecycle disposes the adapter.")]
    public static IFocuserDevice Create(DiscoveredDeviceDto device) =>
        new AlpacaFocuserDevice(new AlpacaFocuser(AlpacaDeviceAddress.Scheme(device),
            AlpacaDeviceAddress.Host(device), device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null));

    public void Connect() => AlpacaConnect.Connect(client);
    public bool Connected => client.Connected;
    public void Stop() => client.Halt();
    public void Disconnect() => client.Connected = false;
    public void Dispose() => client.Dispose();
    public bool IsMoving => client.IsMoving;
    public int Position => client.Position;
    public double Temperature => client.Temperature;
    public bool TempComp { get => client.TempComp; set => client.TempComp = value; }
    public int MaxStep => client.MaxStep;
    public double StepSize => client.StepSize;
    public bool TempCompAvailable => client.TempCompAvailable;
    public bool Absolute => client.Absolute;
    public void Move(int position) => client.Move(position);
    public void Halt() => client.Halt();
}
