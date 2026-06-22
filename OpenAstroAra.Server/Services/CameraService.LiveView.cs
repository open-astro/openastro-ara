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
    // Live View is for framing/focus; a long exposure both defeats that and would hold the capture
    // gate (and a pending stop, since the ImageArray download is non-cancellable) for its whole
    // duration. 15 s is generous for framing while bounding worst-case stop/start latency.
    private const double LiveViewMaxExposureSec = 15.0;
    private static readonly TimeSpan LiveViewInterFrameDelay = TimeSpan.FromMilliseconds(100);
    // After this many consecutive frames that fail or never produce an image, self-stop the loop
    // rather than spinning forever with Active=true / FrameSeq=0 — a misconfig (e.g. a binning the
    // driver rejects) or a persistently-faulting device then surfaces as Active=false, not a silent
    // "running but never delivering" state.
    private const int LiveViewMaxConsecutiveFailures = 10;

    // Serializes Start/Stop so they can never interleave (a Stop awaiting the old loop can't race a
    // concurrent Start spinning up a second loop). State reads (status/frame) stay lock-free. Not
    // disposed: we never touch its AvailableWaitHandle, so there is no unmanaged resource to release,
    // and disposing at teardown would throw ObjectDisposedException into any in-flight WaitAsync.
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "SemaphoreSlim allocates no unmanaged handle here (AvailableWaitHandle is never used); disposing it would race in-flight WaitAsync callers at teardown. GC reclaims it.")]
    private readonly SemaphoreSlim _liveViewMutex = new(1, 1);
    private CancellationTokenSource? _liveViewCts;
    private Task? _liveViewLoop;
    private int _liveViewActive;
    // The latest frame + its seq, published as ONE reference so a reader can't observe a torn
    // (new-jpeg, old-seq) pair. Single writer (the loop), so no CAS needed. null = no frame yet.
    private volatile LiveViewFrameData? _liveViewFrame;
    private volatile LiveViewMeta? _liveViewMeta;

    private sealed record LiveViewMeta(
        int? Width, int? Height, double? ExposureSec, DateTimeOffset StartedAt, DateTimeOffset? LastFrameAt);

    // Readonly fields (not properties) on a private type: no CA1819 byte[]-property concern, and the
    // pair is immutable so a single volatile publish is tear-free.
    private sealed class LiveViewFrameData {
        public readonly byte[] Jpeg;
        public readonly long Seq;
        public LiveViewFrameData(byte[] jpeg, long seq) { Jpeg = jpeg; Seq = seq; }
    }

    public async Task StartLiveViewAsync(LiveViewStartRequestDto request, CancellationToken ct) {
        if (request.ExposureSec <= 0 || request.ExposureSec > LiveViewMaxExposureSec) {
            throw new ArgumentOutOfRangeException(nameof(request),
                $"ExposureSec must be in (0, {LiveViewMaxExposureSec}].");
        }
        if (request.BinX < 1 || request.BinY < 1) {
            throw new ArgumentOutOfRangeException(nameof(request), "BinX/BinY must be at least 1.");
        }
        // Validates connectivity up front (throws if the camera isn't connected); the loop
        // re-resolves the current client each frame so a later reconnect/disconnect is honored.
        var probe = RequireConnectedClient();
        // SensorType.Color returns an already-debayered 3-plane array that ConvertImageArray rejects,
        // so every frame would fail — the loop would spin forever with Active=true and no frame.
        // Refuse up front rather than hide that behind an infinite retry (mono + RGGB are fine: both
        // yield a 2D array ConvertImageArray handles).
        if (probe.SensorType == SensorType.Color) {
            throw new InvalidOperationException(
                "Live View does not support tri-colour (SensorType.Color) cameras, which return an already-debayered 3-plane image array.");
        }

        await _liveViewMutex.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_liveViewLoop is { IsCompleted: false }) {
                return; // already running — idempotent start
            }
            // A self-terminated loop (e.g. camera disconnect) leaves its spent CTS behind; dispose it
            // before replacing so it isn't leaked.
            _liveViewCts?.Dispose();
            // Fresh session: drop any frame from a prior session so FrameSeq == 0 / GET /frame → 204
            // reliably mean "no frame yet this session".
            _liveViewFrame = null;
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
        // CancellationToken.None, NOT ct: a stop must always run to completion once requested. If we
        // honored ct and the caller (HTTP request) disconnected while we were waiting for the mutex
        // (held by an in-flight frame download), WaitAsync would throw and the loop would be left
        // running with no one to stop it.
        await _liveViewMutex.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try {
            var cts = _liveViewCts;
            var loop = _liveViewLoop;
            _liveViewCts = null;
            _liveViewLoop = null;
            Volatile.Write(ref _liveViewActive, 0);
            // Hold the mutex across the cancel + await so a concurrent Start can't spin up a second
            // loop in the gap and race this one on _captureInFlight.
            if (cts is not null) {
                try { await cts.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
            }
            if (loop is not null) {
                // May block until an in-flight frame's non-cancellable ImageArray download finishes
                // (bounded by the exposure + readout, capped at LiveViewMaxExposureSec). Acceptable
                // for framing, where exposures are short; the loop swallows its own faults.
                await loop.ConfigureAwait(false);
            }
            cts?.Dispose();
            // Drop the last frame so GET /liveview/frame returns 204 once stopped. This MUST come
            // after `await loop`: cancellation can land during the non-cancellable ImageArray
            // download, so the loop may still publish one final frame after we cancel — nulling
            // before the await would let that late write resurrect a stale frame.
            _liveViewFrame = null;
        } finally {
            _liveViewMutex.Release();
        }
        LogLiveViewStopped();
    }

    public LiveViewStatusDto GetLiveViewStatus() {
        // Read the frame (the writer's commit point) FIRST: its volatile-read acquire barrier makes
        // the prior _liveViewMeta write visible, so a new FrameSeq always pairs with at-least-as-new
        // meta. Reading meta first could observe a new seq alongside stale Width/Height.
        var f = _liveViewFrame;
        var m = _liveViewMeta;
        return new LiveViewStatusDto(
            Active: Volatile.Read(ref _liveViewActive) == 1,
            FrameSeq: f?.Seq ?? 0,
            Width: m?.Width,
            Height: m?.Height,
            ExposureSec: m?.ExposureSec,
            StartedAtUtc: m?.StartedAt.ToString("O"),
            LastFrameAtUtc: m?.LastFrameAt?.ToString("O"));
    }

    public (ReadOnlyMemory<byte> Jpeg, long Seq)? GetLiveViewFrame() {
        var f = _liveViewFrame; // single volatile read — Jpeg and Seq are always consistent
        // ReadOnlyMemory view: the buffer is shared (published, never mutated in place — each frame
        // is a fresh array), so readers get a read-only window with no per-fetch copy.
        return f is null ? null : (f.Jpeg, f.Seq);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Live View loop boundary: a single frame's device/render failure (ASCOM throw, dropped link, decode error) must not fault the long-running loop — it is logged and the loop continues to the next frame. CA1031's log-and-recover boundary applies.")]
    private async Task RunLiveViewLoopAsync(LiveViewStartRequestDto request, CancellationToken token) {
        var consecutiveFailures = 0;
        try {
            while (!token.IsCancellationRequested) {
                // Per-frame mutual exclusion with real captures: skip this frame if a manual
                // exposure holds the gate, so Live View never collides with a catalog capture. A
                // skipped frame is not a failure (a capture is running), so don't count it.
                if (Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0) {
                    await Task.Delay(LiveViewInterFrameDelay, token).ConfigureAwait(false);
                    continue;
                }
                var produced = false;
                try {
                    AlpacaCamera? client;
                    lock (_gate) {
                        client = !_disposed && _state == EquipmentConnectionState.Connected ? _client : null;
                    }
                    if (client is null) {
                        break; // disconnected/disposed — end the loop
                    }
                    produced = await CaptureLiveFrameAsync(client, request, token).ConfigureAwait(false);
                } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) {
                    LogLiveViewFrameFailed(ex);
                } finally {
                    Interlocked.Exchange(ref _captureInFlight, 0);
                }
                // Self-stop on persistent failure rather than spin forever delivering nothing (a
                // rejected setting, a device that never reaches ImageReady, a render that always
                // throws). A single good frame resets the count.
                if (produced) {
                    consecutiveFailures = 0;
                } else if (++consecutiveFailures >= LiveViewMaxConsecutiveFailures) {
                    LogLiveViewStoppedAfterFailures(consecutiveFailures);
                    break;
                }
                await Task.Delay(LiveViewInterFrameDelay, token).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // Normal stop path.
        } catch (Exception ex) {
            LogLiveViewLoopFaulted(ex);
        } finally {
            Volatile.Write(ref _liveViewActive, 0);
            // Clear the frame on EVERY exit, not just the StopLiveViewAsync path: a self-termination
            // (disconnect → client-null break) or a fault must also leave GET /liveview/frame at 204,
            // not serving a stale last frame with Active=false. Runs after any final in-flight write
            // (CaptureLiveFrameAsync has returned by the time the loop body exits), so it's the last
            // write — and it makes StopLiveViewAsync's post-await null redundant-but-harmless.
            _liveViewFrame = null;
        }
    }

    // Returns true if a frame was published, false if the frame was skipped (device never reached
    // ImageReady / dropped). Throwing propagates to the loop's catch, which counts it as a failure.
    private async Task<bool> CaptureLiveFrameAsync(AlpacaCamera client, LiveViewStartRequestDto request, CancellationToken token) {
        // Reuse the capture path's settings application (bin/gain, full-frame subframe).
        ApplyExposureSettings(client, new ExposureRequestDto(request.ExposureSec, request.Gain, BinX: request.BinX, BinY: request.BinY));
        // ApplyExposureSettings is up to 7 Alpaca round-trips with no ct hook; honor a stop that
        // arrived during it before kicking off an exposure we'd only abort on the first poll.
        token.ThrowIfCancellationRequested();
        client.StartExposure(request.ExposureSec, true);

        var ready = await WaitForImageReadyAsync(client, request.ExposureSec, token).ConfigureAwait(false);
        if (ready != true) {
            // false = device didn't reach ImageReady in time; null = dropped/superseded. Either way
            // WaitForImageReadyAsync does NOT abort, so the exposure may still be running — abort it
            // before the loop's next StartExposure (most ASCOM drivers reject a re-StartExposure
            // while one is still in flight). Quiet: a throw here (disconnected client) is ignored.
            TryAbortQuietly(client);
            return false; // skip this frame, the loop retries
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
        // Mono renders true luminance; an RGGB OSC frame renders its raw CFA mosaic as greyscale (no
        // debayer here) — acceptable for framing/focus. A debayered live render is a later LV slice
        // (tracked in PORT_TODO §64). SensorType.Color is refused at start (3-plane, unsupported).
        var stretched = Stretcher.Apply(StretchAlgorithm.AutoStf, pixels);
        var jpeg = JpegEncoder.EncodeGray(stretched, width, height, maxDim: LiveViewMaxDim);

        // Single writer (this loop): publish meta first, then the frame as the commit point — a
        // reader seeing the new frame seq will also see consistent meta.
        var prev = _liveViewMeta;
        var nextSeq = (_liveViewFrame?.Seq ?? 0) + 1;
        _liveViewMeta = new LiveViewMeta(
            width, height, request.ExposureSec,
            prev?.StartedAt ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        _liveViewFrame = new LiveViewFrameData(jpeg, nextSeq);
        return true;
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

    [LoggerMessage(EventId = 6414, Level = LogLevel.Error,
        Message = "Live View self-stopped after {Failures} consecutive frames produced no image (check exposure/binning vs the camera's capabilities).")]
    partial void LogLiveViewStoppedAfterFailures(int failures);
}
