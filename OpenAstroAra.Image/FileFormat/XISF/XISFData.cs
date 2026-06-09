#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Ionic.Zlib;
using K4os.Compression.LZ4;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Utility;
using System;
using System.Globalization;
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
            CompressionType = fileSaveInfo.XISFCompressionType;
            ChecksumType = fileSaveInfo.XISFChecksumType; ;
            ByteShuffling = fileSaveInfo.XISFByteShuffling;
            ShuffleItemSize = sizeof(ushort);


            /*
             * Convert the data array into a byte[]
             * From here onwards we deal in byte arrays only
             */
            byte[] byteArray = new byte[data.Length * ShuffleItemSize];
            Buffer.BlockCopy(data, 0, byteArray, 0, data.Length * ShuffleItemSize);

            Data = PrepareArray(byteArray);
            Size = (uint)data.Length * sizeof(ushort);
            CompressedSize = CompressionType == XISFCompressionType.NONE ? 0 : (uint)Data.Length;
        }

        public XISFData(int[] data, FileSaveInfo fileSaveInfo) : this(Array.ConvertAll(data, item => (uint)item), fileSaveInfo) {
        }

        public XISFData(uint[] data, FileSaveInfo fileSaveInfo) {
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

            Data = PrepareArray(byteArray);
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
        private byte[] PrepareArray(byte[] byteArray) {
            byte[] outArray;

            /*
             * Compress the data block as configured.
             */
            using (MyStopWatch.Measure($"XISF Compression = {CompressionType}")) {
                if (CompressionType == XISFCompressionType.LZ4) {
                    if (ByteShuffling) {
                        CompressionName = "lz4+sh";
                        byteArray = Shuffle(byteArray, ShuffleItemSize);
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
                        byteArray = Shuffle(byteArray, ShuffleItemSize);
                    } else {
                        CompressionName = "lz4hc";
                    }

                    byte[] tmpArray = new byte[LZ4Codec.MaximumOutputSize(byteArray.Length)];
                    int compressedSize = LZ4Codec.Encode(byteArray, 0, byteArray.Length, tmpArray, 0, tmpArray.Length, LZ4Level.L06_HC);

                    outArray = new byte[compressedSize];
                    Array.Copy(tmpArray, outArray, outArray.Length);
                } else if (CompressionType == XISFCompressionType.ZLIB) {
                    if (ByteShuffling) {
                        CompressionName = "zlib+sh";
                        byteArray = Shuffle(byteArray, ShuffleItemSize);
                    } else {
                        CompressionName = "zlib";
                    }

                    outArray = ZlibStream.CompressBuffer(byteArray);
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

                    case XISFChecksumType.Sha3256:
                        Checksum = GetStringFromHash(SHA3_256.HashData(outArray));
                        ChecksumName = "sha3-256";
                        break;

                    case XISFChecksumType.Sha3512:
                        Checksum = GetStringFromHash(SHA3_512.HashData(outArray));
                        ChecksumName = "sha3-512";
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

        public static byte[] Unshuffle(byte[] shuffled, int itemSize) {
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