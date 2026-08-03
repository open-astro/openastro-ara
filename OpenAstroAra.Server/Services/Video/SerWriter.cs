#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenAstroAra.Server.Services.Video {

    /// <summary>Configuration for one <see cref="SerWriter"/> output file.</summary>
    public sealed record SerWriterOptions {
        public required string Path { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public VideoPixelFormat Format { get; init; } = VideoPixelFormat.Mono8;
        public string Observer { get; init; } = "";
        public string Instrument { get; init; } = "";
        public string Telescope { get; init; } = "";
        public int StagingBytes { get; init; } = 4 * 1024 * 1024;
    }

    /// <summary>
    /// Sequential SER (v3) file writer for the §77.1 drain thread: 178-byte header, raw
    /// frames, per-frame UTC timestamp trailer, FrameCount patched at finalize. On Linux
    /// the file is opened O_DIRECT and frame data flows through a 4 KiB-aligned staging
    /// buffer in full blocks, bypassing the page cache — writeback caching would eat all
    /// free RAM on a 2 GB box mid-capture (§77.1). Filesystems that refuse O_DIRECT
    /// (tmpfs) fall back to buffered I/O with <see cref="UsesDirectIo"/> reporting the
    /// truth. Not thread-safe; owned and driven by a single drain thread.
    /// </summary>
    public sealed class SerWriter : IDisposable {
        internal const int HeaderBytes = 178;
        private const int FrameCountOffset = 38;
        private const int DirectAlignment = 4096;

        private readonly SerWriterOptions options;
        private readonly byte[] staging;
        private readonly List<long> timestamps = new();
        private SafeFileHandle? handle;
        private int stagingUsed;
        private long filePosition;
        private bool finalized;

        public SerWriter(SerWriterOptions options) {
            this.options = options;
            var request = new VideoRequest { Width = options.Width, Height = options.Height, Format = options.Format };
            var frameBytes = VideoFormats.FrameBytes(request);
            if (frameBytes <= 0 || frameBytes > int.MaxValue) {
                throw new VideoCaptureException("SerWriter: invalid frame geometry");
            }
            FrameSize = (int)frameBytes;

            var capacity = Math.Max(options.StagingBytes, DirectAlignment * 2);
            capacity -= capacity % DirectAlignment;
            // Pinned-object-heap array: a stable address for the whole recording, page
            // aligned via the offset trick below is unnecessary — RandomAccess handles
            // alignment requirements as long as the buffer, length, and offset are all
            // 512-byte aligned; POH arrays are at least 8-aligned, so O_DIRECT writes go
            // through DirectIo.Write which enforces the file-offset/length alignment and
            // uses a memory-aligned scratch view of this array.
            staging = GC.AllocateUninitializedArray<byte>(capacity + DirectAlignment, pinned: true);
            StagingCapacity = capacity;

            handle = DirectIo.OpenForWrite(options.Path, out var direct);
            UsesDirectIo = direct;

            var header = BuildHeader();
            header.CopyTo(AlignedStaging(0, HeaderBytes));
            stagingUsed = HeaderBytes;
        }

        public int FrameSize { get; }
        public int StagingCapacity { get; }
        public bool UsesDirectIo { get; }
        public ulong FramesWritten { get; private set; }
        public ulong BytesWritten { get; private set; }

        /// <summary>.NET/SER ticks (100 ns since 0001-01-01 UTC) for "now".</summary>
        public static long UtcTicksNow() => DateTime.UtcNow.Ticks;

        /// <summary>Convert nanoseconds-since-Unix-epoch to SER UTC ticks.</summary>
        public static long UtcTicksFromUnixNanos(long unixNanos) =>
            DateTimeOffset.UnixEpoch.UtcDateTime.Ticks + unixNanos / 100;

        private Span<byte> AlignedStaging(int offset, int length) {
            // Start the logical staging region at the first 4096-aligned byte of the
            // oversized pinned array so O_DIRECT sees an aligned source address.
            var baseOffset = (int)(DirectAlignment - (StagingBaseAddress() % DirectAlignment)) % DirectAlignment;
            return staging.AsSpan(baseOffset + offset, length);
        }

        private unsafe long StagingBaseAddress() {
            fixed (byte* p = staging) {
                return (long)p;
            }
        }

        private byte[] BuildHeader() {
            var header = new byte[HeaderBytes];
            Encoding.ASCII.GetBytes("LUCAM-RECORDER").CopyTo(header, 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), 0);   // LuID
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18), VideoFormats.SerColorId(options.Format));
            // The SER "LittleEndian" flag is historically inverted: mainstream writers
            // (FireCapture, SharpCap) store 16-bit data little-endian with this field 0,
            // and readers (SER Player, PIPP) treat 0 as little-endian.
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(22), 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(26), options.Width);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(30), options.Height);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(34), VideoFormats.BitsPerPlane(options.Format));
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(FrameCountOffset), 0);  // patched at finalize
            WritePaddedAscii(header.AsSpan(42, 40), options.Observer);
            WritePaddedAscii(header.AsSpan(82, 40), options.Instrument);
            WritePaddedAscii(header.AsSpan(122, 40), options.Telescope);
            // DateTime (local) and DateTimeUTC both get the UTC start time — capture
            // boxes run on UTC; a synthesized local time would be less honest.
            var startTicks = UtcTicksNow();
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(162), startTicks);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(170), startTicks);
            return header;
        }

        private static void WritePaddedAscii(Span<byte> field, string value) {
            field.Clear();
            var bytes = Encoding.ASCII.GetBytes(value);
            bytes.AsSpan(0, Math.Min(bytes.Length, field.Length)).CopyTo(field);
        }

        /// <summary>Append one frame (length must equal <see cref="FrameSize"/>).</summary>
        public void WriteFrame(ReadOnlySpan<byte> frame, long timestampUtcTicks) {
            if (finalized || handle is null) {
                throw new VideoCaptureException("SerWriter: write after finalize");
            }
            if (frame.Length != FrameSize) {
                throw new VideoCaptureException("SerWriter: frame size mismatch");
            }
            var copied = 0;
            while (copied < frame.Length) {
                var chunk = Math.Min(frame.Length - copied, StagingCapacity - stagingUsed);
                frame.Slice(copied, chunk).CopyTo(AlignedStaging(stagingUsed, chunk));
                stagingUsed += chunk;
                copied += chunk;
                if (stagingUsed == StagingCapacity) {
                    FlushFullBlocks();
                }
            }
            timestamps.Add(timestampUtcTicks);
            FramesWritten++;
        }

        private void FlushFullBlocks() {
            var full = stagingUsed - stagingUsed % DirectAlignment;
            if (full == 0) {
                return;
            }
            RandomAccess.Write(handle!, AlignedStaging(0, full), filePosition);
            filePosition += full;
            BytesWritten += (ulong)full;
            var remainder = stagingUsed - full;
            if (remainder > 0) {
                // Move the unaligned tail to the front of the staging region.
                // Span.CopyTo has memmove semantics, so the overlap is safe.
                AlignedStaging(full, remainder).CopyTo(AlignedStaging(0, remainder));
            }
            stagingUsed = remainder;
        }

        /// <summary>
        /// Flush the staging tail, append the timestamp trailer, patch FrameCount, fsync,
        /// and close. Idempotent.
        /// </summary>
        public void Complete() {
            if (finalized || handle is null) {
                finalized = true;
                return;
            }
            finalized = true;
            try {
                CompleteCore();
            } catch {
                // A partial finalize (disk full mid-trailer) must not strand the fd
                // until the SafeHandle finalizer runs — close it deterministically.
                handle?.Dispose();
                handle = null;
                throw;
            }
        }

        private void CompleteCore() {
            FlushFullBlocks();
            // Unaligned tail + trailer + header patch need buffered semantics.
            DirectIo.ClearDirect(handle!);
            if (stagingUsed > 0) {
                RandomAccess.Write(handle!, AlignedStaging(0, stagingUsed), filePosition);
                filePosition += stagingUsed;
                BytesWritten += (ulong)stagingUsed;
                stagingUsed = 0;
            }

            if (timestamps.Count > 0) {
                var trailer = new byte[timestamps.Count * 8];
                for (var i = 0; i < timestamps.Count; i++) {
                    BinaryPrimitives.WriteInt64LittleEndian(trailer.AsSpan(i * 8), timestamps[i]);
                }
                RandomAccess.Write(handle!, trailer, filePosition);
                filePosition += trailer.Length;
                BytesWritten += (ulong)trailer.Length;
            }

            var count = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(count, (int)Math.Min(FramesWritten, int.MaxValue));
            RandomAccess.Write(handle!, count, FrameCountOffset);
            RandomAccess.FlushToDisk(handle!);
            handle!.Dispose();
            handle = null;
        }

        public void Dispose() {
            try {
                Complete();
            } catch (IOException) {
                // Dispose must not throw; Complete() is the reporting path.
            } catch (VideoCaptureException) {
            } finally {
                handle?.Dispose();
                handle = null;
            }
        }
    }
}
