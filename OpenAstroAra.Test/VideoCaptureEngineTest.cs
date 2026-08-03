#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenAstroAra.Server.Services.Video;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §77.1 P1 bench tests over the synthetic frame source — the hardware-free test
    /// matrix ported 1:1 from the closed AlpacaBridge PR #168 (the C++ reference):
    /// ring semantics, SER byte-level round-trip, recorder honest accounting.
    /// </summary>
    [TestFixture]
    public class VideoCaptureEngineTest {
        private const int SerHeaderBytes = 178;

        /// <summary>
        /// Deterministic frame source: every frame is filled with its 0-based index
        /// (mod 251, a prime, so wrap boundaries are detectable); delivery can be
        /// throttled to simulate camera pacing. frameLimit 0 = unlimited.
        /// </summary>
        private sealed class SyntheticVideoCapture : IVideoCapture {
            private readonly ulong frameLimit;
            private readonly TimeSpan delay;
            private long produced;
            private int frameSize;
            private volatile bool started;

            public SyntheticVideoCapture(ulong frameLimit, TimeSpan delay) {
                this.frameLimit = frameLimit;
                this.delay = delay;
            }

            public ulong SdkDropped { get; set; }
            public ulong Produced => (ulong)Interlocked.Read(ref produced);
            public ulong DroppedFrames => SdkDropped;

            public void Start(in VideoRequest request) {
                frameSize = (int)VideoFormats.FrameBytes(request);
                Interlocked.Exchange(ref produced, 0);
                started = true;
            }

            public bool GetFrame(Span<byte> buffer, int timeoutMs) {
                if (!started || buffer.Length < frameSize) {
                    return false;
                }
                var index = (ulong)Interlocked.Read(ref produced);
                if (frameLimit != 0 && index >= frameLimit) {
                    Thread.Sleep(1);   // exhausted: behave like a camera timeout
                    return false;
                }
                if (delay > TimeSpan.Zero) {
                    Thread.Sleep(delay);
                }
                buffer[..frameSize].Fill((byte)(index % 251));
                Interlocked.Increment(ref produced);
                return true;
            }

            public void StopCapture() => started = false;

            public void Dispose() => StopCapture();
        }

        private static string TempPath(string tag) =>
            Path.Combine(Path.GetTempPath(), $"video_engine_{tag}_{Guid.NewGuid():N}.ser");

        private static VideoRequest SmallRequest() =>
            new() { Width = 32, Height = 16, Format = VideoPixelFormat.Mono8, ExposureMs = 1, Bin = 1 };

        private static void WaitForProduced(SyntheticVideoCapture source, ulong count) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (source.Produced < count && DateTime.UtcNow < deadline) {
                Thread.Sleep(5);
            }
            source.Produced.Should().BeGreaterThanOrEqualTo(count);
        }

        [Test]
        public void AdaptiveRingBytes_ClampsPerDesign() {
            const long MiB = 1024 * 1024;
            FrameRingBuffer.AdaptiveRingBytes(0).Should().Be(64 * MiB);
            FrameRingBuffer.AdaptiveRingBytes(100 * MiB).Should().Be(64 * MiB);             // floor
            FrameRingBuffer.AdaptiveRingBytes(1024 * MiB).Should().Be(256 * MiB);           // 2 GB iMate class
            FrameRingBuffer.AdaptiveRingBytes(8L * 1024 * MiB).Should().Be(512 * MiB);      // Pi 5 class, ceiling
        }

        [Test]
        public void RingBuffer_RoundTrip_PreservesDataAndOrder() {
            var ring = new FrameRingBuffer(16, 64);   // 4 slots
            for (byte i = 0; i < 3; i++) {
                var slot = ring.BeginWrite();
                slot.IsEmpty.Should().BeFalse();
                slot.Fill((byte)(i + 1));
                ring.CommitWrite(16, 1000 + i);
            }
            ring.FramesQueued.Should().Be(3);

            for (byte i = 0; i < 3; i++) {
                ring.TryPop(100, out var frame, out var ts).Should().BeTrue();
                frame.Length.Should().Be(16);
                ts.Should().Be(1000 + i);
                frame.Span[0].Should().Be((byte)(i + 1));
                frame.Span[15].Should().Be((byte)(i + 1));
                ring.ReleaseRead();
            }
            ring.FramesQueued.Should().Be(0);
        }

        [Test]
        public void RingBuffer_Full_ReturnsEmptyInsteadOfBlocking() {
            var ring = new FrameRingBuffer(16, 32);   // 2 slots
            ring.SlotCount.Should().Be(2);
            for (var i = 0; i < 2; i++) {
                ring.BeginWrite().IsEmpty.Should().BeFalse();
                ring.CommitWrite(16, i);
            }
            ring.BeginWrite().IsEmpty.Should().BeTrue();   // full — caller counts the drop

            ring.TryPop(100, out _, out _).Should().BeTrue();
            ring.BeginWrite().IsEmpty.Should().BeTrue();   // popped slot not yet released
            ring.ReleaseRead();
            ring.BeginWrite().IsEmpty.Should().BeFalse();
        }

        [Test]
        public void RingBuffer_CancelWrite_ReleasesTheClaim() {
            var ring = new FrameRingBuffer(8, 32);
            ring.BeginWrite().IsEmpty.Should().BeFalse();
            // A second claim while one is open reports "full" — the wedge CancelWrite exists to fix.
            ring.BeginWrite().IsEmpty.Should().BeTrue();
            ring.CancelWrite();
            ring.BeginWrite().IsEmpty.Should().BeFalse();
        }

        [Test]
        public void RingBuffer_Close_DrainsRemainingThenPopsFalse() {
            var ring = new FrameRingBuffer(8, 32);
            ring.BeginWrite().IsEmpty.Should().BeFalse();
            ring.CommitWrite(8, 42);

            ring.Close();
            ring.BeginWrite().IsEmpty.Should().BeTrue();   // no writes after close

            ring.TryPop(100, out _, out var ts).Should().BeTrue();
            ts.Should().Be(42);
            ring.ReleaseRead();
            ring.TryPop(10, out _, out _).Should().BeFalse();
        }

        [Test]
        public void RingBuffer_Close_WakesABlockedConsumer() {
            var ring = new FrameRingBuffer(8, 32);
            var closer = new Thread(() => {
                Thread.Sleep(50);
                ring.Close();
            });
            closer.Start();
            // Wait far longer than the close delay: the close must wake us early.
            ring.TryPop(5000, out _, out _).Should().BeFalse();
            closer.Join();
        }

        [Test]
        public void SerWriter_HeaderFramesAndTrailer_RoundTrip() {
            var path = TempPath("roundtrip");
            const int width = 8, height = 6;
            const int frameSize = width * height;
            var t0 = SerWriter.UtcTicksFromUnixNanos(1_700_000_000_000_000_000L);
            try {
                using (var writer = new SerWriter(new SerWriterOptions {
                    Path = path, Width = width, Height = height, Format = VideoPixelFormat.Mono8,
                    Observer = "Bench", Instrument = "SyntheticCam", Telescope = "TestScope"
                })) {
                    var frame = new byte[frameSize];
                    for (byte i = 0; i < 3; i++) {
                        Array.Fill(frame, (byte)(0x10 + i));
                        writer.WriteFrame(frame, t0 + i);
                    }
                    writer.Complete();
                    writer.FramesWritten.Should().Be(3);
                }

                var bytes = File.ReadAllBytes(path);
                bytes.Length.Should().Be(SerHeaderBytes + 3 * frameSize + 3 * 8);
                System.Text.Encoding.ASCII.GetString(bytes, 0, 14).Should().Be("LUCAM-RECORDER");
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18)).Should().Be(0);       // ColorID MONO
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(26)).Should().Be(width);
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(30)).Should().Be(height);
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(34)).Should().Be(8);        // depth
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(38)).Should().Be(3);        // FrameCount patched
                System.Text.Encoding.ASCII.GetString(bytes, 42, 5).Should().Be("Bench");
                BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(170)).Should().BePositive();

                for (var i = 0; i < 3; i++) {
                    bytes[SerHeaderBytes + i * frameSize].Should().Be((byte)(0x10 + i));
                    bytes[SerHeaderBytes + (i + 1) * frameSize - 1].Should().Be((byte)(0x10 + i));
                }
                var trailer = SerHeaderBytes + 3 * frameSize;
                for (var i = 0; i < 3; i++) {
                    BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(trailer + i * 8)).Should().Be(t0 + i);
                }
            } finally {
                File.Delete(path);
            }
        }

        [Test]
        public void SerWriter_Bayer16_StampsColorIdAndDepth() {
            var path = TempPath("raw16");
            try {
                using (var writer = new SerWriter(new SerWriterOptions {
                    Path = path, Width = 4, Height = 2, Format = VideoPixelFormat.BayerRggb16
                })) {
                    writer.FrameSize.Should().Be(4 * 2 * 2);
                    writer.WriteFrame(new byte[writer.FrameSize], 1);
                    writer.Complete();
                }
                var bytes = File.ReadAllBytes(path);
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18)).Should().Be(8);    // BAYER_RGGB
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(34)).Should().Be(16);
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(38)).Should().Be(1);
            } finally {
                File.Delete(path);
            }
        }

        [Test]
        public void SerWriter_LargeFrames_CrossStagingBoundaryIntact() {
            var path = TempPath("staging");
            const int frameSize = 512 * 512 * 2;
            try {
                using (var writer = new SerWriter(new SerWriterOptions {
                    Path = path, Width = 512, Height = 512, Format = VideoPixelFormat.Mono16,
                    StagingBytes = 64 * 1024   // frame (512 KiB) >> staging (64 KiB)
                })) {
                    var frame = new byte[frameSize];
                    for (var i = 0; i < frame.Length; i++) {
                        frame[i] = (byte)(i % 251);
                    }
                    writer.WriteFrame(frame, 7);
                    writer.Complete();
                    writer.BytesWritten.Should().BeGreaterThanOrEqualTo(SerHeaderBytes + frameSize);
                }
                var bytes = File.ReadAllBytes(path);
                bytes.Length.Should().Be(SerHeaderBytes + frameSize + 8);
                for (var i = 0; i < frameSize; i += 4093) {   // prime stride sample
                    bytes[SerHeaderBytes + i].Should().Be((byte)(i % 251));
                }
            } finally {
                File.Delete(path);
            }
        }

        [Test]
        public void SerWriter_RejectsMismatchedSizeAndWriteAfterComplete() {
            var path = TempPath("errors");
            try {
                using var writer = new SerWriter(new SerWriterOptions {
                    Path = path, Width = 8, Height = 8, Format = VideoPixelFormat.Mono8
                });
                var act = () => writer.WriteFrame(new byte[63], 1);
                act.Should().Throw<VideoCaptureException>();

                writer.WriteFrame(new byte[64], 1);
                writer.Complete();
                var after = () => writer.WriteFrame(new byte[64], 2);
                after.Should().Throw<VideoCaptureException>();
                writer.Complete();   // idempotent
            } finally {
                File.Delete(path);
            }
        }

        [Test]
        public void Recorder_SyntheticBench_HonestAccounting() {
            var path = TempPath("bench");
            const ulong frameCount = 200;
            using var source = new SyntheticVideoCapture(frameCount, TimeSpan.FromMicroseconds(50));
            try {
                using var recorder = new VideoRecorder(source, NullLogger<VideoRecorder>.Instance);
                recorder.Start(SmallRequest(), new VideoRecorderOptions {
                    Ser = new SerWriterOptions { Path = path, Observer = "Bench" },
                    RingBytes = 1024 * 1024
                });
                WaitForProduced(source, frameCount);
                recorder.Stop();

                var stats = recorder.Stats();
                stats.Running.Should().BeFalse();
                stats.Error.Should().BeEmpty();
                stats.FramesCaptured.Should().Be(frameCount);
                // Honest accounting: every captured frame is on disk or counted dropped.
                (stats.FramesWritten + stats.RingDroppedFrames).Should().Be(stats.FramesCaptured);
                stats.AchievedFps.Should().BeGreaterThan(0);

                var frameSize = (ulong)(32 * 16);
                var fileSize = (ulong)new FileInfo(path).Length;
                fileSize.Should().Be(SerHeaderBytes + stats.FramesWritten * frameSize + stats.FramesWritten * 8);
                stats.BytesWritten.Should().Be(fileSize);
            } finally {
                File.Delete(path);
            }
        }

        [Test]
        public void Recorder_FramePayloads_SurviveRingIntoSerInOrder() {
            var path = TempPath("payload");
            const ulong frameCount = 20;
            using var source = new SyntheticVideoCapture(frameCount, TimeSpan.Zero);
            try {
                using var recorder = new VideoRecorder(source, NullLogger<VideoRecorder>.Instance);
                recorder.Start(SmallRequest(), new VideoRecorderOptions {
                    Ser = new SerWriterOptions { Path = path },
                    RingBytes = 1024 * 1024
                });
                WaitForProduced(source, frameCount);
                recorder.Stop();

                var stats = recorder.Stats();
                stats.Error.Should().BeEmpty();
                stats.FramesWritten.Should().Be(frameCount);

                var bytes = File.ReadAllBytes(path);
                const int frameSize = 32 * 16;
                for (var i = 0; i < (int)frameCount; i++) {
                    bytes[SerHeaderBytes + i * frameSize].Should().Be((byte)(i % 251));
                    bytes[SerHeaderBytes + (i + 1) * frameSize - 1].Should().Be((byte)(i % 251));
                }
            } finally {
                File.Delete(path);
            }
        }

        [Test]
        public void Recorder_ReportsSdkSideDrops() {
            var path = TempPath("sdkdrops");
            using var source = new SyntheticVideoCapture(10, TimeSpan.Zero) { SdkDropped = 7 };
            try {
                using var recorder = new VideoRecorder(source, NullLogger<VideoRecorder>.Instance);
                recorder.Start(SmallRequest(), new VideoRecorderOptions {
                    Ser = new SerWriterOptions { Path = path },
                    RingBytes = 1024 * 1024
                });
                WaitForProduced(source, 10);
                recorder.Stop();
                recorder.Stats().SdkDroppedFrames.Should().Be(7);
            } finally {
                File.Delete(path);
            }
        }

        [Test]
        public void Recorder_DoubleStartThrows_StopIsIdempotent() {
            var path = TempPath("lifecycle");
            using var source = new SyntheticVideoCapture(5, TimeSpan.Zero);
            try {
                using var recorder = new VideoRecorder(source, NullLogger<VideoRecorder>.Instance);
                var options = new VideoRecorderOptions {
                    Ser = new SerWriterOptions { Path = path },
                    RingBytes = 1024 * 1024
                };
                recorder.Start(SmallRequest(), options);
                var second = () => recorder.Start(SmallRequest(), options);
                second.Should().Throw<VideoCaptureException>();
                recorder.Stop();
                recorder.Stop();   // idempotent
                recorder.Stats().Running.Should().BeFalse();
            } finally {
                File.Delete(path);
            }
        }

        [Test]
        public void Recorder_StartStopStorm_Survives() {
            var path = TempPath("storm");
            try {
                for (var i = 0; i < 20; i++) {
                    using var source = new SyntheticVideoCapture(0, TimeSpan.FromMicroseconds(10));
                    using var recorder = new VideoRecorder(source, NullLogger<VideoRecorder>.Instance);
                    recorder.Start(SmallRequest(), new VideoRecorderOptions {
                        Ser = new SerWriterOptions { Path = path },
                        RingBytes = 256 * 1024
                    });
                    Thread.Sleep(i % 5);
                    recorder.Stop();
                    var stats = recorder.Stats();
                    stats.Error.Should().BeEmpty();
                    (stats.FramesWritten + stats.RingDroppedFrames).Should().Be(stats.FramesCaptured);
                }
            } finally {
                File.Delete(path);
            }
        }
    }
}
