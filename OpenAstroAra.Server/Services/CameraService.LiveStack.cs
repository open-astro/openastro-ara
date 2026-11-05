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
using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services.LiveStack;
using OpenAstroAra.Stretch;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// ↑ara#1169 live stacking (PORT_PLAYBOOK §64.17): the <see cref="LiveStackEngine"/> owned by the camera service and
/// fed from the Live View loop. Opt-in: Live View start disables the engine, only <see cref="StartLiveStack"/>
/// enables it, so a focus-only session pays nothing. Lock order is _liveViewMutex -> engine lock; the loop thread
/// takes only the engine lock.
/// </summary>
public sealed partial class CameraService : ILiveStackControl {

    private readonly LiveStackEngine _liveStack = new();

    internal LiveStackEngine LiveStack => _liveStack;

    // Set at Live View start (under the mutex): the session's Bayer pattern (OSC at 1x1, else null) and the
    // frame size it will produce when the sensor size is known, for the stack start checks.
    private BayerPattern? _liveViewBayerPattern;
    private (int Width, int Height)? _liveViewExpectedSize;

    private bool _liveViewSkipSensorProbe;

    // Test seam (↑ara#1169): when set, the Live View loop takes its frame from here instead of the camera.
    private Func<(ushort[] Pixels, int Width, int Height)?>? _liveViewFrameSourceForTest;

    internal void SetLiveViewFrameSourceForTest(Func<(ushort[] Pixels, int Width, int Height)?> source) =>
        _liveViewFrameSourceForTest = source;

    internal void FeedLiveStackForTest(ushort[] pixels, int width, int height, long session, BayerPattern? bayerPattern) =>
        FeedLiveStack(pixels, width, height, session, bayerPattern);

    /// <summary>Test seam (↑ara#1169): adopts an unreachable client as the connected camera so the real Live View loop
    /// runs (every frame fails fast) without a simulator, and skips the start-time SensorType read, which would
    /// need the device. The service owns the client and disposes it.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership passes to _client, which Dispose releases.")]
    internal void WithLiveViewTestClientForTest() {
        var client = new AlpacaCamera(ServiceType.Http, "127.0.0.1", 1, 0, strictCasing: false, logger: null);
        lock (_gate) {
            _client = client;
            _state = EquipmentConnectionState.Connected;
            _liveViewSkipSensorProbe = true;
        }
    }

    public void StartLiveStack() {
        _liveViewMutex.Wait();
        try {
            var session = Interlocked.Read(ref _liveViewSession);
            if (Volatile.Read(ref _liveViewActive) != 1) {
                throw new InvalidOperationException("Live View is not running; start it before live stacking.");
            }
            if (_liveViewBayerPattern is not null) {
                throw new InvalidOperationException("Live stacking does not support a one-shot-colour camera at 1x1 binning yet; bin the camera 2x2 or use a mono camera.");
            }
            var f = _liveViewFrame;
            var size = f is null ? _liveViewExpectedSize : (f.Width, f.Height);
            if (size is { } s && (long)s.Width * s.Height > LiveStackEngine.DefaultMaxPixels) {
                throw new InvalidOperationException($"The Live View frame ({s.Width}x{s.Height}) is over the live-stack limit of {LiveStackEngine.DefaultMaxPixels / 1_000_000} megapixels; bin the camera.");
            }
            _liveStack.Enable(session);
            // The loop's finally disables the engine without the mutex: if the session ended in between, undo.
            if (Volatile.Read(ref _liveViewActive) != 1 || Interlocked.Read(ref _liveViewSession) != session) {
                _liveStack.Disable();
                throw new InvalidOperationException("Live View is not running; start it before live stacking.");
            }
        } finally {
            _liveViewMutex.Release();
        }
    }

    public void StopLiveStack() => _liveStack.Disable();

    public void ResetLiveStack() => _liveStack.Reset();

    public LiveStackStatusDto GetLiveStackStatus() {
        var s = _liveStack.Snapshot;
        var session = Interlocked.Read(ref _liveViewSession);
        // Enabled only for the session that runs now: a stack bound to a session that ended reads as off.
        var enabled = s.Enabled && Volatile.Read(ref _liveViewActive) == 1 && s.SessionId == session;
        return new LiveStackStatusDto(
            Enabled: enabled, SessionId: session, StackSeq: s.Seq,
            FramesStacked: s.FramesStacked, FramesUnregistered: s.FramesUnregistered, RejectedSamples: s.RejectedSamples,
            ReferenceStars: s.ReferenceStars, Width: s.Width, Height: s.Height,
            LastOffsetX: s.LastOffsetX, LastOffsetY: s.LastOffsetY, LastRotationDeg: s.LastRotationDeg,
            LastOutcome: s.LastOutcome is { } o ? OutcomeToken(o) : null,
            LastFrameAtUtc: s.LastFrameAt?.ToString("O", CultureInfo.InvariantCulture));
    }

    public (ReadOnlyMemory<byte> Jpeg, long Seq, long SessionId, int Frames)? GetLiveStackFrame() {
        TouchLiveViewFetch(); // a stack watcher may never fetch the plain frame; it is still a client
        var s = _liveStack.Snapshot;
        return s.Jpeg is null ? null : (s.Jpeg, s.Seq, s.SessionId, s.FramesStacked);
    }

    // The Live View session is over (stop, self-stop, disconnect, dispose): clear and disable the stack.
    internal void OnLiveViewSessionEnded() => _liveStack.Disable();

    private static string OutcomeToken(StackAddOutcome o) => o switch {
        StackAddOutcome.Reference => "reference",
        StackAddOutcome.Stacked => "stacked",
        StackAddOutcome.Unregistered => "unregistered",
        StackAddOutcome.WrongSize => "wrong_size",
        StackAddOutcome.TooLarge => "too_large",
        _ => "dropped",
    };

    // Feed one raw frame to the stack, after the live JPEG is published and off the capture gate. Own try/catch: a
    // stack failure is logged and neither skips the frame's success accounting nor counts toward the self-stop.
    // OSC at 1x1 is never fed (refused at stack start).
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Live View loop boundary: a stack failure on one frame must not stop the loop; it is logged and the next frame retried.")]
    private void FeedLiveStack(ushort[] pixels, int width, int height, long session, BayerPattern? bayerPattern) {
        if (bayerPattern is not null) {
            return;
        }
        try {
            _liveStack.Add(pixels, width, height, session);
        } catch (Exception ex) {
            LogLiveStackFrameFailed(ex);
        }
    }

    [LoggerMessage(EventId = 6415, Level = LogLevel.Warning, Message = "Live stack frame skipped after an error.")]
    partial void LogLiveStackFrameFailed(Exception ex);
}
