#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;
using OpenAstroAra.Fits;
using OpenAstroAra.Image.FileFormat;
using OpenAstroAra.Image.FileFormat.XISF;
using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;

namespace OpenAstroAra.Server.Services;

/// <summary>#1148 — the container a library frame is written in.</summary>
internal enum FrameFileFormat {
    Fits,
    Xisf,
}

/// <summary>
/// #1148 — how the capture path writes one frame: the container and, for XISF only, whether the
/// block is lossless Zstandard (level 1, byte-shuffled). FITS is always written uncompressed:
/// PixInsight cannot open tile-compressed FITS.
/// </summary>
internal sealed record FrameWriteOptions(FrameFileFormat Format, bool Compress) {

    /// <summary>Plain, uncompressed FITS — the default and every internal temp frame.</summary>
    public static FrameWriteOptions PlainFits { get; } = new(FrameFileFormat.Fits, false);

    /// <summary>The file extension the format is written with.</summary>
    public string Extension => Format == FrameFileFormat.Xisf ? FrameFileReader.XisfExtension : ".fits";

    /// <summary>
    /// The profile's choice for a frame of <paramref name="imageType"/>: XISF compresses when the
    /// compression setting is on, and darks and biases (dark flats included — the catalog files them
    /// as darks) also compress when "Compress darks and bias" is on. Neither setting touches FITS.
    /// </summary>
    public static FrameWriteOptions For(StorageSettingsDto storage, bool compressDarksAndBias, string imageType) {
        ArgumentNullException.ThrowIfNull(storage);
        if (StorageFormatTokens.NormalizeFileFormat(storage.FileFormat) != StorageFormatTokens.Xisf) {
            return PlainFits;
        }
        var compress = StorageFormatTokens.NormalizeCompression(storage.Compression) == StorageFormatTokens.CompressionZstd
            || (compressDarksAndBias && CameraService.MapFrameType(imageType) is FrameType.Dark or FrameType.Bias);
        return new FrameWriteOptions(FrameFileFormat.Xisf, compress);
    }
}

/// <summary>
/// #1148 — one frame being written: pixels, header keywords, then <see cref="Complete"/>. Both
/// containers keep FitsImage's §28.7 atomic semantics: the bytes go to <c>&lt;path&gt;.tmp</c>,
/// Complete renames onto the real name and fsyncs the directory, and Dispose without Complete
/// removes the temp (the §28.8 startup scan reaps any a crash leaves).
/// </summary>
internal interface IFrameFileWriter : IDisposable {
    void WriteImageData(ushort[] pixels);
    void SetHeader(string keyname, string value, string comment);
    void SetHeader(string keyname, double value, string comment);
    void SetHeader(string keyname, int value, string comment);
    void Complete();
}

internal static class FrameFileWriter {
    public static IFrameFileWriter Create(string path, int width, int height, FrameWriteOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        return options.Format == FrameFileFormat.Xisf
            ? new XisfFrameWriter(path, width, height, options.Compress)
            : new FitsFrameWriter(FitsImage.Create(path, width, height, FitsBitDepth.UnsignedShort));
    }
}

/// <summary>
/// The optional-header pass's view of a frame writer: a non-finite reading (a weather station's NaN
/// humidity) is left out the way a missing reading is. Both writers refuse NaN/±Infinity as CFITSIO
/// does, and inside the one try block of the header pass that refusal used to cost every header
/// after it, the sun and moon cards included. It does not own the inner writer.
/// </summary>
internal sealed class FiniteHeaderWriter : IFrameFileWriter {
    private readonly IFrameFileWriter _inner;

    public FiniteHeaderWriter(IFrameFileWriter inner) {
        _inner = inner;
    }

    public void WriteImageData(ushort[] pixels) => _inner.WriteImageData(pixels);
    public void SetHeader(string keyname, string value, string comment) => _inner.SetHeader(keyname, value, comment);
    public void SetHeader(string keyname, double value, string comment) {
        if (double.IsFinite(value)) {
            _inner.SetHeader(keyname, value, comment);
        }
    }
    public void SetHeader(string keyname, int value, string comment) => _inner.SetHeader(keyname, value, comment);
    public void Complete() => _inner.Complete();
    public void Dispose() {
    }
}

/// <summary>The FITS container: CFITSIO through <see cref="FitsImage"/>, unchanged on disk.</summary>
internal sealed class FitsFrameWriter : IFrameFileWriter {
    private readonly FitsImage _fits;

    public FitsFrameWriter(FitsImage fits) {
        _fits = fits;
    }

    public void WriteImageData(ushort[] pixels) => _fits.WriteImageData(pixels);
    public void SetHeader(string keyname, string value, string comment) => _fits.SetHeader(keyname, value, comment);
    public void SetHeader(string keyname, double value, string comment) => _fits.SetHeader(keyname, value, comment);
    public void SetHeader(string keyname, int value, string comment) => _fits.SetHeader(keyname, value, comment);
    public void Complete() => _fits.Complete();
    public void Dispose() => _fits.Dispose();
}

/// <summary>
/// The XISF container through OpenAstroAra.Image's writer. Every keyword the FITS path writes lands
/// as a FITSKeyword element with the value CFITSIO would have put in the card, so a reader sees the
/// same set from either container; the XISF properties PixInsight reads natively (exposure, binning,
/// filter, temperatures, object, site, optics, pointing) and the ColorFilterArray element of an OSC
/// frame are added alongside. The image element's imageType uses the spec's names (Light, Dark,
/// Flat, Bias); the IMAGETYP keyword keeps Ara's own uppercase value.
/// </summary>
internal sealed class XisfFrameWriter : IFrameFileWriter {
    private sealed record Card(string Name, string Literal, string Comment, object Value);

    private readonly string _path;
    private readonly string _tempPath;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _compress;
    private readonly List<Card> _cards = new();
    private ushort[]? _pixels;
    private bool _completed;

    public XisfFrameWriter(string path, int width, int height, bool compress) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _path = path;
        _tempPath = path + ".tmp";
        _width = width;
        _height = height;
        _compress = compress;
        // Same defence as FitsImage.Create: a crash mid-write may have left the temp behind.
        if (File.Exists(_tempPath)) {
            File.Delete(_tempPath);
        }
    }

    public void WriteImageData(ushort[] pixels) {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != _width * _height) {
            throw new ArgumentException(
                $"Pixel buffer length ({pixels.Length}) doesn't match image dimensions ({_width}×{_height} = {_width * _height})",
                nameof(pixels));
        }
        _pixels = pixels;
    }

    public void SetHeader(string keyname, string value, string comment) =>
        Set(new Card(keyname, "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'", comment, value));

    // CFITSIO refuses a non-finite double card (ffuky status 402, BAD_F2C: "bad float to string
    // conversion") and FitsImage surfaces that as a FitsException, writing nothing. The XISF writer
    // refuses it the same way, so a NaN reading costs the same headers in either container.
    private const int CfitsioBadFloatToString = 402;

    public void SetHeader(string keyname, double value, string comment) {
        if (!double.IsFinite(value)) {
            throw new FitsException($"ffuky ({keyname})", CfitsioBadFloatToString);
        }
        Set(new Card(keyname, FitsCardFormat.Double(value), comment, value));
    }

    public void SetHeader(string keyname, int value, string comment) =>
        Set(new Card(keyname, value.ToString(CultureInfo.InvariantCulture), comment, value));

    // CFITSIO's update-key semantics: a keyword set twice keeps its first position, last value.
    private void Set(Card card) {
        var at = _cards.FindIndex(c => string.Equals(c.Name, card.Name, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) {
            _cards[at] = card;
        } else {
            _cards.Add(card);
        }
    }

    public void Complete() {
        if (_completed) {
            return;
        }
        var pixels = _pixels ?? throw new InvalidOperationException("XISF frame has no image data");
        var xisf = new XISF(BuildHeader());
        xisf.AddAttachedImage(pixels, new FileSaveInfo {
            FileType = FileType.XISF,
            XISFCompressionType = _compress ? XISFCompressionType.ZSTD : XISFCompressionType.NONE,
            XISFByteShuffling = _compress,
            XISFChecksumType = XISFChecksumType.NONE,
        });
        try {
            using (var fs = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                // No data fsync, as FitsImage: the rename plus the parent-directory fsync below is the
                // commit point for both containers (an fsync of 22 MB costs ~0.4 s on the CM4's USB drive).
                xisf.Save(fs);
            }
            File.Move(_tempPath, _path, overwrite: true);
        } catch {
            TryDeleteTemp();
            throw;
        }
        SyncParentDirectory(_path);
        _completed = true;
    }

    public void Dispose() {
        if (!_completed) {
            TryDeleteTemp();
        }
    }

    private XISFHeader BuildHeader() {
        var header = new XISFHeader();
        var imageType = StringValue("IMAGETYP");
        var bayer = StringValue("BAYERPAT");
        header.AddImageMetaData(new ImageProperties(_width, _height, 16, isBayered: bayer is not null, gain: 0, offset: 0), SpecImageType(imageType));
        var image = header.Image!;
        var ns = image.Name.Namespace;
        // AddImageMetaData stamps an IMAGETYP keyword of its own; the frame's cards replace it so both
        // containers carry the same keywords in the same order.
        image.Elements(ns + "FITSKeyword").Remove();
        foreach (var card in _cards) {
            image.Add(new XElement(ns + "FITSKeyword",
                new XAttribute("name", card.Name),
                new XAttribute("value", XISFHeader.RemoveInvalidXMLChars(card.Literal)),
                new XAttribute("comment", XISFHeader.RemoveInvalidXMLChars(card.Comment))));
        }
        foreach (var card in _cards) {
            if (Properties.TryGetValue(card.Name, out var property)) {
                AddProperty(header, property, card);
            }
        }
        if (bayer is not null) {
            header.AddCfaAttribute(bayer.Trim().ToUpperInvariant(), 2, 2);
        }
        return header;
    }

    private string? StringValue(string name) =>
        _cards.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Value as string;

    /// <summary>XISF 1.0 names the frame types Light/Dark/Flat/Bias; Ara's DARKFLAT is a dark and an
    /// absent type a light.</summary>
    internal static string SpecImageType(string? imageType) => imageType?.Trim().ToUpperInvariant() switch {
        "DARK" or "DARKFLAT" => "Dark",
        "FLAT" => "Flat",
        "BIAS" => "Bias",
        _ => "Light",
    };

    private sealed record XisfProperty(string Id, string Type, double Scale = 1);

    // FITS keyword → the XISF property PixInsight reads natively, in the spec's units (the optics
    // are metres in XISF, millimetres in the FITS cards). Wind is left out: XISF wants km/h.
    private static readonly Dictionary<string, XisfProperty> Properties = new(StringComparer.OrdinalIgnoreCase) {
        ["DATE-OBS"] = new("Observation:Time:Start", "TimePoint"),
        ["EXPTIME"] = new("Instrument:ExposureTime", "Float32"),
        ["XBINNING"] = new("Instrument:Camera:XBinning", "Int32"),
        ["YBINNING"] = new("Instrument:Camera:YBinning", "Int32"),
        ["INSTRUME"] = new("Instrument:Camera:Name", "String"),
        ["FILTER"] = new("Instrument:Filter:Name", "String"),
        ["FOCUSPOS"] = new("Instrument:Focuser:Position", "Float32"),
        ["CCD-TEMP"] = new("Instrument:Sensor:Temperature", "Float32"),
        ["SET-TEMP"] = new("Instrument:Sensor:TargetTemperature", "Float32"),
        ["XPIXSZ"] = new("Instrument:Sensor:XPixelSize", "Float32"),
        ["YPIXSZ"] = new("Instrument:Sensor:YPixelSize", "Float32"),
        ["TELESCOP"] = new("Instrument:Telescope:Name", "String"),
        ["FOCALLEN"] = new("Instrument:Telescope:FocalLength", "Float32", 1e-3),
        ["APTDIA"] = new("Instrument:Telescope:Aperture", "Float32", 1e-3),
        ["OBSERVER"] = new("Observer:Name", "String"),
        ["OBJECT"] = new("Observation:Object:Name", "String"),
        ["RA"] = new("Observation:Center:RA", "Float64"),
        ["DEC"] = new("Observation:Center:Dec", "Float64"),
        ["EQUINOX"] = new("Observation:Equinox", "Float64"),
        ["SITELAT"] = new("Observation:Location:Latitude", "Float64"),
        ["SITELONG"] = new("Observation:Location:Longitude", "Float64"),
        ["SITEELEV"] = new("Observation:Location:Elevation", "Float64"),
        ["AMBTEMP"] = new("Observation:Meteorology:AmbientTemperature", "Float32"),
        ["PRESSURE"] = new("Observation:Meteorology:AtmosphericPressure", "Float32"),
        ["HUMIDITY"] = new("Observation:Meteorology:RelativeHumidity", "Float32"),
    };

    private static void AddProperty(XISFHeader header, XisfProperty property, Card card) {
        string[] id = [property.Id, property.Type];
        switch (card.Value) {
            case string text when property.Type == "TimePoint":
                // DATE-OBS is UTC without a zone designator; an XISF time point says so explicitly.
                header.AddImageProperty(id, text.EndsWith('Z') ? text : text + "Z", card.Comment, autoaddfits: false);
                break;
            case string text:
                header.AddImageProperty(id, text, card.Comment, autoaddfits: false);
                break;
            case int whole when property.Type == "Int32":
                header.AddImageProperty(id, whole, card.Comment, autoaddfits: false);
                break;
            case int whole:
                header.AddImageProperty(id, whole * property.Scale, card.Comment, autoaddfits: false);
                break;
            case double number:
                header.AddImageProperty(id, number * property.Scale, card.Comment, autoaddfits: false);
                break;
        }
    }

    private void TryDeleteTemp() {
        try {
            File.Delete(_tempPath);
        } catch (IOException) {
            // best effort — the §28.8 startup scan reaps stragglers
        } catch (UnauthorizedAccessException) {
            // best effort — the §28.8 startup scan reaps stragglers
        }
    }

    // FitsImage's §28.7 step 4: fsync the parent so the rename itself survives a power cut.
    private static void SyncParentDirectory(string filePath) {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            return;
        }
        var parent = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(parent)) {
            return;
        }
        try {
            using var dir = new FileStream(parent, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            dir.Flush(true);
        } catch (IOException) {
            // The file's own bytes were flushed; only the rename's commit timing weakens.
        } catch (UnauthorizedAccessException) {
            // Same fallback as above.
        }
    }
}

/// <summary>How CFITSIO renders a double card value (ffuky TDOUBLE: <c>%.15G</c>, a decimal point
/// added when none shows), so an XISF FITSKeyword reads back exactly as the FITS card does.</summary>
internal static class FitsCardFormat {
    public static string Double(double value) {
        var text = value.ToString("G15", CultureInfo.InvariantCulture);
        var e = text.IndexOf('E', StringComparison.Ordinal);
        if (text.Contains('.', StringComparison.Ordinal)) {
            return text;
        }
        // "120" → "120."; "1E-05" → "1.E-05": the point is what marks the card as a real number.
        return e < 0 ? text + "." : text[..e] + "." + text[e..];
    }
}

/// <summary>A library frame read back: pixels, geometry and the header keywords.</summary>
internal sealed record FrameFileContents(ushort[] Pixels, int Width, int Height, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// #1148 — the one place the daemon opens a library frame, dispatching on the extension: FITS
/// (<c>.fits</c>/<c>.fit</c>/<c>.fts</c>) through <see cref="FitsImage"/>, XISF (<c>.xisf</c>) through
/// OpenAstroAra.Image. Keywords come back in <see cref="FitsImage.ReadHeaders"/>'s shape from both:
/// case-insensitive keys, string values with the quotes and trailing padding stripped, numbers as
/// their card text.
/// </summary>
internal static class FrameFileReader {
    public const string XisfExtension = ".xisf";

    private static readonly string[] FitsExtensions = [".fits", ".fit", ".fts"];

    public static bool IsXisf(string path) =>
        path.EndsWith(XisfExtension, StringComparison.OrdinalIgnoreCase);

    public static bool IsFits(string path) =>
        FitsExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>The faults a corrupt, truncated or vanished frame file reads as, from either container.</summary>
    public static bool IsReadFault(Exception ex) =>
        ex is FitsException or IOException or InvalidDataException or XmlException or UnauthorizedAccessException;

    /// <summary>Pixels, geometry and keywords.</summary>
    public static FrameFileContents Read(string path) {
        if (IsXisf(path)) {
            var (pixels, width, height, header) = XISF.ReadImage(path);
            return new FrameFileContents(pixels, width, height, Keywords(header));
        }
        using var fits = FitsImage.Open(path);
        var (w, h) = fits.GetDimensions();
        return new FrameFileContents(fits.ReadImageData16(), w, h, fits.ReadHeaders());
    }

    /// <summary>Keywords only — no pixel decode.</summary>
    public static IReadOnlyDictionary<string, string> ReadHeaders(string path) => ReadHeaderInfo(path).Headers;

    /// <summary>Geometry and keywords without decoding the pixels (the catalog scan's read).
    /// An XISF file that isn't a single-channel UInt16 frame — a PixInsight colour master, a 32-bit
    /// float integration copied onto the drive — throws <see cref="NotSupportedException"/>: the
    /// library's previews and solver work on one 16-bit plane, so cataloguing it would only give a
    /// row whose preview fails.</summary>
    public static (int Width, int Height, IReadOnlyDictionary<string, string> Headers) ReadHeaderInfo(string path) {
        if (IsXisf(path)) {
            var header = XISF.ReadHeader(path);
            var geometry = header.Image?.Attribute("geometry")?.Value.Split(':');
            if (geometry is not { Length: >= 2 }
                    || !int.TryParse(geometry[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                    || !int.TryParse(geometry[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)) {
                throw new InvalidDataException($"XISF: no image geometry in {path}");
            }
            // XISF geometry is width:height[:...]:channels; two tokens means one channel.
            var channels = 1;
            if (geometry.Length > 2 && !int.TryParse(geometry[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out channels)) {
                throw new InvalidDataException($"XISF: malformed image geometry in {path}");
            }
            var sampleFormat = header.Image?.Attribute("sampleFormat")?.Value;
            if (channels != 1 || !string.Equals(sampleFormat, "UInt16", StringComparison.Ordinal)) {
                throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                    $"{channels}-channel {sampleFormat ?? "unknown"} image; the library shows single-channel UInt16 frames"));
            }
            return (width, height, Keywords(header));
        }
        using var fits = FitsImage.Open(path);
        var (w, h) = fits.GetDimensions();
        return (w, h, fits.ReadHeaders());
    }

    /// <summary>The header's FITSKeyword elements as FitsImage.ReadHeaders gives a FITS file's cards.
    /// A quoted value loses its quotes and trailing blanks (and the doubled quote FITS uses for an
    /// apostrophe); a later duplicate wins, as it does there.</summary>
    internal static IReadOnlyDictionary<string, string> Keywords(XISFHeader header) {
        var keywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var image = header.Image;
        if (image is null) {
            return keywords;
        }
        foreach (var element in image.Elements(image.Name.Namespace + "FITSKeyword")) {
            var name = element.Attribute("name")?.Value.Trim();
            if (string.IsNullOrEmpty(name)) {
                continue;
            }
            var value = (element.Attribute("value")?.Value ?? string.Empty).Trim();
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') {
                value = value[1..^1].TrimEnd().Replace("''", "'", StringComparison.Ordinal);
            }
            keywords[name] = value;
        }
        return keywords;
    }
}
