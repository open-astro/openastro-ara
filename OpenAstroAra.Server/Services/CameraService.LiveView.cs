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
using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Stretch;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §64 Live View — a server-driven short-exposure loop on the connected camera that renders the
/// latest frame to a JPEG (via the §65 <see cref="Stretcher"/>/<see cref="JpegEncoder"/> path) for
/// framing and focus. Unlike <see cref="CameraService.StartExposureAsync"/> these frames are
/// ephemeral: never written to FITS, never registered in the §28 catalog. The loop shares the
/// capture <c>_captureInFlight</c> gate, acquiring it per frame so a manual exposure can interleave
/// between live frames (the two are never on the device at once).
/// </summary>
public sealed partial class CameraService {

    // Downscale cap for the live JPEG: framing/focus wants responsiveness over full resolution.
    private const int LiveViewMaxDim = 1024;
    private static readonly TimeSpan LiveViewInterFrameDelay = TimeSpan.FromMilliseconds(100);

    // Serializes Start/Stop so they can never interleave (a Stop awaiting the old loop can't race a
    // concurrent Start spinning up a second loop). State reads (status/frame) stay lock-free.
    private readonly SemaphoreSlim _liveViewMutex = new(1, 1);
    private CancellationTokenSource? _liveViewCts;
    private Task? _liveViewLoop;
    private int _liveViewActive;
    private long _liveViewSeq;
    private volatile byte[]? _liveViewJpeg;
    private volatile LiveViewMeta? _liveViewMeta;

    private sealed record LiveViewMeta(
        int? Width, int? Height, double? ExposureSec, DateTimeOffset StartedAt, DateTimeOffset? LastFrameAt);

    public async Task StartLiveViewAsync(LiveViewStartRequestDto request, CancellationToken ct) {
        if (request.ExposureSec <= 0) {
            throw new ArgumentOutOfRangeException(nameof(request), "ExposureSec must be positive.");
        }
        if (request.BinX < 1 || request.BinY < 1) {
            throw new ArgumentOutOfRangeException(nameof(request), "BinX/BinY must be at least 1.");
        }
        // Validates connectivity up front (throws if the camera isn't connected); the loop
        // re-resolves the current client each frame so a later reconnect/disconnect is honored.
        _ = RequireConnectedClient();

        await _liveViewMutex.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_liveViewLoop is { IsCompleted: false }) {
                return; // already running — idempotent start
            }
            // A self-terminated loop (e.g. camera disconnect) leaves its spent CTS behind; dispose it
            // before replacing so it isn't leaked.
            _liveViewCts?.Dispose();
            var cts = new CancellationTokenSource();
            _liveViewCts = cts;
            _liveViewMeta = new LiveViewMeta(null, null, request.ExposureSec, DateTimeOffset.UtcNow, null);
            Volatile.Write(ref _liveViewActive, 1);
            _liveViewLoop = Task.Run(() => RunLiveViewLoopAsync(request, cts.Token), CancellationToken.None);
        } finally {
            _liveViewMutex.Release();
        }
        LogLiveViewStarted(request.ExposureSec, request.BinX, request.BinY);
    }

    public async Task StopLiveViewAsync(CancellationToken ct) {
        await _liveViewMutex.WaitAsync(ct).ConfigureAwait(false);
        try {
            var cts = _liveViewCts;
            var loop = _liveViewLoop;
            _liveViewCts = null;
            _liveViewLoop = null;
            Volatile.Write(ref _liveViewActive, 0);
            if (cts is null && loop is null) {
                return; // not running — no-op
            }
            // Hold the mutex across the cancel + await so a concurrent Start can't spin up a second
            // loop in the gap and race this one on _captureInFlight.
            if (cts is not null) {
                try { await cts.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
            }
            if (loop is not null) {
                await loop.ConfigureAwait(false); // the loop swallows its own faults; never throws here
            }
            cts?.Dispose();
        } finally {
            _liveViewMutex.Release();
        }
        LogLiveViewStopped();
    }

    public LiveViewStatusDto GetLiveViewStatus() {
        var m = _liveViewMeta;
        return new LiveViewStatusDto(
            Active: Volatile.Read(ref _liveViewActive) == 1,
            FrameSeq: Interlocked.Read(ref _liveViewSeq),
            Width: m?.Width,
            Height: m?.Height,
            ExposureSec: m?.ExposureSec,
            StartedAtUtc: m?.StartedAt.ToString("O"),
            LastFrameAtUtc: m?.LastFrameAt?.ToString("O"));
    }

    public (byte[] Jpeg, long Seq)? GetLiveViewFrame() {
        var jpeg = _liveViewJpeg;
        return jpeg is null ? null : (jpeg, Interlocked.Read(ref _liveViewSeq));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Live View loop boundary: a single frame's device/render failure (ASCOM throw, dropped link, decode error) must not fault the long-running loop — it is logged and the loop continues to the next frame. CA1031's log-and-recover boundary applies.")]
    private async Task RunLiveViewLoopAsync(LiveViewStartRequestDto request, CancellationToken token) {
        try {
            while (!token.IsCancellationRequested) {
                // Per-frame mutual exclusion with real captures: skip this frame if a manual
                // exposure holds the gate, so Live View never collides with a catalog capture.
                if (Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0) {
                    await Task.Delay(LiveViewInterFrameDelay, token).ConfigureAwait(false);
                    continue;
                }
                try {
                    AlpacaCamera? client;
                    lock (_gate) {
                        client = !_disposed && _state == EquipmentConnectionState.Connected ? _client : null;
                    }
                    if (client is null) {
                        break; // disconnected/disposed — end the loop
                    }
                    await CaptureLiveFrameAsync(client, request, token).ConfigureAwait(false);
                } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) {
                    LogLiveViewFrameFailed(ex);
                } finally {
                    Interlocked.Exchange(ref _captureInFlight, 0);
                }
                await Task.Delay(LiveViewInterFrameDelay, token).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // Normal stop path.
        } catch (Exception ex) {
            LogLiveViewLoopFaulted(ex);
        } finally {
            Volatile.Write(ref _liveViewActive, 0);
        }
    }

    private async Task CaptureLiveFrameAsync(AlpacaCamera client, LiveViewStartRequestDto request, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        // Reuse the capture path's settings application (bin/gain, full-frame subframe).
        ApplyExposureSettings(client, new ExposureRequestDto(request.ExposureSec, request.Gain, BinX: request.BinX, BinY: request.BinY));
        token.ThrowIfCancellationRequested();
        client.StartExposure(request.ExposureSec, true);

        var ready = await WaitForImageReadyAsync(client, request.ExposureSec, token).ConfigureAwait(false);
        if (ready != true) {
            return; // not-ready/dropped — skip this frame, the loop retries
        }
        token.ThrowIfCancellationRequested();

        object? imageArray;
        Interlocked.Exchange(ref _downloading, 1);
        try {
            imageArray = client.ImageArray;
        } finally {
            Interlocked.Exchange(ref _downloading, 0);
        }
        var (pixels, width, height) = ConvertImageArray(imageArray);
        var stretched = Stretcher.Apply(StretchAlgorithm.AutoStf, pixels);
        var jpeg = JpegEncoder.EncodeGray(stretched, width, height, maxDim: LiveViewMaxDim);

        _liveViewJpeg = jpeg;
        Interlocked.Increment(ref _liveViewSeq);
        var prev = _liveViewMeta;
        _liveViewMeta = new LiveViewMeta(
            width, height, request.ExposureSec,
            prev?.StartedAt ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    // Called from Dispose: cancel the loop promptly without awaiting (the loop also breaks on the
    // _disposed/_client guard), and release the CTS.
    private void CancelLiveViewForDispose() {
        var cts = _liveViewCts;
        _liveViewCts = null;
        _liveViewLoop = null;
        Volatile.Write(ref _liveViewActive, 0);
        if (cts is not null) {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
        _liveViewMutex.Dispose();
    }

    [LoggerMessage(EventId = 6410, Level = LogLevel.Information,
        Message = "Live View started (exposure {ExposureSec}s, bin {BinX}x{BinY}).")]
    partial void LogLiveViewStarted(double exposureSec, int binX, int binY);

    [LoggerMessage(EventId = 6411, Level = LogLevel.Information, Message = "Live View stopped.")]
    partial void LogLiveViewStopped();

    [LoggerMessage(EventId = 6412, Level = LogLevel.Warning, Message = "Live View frame skipped after a device/render error.")]
    partial void LogLiveViewFrameFailed(Exception ex);

    [LoggerMessage(EventId = 6413, Level = LogLevel.Warning, Message = "Live View loop faulted and stopped.")]
    partial void LogLiveViewLoopFaulted(Exception ex);
}
