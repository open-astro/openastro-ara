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
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Equipment.MyCamera;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.Image.Interfaces;

namespace OpenAstroAra.Server.Services.Equipment;

/// <summary>
/// §38k-13 — headless no-op stub for <see cref="ICameraMediator"/>.
/// Same pattern as the §38k-9 … §38k-12 stubs. Used by the daemon's
/// <see cref="HeadlessSequencerFactory"/> to construct the camera-control
/// instruction prototypes (<c>CoolCamera</c>, <c>WarmCamera</c>,
/// <c>SetUSBLimit</c>, <c>SetReadoutMode</c>, <c>DewHeater</c>) — all of
/// which depend only on <see cref="ICameraMediator"/>.
///
/// Scope note: the exposure-producing members (<see cref="Capture"/>,
/// <see cref="Download"/>, <see cref="LiveView(System.Threading.CancellationToken)"/>)
/// have no honest "empty" sentinel — there is no such thing as a placeholder
/// <see cref="IExposureData"/> — so they throw <see cref="NotSupportedException"/>
/// rather than hand back a misleading value (same reasoning as
/// <see cref="GetDevice"/>). None of the registered camera-control prototypes
/// invoke them; the full image-capture path (TakeExposure) lands once an
/// <c>IImagingMediator</c> stub + the image pipeline are wired in a later §38k
/// sub-PR. Real Alpaca-backed wiring swaps in at the DI registration point
/// once §14e Alpaca simulator pinning lands.
/// </summary>
public sealed class HeadlessCameraMediator : ICameraMediator {

    private static readonly CameraInfo DisconnectedInfo = new() {
        Connected = false,
    };

    // IDeviceMediator surface
    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(ICameraConsumer consumer) { }
    public void RemoveConsumer(ICameraConsumer consumer) { }
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public void Broadcast(CameraInfo deviceInfo) { }
    public CameraInfo GetInfo() => DisconnectedInfo;
    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }
    public IDevice GetDevice() =>
        throw new NotSupportedException(
            "Headless camera stub has no backing IDevice; real Alpaca-backed wiring swaps in at the DI registration point once §14e Alpaca simulator pinning lands.");

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;

    // ICameraMediator additions.

    // Exposure-producing members: no honest headless sentinel exists, so throw
    // rather than fabricate an IExposureData. No registered prototype calls these.
    public Task Capture(CaptureSequence sequence, CancellationToken token, IProgress<ApplicationStatus> progress) =>
        throw new NotSupportedException(
            "Headless camera stub cannot capture exposures; the full TakeExposure path lands with the IImagingMediator stub + image pipeline in a later §38k sub-PR.");

    public IAsyncEnumerable<IExposureData> LiveView(CancellationToken token) =>
        throw new NotSupportedException("Headless camera stub cannot stream a live view.");

    public IAsyncEnumerable<IExposureData> LiveView(CaptureSequence sequence, CancellationToken token) =>
        throw new NotSupportedException("Headless camera stub cannot stream a live view.");

    public Task<IExposureData> Download(CancellationToken token) =>
        throw new NotSupportedException("Headless camera stub cannot download exposures.");

    public void AbortExposure() { }

    // Camera-control no-ops — the registered prototypes call these (and GetInfo)
    // and bail out cleanly off the "not connected" sentinel.
    public void SetReadoutMode(short mode) { }
    public void SetReadoutModeForNormalImages(short mode) { }
    public void SetBinning(short x, short y) { }
    public void SetDewHeater(bool onOff) { }
    public void SetUSBLimit(int usbLimit) { }
    public void SetSubSambleRectangle(ObservableRectangle observableRectangle) { }

    // No real cooler attached: not at any target, and no target temperature set.
    public bool AtTargetTemp => false;
    public double TargetTemp => double.NaN;

    // Cool/Warm report "didn't succeed" — the instructions read this + GetInfo().
    public Task<bool> CoolCamera(double temperature, TimeSpan duration, IProgress<ApplicationStatus> progress, CancellationToken ct) =>
        Task.FromResult(false);

    public Task<bool> WarmCamera(TimeSpan duration, IProgress<ApplicationStatus> progress, CancellationToken ct) =>
        Task.FromResult(false);

    // Capture-block coordination: nothing holds a block in the headless stub,
    // so registration is a no-op and the camera always reports free-to-capture.
    public void RegisterCaptureBlock(ICameraConsumer cameraConsumer) { }
    public void ReleaseCaptureBlock(ICameraConsumer cameraConsumer) { }
    public bool IsFreeToCapture(ICameraConsumer cameraConsumer) => true;
    public void RegisterCaptureBlock(object cameraConsumer) { }
    public void ReleaseCaptureBlock(object cameraConsumer) { }
    public bool IsFreeToCapture(object cameraConsumer) => true;

    public event Func<object, EventArgs, Task>? DownloadTimeout;
}
