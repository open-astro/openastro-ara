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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Everything known about a frame at the moment it's named. Nullable fields
/// expand to nothing — their token vanishes and the surrounding separators
/// collapse, so a rig without a filter wheel doesn't get "__" scars in every
/// filename.
/// </summary>
public sealed record FrameNamingContext(
    string ImageType,
    DateTimeOffset CapturedLocal,
    double ExposureSec,
    string? Filter = null,
    int? Gain = null,
    int? Offset = null,
    int BinX = 1,
    int BinY = 1,
    double? SensorTemp = null,
    string? CameraName = null,
    string? TargetName = null,
    int FrameNumber = 1);

/// <summary>
/// §29.2 — expands the profile's filename template into the relative path a
/// frame is written to. The template language is inherited (<c>$$TOKEN$$</c>,
/// <c>\\</c> or <c>/</c> as folder separators) so existing NINA profiles keep
/// meaning what they meant, but the daemon — not a Windows client — is now the
/// thing that speaks it.
///
/// Until this existed the template was stored, shown, imported, exported…
/// and consulted by nothing: every capture landed as <c>{guid}.fits</c>. The
/// scanner and catalog didn't care, but a human opening their disk saw a
/// directory of UUIDs where their night should be.
/// </summary>
public static class FrameNaming {

    /// <summary>
    /// Expand [template] against [ctx] into a relative path (no extension,
    /// no leading separator). Returns null when the template produces
    /// nothing usable — the caller falls back to its id-based name; naming
    /// must never cost a frame.
    /// </summary>
    public static string? ExpandRelativePath(string? template, FrameNamingContext ctx) {
        if (string.IsNullOrWhiteSpace(template)) return null;

        // Both separators mean "folder": the inherited default uses '\\',
        // hand-written ones tend to use '/'.
        var segments = template.Replace('\\', '/').Split('/');
        var expanded = new List<string>();
        foreach (var segment in segments) {
            var value = SanitizeSegment(ExpandTokens(segment, ctx));
            if (value.Length > 0) expanded.Add(value);
        }
        return expanded.Count == 0 ? null : string.Join(Path.DirectorySeparatorChar, expanded);
    }

    private static string ExpandTokens(string segment, FrameNamingContext ctx) {
        var sb = new StringBuilder(segment.Length + 16);
        var i = 0;
        while (i < segment.Length) {
            var start = segment.IndexOf("$$", i, StringComparison.Ordinal);
            if (start < 0) { sb.Append(segment, i, segment.Length - i); break; }
            var end = segment.IndexOf("$$", start + 2, StringComparison.Ordinal);
            if (end < 0) { sb.Append(segment, i, segment.Length - i); break; }
            sb.Append(segment, i, start - i);
            sb.Append(TokenValue(segment.Substring(start + 2, end - start - 2), ctx));
            i = end + 2;
        }
        return sb.ToString();
    }

    private static string TokenValue(string token, FrameNamingContext ctx) {
        var t = ctx.CapturedLocal;
        return token.ToUpperInvariant() switch {
            "DATE" => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "TIME" => t.ToString("HH-mm-ss", CultureInfo.InvariantCulture),
            "DATETIME" => t.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture),
            // The astronomer's night: everything before local noon belongs to
            // the evening it started.
            "DATEMINUS12" => t.AddHours(-12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "DATEUTC" => t.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "TIMEUTC" => t.ToUniversalTime().ToString("HH-mm-ss", CultureInfo.InvariantCulture),
            "IMAGETYPE" => Capitalize(ctx.ImageType),
            "FILTER" => ctx.Filter ?? string.Empty,
            "EXPOSURETIME" => FormatExposure(ctx.ExposureSec),
            "GAIN" => ctx.Gain?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "OFFSET" => ctx.Offset?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "BINNING" => string.Create(CultureInfo.InvariantCulture, $"{ctx.BinX}x{ctx.BinY}"),
            "SENSORTEMP" => ctx.SensorTemp is double temp
                ? Math.Round(temp).ToString("0", CultureInfo.InvariantCulture) + "C"
                : string.Empty,
            "CAMERA" => ctx.CameraName ?? string.Empty,
            "TARGETNAME" => ctx.TargetName ?? string.Empty,
            "FRAMENR" => ctx.FrameNumber.ToString("0000", CultureInfo.InvariantCulture),
            // Unknown token: vanish rather than leak $$X$$ into a filename.
            _ => string.Empty,
        };
    }

    /// <summary>"Light", not "LIGHT" — folder names are for people.</summary>
    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    /// <summary>180 → "180", 12.50 → "12.5". No unit suffix — the inherited
    /// default template writes the literal "s" itself ($$EXPOSURETIME$$s),
    /// and doubling it made "180ss".</summary>
    private static string FormatExposure(double sec) =>
        sec.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// One path segment, safe on ext4/APFS/NTFS alike: invalid characters
    /// become '-', separator scars from vanished tokens collapse, and edges
    /// are trimmed so "_L_" never starts a name just because DATE vanished.
    /// </summary>
    private static string SanitizeSegment(string raw) {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw) {
            sb.Append(ch switch {
                '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\0' => '-',
                _ => ch,
            });
        }
        var s = sb.ToString();
        while (s.Contains("__", StringComparison.Ordinal)) s = s.Replace("__", "_");
        while (s.Contains("-_", StringComparison.Ordinal)) s = s.Replace("-_", "_");
        // "_-" is a scar only when the '-' is NOT a minus sign: "_-10C" is a
        // cold sensor mid-name and must keep its sign ("180s_-10C", not
        // "180s_10C" — a silently wrong temperature is worse than an ugly one).
        var scar = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++) {
            var isScarDash = s[i] == '-' && i > 0 && (s[i - 1] == '_' || s[i - 1] == '-')
                && (i + 1 >= s.Length || !char.IsAsciiDigit(s[i + 1]));
            if (!isScarDash) scar.Append(s[i]);
        }
        s = scar.ToString();
        // Trim separator scars off the edges — but a leading '-' that starts
        // a number is a minus sign, not a scar: "-10C" is a cold sensor, the
        // normal case, and must survive. (The temperature test caught the
        // plain Trim eating it.)
        s = s.TrimEnd(' ', '_', '-', '.');
        while (s.Length > 0 && (s[0] is ' ' or '_' or '.' ||
               (s[0] == '-' && (s.Length == 1 || !char.IsAsciiDigit(s[1]))))) {
            s = s[1..];
        }
        return s;
    }
}
