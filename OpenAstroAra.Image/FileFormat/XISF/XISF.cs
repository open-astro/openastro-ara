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

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Lowercasing ASCII file-format tokens (XISF codec/checksum names, file extensions, EXIF tags) to match lowercase identifiers; not a security decision.")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "XISF metadata-extraction boundary: extracting metadata from arbitrary XISF XML may throw various parse/format exceptions; the failure is logged and load continues with default metadata.")]
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

                    // Guard against a malformed/hostile header length before allocating for it.
                    if (headerLength == 0 || headerLength > fs.Length) {
                        Logger.Error($"XISF: header length {headerLength} is invalid for a file of {fs.Length} bytes");
                        throw new InvalidDataException(Loc.Instance["LblXisfInvalidFile"]);
                    }

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
                    int channels = 1;

                    try {
                        string[] geometry = RequiredAttribute(imageElement, "geometry").Split(':');
                        width = int.Parse(geometry[0], CultureInfo.InvariantCulture);
                        height = int.Parse(geometry[1], CultureInfo.InvariantCulture);
                        // Per the XISF spec the last geometry token is the channel count.
                        if (geometry.Length > 2) {
                            channels = int.Parse(geometry[^1], CultureInfo.InvariantCulture);
                        }
                    } catch (Exception ex) {
                        Logger.Error($"XISF: Could not find image geometry: {ex}");
                        throw new InvalidDataException(Loc.Instance["LblXisfInvalidGeometry"]);
                    }

                    if (width <= 0 || height <= 0 || channels <= 0) {
                        Logger.Error($"XISF: invalid geometry width={width}, height={height}, channels={channels}");
                        throw new InvalidDataException(Loc.Instance["LblXisfInvalidGeometry"]);
                    }

                    long expectedSamples = (long)width * height * channels;

                    // Spec attributes that shape the block. Both are optional with defaults; a value
                    // outside the spec's vocabulary is a malformed file, not something to guess at.
                    string pixelStorage = imageElement.Attribute("pixelStorage")?.Value ?? "Planar";
                    if (!pixelStorage.Equals("Planar", StringComparison.OrdinalIgnoreCase) && !pixelStorage.Equals("Normal", StringComparison.OrdinalIgnoreCase)) {
                        throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, "XISF: unsupported pixelStorage '{0}'", pixelStorage));
                    }
                    string colorSpace = imageElement.Attribute("colorSpace")?.Value ?? "Gray";
                    if (!colorSpace.Equals("Gray", StringComparison.OrdinalIgnoreCase) && !colorSpace.Equals("RGB", StringComparison.OrdinalIgnoreCase) && !colorSpace.Equals("CIELab", StringComparison.OrdinalIgnoreCase)) {
                        throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, "XISF: unsupported colorSpace '{0}'", colorSpace));
                    }
                    // The image model is single-channel (an OSC frame is a Bayered mono array). A
                    // multi-channel block would otherwise be handed to a WxH image as a 3*W*H array,
                    // so say so instead of producing a scrambled picture (#996, remaining scope).
                    if (channels != 1) {
                        Logger.Error($"XISF: {channels}-channel {colorSpace} image; only single-channel images are supported");
                        throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, "XISF: {0}-channel images are not supported yet (colorSpace={1}, pixelStorage={2})", channels, colorSpace, pixelStorage));
                    }

                    Logger.Debug($"XISF: File geometry: width={width}, height={height}, channels={channels}");

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

                    // Floating-point samples are nominally in [0, 1]; `bounds="lo:hi"` declares the
                    // range actually used, and the spec makes it mandatory for float formats. Integer
                    // formats ignore it (their range is the type's).
                    double boundsLow = 0d, boundsHigh = 1d;
                    bool floatFormat = sampleFormat.StartsWith("Float", StringComparison.Ordinal) || sampleFormat.StartsWith("Complex", StringComparison.Ordinal);
                    if (floatFormat && imageElement.Attribute("bounds") is XAttribute boundsAttribute) {
                        string[] b = boundsAttribute.Value.Split(':');
                        if (b.Length != 2
                            || !double.TryParse(b[0], NumberStyles.Float, CultureInfo.InvariantCulture, out boundsLow)
                            || !double.TryParse(b[1], NumberStyles.Float, CultureInfo.InvariantCulture, out boundsHigh)
                            || !(boundsHigh > boundsLow)) {
                            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, "XISF: malformed bounds '{0}'", boundsAttribute.Value));
                        }
                    }

                    // Expected number of raw sample bytes for this image. Used to bound decompression
                    // allocations so a malformed/hostile "uncompressed size" cannot trigger a decompression bomb.
                    long expectedBytes = expectedSamples * SampleFormatByteSize(sampleFormat);

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
                        // Reject an uncompressed size that is non-positive or exceeds the raw image byte count.
                        if (compressionInfo.UncompressedSize <= 0 || compressionInfo.UncompressedSize > expectedBytes) {
                            Logger.Error($"XISF: declared uncompressed size {compressionInfo.UncompressedSize} is invalid (expected {expectedBytes} bytes)");
                            throw new InvalidDataException(Loc.Instance["LblXisfInvalidFile"]);
                        }
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
                            if (cksum.Length != 2 || string.IsNullOrEmpty(cksum[1])) {
                                throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Loc.Instance["LblXisfUnsupportedChecksum"], string.Join(':', cksum)));
                            }

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
                    string location = RequiredAttribute(imageElement, "location");
                    byte[] raw;

                    if (location.StartsWith("attachment", StringComparison.Ordinal)) {
                        string[] parts = location.Split(':');
                        if (parts.Length < 3) {
                            throw new InvalidDataException(Loc.Instance["LblXisfInvalidFile"]);
                        }
                        long start = long.Parse(parts[1], CultureInfo.InvariantCulture);
                        long size = long.Parse(parts[2], CultureInfo.InvariantCulture);

                        // Validate the data block lies within the file before allocating for it.
                        if (start < 0 || size < 0 || size > int.MaxValue || start + size > fs.Length) {
                            Logger.Error($"XISF: data block (start={start}, size={size}) lies outside the {fs.Length}-byte file");
                            throw new InvalidDataException(Loc.Instance["LblXisfInvalidFile"]);
                        }

                        Logger.Debug($"XISF: Data block type: attachment, Data block start: {start}, Data block size: {size}");

                        raw = new byte[size];
                        fs.Seek(start, SeekOrigin.Begin);
                        fs.ReadExactly(raw, 0, (int)size);
                    } else {
                        raw = ReadInlineOrEmbeddedBlock(imageElement, location);
                    }

                    // Same pipeline for every block location: the spec's checksum and compression
                    // attributes describe the block wherever it is stored.
                    var img = DecodeBlock(raw, sampleFormat, compressionInfo, cksumType, cksumHash, expectedSamples, boundsLow, boundsHigh);
                    BaseImageData imageData = imageDataFactory.CreateBaseImageData(img, width, height, 16, isBayered, metaData);

                    return imageData;
                }
            }, ct);
        }

        /// <summary>
        /// Reads an inline block (location "inline:encoding": the element's own text, per XISF 1.0
        /// "Inline data blocks") or an embedded block (location "embedded": a child Data element
        /// with an encoding attribute). A legacy child Data element under an inline location, as
        /// earlier writers of this code base emitted, is still accepted.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Lowercasing ASCII XISF encoding tokens to match the spec's lowercase identifiers; not a security decision.")]
        private static byte[] ReadInlineOrEmbeddedBlock(XElement imageElement, string location) {
            string encoding;
            string payload;
            var dataElement = imageElement.Element("Data") ?? imageElement.Element(XName.Get("Data", imageElement.Name.NamespaceName));

            if (location.StartsWith("inline", StringComparison.Ordinal)) {
                string[] parts = location.Split(':');
                encoding = parts.Length > 1 ? parts[1].ToLowerInvariant() : "base64";
                // Only the element's own text nodes: Image also carries FITSKeyword/Property children.
                payload = string.Concat(imageElement.Nodes().OfType<XText>().Select(t => t.Value));
                if (string.IsNullOrWhiteSpace(payload) && dataElement != null) {
                    payload = dataElement.Value;
                    encoding = (dataElement.Attribute("encoding")?.Value ?? encoding).ToLowerInvariant();
                }
            } else if (location.StartsWith("embedded", StringComparison.Ordinal)) {
                if (dataElement == null) {
                    throw new InvalidDataException("XISF: embedded image data block has no Data element");
                }
                encoding = (dataElement.Attribute("encoding")?.Value ?? "base64").ToLowerInvariant();
                payload = dataElement.Value;
            } else {
                throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, "XISF: unsupported block location '{0}'", location));
            }

            // Strip whitespace without a per-char LINQ pipeline: inline blocks can be multi-MB.
            var compact = new StringBuilder(payload.Length);
            foreach (char c in payload) {
                if (!char.IsWhiteSpace(c)) { compact.Append(c); }
            }
            payload = compact.ToString();
            try {
                return encoding switch {
                    "base64" => Convert.FromBase64String(payload),
                    "hex" => Convert.FromHexString(payload),
                    _ => throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, "XISF: unsupported block encoding '{0}'", encoding)),
                };
            } catch (FormatException ex) {
                // Same exception type as every other malformed-input path in Load.
                throw new InvalidDataException($"XISF: malformed {encoding} block payload", ex);
            }
        }

        /// <summary>
        /// Verifies, decompresses, unshuffles and converts one data block. A checksum mismatch is an
        /// error, not a warning: a block that fails its own integrity check must never be handed on
        /// as pixels (design/AUDIT.MD #H3).
        /// </summary>
        private static ushort[] DecodeBlock(byte[] raw, string sampleFormat, XISFCompressionInfo compressionInfo, XISFChecksumType cksumType, string cksumHash, long expectedSamples, double boundsLow, double boundsHigh) {
            if (cksumType != XISFChecksumType.NONE) {
                switch (VerifyChecksum(raw, cksumType, cksumHash)) {
                    case ChecksumVerdict.Mismatch:
                        throw new InvalidDataException(Loc.Instance["LblXisfBadChecksum"]);
                    case ChecksumVerdict.Unverifiable:
                        Logger.Warning($"XISF: checksum algorithm {cksumType} is not supported on this platform; the block was not verified");
                        break;
                    case ChecksumVerdict.Match:
                    default:
                        break;
                }
            }

            if (compressionInfo.CompressionType != XISFCompressionType.NONE) {
                raw = UncompressData(raw, compressionInfo);

                if (compressionInfo.IsShuffled) {
                    raw = XISFData.Unshuffle(raw, compressionInfo.ItemSize);
                }
            }

            int sampleBytes = SampleFormatByteSize(sampleFormat);
            if (raw.LongLength != expectedSamples * sampleBytes) {
                Logger.Error($"XISF: data block holds {raw.LongLength} bytes, geometry needs {expectedSamples * sampleBytes}");
                throw new InvalidDataException(Loc.Instance["LblXisfInvalidGeometry"]);
            }

            var img = GetConverter(sampleFormat, boundsLow, boundsHigh).Convert(raw);

            if (img.LongLength != expectedSamples) {
                Logger.Error($"XISF: decoded sample count {img.LongLength} does not match geometry {expectedSamples}");
                throw new InvalidDataException(Loc.Instance["LblXisfInvalidGeometry"]);
            }

            return img;
        }

        private static int SampleFormatByteSize(string sampleFormat) {
            switch (sampleFormat) {
                case "UInt8": return 1;
                case "UInt16": return 2;
                case "UInt32": return 4;
                case "UInt64": return 8;
                case "Float32": return 4;
                case "Float64": return 8;
                default: throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Loc.Instance["LblXisfUnsupportedFormat"], sampleFormat));
            }
        }

        private static IDataConverter GetConverter(string sampleFormat, double boundsLow = 0d, double boundsHigh = 1d) {
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
                    return new Float32Converter(boundsLow, boundsHigh);

                case "Float64":
                    return new Float64Converter(boundsLow, boundsHigh);

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

            // `codec:uncompressedSize[:itemSize]` -- a truncated attribute is a malformed file and
            // must read as one, not as an index exception escaping Load.
            bool shuffled = codec.EndsWith("+sh", StringComparison.Ordinal);
            if (compression.Length < (shuffled ? 3 : 2)
                || !int.TryParse(compression[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int uncompressedSize)
                || (shuffled && !int.TryParse(compression[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))) {
                throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Loc.Instance["LblXisfUnsupportedCompression"], string.Join(':', compression)));
            }

            XISFCompressionInfo info = new XISFCompressionInfo();
            info.UncompressedSize = uncompressedSize;

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

                case "zstd":
                    info.CompressionType = XISFCompressionType.ZSTD;
                    break;

                case "zstd+sh":
                    info.CompressionType = XISFCompressionType.ZSTD;
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

        /// <summary>
        /// Outcome of checking a block against its declared checksum. "Unverifiable" means this
        /// platform cannot compute the algorithm (SHA-3 on macOS); that is not evidence against
        /// the file, so the caller logs it and carries on, whereas a mismatch is fatal.
        /// </summary>
        private enum ChecksumVerdict { Match, Mismatch, Unverifiable }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do not use weak cryptographic algorithms", Justification = "SHA-1 is one of the integrity checksum algorithms defined by the XISF file-format specification; the algorithm is selected by the file being read/written for data-integrity verification, not for any security decision.")]
        private static ChecksumVerdict VerifyChecksum(byte[] raw, XISFChecksumType cksumType, string providedCksum) {
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
                        if (!SHA3_256.IsSupported) { return ChecksumVerdict.Unverifiable; }
                        computedCksum = GetStringFromHash(SHA3_256.HashData(raw));
                        break;

                    case XISFChecksumType.Sha3512:
                        if (!SHA3_512.IsSupported) { return ChecksumVerdict.Unverifiable; }
                        computedCksum = GetStringFromHash(SHA3_512.HashData(raw));
                        break;

                    default:
                        return ChecksumVerdict.Unverifiable;
                }
            }
            if (computedCksum.Equals(providedCksum, StringComparison.OrdinalIgnoreCase)) {
                return ChecksumVerdict.Match;
            }
            Logger.Error($"XISF: Invalid data block checksum! Expected: {providedCksum} Got: {computedCksum}");
            return ChecksumVerdict.Mismatch;
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
                                throw new InvalidDataException($"XISF: Indicated uncompressed size does not equal actual size: Indicated: {compressionInfo.UncompressedSize}, Actual: {size}");
                            }

                            break;

                        case XISFCompressionType.ZLIB:
                            // Inflate through a length-capped stream into the pre-sized (and pre-validated)
                            // output buffer so a malicious stream cannot expand beyond UncompressedSize.
                            using (var input = new MemoryStream(raw))
                            using (var zlib = new ZlibStream(input, CompressionMode.Decompress)) {
                                int total = 0;
                                int read;
                                while (total < outArray.Length &&
                                       (read = zlib.Read(outArray, total, outArray.Length - total)) > 0) {
                                    total += read;
                                }
                                if (total != outArray.Length) {
                                    throw new InvalidDataException($"XISF: zlib decompressed size {total} does not match expected {outArray.Length}");
                                }
                            }
                            break;

                        case XISFCompressionType.ZSTD:
                            // Decompress into the pre-sized, pre-validated buffer: a frame that wants
                            // more than UncompressedSize fails inside Unwrap instead of growing.
                            using (var decompressor = new ZstdSharp.Decompressor()) {
                                int written;
                                try {
                                    written = decompressor.Unwrap(raw, outArray);
                                } catch (Exception ex) when (ex is ZstdSharp.ZstdException or ArgumentException or InsufficientMemoryException) {
                                    // Which of these fires for an oversized frame depends on the
                                    // ZstdSharp build's pre-check; all mean the same malformed input.
                                    throw new InvalidDataException("XISF: zstd frame is malformed or larger than its declared uncompressed size", ex);
                                }
                                if (written != outArray.Length) {
                                    throw new InvalidDataException($"XISF: zstd decompressed size {written} does not match expected {outArray.Length}");
                                }
                            }
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

        public void AddAttachedImage(ushort[] data, FileSaveInfo fileSaveInfo) => AttachData(new XISFData(data, fileSaveInfo));

        /// <summary>
        /// Attach integer ADU samples as a UInt32 block. <paramref name="bitDepth"/> is the camera's,
        /// so the values can be scaled onto the spec's full [0, 2^32-1] range (see XISFData).
        /// </summary>
        public void AddAttachedImageInt(int[] data, int bitDepth, FileSaveInfo fileSaveInfo) => AttachData(new XISFData(data, bitDepth, fileSaveInfo));

        /// <summary>
        /// Records the prepared block in the header: checksum and compression attributes, then a
        /// location="attachment:start:size" whose start is the first PaddedBlockSize boundary at or
        /// after the end of the serialized header. The location attribute's own text changes the
        /// header length, so the start is found by fixed-point iteration rather than by assuming a
        /// fixed byte budget for the attributes: the old 256-byte assumption could put the declared
        /// offset before the header's end and produce an unreadable file.
        /// </summary>
        private void AttachData(XISFData data) {
            if (Header.Image == null) { throw new InvalidOperationException("No Image Header Information available for attaching image. Add Image Header first!"); }

            Data = data;

            if (Data.ChecksumType != XISFChecksumType.NONE) {
                Header.Image.SetAttributeValue("checksum", $"{Data.ChecksumName}:{Data.Checksum}");
            }

            long blockSize = Data.Size;
            if (Data.CompressionType != XISFCompressionType.NONE) {
                blockSize = Data.CompressedSize;
                Header.Image.SetAttributeValue("compression", Data.ByteShuffling
                    ? $"{Data.CompressionName}:{Data.Size}:{Data.ShuffleItemSize}"
                    : $"{Data.CompressionName}:{Data.Size}");
            }

            const int headerLengthBytes = 4;
            const int reservedBytes = 4;
            long start = 0;
            for (int attempt = 0; attempt < 8; attempt++) {
                Header.Image.SetAttributeValue("location", $"attachment:{start}:{blockSize}");
                long headerEnd = xisfSignature.Length + headerLengthBytes + reservedBytes + Header.ByteCount;
                long aligned = (headerEnd + PaddedBlockSize - 1) / PaddedBlockSize * PaddedBlockSize;
                if (aligned == start) {
                    return;
                }
                start = aligned;
            }
            throw new InvalidOperationException("XISF: data block offset did not converge");
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
            // Everything up to the data block is staged in memory, so a header/offset inconsistency
            // throws before a single byte reaches the caller's stream rather than after a partial
            // header has been written to a FileMode.Create file.
            using var staged = new MemoryStream();
            WriteHeaderAndPadding(staged);
            staged.Position = 0;
            staged.CopyTo(s);

            if (Data != null) {
                Data.Save(s);
            }

            return true;
        }

        private void WriteHeaderAndPadding(Stream s) {
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
            if (remainingBlockPadding < 0) {
                throw new InvalidDataException($"XISF: header ends at byte {s.Position} but the data block is declared to start at {s.Position + remainingBlockPadding}");
            }

            for (int i = 0; i < remainingBlockPadding; i++) {
                s.WriteByte(0x0);
            }
        }
    }
}