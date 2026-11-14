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
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

// ADR 0011 rule 4 — the Alpaca adapter of the Camera. It holds the Alpaca client, sends the calls the
// service used to send, and is the only place that builds one (ADR 0003). Tests hand the service a fake.

internal interface ICameraDevice : IDeviceAdapter {
    CameraState CameraState { get; }
    double CCDTemperature { get; }
    bool CoolerOn { get; set; }
    double CoolerPower { get; }
    double SetCCDTemperature { get; set; }
    double HeatSinkTemperature { get; }
    short PercentCompleted { get; }
    IList<string> ReadoutModes { get; }
    short ReadoutMode { get; set; }
    IList<string> Gains { get; }
    IList<string> Offsets { get; }
    short Gain { get; set; }
    int Offset { get; set; }
    short BinX { get; set; }
    short BinY { get; set; }
    int NumX { get; set; }
    int NumY { get; set; }
    int StartX { get; set; }
    int StartY { get; set; }
    bool ImageReady { get; }
    object ImageArray { get; }
    SensorType SensorType { get; }
    int CameraXSize { get; }
    int CameraYSize { get; }
    double PixelSizeX { get; }
    double PixelSizeY { get; }
    bool CanSetCCDTemperature { get; }
    bool CanAbortExposure { get; }
    int GainMin { get; }
    int GainMax { get; }
    int OffsetMin { get; }
    int OffsetMax { get; }
    int MaxBinX { get; }
    int MaxBinY { get; }
    double ExposureMin { get; }
    double ExposureMax { get; }
    double ExposureResolution { get; }
    int MaxADU { get; }
    double ElectronsPerADU { get; }
    double FullWellCapacity { get; }
    bool HasShutter { get; }
    string SensorName { get; }
    int BayerOffsetX { get; }
    int BayerOffsetY { get; }
    void StartExposure(double durationSec, bool light);
    void AbortExposure();

    /// <summary>The preferred image download: the raw ImageBytes payload decoded straight into the raster
    /// buffer, skipping the client library's int[,] inflation. Null when the device has no ImageBytes
    /// support (the caller then reads <see cref="ImageArray"/>). Throws on transport errors.</summary>
    Task<(ushort[] Pixels, int Width, int Height)?> DownloadImageBytesAsync(CancellationToken ct);
}

internal sealed class AlpacaCameraDevice : ICameraDevice {
    private static readonly HttpClient ImageBytesHttp = new() { Timeout = TimeSpan.FromSeconds(120) };

    private readonly AlpacaCamera _client;
    private readonly Uri _baseAddress;
    private readonly int _deviceNumber;

    private AlpacaCameraDevice(AlpacaCamera client, Uri baseAddress, int deviceNumber) {
        _client = client;
        _baseAddress = baseAddress;
        _deviceNumber = deviceNumber;
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The adapter owns the AlpacaCamera; the lifecycle disposes the adapter.")]
    public static ICameraDevice Create(DiscoveredDeviceDto device) {
        var host = AlpacaDeviceAddress.Host(device);
        var client = new AlpacaCamera(AlpacaDeviceAddress.Scheme(device),
            host, device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null);
        return new AlpacaCameraDevice(client,
            new Uri($"{(device.UseHttps ? "https" : "http")}://{host}:{device.IpPort}/"),
            device.AlpacaDeviceNumber);
    }

    public void Connect() => AlpacaConnect.Connect(_client);
    public bool Connected => _client.Connected;

    // The service asks the lifecycle for this only while a capture is in flight (see DeviceLifecycle stopOnTeardown).
    public void Stop() => _client.AbortExposure();

    public void Disconnect() => _client.Connected = false;
    public void Dispose() => _client.Dispose();
    public CameraState CameraState => _client.CameraState;
    public double CCDTemperature => _client.CCDTemperature;
    public bool CoolerOn { get => _client.CoolerOn; set => _client.CoolerOn = value; }
    public double CoolerPower => _client.CoolerPower;
    public double SetCCDTemperature { get => _client.SetCCDTemperature; set => _client.SetCCDTemperature = value; }
    public double HeatSinkTemperature => _client.HeatSinkTemperature;
    public short PercentCompleted => _client.PercentCompleted;
    public IList<string> ReadoutModes => _client.ReadoutModes;
    public short ReadoutMode { get => _client.ReadoutMode; set => _client.ReadoutMode = value; }
    public IList<string> Gains => _client.Gains;
    public IList<string> Offsets => _client.Offsets;
    public short Gain { get => _client.Gain; set => _client.Gain = value; }
    public int Offset { get => _client.Offset; set => _client.Offset = value; }
    public short BinX { get => _client.BinX; set => _client.BinX = value; }
    public short BinY { get => _client.BinY; set => _client.BinY = value; }
    public int NumX { get => _client.NumX; set => _client.NumX = value; }
    public int NumY { get => _client.NumY; set => _client.NumY = value; }
    public int StartX { get => _client.StartX; set => _client.StartX = value; }
    public int StartY { get => _client.StartY; set => _client.StartY = value; }
    public bool ImageReady => _client.ImageReady;
    public object ImageArray => _client.ImageArray;
    public SensorType SensorType => _client.SensorType;
    public int CameraXSize => _client.CameraXSize;
    public int CameraYSize => _client.CameraYSize;
    public double PixelSizeX => _client.PixelSizeX;
    public double PixelSizeY => _client.PixelSizeY;
    public bool CanSetCCDTemperature => _client.CanSetCCDTemperature;
    public bool CanAbortExposure => _client.CanAbortExposure;
    public int GainMin => _client.GainMin;
    public int GainMax => _client.GainMax;
    public int OffsetMin => _client.OffsetMin;
    public int OffsetMax => _client.OffsetMax;
    public int MaxBinX => _client.MaxBinX;
    public int MaxBinY => _client.MaxBinY;
    public double ExposureMin => _client.ExposureMin;
    public double ExposureMax => _client.ExposureMax;
    public double ExposureResolution => _client.ExposureResolution;
    public int MaxADU => _client.MaxADU;
    public double ElectronsPerADU => _client.ElectronsPerADU;
    public double FullWellCapacity => _client.FullWellCapacity;
    public bool HasShutter => _client.HasShutter;
    public string SensorName => _client.SensorName;
    public int BayerOffsetX => _client.BayerOffsetX;
    public int BayerOffsetY => _client.BayerOffsetY;
    public void StartExposure(double durationSec, bool light) => _client.StartExposure(durationSec, light);
    public void AbortExposure() => _client.AbortExposure();

    public Task<(ushort[] Pixels, int Width, int Height)?> DownloadImageBytesAsync(CancellationToken ct) =>
        AlpacaImageBytes.DownloadAsync(ImageBytesHttp, _baseAddress, _deviceNumber, ct);
}