#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>ADR 0016 rule 4 — what the Polar Align Routine and guide focus need from the Guider: custody of the
/// Guide Camera, the out-of-step check, and a link to the connected guider for captures.</summary>
internal interface IGuideCapture {
    /// <summary>The link to the guider connected now; throws <see cref="InvalidOperationException"/>
    /// ("guider is not connected") when there is none.</summary>
    IGuideCaptureLink ConnectedLink();

    string? OutOfStepReason { get; }

    void ThrowIfOutOfStep(string operation);

    /// <summary>Asks the custody module for the camera (refused while the Guider is busy).</summary>
    Task<GuideCameraCustody.Claim> TakeCameraAsync(CameraUse use, Func<CameraUse, Task> stop, CancellationToken ct);

    /// <summary>#2173 — asks for the camera only when nobody holds it (<see cref="GuideCameraCustody.TakeIfFreeAsync"/>):
    /// refused while the Guider is busy or any activity holds it, never stopping one.</summary>
    Task<GuideCameraCustody.Claim> TakeCameraIfFreeAsync(CameraUse use, Func<CameraUse, Task> stop, CancellationToken ct);

    /// <summary>#2066 (ARA-2) — the guider's PHD2 app state now (<see cref="GuideCameraCustody.IsBusy"/> says whether it
    /// guides or calibrates), or null when none is known. Polar Align reads it before each motion: a guider that started
    /// guiding by its own controls would chase the turn.</summary>
    string? AppState => null;

    /// <summary>#2173 — the setup activity that holds the Guide Camera now (<see cref="GuideCameraCustody.Holder"/>), or
    /// null. The plate-solve check counts the camera busy while one holds it, and never stops it.</summary>
    CameraUse? CameraHolder => null;
}

/// <summary>One connected guider's capture operations.</summary>
internal interface IGuideCaptureLink {
    string? Host { get; }
    int RpcPort { get; }
    Task WaitForCaptureIdleAsync(CancellationToken ct);
    Task<bool> StopLoopForCaptureAsync(TimeSpan timeout, CancellationToken ct);
    Task<(int Width, int Height)?> GetCameraFrameSizeAsync(CancellationToken ct);
    /// <summary>One saved frame (daemon-side save); the result arrives on <see cref="OnFrameComplete"/>.</summary>
    Task CaptureFrameAsync(int exposureMs, int? binning, CancellationToken ct);
    /// <summary>Subscribes until the returned handle is disposed.</summary>
    IDisposable OnFrameComplete(Action<SingleFrameCompleteEventArgs> handler);
}

/// <summary>The PHD2 adapter's link: one <see cref="PHD2Guider"/>, the client connected when it was made.</summary>
internal sealed class Phd2GuideCaptureLink(PHD2Guider client) : IGuideCaptureLink {
    public string? Host => client.ConnectedHost;
    public int RpcPort => client.ConnectedRpcPort;
    public Task WaitForCaptureIdleAsync(CancellationToken ct) => client.WaitForCaptureIdleAsync(ct);
    public Task<bool> StopLoopForCaptureAsync(TimeSpan timeout, CancellationToken ct) => client.StopLoopForCaptureAsync(timeout, ct);
    public Task<(int Width, int Height)?> GetCameraFrameSizeAsync(CancellationToken ct) => client.GetCameraFrameSizeAsync(ct);
    public Task CaptureFrameAsync(int exposureMs, int? binning, CancellationToken ct) =>
        client.CaptureSolverFrameAsync(exposureMs, binning, gain: null, subframe: null, path: null, save: true, ct);

    public IDisposable OnFrameComplete(Action<SingleFrameCompleteEventArgs> handler) {
        ArgumentNullException.ThrowIfNull(handler);
        void OnEvent(object? sender, SingleFrameCompleteEventArgs e) => handler(e);
        client.SingleFrameComplete += OnEvent;
        return new Subscription(() => client.SingleFrameComplete -= OnEvent);
    }

    private sealed class Subscription(Action remove) : IDisposable {
        private int _disposed;
        public void Dispose() {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) {
                remove();
            }
        }
    }
}