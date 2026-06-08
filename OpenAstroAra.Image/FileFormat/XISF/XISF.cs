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
using OpenAstroAra.Core.Locale;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Core.Utility.Notification;
using OpenAstroAra.Image.FileFormat.XISF.DataConverter;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace OpenAstroAra.Image.FileFormat.XISF {

    public class XISF {

        public XISF(XISFHeader header) {
            this.Header = header;
        }

        public XISFHeader Header { get; private set; }

        public XISFData? Data { get; private set; }

        // XISF0100
        private static readonly byte[] xisfSignature = new byte[] { 0x58, 0x49, 0x53, 0x46, 0x30, 0x31, 0x30, 0x30 };

        /// <summary>
        /// The header xml + padding will consist of a muliple of bytes from this size
        /// </summary>
        public static int PaddedBlockSize => 1024;

        public static async Task<IImageData> Load(Uri filePath, bool isBayered, IImageDataFactory imageDataFactory, CancellationToken ct) {
            return await Task.Run(() => {
                using (FileStream fs = new FileStream(filePath.LocalPath, FileMode.Open, FileAccess.Read)) {
                    // First make sure we are opening a XISF file by looking for the XISF signature at bytes 1-8
                    byte[] fileSig = new byte[xisfSignature.Length];
                    fs.ReadExactly(fileSig, 0, fileSig.Length);

                    if (!fileSig.SequenceEqual(xisfSignature)) {
                        Logger.Error($"XISF: Opened file \"{filePath.LocalPath}\" is not a valid XISF file");
                        throw new InvalidDataException(Loc.Instance["LblXisfInvalidFile"]);
                    }

                    Logger.Debug($"XISF: Opening file \"{filePath.LocalPath}\"");

                    // Get the header length info, bytes 9-12
                    byte[] headerLengthInfo = new byte[4];
                    fs.ReadExactly(headerLengthInfo, 0, headerLengthInfo.Length);
                    uint headerLength = BitConverter.ToUInt32(headerLengthInfo, 0);

                    // Skip the next 4 bytes as they are reserved space
                    fs.Seek(4, SeekOrigin.Current);

                    // XML document starts at byte 17
                    byte[] bytes = new byte[headerLength];
                    fs.ReadExactly(bytes, 0, (int)headerLength);
                    string xmlString = Encoding.UTF8.GetString(bytes);

                    /*
                     * Create the header for ease of access
                     */
                    XElement xml = XElement.Parse(xmlString);
                    var header = new XISFHeader(xml);
                    var imageElement = header.Image
                        ?? throw new InvalidDataException(Loc.Instance["LblXisfInvalidFile"]);

                    var metaData = new ImageMetaData();
                    try {
                        metaData = header.ExtractMetaData();
                    } catch (Exception ex) {
                        Logger.Error($"XISF: Error during metadata extraction {ex.Message}");
                    }

                    /*
                     * Retrieve the geometry attribute.
                     */
                    int width = 0;
                    int height = 0;

                    try {
                        string[] geometry = RequiredAttribute(imageElement, "geometry").Split(':');
                        width = int.Parse(geometry[0], CultureInfo.InvariantCulture);
                        height = int.Parse(geometry[1], CultureInfo.InvariantCulture);
                    } catch (Exception ex) {
                        Logger.Error($"XISF: Could not find image geometry: {ex}");
                        throw new InvalidDataException(Loc.Instance["LblXisfInvalidGeometry"]);
                    }

                    Logger.Debug($"XISF: File geometry: width={width}, height={height}");

                    /*
                     * Retrieve the pixel data type
                     */
                    string sampleFormat = "UInt16";
                    try {
                        sampleFormat = RequiredAttribute(imageElement, "sampleFormat");
                    } catch (InvalidDataException ex) {
                        Logger.Error($"XISF: Could not read image data: {ex}");
                        throw;
                    } catch (Exception ex) {
                        Logger.Error($"XISF: Could not find image data type: {ex}");
                        throw new InvalidDataException("Could not find XISF image data type");
                    }

                    /*
                     * Determine if the data block is compressed and if a checksum is provided for it
                     */
                    XISFCompressionInfo compressionInfo = new XISFCompressionInfo();
                    string[]? compression = null;

                    try {
                        if (imageElement.Attribute("compression") != null) {
                            // [compression codec]:[uncompressed size]:[sizeof shuffled typedef]
                            compression = RequiredAttribute(imageElement, "compression").ToLowerInvariant().Split(':');

                            if (!string.IsNullOrEmpty(compression[0])) {
                                compressionInfo = GetCompressionType(compression);
                            }
                        } else {
                            Logger.Debug("XISF: Compressed data block was not encountered");
                        }
                    } catch (InvalidDataException) {
                        Logger.Error($"XISF: Unknown compression codec encountered: {compression?[0]}");
                        throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Loc.Instance["LblXisfUnsupportedCompression"], compression?[0]));
                    }

                    if (compressionInfo.CompressionType != XISFCompressionType.NONE) {
                        Logger.Debug(string.Format(CultureInfo.InvariantCulture, "XISF: CompressionType: {0}, UncompressedSize: {1}, IsShuffled: {2}, ItemSize: {3}",
                            compressionInfo.CompressionType,
                            compressionInfo.UncompressedSize,
                            compressionInfo.IsShuffled,
                            compressionInfo.ItemSize));
                    }

                    /*
                     * Determine if a checksum is provided for the datablock.
                     * If the data block is compressed, the checksum is for the compressed form.
                     */
                    XISFChecksumType cksumType = XISFChecksumType.NONE;
                    string cksumHash = string.Empty;
                    string[]? cksum = null;

                    try {
                        if (imageElement.Attribute("checksum") != null) {
                            // [hash type]:[hash string]
                            cksum = RequiredAttribute(imageElement, "checksum").ToLowerInvariant().Split(':');

                            if (!string.IsNullOrEmpty(cksum[0])) {
                                cksumType = GetChecksumType(cksum[0]);
                                cksumHash = cksum[1];
                            }
                        } else {
                            Logger.Debug("XISF: Checksummed data block was not encountered");
                        }
                    } catch (InvalidDataException) {
                        Logger.Error($"XISF: Unknown checksum type: {cksum?[0]}");
                        throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Loc.Instance["LblXisfUnsupportedChecksum"], cksum?[0]));
                    }

                    if (cksumType != XISFChecksumType.NONE) {
                        Logger.Debug($"XISF: Checksum type: {cksumType}, Hash: {cksumHash}");
                    }

                    /*
                     * Retrieve the attachment attribute to find the start and length of the data block.
                     * If the attachment attribute does not exist, we assume that the image data is
                     * inside a <Data> element and is base64-encoded.
                     */
                    BaseImageData imageData;

                    if (RequiredAttribute(imageElement, "location").StartsWith("attachment", StringComparison.Ordinal)) {
                        string[] location = RequiredAttribute(imageElement, "location").Split(':');
                        int start = int.Parse(location[1], CultureInfo.InvariantCulture);
                        int size = int.Parse(location[2], CultureInfo.InvariantCulture);

                        Logger.Debug($"XISF: Data block type: attachment, Data block start: {start}, Data block size: {size}");

                        // Read the data block in, starting at the specified offset
                        byte[] raw = new byte[size];
                        fs.Seek(start, SeekOrigin.Begin);
                        fs.ReadExactly(raw, 0, size);

                        // Validate the data block's checksum
                        if (cksumType != XISFChecksumType.NONE) {
                            if (!VerifyChecksum(raw, cksumType, cksumHash)) {
                                // Only emit a warning to the user about a bad checksum for now
                                Notifier.ShowWarning(Loc.Instance["LblXisfBadChecksum"]);
                            }
                        }

                        // Uncompress the data block
                        if (compressionInfo.CompressionType != XISFCompressionType.NONE) {
                            raw = UncompressData(raw, compressionInfo);

                            if (compressionInfo.IsShuffled) {
                                raw = XISFData.Unshuffle(raw, compressionInfo.ItemSize);
                            }
                        }

                        var converter = GetConverter(sampleFormat);
                        var img = converter.Convert(raw);

                        imageData = imageDataFactory.CreateBaseImageData(img, width, height, 16, isBayered, metaData);
                    } else {
                        string base64Img = (imageElement.Element("Data")
                            ?? throw new InvalidDataException("XISF: image data block has no Data element")).Value;
                        byte[] encodedImg = Convert.FromBase64String(base64Img);

                        var converter = GetConverter(sampleFormat);
                        var img = converter.Convert(encodedImg);

                        imageData = imageDataFactory.CreateBaseImageData(img, width, height, 16, isBayered, metaData);
                    }

                    return imageData;
                }
            }, ct);
        }

        private static IDataConverter GetConverter(string sampleFormat) {
            switch (sampleFormat) {
                case "UInt8":
                    return new UInt8Converter();

                case "UInt16":
                    return new UInt16Converter();

                case "UInt32":
                    return new UInt32Converter();

                case "UInt64":
                    return new UInt64Converter();

                case "Float32":
                    return new Float32Converter();

                case "Float64":
                    return new Float64Converter();

                default: throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Loc.Instance["LblXisfUnsupportedFormat"], sampleFormat));
            }
        }

        private static unsafe ushort GetConvertedPixelValue(byte[] rawData, int offset, string sampleFormat) {
            switch (sampleFormat) {
                case "UInt8":
                    return (ushort)(((rawData[offset]) / (double)byte.MaxValue) * ushort.MaxValue);

                case "UInt16":
                    return (ushort)((rawData[(offset * 2) + 1] << 8) | (rawData[offset * 2]));

                case "UInt32":
                    return (ushort)(((rawData[(offset * 4) + 3] << 24) | (rawData[(offset * 4) + 2] << 16) | (rawData[(offset * 4) + 1] << 8) | (rawData[offset * 4])) / (double)int.MaxValue * ushort.MaxValue);

                case "UInt64":
                    return (ushort)((((long)rawData[(offset * 8) + 7] << 56) | ((long)rawData[(offset * 8) + 6] << 48) | ((long)rawData[(offset * 8) + 5] << 40) | ((long)rawData[(offset * 8) + 4] << 32) | ((long)rawData[(offset * 8) + 3] << 24) | ((long)rawData[(offset * 8) + 2] << 16) | ((long)rawData[(offset * 8) + 1] << 8) | ((long)rawData[offset * 8])) / (double)long.MaxValue * ushort.MaxValue);

                case "Float32":
                    var integer = ((rawData[(offset * 4) + 3] << 24) | (rawData[(offset * 4) + 2] << 16) | (rawData[(offset * 4) + 1] << 8) | (rawData[offset * 4]));
                    return (ushort)((*(float*)&integer) * ushort.MaxValue);

                case "Float64":
                    var l = (((long)rawData[(offset * 8) + 7] << 56) | ((long)rawData[(offset * 8) + 6] << 48) | ((long)rawData[(offset * 8) + 5] << 40) | ((long)rawData[(offset * 8) + 4] << 32) | ((long)rawData[(offset * 8) + 3] << 24) | ((long)rawData[(offset * 8) + 2] << 16) | ((long)rawData[(offset * 8) + 1] << 8) | ((long)rawData[offset * 8]));
                    return (ushort)((*(double*)&l) * ushort.MaxValue);

                default: throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Loc.Instance["LblXisfUnsupportedFormat"], sampleFormat));
            }
        }

        private sealed class XISFCompressionInfo {
            public XISFCompressionType CompressionType { get; set; } = XISFCompressionType.NONE;
            public bool IsShuffled { get; set; }
            public int UncompressedSize { get; set; }
            public int ItemSize { get; set; }
        }

        private static XISFCompressionInfo GetCompressionType(string[] compression) {
            string codec = compression[0];

            XISFCompressionInfo info = new XISFCompressionInfo();
            info.UncompressedSize = int.Parse(compression[1], CultureInfo.InvariantCulture);

            switch (codec) {
                case "lz4":
                    info.CompressionType = XISFCompressionType.LZ4;
                    break;

                case "lz4+sh":
                    info.CompressionType = XISFCompressionType.LZ4;
                    info.ItemSize = int.Parse(compression[2], CultureInfo.InvariantCulture);
                    info.IsShuffled = true;
                    break;

                case "lz4hc":
                    info.CompressionType = XISFCompressionType.LZ4HC;
                    break;

                case "lz4hc+sh":
                    info.CompressionType = XISFCompressionType.LZ4HC;
                    info.ItemSize = int.Parse(compression[2], CultureInfo.InvariantCulture);
                    info.IsShuffled = true;
                    break;

                case "zlib":
                    info.CompressionType = XISFCompressionType.ZLIB;
                    break;

                case "zlib+sh":
                    info.CompressionType = XISFCompressionType.ZLIB;
                    info.ItemSize = int.Parse(compression[2], CultureInfo.InvariantCulture);
                    info.IsShuffled = true;
                    break;

                default:
                    throw new InvalidDataException();
            }

            return info;
        }

        private static XISFChecksumType GetChecksumType(string cksum) {
            switch (cksum) {
                case "sha-1":
                case "sha1":
                    return XISFChecksumType.SHA1;

                case "sha-256":
                case "sha256":
                    return XISFChecksumType.SHA256;

                case "sha-512":
                case "sha512":
                    return XISFChecksumType.SHA512;

                case "sha3-256":
                    return XISFChecksumType.Sha3256;

                case "sha3-512":
                    return XISFChecksumType.Sha3512;

                default:
                    throw new InvalidDataException();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do not use weak cryptographic algorithms", Justification = "SHA-1 is one of the integrity checksum algorithms defined by the XISF file-format specification; the algorithm is selected by the file being read/written for data-integrity verification, not for any security decision.")]
        private static bool VerifyChecksum(byte[] raw, XISFChecksumType cksumType, string providedCksum) {
            string computedCksum;

            using (MyStopWatch.Measure($"XISF Checksum = {cksumType}")) {
                switch (cksumType) {
                    case XISFChecksumType.SHA1:
                        computedCksum = GetStringFromHash(SHA1.HashData(raw));
                        break;

                    case XISFChecksumType.SHA256:
                        computedCksum = GetStringFromHash(SHA256.HashData(raw));
                        break;

                    case XISFChecksumType.SHA512:
                        computedCksum = GetStringFromHash(SHA512.HashData(raw));
                        break;

                    case XISFChecksumType.Sha3256:
                        computedCksum = GetStringFromHash(SHA3_256.HashData(raw));
                        break;

                    case XISFChecksumType.Sha3512:
                        computedCksum = GetStringFromHash(SHA3_512.HashData(raw));
                        break;

                    default:
                        return false;
                }
            }
            if (computedCksum.Equals(providedCksum, StringComparison.Ordinal)) {
                return true;
            } else {
                Logger.Error($"XISF: Invalid data block checksum! Expected: {providedCksum} Got: {computedCksum}");
                return false;
            }
        }

        // Reads a required XISF attribute, throwing a clear InvalidDataException if it is
        // absent (the surrounding parse treats a missing attribute as a malformed file).
        private static string RequiredAttribute(XElement element, string name) =>
            element.Attribute(name)?.Value
            ?? throw new InvalidDataException($"XISF: required attribute '{name}' is missing");

        private static byte[] UncompressData(byte[] raw, XISFCompressionInfo compressionInfo) {
            byte[] outArray = raw;

            if (compressionInfo.CompressionType != XISFCompressionType.NONE) {
                outArray = new byte[compressionInfo.UncompressedSize];

                using (MyStopWatch.Measure($"XISF Decompression = {compressionInfo.CompressionType}")) {
                    switch (compressionInfo.CompressionType) {
                        case XISFCompressionType.LZ4:
                        case XISFCompressionType.LZ4HC:
                            int size = LZ4Codec.Decode(raw, 0, raw.Length, outArray, 0, compressionInfo.UncompressedSize);

                            if (size != compressionInfo.UncompressedSize) {
                                Logger.Error($"XISF: Indicated uncompressed size does not equal actual size: Indicated: {compressionInfo.UncompressedSize}, Actual: {size}");
                            }

                            break;

                        case XISFCompressionType.ZLIB:
                            outArray = ZlibStream.UncompressBuffer(raw);
                            break;
                    }
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

        public void AddAttachedImage(ushort[] data, FileSaveInfo fileSaveInfo) {
            if (Header.Image == null) { throw new InvalidOperationException("No Image Header Information available for attaching image. Add Image Header first!"); }

            // Add Attached data location info to header
            Data = new XISFData(data, fileSaveInfo);

            if (Data.ChecksumType != XISFChecksumType.NONE) {
                Header.Image.Add(new XAttribute("checksum", $"{Data.ChecksumName}:{Data.Checksum}"));
            }

            int headerLengthBytes = 4;
            int reservedBytes = 4;
            int attachmentInfoMaxBytes = 256; // Assume max 256 bytes for the attachment, compression, and checksum attributes.
            int currentHeaderSize = Header.ByteCount + xisfSignature.Length + headerLengthBytes + reservedBytes + attachmentInfoMaxBytes;

            int dataBlockStart = currentHeaderSize + (PaddedBlockSize - currentHeaderSize % PaddedBlockSize);

            if (Data.CompressionType != XISFCompressionType.NONE) {
                Header.Image.Add(new XAttribute("location", $"attachment:{dataBlockStart}:{Data.CompressedSize}"));

                if (Data.ByteShuffling == true) {
                    Header.Image.Add(new XAttribute("compression", $"{Data.CompressionName}:{Data.Size}:{Data.ShuffleItemSize}"));
                } else {
                    Header.Image.Add(new XAttribute("compression", $"{Data.CompressionName}:{Data.Size}"));
                }
            } else {
                Header.Image.Add(new XAttribute("location", $"attachment:{dataBlockStart}:{Data.Size}"));
            }
        }

        public void AddAttachedImageInt(int[] data, FileSaveInfo fileSaveInfo) {
            if (Header.Image == null) { throw new InvalidOperationException("No Image Header Information available for attaching image. Add Image Header first!"); }

            // Add Attached data location info to header
            Data = new XISFData(data, fileSaveInfo);

            if (Data.ChecksumType != XISFChecksumType.NONE) {
                Header.Image.Add(new XAttribute("checksum", $"{Data.ChecksumName}:{Data.Checksum}"));
            }

            int headerLengthBytes = 4;
            int reservedBytes = 4;
            int attachmentInfoMaxBytes = 256; // Assume max 256 bytes for the attachment, compression, and checksum attributes.
            int currentHeaderSize = Header.ByteCount + xisfSignature.Length + headerLengthBytes + reservedBytes + attachmentInfoMaxBytes;

            int dataBlockStart = currentHeaderSize + (PaddedBlockSize - currentHeaderSize % PaddedBlockSize);

            if (Data.CompressionType != XISFCompressionType.NONE) {
                Header.Image.Add(new XAttribute("location", $"attachment:{dataBlockStart}:{Data.CompressedSize}"));

                if (Data.ByteShuffling == true) {
                    Header.Image.Add(new XAttribute("compression", $"{Data.CompressionName}:{Data.Size}:{Data.ShuffleItemSize}"));
                } else {
                    Header.Image.Add(new XAttribute("compression", $"{Data.CompressionName}:{Data.Size}"));
                }
            } else {
                Header.Image.Add(new XAttribute("location", $"attachment:{dataBlockStart}:{Data.Size}"));
            }
        }

        /// <summary>
        /// Writes monolithic XISF data to stream
        ///
        /// XISF Signature              - 8 bytes
        /// Header Length               - 4 bytes
        /// Reserved Space              - 4 bytes
        /// XISF Header                 - n bytes
        /// Padding                     - Fit the above into a multiple of PaddedBlockSize. Remaining space will be null-padded
        /// Attached XISF data block    - byte size of image data array
        /// </summary>
        /// <param name="s">Stream to write XISF data to</param>
        /// <returns></returns>
        /// <remarks>https://pixinsight.com/doc/docs/XISF-1.0-spec/XISF-1.0-spec.html#monolithic_xisf_file</remarks>
        public bool Save(Stream s) {
            // XISF0100
            s.Write(xisfSignature, 0, xisfSignature.Length);

            // XML header length
            byte[] headerlength = BitConverter.GetBytes(Header.ByteCount);
            s.Write(headerlength, 0, headerlength.Length);

            // reserved space. 4 null bytes
            byte[] reserved = new byte[] { 0, 0, 0, 0 };
            s.Write(reserved, 0, reserved.Length);

            // XISF header XML document
            Header.Save(s);

            var location = Header.Image?.Attribute("location");
            if (location == null) {
                throw new InvalidDataException("Header Image is missing location information");
            }

            // Pad space between the header and data blocks null bytes
            var remainingBlockPadding = long.Parse(location.Value.Split(':')[1], CultureInfo.InvariantCulture) - s.Position;

            for (int i = 0; i < remainingBlockPadding; i++) {
                s.WriteByte(0x0);
            }

            if (Data != null) {
                Data.Save(s);
            }

            return true;
        }
    }
}