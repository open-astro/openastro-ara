#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using K4os.Compression.LZ4;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Utility;
using System;
using System.Globalization;
using System.Runtime.Intrinsics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OpenAstroAra.Image.FileFormat.XISF {

    public class XISFData {

        /// <summary>
        /// Image data array
        /// </summary>
        public byte[] Data { get; }

        /// <summary>
        /// Uncompressed array size in bytes
        /// </summary>
        public uint Size { get; }

        /// <summary>
        /// Array compression algorithm
        /// </summary>
        public XISFCompressionType CompressionType { get; private set; }

        /// <summary>
        /// Array compression algorithm textual name
        /// </summary>
        public string? CompressionName { get; private set; }

        /// <summary>
        /// Compressed array size in bytes. -1 for an uncompressed array
        /// </summary>
        public uint CompressedSize { get; }

        /// <summary>
        /// Perform byte shuffling on the byte array prior to compression
        /// </summary>
        public bool ByteShuffling { get; private set; }

        /// <summary>
        /// Length in bytes of a data item for the shuffling algorithm
        /// </summary>
        public int ShuffleItemSize { get; private set; }

        /// <summary>
        /// XISF block checksum algorithm
        /// </summary>
        public XISFChecksumType ChecksumType { get; }

        /// <summary>
        /// XISF block checksum algorithm textual name
        /// </summary>
        public string? ChecksumName { get; private set; }

        /// <summary>
        /// XISF block checksum value. Empty if no checksum applied
        /// </summary>
        public string? Checksum { get; private set; }

        public XISFData(ushort[] data, FileSaveInfo fileSaveInfo) {
            RejectUnsupportedWrite(fileSaveInfo);
            CompressionType = fileSaveInfo.XISFCompressionType;
            ChecksumType = fileSaveInfo.XISFChecksumType; ;
            ByteShuffling = fileSaveInfo.XISFByteShuffling;
            ShuffleItemSize = sizeof(ushort);


            /*
             * Convert the data array into a byte[]
             * From here onwards we deal in byte arrays only. A shuffled block is built straight from
             * the samples in one vectorized pass instead of a copy and a byte-at-a-time shuffle: on
             * the CM4 that is most of the 0.2 s the shuffle used to cost per 52 MB frame.
             */
            bool shuffleNow = ByteShuffling && CompressionType != XISFCompressionType.NONE;
            byte[] byteArray;
            if (shuffleNow) {
                byteArray = ShuffleUInt16(data);
            } else {
                byteArray = new byte[data.Length * ShuffleItemSize];
                Buffer.BlockCopy(data, 0, byteArray, 0, data.Length * ShuffleItemSize);
            }

            Data = PrepareArray(byteArray, alreadyShuffled: shuffleNow);
            Size = (uint)data.Length * sizeof(ushort);
            CompressedSize = CompressionType == XISFCompressionType.NONE ? 0 : (uint)Data.Length;
        }

        /// <summary>
        /// zlib is read-only (#1148): other software writes it and the reader keeps decoding it, but
        /// writing it costs ~55 s per 52 MB frame on a CM4 against ~0.5 s for Zstandard (level 1, two threads) at a similar
        /// ratio. A request for it is refused rather than quietly written as something else.
        /// </summary>
        private static void RejectUnsupportedWrite(FileSaveInfo fileSaveInfo) {
            ArgumentNullException.ThrowIfNull(fileSaveInfo);
            if (fileSaveInfo.XISFCompressionType == XISFCompressionType.ZLIB) {
                throw new ArgumentException("XISF: zlib compression is read-only; write with Zstandard (XISFCompressionType.ZSTD) instead.", nameof(fileSaveInfo));
            }
        }

        /// <summary>
        /// Integer ADU samples from a <paramref name="bitDepth"/>-bit camera, scaled onto the spec's
        /// UInt32 range [0, 2^32-1] so a spec-correct reader (ours included) maps them back to the
        /// same ADU. Writing raw ADU into a UInt32 block, as this used to, read back as ~0.
        /// </summary>
        public XISFData(int[] data, int bitDepth, FileSaveInfo fileSaveInfo) : this(ScaleToUInt32(data, bitDepth), fileSaveInfo) {
        }

        private static uint[] ScaleToUInt32(int[] data, int bitDepth) {
            // The reported bit depth is the scale, full stop. It must be frame-independent: a
            // light and its matching dark from one camera have to encode the same ADU the same
            // way or calibration subtracts the wrong level, so no content-driven widening. An
            // ADU above the reported depth (a driver under-reporting BitDepth) is clipped, and
            // the clip is logged with a count so it cannot pass silently -- the fix for that
            // case is the driver's report, not this scale.
            int effectiveDepth = Math.Clamp(bitDepth, 1, 32);
            if (effectiveDepth != bitDepth) {
                Logger.Warning($"XISF: reported bit depth {bitDepth} is outside 1..32; scaling as {effectiveDepth}-bit data");
            }
            double max = Math.Pow(2, effectiveDepth) - 1;
            var scaled = new uint[data.Length];
            int clipped = 0;
            for (int i = 0; i < data.Length; i++) {
                double v = data[i];
                if (v > max) { v = max; clipped++; } else if (v < 0) { v = 0; }
                scaled[i] = (uint)Math.Round(v / max * uint.MaxValue);
            }
            if (clipped > 0) {
                Logger.Warning($"XISF: {clipped} sample(s) exceed the reported {bitDepth}-bit depth and were clipped; the camera driver is under-reporting its bit depth");
            }
            return scaled;
        }

        public XISFData(uint[] data, FileSaveInfo fileSaveInfo) {
            RejectUnsupportedWrite(fileSaveInfo);
            CompressionType = fileSaveInfo.XISFCompressionType;
            ChecksumType = fileSaveInfo.XISFChecksumType; ;
            ByteShuffling = fileSaveInfo.XISFByteShuffling;
            ShuffleItemSize = sizeof(uint);

            /*
             * Convert the data array into a byte[]
             * From here onwards we deal in byte arrays only
             */
            byte[] byteArray = new byte[data.Length * ShuffleItemSize];
            Buffer.BlockCopy(data, 0, byteArray, 0, data.Length * ShuffleItemSize);

            Data = PrepareArray(byteArray, alreadyShuffled: false);
            Size = (uint)data.Length * sizeof(uint);
            CompressedSize = CompressionType == XISFCompressionType.NONE ? 0 : (uint)Data.Length;
        }

        /// <summary>
        /// Write image data to stream
        /// </summary>
        /// <param name="s"></param>
        /// <remarks>XISF's default endianess is little endian</remarks>
        internal void Save(Stream s) {
            s.Write(Data, 0, Data.Length);
        }

        /// <summary>
        /// Convert the ushort array to a byte arraay, compressing with the requested algorithm if required
        /// </summary>
        /// <param name="data"></param>
        /// <returns>Uncompressed or compressed byte array</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do not use weak cryptographic algorithms", Justification = "SHA-1 is one of the integrity checksum algorithms defined by the XISF file-format specification; the algorithm is selected per the file for data-integrity verification, not for any security decision.")]
        private byte[] PrepareArray(byte[] byteArray, bool alreadyShuffled) {
            byte[] outArray;

            /*
             * Compress the data block as configured.
             */
            using (MyStopWatch.Measure($"XISF Compression = {CompressionType}")) {
                if (CompressionType == XISFCompressionType.LZ4) {
                    if (ByteShuffling) {
                        CompressionName = "lz4+sh";
                        if (!alreadyShuffled) {
                            byteArray = Shuffle(byteArray, ShuffleItemSize);
                        }
                    } else {
                        CompressionName = "lz4";
                    }

                    byte[] tmpArray = new byte[LZ4Codec.MaximumOutputSize(byteArray.Length)];
                    int compressedSize = LZ4Codec.Encode(byteArray, 0, byteArray.Length, tmpArray, 0, tmpArray.Length, LZ4Level.L00_FAST);

                    outArray = new byte[compressedSize];
                    Array.Copy(tmpArray, outArray, outArray.Length);
                } else if (CompressionType == XISFCompressionType.LZ4HC) {
                    if (ByteShuffling) {
                        CompressionName = "lz4hc+sh";
                        if (!alreadyShuffled) {
                            byteArray = Shuffle(byteArray, ShuffleItemSize);
                        }
                    } else {
                        CompressionName = "lz4hc";
                    }

                    byte[] tmpArray = new byte[LZ4Codec.MaximumOutputSize(byteArray.Length)];
                    int compressedSize = LZ4Codec.Encode(byteArray, 0, byteArray.Length, tmpArray, 0, tmpArray.Length, LZ4Level.L06_HC);

                    outArray = new byte[compressedSize];
                    Array.Copy(tmpArray, outArray, outArray.Length);
                } else if (CompressionType == XISFCompressionType.ZSTD) {
                    if (ByteShuffling) {
                        CompressionName = "zstd+sh";
                        if (!alreadyShuffled) {
                            byteArray = Shuffle(byteArray, ShuffleItemSize);
                        }
                    } else {
                        CompressionName = "zstd";
                    }

                    // Level 1 on two worker threads: on the CM4 a 52 MB frame takes 0.33 s and 22.5 MB
                    // (one thread 0.55 s; zstd's default level 3 1.2 s for 22.4 MB). A third or fourth
                    // worker gains nothing there and takes the cores guiding and frame analysis use; peak
                    // memory is the same as single-threaded. The spec fixes only the frame format, so any
                    // level or thread count reads back the same.
                    using var compressor = new ZstdSharp.Compressor(1);
                    if (ZstdWorkers > 0) {
                        compressor.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_nbWorkers, ZstdWorkers);
                    }
                    outArray = compressor.Wrap(byteArray).ToArray();
                } else {
                    outArray = new byte[byteArray.Length];
                    Array.Copy(byteArray, outArray, outArray.Length);
                    CompressionName = null;
                }
            }

            // Revert to the original data array in case the compression is bigger than uncompressed
            if (outArray.Length > byteArray.Length) {
                if (ByteShuffling) {
                    //As the original array is shuffled it needs to be unshuffled again - this scenario should be highly unlikely anyways
                    outArray = Unshuffle(byteArray, ShuffleItemSize);
                } else {
                    outArray = byteArray;
                }
                CompressionType = XISFCompressionType.NONE;
                CompressionName = null;

                Logger.Debug("XISF output array is larger after compression. Image will be prepared uncompressed instead.");
            }

            if (CompressionType != XISFCompressionType.NONE) {
                double percentChanged = (1 - ((double)outArray.Length / (double)byteArray.Length)) * 100;
                Logger.Debug($"XISF: {CompressionType} compressed {byteArray.Length} bytes to {outArray.Length} bytes ({percentChanged.ToString("#.##", CultureInfo.InvariantCulture)}%)");
            }

            /*
             * Checksum the data block as configured.
             * If the data block is compressed, we always checksum the compressed form, not the uncompressed form.
             */
            using (MyStopWatch.Measure($"XISF Checksum = {ChecksumType}")) {
                switch (ChecksumType) {
                    case XISFChecksumType.SHA1:
                        Checksum = GetStringFromHash(SHA1.HashData(outArray));
                        ChecksumName = "sha-1";
                        break;

                    case XISFChecksumType.SHA256:
                        Checksum = GetStringFromHash(SHA256.HashData(outArray));
                        ChecksumName = "sha-256";
                        break;

                    case XISFChecksumType.SHA512:
                        Checksum = GetStringFromHash(SHA512.HashData(outArray));
                        ChecksumName = "sha-512";
                        break;

                    // SHA-3 is not available on every platform .NET runs on (macOS lacks it).
                    // A save must not fail over a checksum choice: fall back to SHA-256, which
                    // the spec also allows, and say so once in the log.
                    case XISFChecksumType.Sha3256:
                        if (SHA3_256.IsSupported) {
                            Checksum = GetStringFromHash(SHA3_256.HashData(outArray));
                            ChecksumName = "sha3-256";
                        } else {
                            Logger.Warning("XISF: SHA3-256 is not supported on this platform; writing a SHA-256 checksum instead");
                            Checksum = GetStringFromHash(SHA256.HashData(outArray));
                            ChecksumName = "sha-256";
                        }
                        break;

                    case XISFChecksumType.Sha3512:
                        if (SHA3_512.IsSupported) {
                            Checksum = GetStringFromHash(SHA3_512.HashData(outArray));
                            ChecksumName = "sha3-512";
                        } else {
                            Logger.Warning("XISF: SHA3-512 is not supported on this platform; writing a SHA-512 checksum instead");
                            Checksum = GetStringFromHash(SHA512.HashData(outArray));
                            ChecksumName = "sha-512";
                        }
                        break;

                    case XISFChecksumType.NONE:
                    default:
                        Checksum = null;
                        ChecksumName = null;
                        break;
                }
            }

            return outArray;
        }

        private static string GetStringFromHash(byte[] hash) {
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < hash.Length; i++) {
                result.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return result.ToString();
        }

        public static byte[] Shuffle(byte[] unshuffled, int itemSize) {
            if (itemSize <= 0 || unshuffled.Length % itemSize != 0) {
                throw new InvalidDataException($"XISF: invalid shuffle item size {itemSize} for buffer of {unshuffled.Length} bytes");
            }
            int size = unshuffled.Length;
            byte[] shuffled = new byte[size];
            int numberOfItems = size / itemSize;
            int s = 0;

            using (MyStopWatch.Measure("XISF Byte Shuffle")) {
                for (int j = 0; j < itemSize; ++j) {
                    int u = 0 + j;
                    for (int i = 0; i < numberOfItems; ++i, ++s, u += itemSize) {
                        shuffled[s] = unshuffled[u];
                    }
                }
            }

            return shuffled;
        }

        /// <summary>Zstandard worker threads: two on a machine with four or more cores (the Pi 4 / CM4),
        /// none (compress on the calling thread) below that.</summary>
        internal static int ZstdWorkers { get; } = Environment.ProcessorCount >= 4 ? 2 : 0;

        /// <summary>
        /// <see cref="Shuffle"/> for UInt16 samples, straight from the sample array: every low byte,
        /// then every high byte (XISF blocks are little-endian), 16 samples per vector step.
        /// </summary>
        public static byte[] ShuffleUInt16(ReadOnlySpan<ushort> samples) {
            int n = samples.Length;
            byte[] shuffled = new byte[n * sizeof(ushort)];
            Span<byte> low = shuffled.AsSpan(0, n);
            Span<byte> high = shuffled.AsSpan(n, n);
            int i = 0;
            if (Vector128.IsHardwareAccelerated) {
                var lowMask = Vector128.Create((ushort)0x00FF);
                for (; i <= n - 16; i += 16) {
                    var a = Vector128.Create(samples.Slice(i, 8));
                    var b = Vector128.Create(samples.Slice(i + 8, 8));
                    Vector128.Narrow(a & lowMask, b & lowMask).CopyTo(low.Slice(i, 16));
                    Vector128.Narrow(Vector128.ShiftRightLogical(a, 8), Vector128.ShiftRightLogical(b, 8)).CopyTo(high.Slice(i, 16));
                }
            }
            for (; i < n; i++) {
                low[i] = (byte)samples[i];
                high[i] = (byte)(samples[i] >> 8);
            }
            return shuffled;
        }

        public static byte[] Unshuffle(byte[] shuffled, int itemSize) {
            if (itemSize <= 0 || shuffled.Length % itemSize != 0) {
                throw new InvalidDataException($"XISF: invalid shuffle item size {itemSize} for buffer of {shuffled.Length} bytes");
            }
            int size = shuffled.Length;
            byte[] unshuffled = new byte[size];
            int numberOfItems = size / itemSize;
            int s = 0;

            using (MyStopWatch.Measure("XISF Byte Unshuffle")) {
                for (int j = 0; j < itemSize; ++j) {
                    int u = 0 + j;
                    for (int i = 0; i < numberOfItems; ++i, ++s, u += itemSize) {
                        unshuffled[u] = shuffled[s];
                    }
                }
            }

            return unshuffled;
        }
    }
}