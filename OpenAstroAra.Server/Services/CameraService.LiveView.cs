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
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Stretch;
using System;
using System.Collections.Generic;
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

    // §64/§59 star-marker overlay tuning. Cap the drawn markers so a rich field stays readable and the draw
    // cost is bounded; scale each circle's radius from the star's HFR with a floor so tight stars stay visible.
    private const int LiveViewMaxMarkers = 250;
    private const double LiveViewMarkerMinRadius = 5.0;
    private const double LiveViewMarkerHfrScale = 3.0;
    // Live View is for framing/focus; a long exposure both defeats that and would hold the capture
    // gate (and a pending stop, since the ImageArray download is non-cancellable) for its whole
    // duration. 15 s is generous for framing while bounding worst-case stop/start latency.
    private const double LiveViewMaxExposureSec = 15.0;
    private static readonly TimeSpan LiveViewInterFrameDelay = TimeSpan.FromMilliseconds(100);
    // After this many consecutive frames that fail or never produce an image, self-stop the loop
    // rather than spinning forever with Active=true / FrameSeq=0 — a misconfig (e.g. a binning the
    // driver rejects) or a persistently-faulting device then surfaces as Active=false, not a silent
    // "running but never delivering" state. A self-stop signals a config/device problem to act on,
    // not a transient glitch (transient failures reset the counter on the next good frame).
    private const int LiveViewMaxConsecutiveFailures = 10;
    // Reject an absurd binning up front (→ 400) rather than let the driver fail every frame until the
    // consecutive-failure self-stop trips. 16 covers any real sensor.
    private const int LiveViewMaxBin = 16;

    // Serializes Start/Stop so they can never interleave (a Stop awaiting the old loop can't race a
    // concurrent Start spinning up a second loop). State reads (status/frame) stay lock-free. Not
    // disposed: we never touch its AvailableWaitHandle, so there is no unmanaged resource to release,
    // and disposing at teardown would throw ObjectDisposedException into any in-flight WaitAsync.
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "SemaphoreSlim allocates no unmanaged handle here (AvailableWaitHandle is never used); disposing it would race in-flight WaitAsync callers at teardown. GC reclaims it.")]
    private readonly SemaphoreSlim _liveViewMutex = new(1, 1);
    // volatile: written under the mutex by Start/Stop, but read WITHOUT it in CancelLiveViewForDispose
    // (which can't take the mutex — see there). volatile gives that lockless read an acquire fence so
    // it can't observe a stale value on a weak memory model (ARM).
    private volatile CancellationTokenSource? _liveViewCts;
    private Task? _liveViewLoop;
    private int _liveViewActive;
    // Bumped once per start (under the mutex). Lets a polling client distinguish a session restart
    // (FrameSeq resets to 1) from a mid-stream frame: a changed SessionId means "new session".
    private long _liveViewSession;
    // The latest frame + its seq, published as ONE reference so a reader can't observe a torn
    // (new-jpeg, old-seq) pair. Single writer (the loop), so no CAS needed. null = no frame yet.
    private volatile LiveViewFrameData? _liveViewFrame;
    private volatile LiveViewMeta? _liveViewMeta;

    // Session-constant status (set once at start, nulled at teardown): the requested exposure and
    // the session start. NOT written per frame, so a status read never tears against the frame.
    private sealed record LiveViewMeta(double ExposureSec, DateTimeOffset StartedAt);

    // Everything per-frame that status exposes, published as ONE immutable volatile reference so a
    // reader gets a consistent (Seq, Width, Height, CapturedAt) snapshot — no field-vs-field tear.
    // Readonly fields (not properties) on a private type avoid the CA1819 byte[]-property concern.
    private sealed class LiveViewFrameData {
        public readonly byte[] Jpeg;
        public readonly long Seq;
        public readonly long SessionId;
        public readonly int Width;
        public readonly int Height;
        public readonly DateTimeOffset CapturedAt;
        public LiveViewFrameData(byte[] jpeg, long seq, long sessionId, int width, int height, DateTimeOffset capturedAt) {
            Jpeg = jpeg; Seq = seq; SessionId = sessionId; Width = width; Height = height; CapturedAt = capturedAt;
        }
    }

    public async Task StartLiveViewAsync(LiveViewStartRequestDto request, CancellationToken ct) {
        if (request.ExposureSec <= 0 || request.ExposureSec > LiveViewMaxExposureSec) {
            throw new ArgumentOutOfRangeException(nameof(request),
                $"ExposureSec must be in (0, {LiveViewMaxExposureSec}].");
        }
        if (request.BinX < 1 || request.BinX > LiveViewMaxBin || request.BinY < 1 || request.BinY > LiveViewMaxBin) {
            throw new ArgumentOutOfRangeException(nameof(request), $"BinX/BinY must be in [1, {LiveViewMaxBin}].");
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
                // Already running: refuse rather than silently ignore the new exposure/binning/gain
                // (a 202 would lie). Stop-then-start to reconfigure. This also keeps the session's
                // frame dimensions constant, so GetLiveViewStatus's frame/meta reads can't disagree.
                throw new InvalidOperationException(
                    "Live View is already running; stop it before starting with new parameters.");
            }
            // A self-terminated loop (e.g. camera disconnect) leaves its spent CTS behind; dispose it
            // before replacing so it isn't leaked.
            _liveViewCts?.Dispose();
            // Fresh session: drop any frame from a prior session so FrameSeq == 0 / GET /frame → 204
            // reliably mean "no frame yet this session".
            _liveViewFrame = null;
            var cts = new CancellationTokenSource();
            _liveViewCts = cts;
            _liveViewMeta = new LiveViewMeta(request.ExposureSec, DateTimeOffset.UtcNow);
            // §64 OSC: resolve the session's Bayer pattern once at start (it can't change mid-session
            // — a restart is required to rebin anyway). Debayer only at 1×1, mirroring the capture
            // path's BAYERPAT stamping: hardware binning averages adjacent cells, mixing R/G/B, so a
            // binned readout is no longer a Bayer mosaic and renders as luminance instead.
            BayerPattern? bayerPattern = null;
            if (request.BinX == 1 && request.BinY == 1
                    && Debayer.TryParse(_capabilities?.BayerPattern, out var parsedPattern)) {
                bayerPattern = parsedPattern;
            }
            var session = Interlocked.Increment(ref _liveViewSession);
            Volatile.Write(ref _liveViewActive, 1);
            _liveViewLoop = Task.Run(
                () => RunLiveViewLoopAsync(request, session, bayerPattern, cts.Token),
                CancellationToken.None);
        } finally {
            _liveViewMutex.Release();
        }
        LogLiveViewStarted(request.ExposureSec, request.BinX, request.BinY);
    }

    // No CancellationToken parameter by design: a stop always runs to completion once requested (a
    // caller cannot race-cancel it). The endpoint returns 204 only after the loop has fully drained
    // (up to LiveViewMaxExposureSec on a slow, non-cancellable ImageArray download), so by the time
    // the caller sees 204, status is already Active=false / FrameSeq=0.
    public async Task StopLiveViewAsync() {
        // CancellationToken.None: even if a caller wanted to cancel, honoring it here could strand
        // the loop running (e.g. an HTTP disconnect while we wait on the mutex held by an in-flight
        // frame download). A stop is unconditional.
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
            // Guarded like CancelAsync above: CancelLiveViewForDispose (DI teardown, no mutex) can
            // dispose the same CTS concurrently. Dispose is idempotent in practice, but guard it to
            // match the cancel path and stay safe across that narrow window.
            try { cts?.Dispose(); } catch (ObjectDisposedException) { }
            // Drop the last frame AND its meta so GET /liveview/frame returns 204 and GET /liveview
            // reports no dimensions once stopped (honors the DTO "null between sessions" contract).
            // MUST come after `await loop`: cancellation can land during the non-cancellable
            // ImageArray download, so the loop may still publish one final frame after we cancel —
            // nulling before the await would let that late write resurrect a stale frame. Null the
            // frame FIRST, meta second (status now reads frame→meta, and Width/Height live in the
            // frame): the only transient a reader can catch is frame=null + meta=set, which is the
            // legitimate "started, no frame yet" shape — not a frame-without-config contradiction.
            _liveViewFrame = null;
            _liveViewMeta = null;
        } finally {
            _liveViewMutex.Release();
        }
        LogLiveViewStopped();
    }

    public LiveViewStatusDto GetLiveViewStatus() {
        // Per-frame fields (Seq/Width/Height/LastFrameAt) come from the single atomic _liveViewFrame
        // snapshot, so they can never tear against each other. The session-constant meta (exposure /
        // start) is set once at start and not written per frame, so it can't tear against the frame
        // either. (Both are nulled at teardown; gate "live" on Active per the DTO contract.)
        // Read Active FIRST: Start writes meta (release) then Active=1 (release), so an Active
        // acquire-read gates the meta/frame reads that follow — observing Active=true then guarantees
        // the matching non-null meta/frame is visible (reading them before the Active acquire could
        // see Active=true with stale nulls on a weak memory model).
        var active = Volatile.Read(ref _liveViewActive) == 1;
        var f = _liveViewFrame;
        var m = _liveViewMeta;
        return new LiveViewStatusDto(
            Active: active,
            SessionId: Interlocked.Read(ref _liveViewSession),
            FrameSeq: f?.Seq ?? 0,
            Width: f?.Width,
            Height: f?.Height,
            ExposureSec: m?.ExposureSec,
            StartedAtUtc: m?.StartedAt.ToString("O"),
            LastFrameAtUtc: f?.CapturedAt.ToString("O"));
    }

    public (ReadOnlyMemory<byte> Jpeg, long Seq, long SessionId)? GetLiveViewFrame() {
        var f = _liveViewFrame; // single volatile read — all fields are a consistent snapshot
        // ReadOnlyMemory view: the buffer is shared (published, never mutated in place — each frame
        // is a fresh array), so readers get a read-only window with no per-fetch copy.
        return f is null ? null : (f.Jpeg, f.Seq, f.SessionId);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Live View loop boundary: a single frame's device/render failure (ASCOM throw, dropped link, decode error) must not fault the long-running loop — it is logged and the loop continues to the next frame. CA1031's log-and-recover boundary applies.")]
    private async Task RunLiveViewLoopAsync(
            LiveViewStartRequestDto request, long session, BayerPattern? bayerPattern,
            CancellationToken token) {
        var consecutiveFailures = 0;
        // The bin/gain/subframe are constant for a session, so ApplyExposureSettings (up to ~10
        // Alpaca round-trips) only needs to run once — re-running it every frame is pure latency on
        // a slow bridge. Re-apply only when the device's settings may have changed under us: a manual
        // capture interleaving (gate contention) applies its own settings, and a frame error leaves
        // the device state unknown.
        var settingsApplied = false;
        try {
            while (!token.IsCancellationRequested) {
                // Per-frame mutual exclusion with real captures: skip this frame if a manual
                // exposure holds the gate, so Live View never collides with a catalog capture. A
                // skipped frame is not a failure (a capture is running), so don't count it — but the
                // capture changes the camera's settings, so re-apply ours on the next frame.
                if (Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0) {
                    settingsApplied = false;
                    await Task.Delay(LiveViewInterFrameDelay, token).ConfigureAwait(false);
                    continue;
                }
                (ushort[] Pixels, int Width, int Height)? acquired = null;
                try {
                    AlpacaCamera? client;
                    lock (_gate) {
                        client = !_disposed && _state == EquipmentConnectionState.Connected ? _client : null;
                    }
                    if (client is null) {
                        break; // disconnected/disposed — end the loop
                    }
                    acquired = await AcquireLiveFramePixelsAsync(
                        client, request, applySettings: !settingsApplied, token).ConfigureAwait(false);
                    settingsApplied = true; // settings are current (just applied, or unchanged since we hold the gate)
                } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) {
                    LogLiveViewFrameFailed(ex);
                    settingsApplied = false; // device state unknown after a failure — re-apply next frame
                } finally {
                    Interlocked.Exchange(ref _captureInFlight, 0);
                }
                // §64 — the CPU half runs OFF the capture gate (PORT_TODO follow-up from #775):
                // RenderLiveFrame (stretch/debayer/JPEG and, with annotate, the full-frame
                // StarDetector pass — real CPU time on a big sensor) used to run inside
                // _captureInFlight, so every annotated live frame extended the window a manual
                // catalog capture was blocked. The device work is finished by here; a capture
                // can interleave while this frame renders.
                var produced = false;
                if (acquired is { } frame) {
                    try {
                        PublishLiveFrame(frame.Pixels, frame.Width, frame.Height, request, session, bayerPattern);
                        produced = true;
                    } catch (Exception ex) {
                        // Render/encode failure: the device was untouched (settings stay
                        // current), but no frame landed — still counted below so a render that
                        // always throws self-stops the loop instead of spinning forever.
                        LogLiveViewFrameFailed(ex);
                    }
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
            // write — and it makes StopLiveViewAsync's post-await null redundant-but-harmless. Frame
            // first, meta second (status reads frame→meta; Width/Height live in the frame): the only
            // transient is frame=null + meta=set, i.e. the legitimate "started, no frame yet" shape.
            _liveViewFrame = null;
            _liveViewMeta = null;
        }
    }

    // The DEVICE half of one live frame, run under the _captureInFlight gate: settings →
    // expose → ImageReady poll → download → pixel conversion. Returns the raw pixels (the
    // CPU render half runs off-gate in the loop), or null when the frame was skipped (device
    // never reached ImageReady / dropped). Throwing propagates to the loop's catch, which
    // counts it as a failure.
    private async Task<(ushort[] Pixels, int Width, int Height)?> AcquireLiveFramePixelsAsync(
            AlpacaCamera client, LiveViewStartRequestDto request, bool applySettings, CancellationToken token) {
        // Reuse the capture path's settings application (bin/gain, full-frame subframe). Applied only
        // when the loop knows the device settings may be stale (first frame of a session, or after a
        // manual capture / error) — see RunLiveViewLoopAsync's settingsApplied tracking.
        if (applySettings) {
            ApplyExposureSettings(client, new ExposureRequestDto(request.ExposureSec, request.Gain, BinX: request.BinX, BinY: request.BinY));
        }
        // ApplyExposureSettings is up to 7 Alpaca round-trips with no ct hook; honor a stop that
        // arrived during it before kicking off an exposure we'd only abort on the first poll.
        token.ThrowIfCancellationRequested();
        client.StartExposure(request.ExposureSec, true);

        bool? ready;
        try {
            ready = await WaitForImageReadyAsync(client, request.ExposureSec, token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            // A stop arrived mid-wait: the exposure is still running on the device. Abort it before
            // propagating, so the next session's StartExposure doesn't collide with a live exposure.
            TryAbortQuietly(client);
            throw;
        }
        if (ready != true) {
            // false = device didn't reach ImageReady in time; null = dropped/superseded. Either way
            // WaitForImageReadyAsync does NOT abort, so the exposure may still be running — abort it
            // before the loop's next StartExposure (most ASCOM drivers reject a re-StartExposure
            // while one is still in flight). Quiet: a throw here (disconnected client) is ignored.
            TryAbortQuietly(client);
            return null; // skip this frame, the loop retries
        }
        // Exposure already completed (ImageReady): the camera holds a finished image, so a cancel
        // here needs no abort — nothing is running to stop.
        token.ThrowIfCancellationRequested();

        object? imageArray;
        Interlocked.Exchange(ref _downloading, 1);
        try {
            imageArray = client.ImageArray;
        } finally {
            Interlocked.Exchange(ref _downloading, 0);
        }
        return ConvertImageArray(imageArray);
    }

    // The CPU half of one live frame, run OFF the capture gate: render (stretch / debayer /
    // detect / JPEG) and publish. Single writer (the live loop). Meta (exposure/start) was set
    // once at start and isn't touched here, so the only per-frame publish is the frame itself —
    // one atomic volatile write carrying seq + dimensions + capture time, so status reads can't
    // tear.
    private void PublishLiveFrame(ushort[] pixels, int width, int height,
            LiveViewStartRequestDto request, long session, BayerPattern? bayerPattern) {
        var (jpeg, outWidth, outHeight) = RenderLiveFrame(pixels, width, height, bayerPattern, request.Annotate);
        var nextSeq = (_liveViewFrame?.Seq ?? 0) + 1;
        _liveViewFrame = new LiveViewFrameData(jpeg, nextSeq, session, outWidth, outHeight, DateTimeOffset.UtcNow);
    }

    // §64 render: raw pixels → live JPEG. An OSC session at 1×1 (bayerPattern set at start) renders
    // debayered COLOUR via the shared §65 preview recipe — super-pixel halves the dimensions, which
    // the published frame dims must reflect (the client sizes by them). Mono — or a binned OSC
    // readout, which is no longer a Bayer mosaic — renders greyscale luminance as before.
    // SensorType.Color (already-debayered 3-plane) is refused at start.
    internal static (byte[] Jpeg, int Width, int Height) RenderLiveFrame(
            ushort[] pixels, int width, int height, BayerPattern? bayerPattern, bool annotate = false) {
        if (bayerPattern is { } pattern) {
            if (annotate) {
                // §64 OSC annotation — detecting on the raw Bayer mosaic would be wrong (the CFA
                // pattern reads as per-pixel noise to the detector), so detect on the super-pixel
                // LUMINANCE plane: raw 16-bit dynamics on the SAME half-res grid as the colour
                // output, so markers land on their stars with no coordinate scaling.
                var (argb, luminance, aw, ah) = Debayer.SuperPixelStretchedWithLuminance(
                    pixels, width, height, pattern, StretchAlgorithm.AutoStf);
                var colorMarkers = DetectStarMarkers(luminance, aw, ah);
                return (JpegEncoder.EncodeColorAnnotated(argb, aw, ah, colorMarkers, maxDim: LiveViewMaxDim), aw, ah);
            }
            var (rgb, ow, oh) = Debayer.SuperPixelStretched(
                pixels, width, height, pattern, StretchAlgorithm.AutoStf);
            return (JpegEncoder.EncodeColor(rgb, ow, oh, maxDim: LiveViewMaxDim), ow, oh);
        }
        var stretched = Stretcher.Apply(StretchAlgorithm.AutoStf, pixels);
        if (annotate) {
            // Detect on the RAW pixels — the same width×height grid as the stretched preview, since the stretch
            // is per-pixel and never moves a star — so markers land on their stars; EncodeGrayAnnotated downscales
            // the frame first, then draws the markers into the ≤maxDim output so their rings survive the cap.
            var markers = DetectStarMarkers(pixels, width, height);
            return (JpegEncoder.EncodeGrayAnnotated(stretched, width, height, markers, maxDim: LiveViewMaxDim), width, height);
        }
        return (JpegEncoder.EncodeGray(stretched, width, height, maxDim: LiveViewMaxDim), width, height);
    }

    // §64/§59 — run the shared star detector on a live frame and turn each detected star into an overlay
    // circle, its radius scaled from the star's HFR (with a floor so tight stars stay visible). Uses the shared
    // AnalysisDetectionParams so the overlay matches what the §59 HFR trend sees. LiveViewMaxMarkers caps the
    // returned/drawn markers so a rich field doesn't bury the preview — and the detector's capped path
    // measures brightest-first only until the cap fills, so a dense field's tail blobs cost a flood-fill
    // but no per-blob measurement.
    private static List<StarMarker> DetectStarMarkers(ushort[] pixels, int width, int height) {
        var result = StarDetector.Detect(
            pixels, width, height, AnalysisDetectionParams(LiveViewMaxMarkers), CancellationToken.None);
        var markers = new List<StarMarker>(result.StarList.Count);
        foreach (var s in result.StarList) {
            // Unpack decodes StarDetector's row-major packed Position — going through the helper keeps this
            // caller from silently mislocating every marker if that packing ever changes.
            var (x, y) = s.Unpack(width);
            float radius = (float)Math.Max(LiveViewMarkerMinRadius, s.HFR * LiveViewMarkerHfrScale);
            markers.Add(new StarMarker(x, y, radius));
        }
        return markers;
    }

    // Called from Dispose: cancel the loop promptly without awaiting it, and release the CTS. Not
    // awaiting is safe — the orphaned loop always completes on its own: it breaks on the
    // _disposed/_client guard, and even if it's mid-download when Dispose tears down the
    // AlpacaCamera, the synchronous client.ImageArray throws ObjectDisposedException, which the
    // loop's top-level catch(Exception) absorbs (it never faults the task).
    private void CancelLiveViewForDispose() {
        // Only READ the shared fields here — never write _liveViewCts/_liveViewLoop. This path runs
        // without the mutex (it can't take it: StopLiveViewAsync holds the mutex while awaiting the
        // loop, so blocking on it would deadlock). Writing _liveViewLoop=null here would race a
        // concurrent StopLiveViewAsync, which could then read null, skip `await loop`, and report the
        // stop complete before the loop drained. Cancelling the CTS is enough to end the loop; the
        // mutex-protected Start/Stop own the field lifecycle and dispose the CTS.
        var cts = _liveViewCts;
        Volatile.Write(ref _liveViewActive, 0);
        if (cts is not null) {
            // Guard both: a concurrent StopLiveViewAsync (under the mutex) may dispose the same CTS.
            // Dispose is idempotent, but guard it to match the Cancel guard above and stay consistent.
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            try { cts.Dispose(); } catch (ObjectDisposedException) { }
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
