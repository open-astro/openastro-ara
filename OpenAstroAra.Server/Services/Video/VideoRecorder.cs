#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Threading;

namespace OpenAstroAra.Server.Services.Video {

    /// <summary>Configuration for one <see cref="VideoRecorder"/> recording.</summary>
    public sealed record VideoRecorderOptions {
        public required SerWriterOptions Ser { get; init; }

        /// <summary>0 = adaptive: clamp(MemAvailable / 4, 64 MB, 512 MB).</summary>
        public long RingBytes { get; init; }
    }

    /// <summary>
    /// Snapshot of a recording's live counters (§77.1 honest accounting: no silent loss,
    /// ever). Once the recording has stopped, FramesCaptured == FramesWritten + RingDroppedFrames.
    /// </summary>
    public sealed record RecorderStats {
        public bool Running { get; init; }
        public ulong FramesCaptured { get; init; }
        public ulong FramesWritten { get; init; }
        public ulong RingDroppedFrames { get; init; }     // ring full — disk couldn't keep up
        public ulong SdkDroppedFrames { get; init; }      // vendor SDK / USB side
        public ulong BytesWritten { get; init; }
        public double AchievedFps { get; init; }          // measured over the whole recording
        public string Error { get; init; } = "";          // non-empty when a worker died
    }

    /// <summary>
    /// Drives one SER recording (§77.1): a capture thread pulls frames from an
    /// <see cref="IVideoCapture"/> seam straight into the pre-allocated ring, and a drain
    /// thread writes them out through <see cref="SerWriter"/>. Capture never blocks on
    /// disk — a full ring counts a drop and moves on.
    /// </summary>
    public sealed partial class VideoRecorder : IDisposable {
        private const int FrameWaitSlackMs = 500;
        private const int DrainPollMs = 100;

        private readonly IVideoCapture source;
        private readonly ILogger logger;
        private readonly object lifecycleGate = new();
        private readonly Stopwatch captureClock = new();

        private Thread? captureThread;
        private Thread? drainThread;
        private FrameRingBuffer? ring;
        private SerWriter? writer;

        private volatile bool running;
        private volatile bool stopRequested;
        private long framesCaptured;
        private long framesWritten;
        private long ringDropped;
        private long bytesWritten;
        private long sdkDropped;
        private long captureElapsedTicks;
        private string error = "";
        private readonly object errorGate = new();

        public VideoRecorder(IVideoCapture source, ILogger<VideoRecorder> logger) {
            this.source = source;
            this.logger = logger;
        }

        /// <summary>Start the source and both worker threads.</summary>
        public void Start(in VideoRequest request, VideoRecorderOptions options) {
            lock (lifecycleGate) {
                if (running) {
                    throw new VideoCaptureException("VideoRecorder: already recording");
                }
                var slot = VideoFormats.FrameBytes(request);
                if (slot <= 0 || slot > int.MaxValue) {
                    throw new VideoCaptureException("VideoRecorder: invalid frame geometry");
                }
                var ringBytes = options.RingBytes > 0
                    ? options.RingBytes
                    : FrameRingBuffer.AdaptiveRingBytes(FrameRingBuffer.ReadMemAvailableBytes());

                Interlocked.Exchange(ref framesCaptured, 0);
                Interlocked.Exchange(ref framesWritten, 0);
                Interlocked.Exchange(ref ringDropped, 0);
                Interlocked.Exchange(ref bytesWritten, 0);
                Interlocked.Exchange(ref sdkDropped, 0);
                Interlocked.Exchange(ref captureElapsedTicks, 0);
                lock (errorGate) {
                    error = "";
                }
                stopRequested = false;

                ring = new FrameRingBuffer((int)slot, ringBytes);
                writer = new SerWriter(options.Ser with {
                    Width = request.Width,
                    Height = request.Height,
                    Format = request.Format
                });

                source.Start(request);
                running = true;
                captureClock.Restart();
                var req = request;
                captureThread = new Thread(() => CaptureLoop(req)) { Name = "video-capture", IsBackground = true };
                drainThread = new Thread(DrainLoop) { Name = "video-drain", IsBackground = true };
                captureThread.Start();
                drainThread.Start();
                LogStarted(logger, options.Ser.Path, ringBytes, writer.UsesDirectIo);
            }
        }

        private void CaptureLoop(VideoRequest request) {
            var waitMs = request.ExposureMs * 2 + FrameWaitSlackMs;
            // Pre-allocated once: the drop path must not allocate at frame rate.
            var discard = GC.AllocateUninitializedArray<byte>(ring!.SlotBytes, pinned: true);
            try {
                while (!stopRequested) {
                    var slot = ring.BeginWrite();
                    if (slot.IsEmpty) {
                        // Ring full: pull the frame anyway so the SDK's own buffer
                        // doesn't overflow, then honestly count it as dropped.
                        if (source.GetFrame(discard, waitMs)) {
                            Interlocked.Increment(ref framesCaptured);
                            Interlocked.Increment(ref ringDropped);
                        }
                        continue;
                    }
                    if (!source.GetFrame(slot, waitMs)) {
                        // Timeout: nothing arrived — release the slot claim so the
                        // next iteration can take a fresh one, then re-check stop.
                        ring.CancelWrite();
                        continue;
                    }
                    Interlocked.Increment(ref framesCaptured);
                    ring.CommitWrite(ring.SlotBytes, SerWriter.UtcTicksNow());
                }
            } catch (Exception ex) when (ex is VideoCaptureException or InvalidOperationException) {
                RecordError(ex.Message);
                LogCaptureFailed(logger, ex);
            } finally {
                Interlocked.Exchange(ref captureElapsedTicks, captureClock.Elapsed.Ticks);
                try {
                    // A dead/removed camera throws here too (e.g. ZWO CameraClosed);
                    // an unhandled throw on this background thread would kill the
                    // whole daemon and skip the Close below, hanging Stop's join.
                    Interlocked.Exchange(ref sdkDropped, (long)source.DroppedFrames);
                } catch (Exception ex) when (ex is VideoCaptureException or InvalidOperationException) {
                    LogSdkDropReadFailed(logger, ex);   // keep the last-known value
                }
                ring.Close();
            }
        }

        private void DrainLoop() {
            try {
                while (true) {
                    if (!ring!.TryPop(DrainPollMs, out var frame, out var timestamp)) {
                        if (ring.Closed && ring.FramesQueued == 0) {
                            return;
                        }
                        continue;
                    }
                    writer!.WriteFrame(frame.Span, timestamp);
                    Interlocked.Increment(ref framesWritten);
                    Interlocked.Exchange(ref bytesWritten, (long)writer.BytesWritten);
                    ring.ReleaseRead();
                }
            } catch (Exception ex) when (ex is VideoCaptureException or System.IO.IOException) {
                // Deliberately no ReleaseRead here: the recording is already dead
                // (error surfaced via Stats().Error) and this ring is discarded with
                // it — a fresh Start() allocates a fresh ring. Signal the capture
                // thread too, so a dead writer doesn't leave it spinning drop-counts
                // into a full ring until the caller happens to poll Stats().
                RecordError(ex.Message);
                stopRequested = true;
                LogDrainFailed(logger, ex);
            }
        }

        /// <summary>Stop capture, drain the ring to disk, finalize the SER file. Idempotent.</summary>
        public void Stop() {
            lock (lifecycleGate) {
                if (!running) {
                    return;
                }
                stopRequested = true;
                captureThread?.Join();
                source.StopCapture();
                drainThread?.Join();
                if (writer is not null) {
                    try {
                        writer.Complete();
                        Interlocked.Exchange(ref bytesWritten, (long)writer.BytesWritten);
                    } catch (Exception ex) when (ex is VideoCaptureException or System.IO.IOException) {
                        RecordError(ex.Message);
                        LogFinalizeFailed(logger, ex);
                    }
                }
                running = false;
                var captured = Interlocked.Read(ref framesCaptured);
                var written = Interlocked.Read(ref framesWritten);
                var rDropped = Interlocked.Read(ref ringDropped);
                var sDropped = Interlocked.Read(ref sdkDropped);
                LogStopped(logger, captured, written, rDropped, sDropped);
            }
        }

        public RecorderStats Stats() {
            var captured = Interlocked.Read(ref framesCaptured);
            // Live readout (§77.4 achieved-fps): while the capture thread runs, the
            // frozen end-of-capture value isn't written yet — read the running clock.
            var elapsed = running ? captureClock.Elapsed.Ticks : Interlocked.Read(ref captureElapsedTicks);
            string currentError;
            lock (errorGate) {
                currentError = error;
            }
            return new RecorderStats {
                Running = running,
                FramesCaptured = (ulong)captured,
                FramesWritten = (ulong)Interlocked.Read(ref framesWritten),
                RingDroppedFrames = (ulong)Interlocked.Read(ref ringDropped),
                SdkDroppedFrames = (ulong)Interlocked.Read(ref sdkDropped),
                BytesWritten = (ulong)Interlocked.Read(ref bytesWritten),
                AchievedFps = elapsed > 0 ? captured / TimeSpan.FromTicks(elapsed).TotalSeconds : 0,
                Error = currentError
            };
        }

        private void RecordError(string message) {
            lock (errorGate) {
                if (error.Length == 0) {
                    error = message;
                }
            }
        }

        public void Dispose() {
            Stop();
            writer?.Dispose();
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "SER recording started: {Path} (ring {RingBytes} B, directIo={DirectIo}).")]
        private static partial void LogStarted(ILogger logger, string path, long ringBytes, bool directIo);

        [LoggerMessage(Level = LogLevel.Error, Message = "Video capture loop failed.")]
        private static partial void LogCaptureFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SDK dropped-frame counter unreadable at capture end; keeping last-known value.")]
        private static partial void LogSdkDropReadFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "SER drain loop failed.")]
        private static partial void LogDrainFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "SER finalize failed.")]
        private static partial void LogFinalizeFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "SER recording stopped: captured {Captured}, written {Written}, ring-dropped {RingDropped}, sdk-dropped {SdkDropped}.")]
        private static partial void LogStopped(ILogger logger, long captured, long written, long ringDropped, long sdkDropped);
    }
}
