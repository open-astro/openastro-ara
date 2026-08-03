#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.IO;
using System.Threading;

namespace OpenAstroAra.Server.Services.Video {

    /// <summary>
    /// Single-producer / single-consumer ring of fixed-size frame slots, pre-allocated up
    /// front — no allocation in the hot path (§77.1). The producer is the capture thread
    /// (the vendor SDK copies straight into a slot); the consumer is the SER drain thread.
    /// When the ring is full the producer gets a null slot and counts the drop — capture
    /// never blocks on disk. The arena lives on the pinned object heap so slot spans can
    /// be handed to P/Invoke without per-frame pinning.
    /// </summary>
    public sealed class FrameRingBuffer {
        private const long MinRingBytes = 64L * 1024 * 1024;
        private const long MaxRingBytes = 512L * 1024 * 1024;

        private readonly byte[] arena;
        private readonly long[] slotTimestamps;
        private readonly int[] slotSizes;
        private readonly object gate = new();

        private int head;            // next slot to write
        private int tail;            // next slot to read
        private int queued;          // committed, not yet released
        private bool writeOpen;      // BeginWrite handed out, commit pending
        private bool readOpen;       // TryPop handed out, release pending
        private bool closed;

        public FrameRingBuffer(int slotBytes, long capacityBytes) {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotBytes);
            SlotBytes = slotBytes;
            SlotCount = (int)Math.Max(2, capacityBytes / slotBytes);
            arena = GC.AllocateUninitializedArray<byte>(checked(SlotBytes * SlotCount), pinned: true);
            slotTimestamps = new long[SlotCount];
            slotSizes = new int[SlotCount];
        }

        public int SlotBytes { get; }
        public int SlotCount { get; }

        public bool Closed {
            get { lock (gate) { return closed; } }
        }

        public int FramesQueued {
            get { lock (gate) { return queued; } }
        }

        /// <summary>§77.1 adaptive sizing: clamp(memAvailable / 4, 64 MB, 512 MB).</summary>
        public static long AdaptiveRingBytes(long memAvailableBytes) =>
            Math.Clamp(memAvailableBytes / 4, MinRingBytes, MaxRingBytes);

        /// <summary>
        /// MemAvailable from /proc/meminfo, in bytes; 0 when unknown (non-Linux dev
        /// hosts), which callers treat as "use the 64 MB floor".
        /// </summary>
        public static long ReadMemAvailableBytes() {
            try {
                if (!OperatingSystem.IsLinux()) {
                    return 0;
                }
                foreach (var line in File.ReadLines("/proc/meminfo")) {
                    if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) {
                        var fields = line.AsSpan("MemAvailable:".Length).Trim();
                        var space = fields.IndexOf(' ');
                        var digits = space >= 0 ? fields[..space] : fields;
                        return long.TryParse(digits, out var kib) ? kib * 1024 : 0;
                    }
                }
            } catch (IOException) {
                // Unreadable meminfo — fall through to "unknown".
            }
            return 0;
        }

        /// <summary>
        /// Producer: the next free slot, or an empty span when the ring is full (caller
        /// counts a drop) or closed.
        /// </summary>
        public Span<byte> BeginWrite() {
            lock (gate) {
                if (closed || writeOpen) {
                    return Span<byte>.Empty;
                }
                // Full when every slot is either committed or handed to the reader.
                var inUse = queued + (readOpen ? 1 : 0);
                if (inUse >= SlotCount) {
                    return Span<byte>.Empty;
                }
                writeOpen = true;
                return arena.AsSpan(head * SlotBytes, SlotBytes);
            }
        }

        /// <summary>
        /// Producer: abandon the claim from <see cref="BeginWrite"/> without publishing
        /// (frame timeout). Without this, a timed-out claim would wedge the producer —
        /// every later BeginWrite would see the open claim and report the ring full.
        /// </summary>
        public void CancelWrite() {
            lock (gate) {
                if (!writeOpen) {
                    throw new InvalidOperationException("CancelWrite without BeginWrite");
                }
                writeOpen = false;
            }
        }

        /// <summary>Producer: publish the slot returned by <see cref="BeginWrite"/>.</summary>
        public void CommitWrite(int size, long timestampUtcTicks) {
            lock (gate) {
                if (!writeOpen) {
                    throw new InvalidOperationException("CommitWrite without BeginWrite");
                }
                if (size < 0 || size > SlotBytes) {
                    throw new ArgumentOutOfRangeException(nameof(size));
                }
                slotSizes[head] = size;
                slotTimestamps[head] = timestampUtcTicks;
                head = (head + 1) % SlotCount;
                queued++;
                writeOpen = false;
                Monitor.Pulse(gate);
            }
        }

        /// <summary>
        /// Consumer: block up to <paramref name="waitMs"/> for the next frame. False on
        /// timeout, or when the ring is closed and drained. The returned memory is valid
        /// until <see cref="ReleaseRead"/>.
        /// </summary>
        public bool TryPop(int waitMs, out ReadOnlyMemory<byte> frame, out long timestampUtcTicks) {
            lock (gate) {
                if (readOpen) {
                    throw new InvalidOperationException("TryPop without ReleaseRead");
                }
                var deadline = Environment.TickCount64 + waitMs;
                while (queued == 0 && !closed) {
                    var remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0 || !Monitor.Wait(gate, (int)Math.Min(remaining, int.MaxValue))) {
                        break;
                    }
                }
                if (queued == 0) {
                    frame = ReadOnlyMemory<byte>.Empty;
                    timestampUtcTicks = 0;
                    return false;
                }
                frame = arena.AsMemory(tail * SlotBytes, slotSizes[tail]);
                timestampUtcTicks = slotTimestamps[tail];
                readOpen = true;
                return true;
            }
        }

        /// <summary>Consumer: release the slot returned by the last successful <see cref="TryPop"/>.</summary>
        public void ReleaseRead() {
            lock (gate) {
                if (!readOpen) {
                    throw new InvalidOperationException("ReleaseRead without TryPop");
                }
                tail = (tail + 1) % SlotCount;
                queued--;
                readOpen = false;
            }
        }

        /// <summary>
        /// Producer side is done: wake the consumer; <see cref="TryPop"/> returns false
        /// once the remaining frames are drained.
        /// </summary>
        public void Close() {
            lock (gate) {
                closed = true;
                Monitor.PulseAll(gate);
            }
        }
    }
}
