#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Globalization;
using System.Text;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Image.FileFormat;
using OpenAstroAra.Image.FileFormat.XISF;
using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Image.Interfaces;
using Xunit;

namespace OpenAstroAra.Image.Tests;

/// <summary>
/// Writer/reader round trips through the real <see cref="XISF"/> container, one case per
/// (compression, byte-shuffling, checksum) combination the writer can emit, plus the reader-only
/// paths (inline/embedded blocks, every sample format, corrupt blocks). Every file is synthesized
/// in a temp directory; nothing is committed as a fixture (.xisf is LFS-tracked, and CI checks out
/// with lfs: false).
/// </summary>
public sealed class XisfRoundTripTests : IDisposable {
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ara-xisf-tests-" + Guid.NewGuid().ToString("N"));

    public XisfRoundTripTests() { Directory.CreateDirectory(_dir); }

    public void Dispose() {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string TempPath(string name) => Path.Combine(_dir, name);

    private static ushort[] Pattern(int w, int h) {
        // A gradient with the extremes present, so a scaling bug at either end shows up.
        var px = new ushort[w * h];
        for (int i = 0; i < px.Length; i++) {
            px[i] = (ushort)(i * (ushort.MaxValue / (px.Length - 1)));
        }
        px[0] = 0;
        px[^1] = ushort.MaxValue;
        return px;
    }

    private static FileSaveInfo SaveInfo(XISFCompressionType c, bool shuffle, XISFChecksumType k) => new() {
        FileType = FileType.XISF,
        XISFCompressionType = c,
        XISFByteShuffling = shuffle,
        XISFChecksumType = k,
    };

    private static byte[] Write(ushort[] px, int w, int h, FileSaveInfo info, ImageMetaData? meta = null) {
        var header = new XISFHeader();
        header.AddImageMetaData(new ImageProperties(w, h, 16, false, 0, 0), "LIGHT");
        header.Populate(meta ?? new ImageMetaData());
        var xisf = new XISF(header);
        xisf.AddAttachedImage(px, info);
        using var ms = new MemoryStream();
        Assert.True(xisf.Save(ms));
        return ms.ToArray();
    }

    private async Task<IImageData> Read(byte[] file, string name = "img.xisf") {
        var path = TempPath(name);
        await File.WriteAllBytesAsync(path, file);
        return await XISF.Load(new Uri(path), false, new FakeImageDataFactory(), CancellationToken.None);
    }

    /// <summary>Every writer option combination. The reader must return the identical pixels.</summary>
    public static IEnumerable<object[]> WriterOptions() {
        foreach (var c in new[] { XISFCompressionType.NONE, XISFCompressionType.LZ4, XISFCompressionType.LZ4HC, XISFCompressionType.ZLIB, XISFCompressionType.ZSTD }) {
            foreach (var shuffle in new[] { false, true }) {
                if (c == XISFCompressionType.NONE && shuffle) { continue; }
                foreach (var k in new[] { XISFChecksumType.NONE, XISFChecksumType.SHA1, XISFChecksumType.SHA256, XISFChecksumType.SHA512, XISFChecksumType.Sha3256, XISFChecksumType.Sha3512 }) {
                    yield return new object[] { c, shuffle, k };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(WriterOptions))]
    public async Task UInt16RoundTripIsLossless(XISFCompressionType c, bool shuffle, XISFChecksumType k) {
        const int w = 37, h = 23;  // odd sizes: nothing aligns by accident
        var px = Pattern(w, h);
        var img = await Read(Write(px, w, h, SaveInfo(c, shuffle, k)));
        Assert.Equal(w, img.Properties.Width);
        Assert.Equal(h, img.Properties.Height);
        Assert.Equal(px, img.Data.FlatArray);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(12)]
    [InlineData(14)]
    public async Task UInt32WriterRoundTripsAduExactlyThroughTheSpecScaling(int bitDepth) {
        // ADU from a bitDepth-bit camera go into a UInt32 block scaled onto [0, 2^32-1]; the
        // spec-correct reader maps them back onto 16 bits. For a 16-bit camera that is exact;
        // for shallower cameras it is the ADU rescaled to 16 bits, as every other reader would.
        const int w = 8, h = 4;
        int max = (1 << bitDepth) - 1;
        var px = new int[w * h];
        for (int i = 0; i < px.Length; i++) { px[i] = (int)((long)i * max / (px.Length - 1)); }
        var header = new XISFHeader();
        header.AddImageMetaData(new ImageProperties(w, h, bitDepth, false, 0, 0), "LIGHT", XISFSampleFormat.UInt32);
        header.Populate(new ImageMetaData());
        var xisf = new XISF(header);
        xisf.AddAttachedImageInt(px, bitDepth, SaveInfo(XISFCompressionType.LZ4, true, XISFChecksumType.SHA256));
        using var ms = new MemoryStream();
        xisf.Save(ms);
        var img = await Read(ms.ToArray());
        var expected = px.Select(v => (ushort)Math.Round(v / (double)max * ushort.MaxValue)).ToArray();
        Assert.Equal(expected, img.Data.FlatArray);
        Assert.Equal(ushort.MaxValue, img.Data.FlatArray[^1]);
    }

    [Fact]
    public async Task MetadataSurvivesTheRoundTrip() {
        var meta = new ImageMetaData();
        meta.Image.ExposureTime = 120.5;
        meta.Image.ImageType = "LIGHT";
        meta.Camera.Name = "Test Camera";
        meta.Camera.Gain = 100;
        meta.Camera.Offset = 20;
        meta.Camera.Temperature = -10.5;
        meta.Telescope.Name = "Test Scope";
        meta.FilterWheel.Filter = "Ha";
        meta.Target.Name = "M31";
        meta.Observer.Latitude = 51.5;
        meta.Observer.Longitude = -0.12;
        var img = await Read(Write(Pattern(4, 4), 4, 4, SaveInfo(XISFCompressionType.NONE, false, XISFChecksumType.NONE), meta));
        var m = img.MetaData;
        Assert.Equal(120.5, m.Image.ExposureTime);
        Assert.Equal("LIGHT", m.Image.ImageType);
        Assert.Equal("Test Camera", m.Camera.Name);
        Assert.Equal(100, m.Camera.Gain);
        Assert.Equal(20, m.Camera.Offset);
        Assert.Equal(-10.5, m.Camera.Temperature);
        Assert.Equal("Test Scope", m.Telescope.Name);
        Assert.Equal("Ha", m.FilterWheel.Filter);
        Assert.Equal("M31", m.Target.Name);
        Assert.Equal(51.5, m.Observer.Latitude, 6);
        Assert.Equal(-0.12, m.Observer.Longitude, 6);
    }

    [Theory]
    [InlineData(XISFChecksumType.Sha3256, "sha3-256", "sha-256")]
    [InlineData(XISFChecksumType.Sha3512, "sha3-512", "sha-512")]
    public void Sha3FallsBackToSha2WhereThePlatformLacksIt(XISFChecksumType k, string sha3Name, string fallbackName) {
        // .NET has no SHA-3 on macOS. A save must still succeed, with a checksum the spec allows.
        var file = Write(Pattern(8, 8), 8, 8, SaveInfo(XISFCompressionType.NONE, false, k));
        string xml = Encoding.UTF8.GetString(file, 16, (int)BitConverter.ToUInt32(file, 8));
        bool supported = k == XISFChecksumType.Sha3256 ? System.Security.Cryptography.SHA3_256.IsSupported : System.Security.Cryptography.SHA3_512.IsSupported;
        Assert.Contains($"checksum=\"{(supported ? sha3Name : fallbackName)}:", xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASha3ChecksumTheReaderCannotComputeIsSkippedNotRejected() {
        // The hash below is deliberately wrong. Where SHA-3 exists the mismatch is fatal; where
        // it does not, the block cannot be verified, which is not evidence against the file.
        var raw = new byte[] { 0x01, 0x00, 0x02, 0x00 };
        var file = Monolithic("geometry=\"2:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"inline:base64\" checksum=\"sha3-256:" + new string('0', 64) + "\"", Convert.ToBase64String(raw));
        if (System.Security.Cryptography.SHA3_256.IsSupported) {
            await Assert.ThrowsAsync<InvalidDataException>(() => Read(file));
        } else {
            Assert.Equal(new ushort[] { 1, 2 }, (await Read(file)).Data.FlatArray);
        }
    }

    [Fact]
    public void WriterEmitsTheSpecHeaderAndAnAlignedBlockAfterIt() {
        var file = Write(Pattern(300, 200), 300, 200, SaveInfo(XISFCompressionType.NONE, false, XISFChecksumType.SHA256));
        Assert.Equal("XISF0100", Encoding.ASCII.GetString(file, 0, 8));
        uint headerLen = BitConverter.ToUInt32(file, 8);
        Assert.Equal(0u, BitConverter.ToUInt32(file, 12));  // reserved
        string xml = Encoding.UTF8.GetString(file, 16, (int)headerLen);
        Assert.Contains("http://www.pixinsight.com/xisf", xml, StringComparison.Ordinal);
        Assert.Contains("version=\"1.0\"", xml, StringComparison.Ordinal);
        Assert.Contains("sampleFormat=\"UInt16\"", xml, StringComparison.Ordinal);
        Assert.Contains("colorSpace=\"Gray\"", xml, StringComparison.Ordinal);
        Assert.Contains("checksum=\"sha-256:", xml, StringComparison.Ordinal);
        Assert.Contains("name=\"SWCREATE\"", xml, StringComparison.Ordinal);
        Assert.Contains("pixelStorage=\"Planar\"", xml, StringComparison.Ordinal);
        Assert.Contains("id=\"XISF:BlockAlignmentSize\"", xml, StringComparison.Ordinal);
        Assert.Contains($"value=\"{XISF.PaddedBlockSize}\"", xml, StringComparison.Ordinal);
        Assert.Contains("OpenAstro Ara", xml.Substring(xml.IndexOf("XISF:CreatorApplication", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.DoesNotContain("N.I.N.A.", xml.Substring(xml.IndexOf("SWCREATE", StringComparison.Ordinal)), StringComparison.Ordinal);

        var loc = System.Text.RegularExpressions.Regex.Match(xml, "location=\"attachment:(\\d+):(\\d+)\"");
        Assert.True(loc.Success);
        long start = long.Parse(loc.Groups[1].Value, CultureInfo.InvariantCulture);
        long size = long.Parse(loc.Groups[2].Value, CultureInfo.InvariantCulture);
        Assert.True(start >= 16 + headerLen, "data block must start after the header");
        Assert.Equal(0, start % XISF.PaddedBlockSize);
        Assert.Equal(300L * 200 * 2, size);
        Assert.Equal(start + size, file.LongLength);
        // The gap between header and block is null padding, per the spec.
        for (long i = 16 + headerLen; i < start; i++) { Assert.Equal(0, file[i]); }
    }

    [Fact]
    public void BlockOffsetConvergesWhenTheHeaderSitsNearABoundary() {
        // Pad the header with generic keywords until it lands just under and just over a
        // 1024-byte boundary; the location attribute must always point past the header.
        for (int keywords = 0; keywords < 120; keywords += 3) {
            var meta = new ImageMetaData();
            var headers = new List<IGenericMetaDataHeader>();
            for (int i = 0; i < keywords; i++) { headers.Add(new StringMetaDataHeader($"KEY{i:D4}", $"value {i}", "padding")); }
            meta.GenericHeaders = headers;
            var file = Write(Pattern(2, 2), 2, 2, SaveInfo(XISFCompressionType.NONE, false, XISFChecksumType.NONE), meta);
            uint headerLen = BitConverter.ToUInt32(file, 8);
            string xml = Encoding.UTF8.GetString(file, 16, (int)headerLen);
            var loc = System.Text.RegularExpressions.Regex.Match(xml, "location=\"attachment:(\\d+):(\\d+)\"");
            long start = long.Parse(loc.Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.True(start >= 16 + headerLen, $"keywords={keywords}: block at {start} overlaps a {16 + headerLen}-byte header");
            Assert.Equal(0, start % XISF.PaddedBlockSize);
            // The FIRST boundary at or after the header, not a later one: the old fixed 256-byte
            // budget left up to a full extra block of padding, and this is what distinguishes
            // the converging computation from it.
            Assert.True(start - (16 + headerLen) < XISF.PaddedBlockSize, $"keywords={keywords}: {start - (16 + headerLen)} bytes of padding before the block");
            Assert.Equal(start + 8, file.LongLength);
        }
    }

    [Theory]
    [InlineData(XISFChecksumType.SHA1)]
    [InlineData(XISFChecksumType.SHA256)]
    [InlineData(XISFChecksumType.Sha3512)]
    public async Task ACorruptBlockFailsItsChecksumAndIsRejected(XISFChecksumType k) {
        var file = Write(Pattern(16, 16), 16, 16, SaveInfo(XISFCompressionType.NONE, false, k));
        file[^1] ^= 0xFF;
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(file));
    }

    [Fact]
    public async Task ACorruptCompressedBlockIsRejectedBeforeDecompression() {
        var file = Write(Pattern(64, 64), 64, 64, SaveInfo(XISFCompressionType.ZLIB, true, XISFChecksumType.SHA256));
        file[^5] ^= 0x01;
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(file));
    }

    [Fact]
    public async Task NotAnXisfFileIsRejected() {
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(Encoding.ASCII.GetBytes("SIMPLE  = T this is FITS, not XISF")));
    }

    // ---- reader-only paths: hand-built files ------------------------------------------------

    private static byte[] Monolithic(string imageAttributes, string imageContent) {
        string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<xisf version=\"1.0\" xmlns=\"http://www.pixinsight.com/xisf\">" +
            $"<Image {imageAttributes}>{imageContent}</Image></xisf>";
        var header = Encoding.UTF8.GetBytes(xml);
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("XISF0100"));
        ms.Write(BitConverter.GetBytes((uint)header.Length));
        ms.Write(new byte[4]);
        ms.Write(header);
        return ms.ToArray();
    }

    private static byte[] Monolithic(string imageAttributes, string imageContent, byte[] attachment) {
        var head = Monolithic(imageAttributes + " location=\"attachment:__START__:" + attachment.Length + "\"", imageContent);
        // Resolve the placeholder to the first 1024 boundary past the header, as the writer does.
        long start = (head.Length + 1023) / 1024 * 1024;
        string s = Encoding.UTF8.GetString(head, 16, head.Length - 16).Replace("__START__", start.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var header = Encoding.UTF8.GetBytes(s);
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("XISF0100"));
        ms.Write(BitConverter.GetBytes((uint)header.Length));
        ms.Write(new byte[4]);
        ms.Write(header);
        ms.Write(new byte[start - ms.Position]);
        ms.Write(attachment);
        return ms.ToArray();
    }

    [Fact]
    public async Task InlineBase64BlockIsTheElementText() {
        var raw = new byte[] { 0x00, 0x00, 0xFF, 0xFF, 0x34, 0x12, 0x00, 0x80 };
        var file = Monolithic("geometry=\"2:2:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"inline:base64\"", Convert.ToBase64String(raw));
        var img = await Read(file);
        Assert.Equal(new ushort[] { 0, 0xFFFF, 0x1234, 0x8000 }, img.Data.FlatArray);
    }

    [Fact]
    public async Task ImageTypeFallsBackToTheImagetypKeywordWhenTheAttributeIsAbsent() {
        // PixInsight-written files carry IMAGETYP as a FITSKeyword and may omit the attribute.
        var raw = new byte[] { 0x01, 0x00 };
        var file = Monolithic("geometry=\"1:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"inline:base64\"",
            "<FITSKeyword name=\"IMAGETYP\" value=\"'DARK'\" comment=\"\"/>" + Convert.ToBase64String(raw));
        var img = await Read(file);
        Assert.Equal("DARK", img.MetaData.Image.ImageType);
        Assert.Equal(new ushort[] { 1 }, img.Data.FlatArray);
    }

    [Fact]
    public async Task InlineHexBlockIsAccepted() {
        var file = Monolithic("geometry=\"2:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"inline:hex\"", "3412 ffff");
        var img = await Read(file);
        Assert.Equal(new ushort[] { 0x1234, 0xFFFF }, img.Data.FlatArray);
    }

    [Fact]
    public async Task AMalformedInlinePayloadIsAnInvalidDataException() {
        var file = Monolithic("geometry=\"2:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"inline:base64\"", "not*base64!");
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(file));
    }

    [Fact]
    public async Task EmbeddedBlockUsesTheDataChildElement() {
        var raw = new byte[] { 0x01, 0x00, 0x02, 0x00 };
        var file = Monolithic("geometry=\"2:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"embedded\"",
            $"<Data encoding=\"base64\">{Convert.ToBase64String(raw)}</Data>");
        var img = await Read(file);
        Assert.Equal(new ushort[] { 1, 2 }, img.Data.FlatArray);
    }

    [Fact]
    public async Task InlineBlockWithAChecksumIsVerifiedToo() {
        var raw = new byte[] { 0x01, 0x00, 0x02, 0x00 };
        string good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw)).ToUpperInvariant();
        var ok = Monolithic($"geometry=\"2:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"inline:base64\" checksum=\"sha-256:{good}\"", Convert.ToBase64String(raw));
        Assert.Equal(new ushort[] { 1, 2 }, (await Read(ok)).Data.FlatArray);
        var bad = Monolithic("geometry=\"2:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"inline:base64\" checksum=\"sha-256:" + new string('0', 64) + "\"", Convert.ToBase64String(raw));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(bad));
    }

    [Fact]
    public async Task InlineCompressedBlockIsDecompressed() {
        var pixels = new byte[] { 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x00 };
        var shuffled = XISFData.Shuffle(pixels, 2);
        var packed = Ionic.Zlib.ZlibStream.CompressBuffer(shuffled);
        var file = Monolithic($"geometry=\"4:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"inline:base64\" compression=\"zlib+sh:{pixels.Length}:2\"", Convert.ToBase64String(packed));
        Assert.Equal(new ushort[] { 1, 2, 3, 4 }, (await Read(file)).Data.FlatArray);
    }

    public static IEnumerable<object[]> SampleFormats() {
        // (sampleFormat, little-endian bytes for samples [min, max, mid], expected 16-bit)
        yield return new object[] { "UInt8", new byte[] { 0, 255, 128 }, new ushort[] { 0, 65535, 32896 } };
        yield return new object[] { "UInt16", new byte[] { 0, 0, 0xFF, 0xFF, 0x00, 0x80 }, new ushort[] { 0, 65535, 32768 } };
        yield return new object[] { "UInt32", Bytes(0u, uint.MaxValue, 0x80000000u), new ushort[] { 0, 65535, 32768 } };
        yield return new object[] { "UInt64", Bytes(0ul, ulong.MaxValue, 0x8000000000000000ul), new ushort[] { 0, 65535, 32768 } };
        yield return new object[] { "Float32", Bytes(0f, 1f, 0.5f), new ushort[] { 0, 65535, 32768 } };
        yield return new object[] { "Float64", Bytes(0d, 1d, 0.5d), new ushort[] { 0, 65535, 32768 } };
    }

    private static byte[] Bytes<T>(params T[] values) where T : struct {
        var ms = new MemoryStream();
        foreach (var v in values) {
            byte[] b = v switch {
                uint u => BitConverter.GetBytes(u),
                ulong u => BitConverter.GetBytes(u),
                float f => BitConverter.GetBytes(f),
                double d => BitConverter.GetBytes(d),
                _ => throw new NotSupportedException(),
            };
            if (!BitConverter.IsLittleEndian) { Array.Reverse(b); }
            ms.Write(b);
        }
        return ms.ToArray();
    }

    [Theory]
    [MemberData(nameof(SampleFormats))]
    public async Task EverySampleFormatMapsItsFullRangeOnto16Bits(string format, byte[] raw, ushort[] expected) {
        var file = Monolithic($"geometry=\"3:1:1\" sampleFormat=\"{format}\" colorSpace=\"Gray\"", string.Empty, raw);
        var img = await Read(file, format + ".xisf");
        Assert.Equal(expected, img.Data.FlatArray);
    }

    [Fact]
    public async Task FloatSamplesOutOfRangeAreClampedAndNaNReadsAsZero() {
        var raw = Bytes(-0.5f, 2f, float.NaN, 0.25f);
        var file = Monolithic("geometry=\"4:1:1\" sampleFormat=\"Float32\" colorSpace=\"Gray\"", string.Empty, raw);
        Assert.Equal(new ushort[] { 0, 65535, 0, 16384 }, (await Read(file)).Data.FlatArray);
    }

    [Theory]
    [InlineData("Complex32")]
    [InlineData("Complex64")]
    [InlineData("Int16")]
    public async Task AnUnsupportedSampleFormatIsAClearErrorNotGarbage(string format) {
        var file = Monolithic($"geometry=\"1:1:1\" sampleFormat=\"{format}\" colorSpace=\"Gray\"", string.Empty, new byte[8]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(file));
    }

    [Fact]
    public async Task AnUnsupportedCompressionCodecIsAClearError() {
        var file = Monolithic("geometry=\"1:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" compression=\"bzip2:2\"", string.Empty, new byte[2]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(file));
    }

    [Fact]
    public async Task ABlockShorterThanTheGeometryIsRejected() {
        var file = Monolithic("geometry=\"4:4:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\"", string.Empty, new byte[6]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(file));
    }

    [Fact]
    public async Task FloatBoundsRescaleTheDeclaredRange() {
        // bounds="0:100": 50 is mid-scale, 100 is full, values outside clamp.
        var raw = Bytes(0f, 100f, 50f, 150f, -5f);
        var file = Monolithic("geometry=\"5:1:1\" sampleFormat=\"Float32\" colorSpace=\"Gray\" bounds=\"0:100\"", string.Empty, raw);
        Assert.Equal(new ushort[] { 0, 65535, 32768, 65535, 0 }, (await Read(file)).Data.FlatArray);
        var shifted = Monolithic("geometry=\"2:1:1\" sampleFormat=\"Float64\" colorSpace=\"Gray\" bounds=\"-1:1\"", string.Empty, Bytes(-1d, 0d));
        Assert.Equal(new ushort[] { 0, 32768 }, (await Read(shifted)).Data.FlatArray);
    }

    [Theory]
    [InlineData("bounds=\"1:0\"", "Float32", 4)]
    [InlineData("bounds=\"0\"", "Float32", 4)]
    [InlineData("bounds=\"a:b\"", "Float64", 8)]
    [InlineData("pixelStorage=\"Diagonal\"", "UInt16", 2)]
    [InlineData("colorSpace=\"CMYK\"", "UInt16", 2)]
    public async Task AMalformedShapeAttributeIsRejected(string attribute, string sampleFormat, int sampleBytes) {
        var file = Monolithic($"geometry=\"1:1:1\" sampleFormat=\"{sampleFormat}\" {attribute}", string.Empty, new byte[sampleBytes]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(file));
    }

    [Fact]
    public async Task PixelStorageAndColorSpaceAreAcceptedInTheSpecVocabulary() {
        var file = Monolithic("geometry=\"1:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" pixelStorage=\"Normal\"", string.Empty, new byte[] { 0x34, 0x12 });
        Assert.Equal(new ushort[] { 0x1234 }, (await Read(file)).Data.FlatArray);
    }

    [Theory]
    [InlineData("RGB", 1)]
    [InlineData("CIELab", 1)]
    [InlineData("Gray", 3)]
    public async Task AColorSpaceThatDisagreesWithTheChannelCountIsRejected(string colorSpace, int channels) {
        var file = Monolithic($"geometry=\"2:1:{channels}\" sampleFormat=\"UInt16\" colorSpace=\"{colorSpace}\"", string.Empty, new byte[4 * channels]);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Read(file));
        Assert.Contains("implies", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMultiChannelImageIsAClearErrorNotAScrambledPicture() {
        var file = Monolithic("geometry=\"2:1:3\" sampleFormat=\"UInt16\" colorSpace=\"RGB\"", string.Empty, new byte[12]);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Read(file));
        Assert.Contains("3-channel", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("compression=\"lz4+sh:2\"")]
    [InlineData("compression=\"zlib\"")]
    [InlineData("compression=\"lz4:abc\"")]
    [InlineData("checksum=\"sha-256\"")]
    [InlineData("checksum=\"sha-256:\"")]
    public async Task ATruncatedCompressionOrChecksumAttributeIsInvalidData(string attribute) {
        var file = Monolithic($"geometry=\"1:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" {attribute}", string.Empty, new byte[2]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(file));
    }

    [Fact]
    public async Task ZstdInlineBlockFromAnotherWriterIsDecompressed() {
        // A zstd frame we did not write ourselves (level 19, shuffled), as PixInsight would emit.
        var pixels = new byte[] { 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x00, 0x05, 0x00, 0x06, 0x00 };
        var shuffled = XISFData.Shuffle(pixels, 2);
        using var compressor = new ZstdSharp.Compressor(19);
        var packed = compressor.Wrap(shuffled).ToArray();
        var file = Monolithic($"geometry=\"6:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"inline:base64\" compression=\"zstd+sh:{pixels.Length}:2\"", Convert.ToBase64String(packed));
        Assert.Equal(new ushort[] { 1, 2, 3, 4, 5, 6 }, (await Read(file)).Data.FlatArray);
    }

    [Fact]
    public async Task AZstdFrameLargerThanItsDeclaredSizeIsRejected() {
        // Declares 4 uncompressed bytes but the frame holds 12: the bounded Unwrap must refuse.
        var pixels = new byte[12];
        using var compressor = new ZstdSharp.Compressor(3);
        var packed = compressor.Wrap(pixels).ToArray();
        var file = Monolithic($"geometry=\"2:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"inline:base64\" compression=\"zstd:4\"", Convert.ToBase64String(packed));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(file));
    }

    [Fact]
    public async Task AJunkBoundsAttributeOnAnIntegerFormatIsIgnoredPerSpec() {
        // XISF 1.0: bounds applies to floating-point and complex formats and is ignored otherwise.
        var file = Monolithic("geometry=\"1:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" bounds=\"junk\"", string.Empty, new byte[] { 0x34, 0x12 });
        Assert.Equal(new ushort[] { 0x1234 }, (await Read(file)).Data.FlatArray);
    }

    [Fact]
    public async Task AduDeeperThanTheReportedBitDepthAreNotClipped() {
        // A driver reporting 12 bits while delivering 16-bit ADU: the writer widens the scale
        // instead of clipping every highlight to 4095.
        var px = new[] { 0, 4095, 65535 };
        var header = new XISFHeader();
        header.AddImageMetaData(new ImageProperties(3, 1, 12, false, 0, 0), "LIGHT", XISFSampleFormat.UInt32);
        header.Populate(new ImageMetaData());
        var xisf = new XISF(header);
        xisf.AddAttachedImageInt(px, 12, SaveInfo(XISFCompressionType.NONE, false, XISFChecksumType.NONE));
        using var ms = new MemoryStream();
        xisf.Save(ms);
        var read = (await Read(ms.ToArray())).Data.FlatArray;
        Assert.Equal(0, read[0]);
        Assert.Equal(4095, read[1]);
        Assert.Equal(ushort.MaxValue, read[2]);
    }

    [Fact]
    public void SaveWritesNothingWhenTheDeclaredOffsetIsInsideTheHeader() {
        var header = new XISFHeader();
        header.AddImageMetaData(new ImageProperties(2, 2, 16, false, 0, 0), "LIGHT");
        header.Populate(new ImageMetaData());
        var xisf = new XISF(header);
        xisf.AddAttachedImage(Pattern(2, 2), SaveInfo(XISFCompressionType.NONE, false, XISFChecksumType.NONE));
        // Corrupt the converged offset the way a future AttachData bug would.
        header.Image!.SetAttributeValue("location", "attachment:16:8");
        using var ms = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => xisf.Save(ms));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public async Task FromFileDispatchesXisfByExtension() {
        var path = TempPath("dispatch.xisf");
        await File.WriteAllBytesAsync(path, Write(Pattern(3, 3), 3, 3, SaveInfo(XISFCompressionType.LZ4HC, false, XISFChecksumType.SHA512)));
        var img = await new FakeImageDataFactory().CreateFromFile(path, 16, false, RawConverter.DCRAW);
        Assert.Equal(Pattern(3, 3), img.Data.FlatArray);
    }
}
