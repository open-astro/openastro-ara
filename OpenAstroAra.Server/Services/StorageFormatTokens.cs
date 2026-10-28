#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using System;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1148 — the §29 storage tokens the daemon acts on. <c>file_format</c> is <c>fits</c> or
/// <c>xisf</c>; <c>compression</c> is <c>off</c> or <c>zstd</c> and applies only to XISF (FITS is
/// always written uncompressed). Older profiles carry tokens that no longer exist: the FITS + RICE
/// and FITS + gzip formats (PixInsight cannot open tile-compressed FITS) read as plain <c>fits</c>,
/// and the <c>rice</c>/<c>gzip</c> compression values meant "compress", so they read as
/// <c>zstd</c>. Applied wherever a stored or incoming value enters the profile, so an old profile
/// keeps working and the wire only ever carries the current tokens.
/// </summary>
public static class StorageFormatTokens {
    public const string Fits = "fits";
    public const string Xisf = "xisf";
    public const string CompressionOff = "off";
    public const string CompressionZstd = "zstd";

    /// <summary><c>xisf</c> stays XISF; everything else — <c>fits</c>, the retired
    /// <c>fits_rice</c>/<c>fits_gzip</c>, a missing or unknown value — is plain FITS, the default.</summary>
    public static string NormalizeFileFormat(string? fileFormat) =>
        string.Equals(fileFormat?.Trim(), Xisf, StringComparison.OrdinalIgnoreCase) ? Xisf : Fits;

    /// <summary><c>off</c> stays off; everything else — <c>zstd</c>, the legacy <c>rice</c>/<c>gzip</c>,
    /// a missing value — is <c>zstd</c>: compression on is the default.</summary>
    public static string NormalizeCompression(string? compression) =>
        string.Equals(compression?.Trim(), CompressionOff, StringComparison.OrdinalIgnoreCase) ? CompressionOff : CompressionZstd;

    /// <summary>The section with both tokens normalized; the same instance when nothing changes.</summary>
    public static StorageSettingsDto Normalize(StorageSettingsDto storage) {
        ArgumentNullException.ThrowIfNull(storage);
        var format = NormalizeFileFormat(storage.FileFormat);
        var compression = NormalizeCompression(storage.Compression);
        return string.Equals(format, storage.FileFormat, StringComparison.Ordinal)
            && string.Equals(compression, storage.Compression, StringComparison.Ordinal)
            ? storage
            : storage with { FileFormat = format, Compression = compression };
    }
}
