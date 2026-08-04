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
/// frame is written to.
///
/// Ara's template language is <c>{night}/{type}/{datetime}_{filter}</c> —
/// lowercase words in braces, slash for folders. The inherited NINA language
/// (<c>$$DATEMINUS12$$</c>, <c>\\</c> separators) is accepted forever so
/// imported profiles keep meaning what they meant, but it's a reading
/// dialect, not the one Ara writes.
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

        // Both separators mean "folder": the inherited NINA default uses
        // '\\', Ara's own templates use '/'.
        var segments = Canonicalize(template).Replace('\\', '/').Split('/');
        var expanded = new List<string>();
        foreach (var segment in segments) {
            var value = SanitizeSegment(ExpandTokens(segment, ctx));
            if (value.Length > 0) expanded.Add(value);
        }
        return expanded.Count == 0 ? null : string.Join(Path.DirectorySeparatorChar, expanded);
    }

    /// <summary>
    /// Rewrite the inherited <c>$$TOKEN$$</c> dialect into Ara's
    /// <c>{token}</c> form so one expander serves both. Unknown legacy
    /// tokens become unknown braced tokens and vanish the same way.
    /// </summary>
    public static string Canonicalize(string template) {
        var sb = new StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length) {
            var start = template.IndexOf("$$", i, StringComparison.Ordinal);
            if (start < 0) { sb.Append(template, i, template.Length - i); break; }
            var end = template.IndexOf("$$", start + 2, StringComparison.Ordinal);
            if (end < 0) { sb.Append(template, i, template.Length - i); break; }
            sb.Append(template, i, start - i);
            var legacy = template.Substring(start + 2, end - start - 2).ToUpperInvariant();
            sb.Append('{').Append(legacy switch {
                "DATEMINUS12" => "night",
                "IMAGETYPE" => "type",
                "SENSORTEMP" => "temp",
                "EXPOSURETIME" => "exposure",
                "FRAMENR" => "n",
                "TARGETNAME" => "target",
                _ => legacy.ToLowerInvariant(),
            }).Append('}');
            i = end + 2;
        }
        return sb.ToString();
    }

    private static string ExpandTokens(string segment, FrameNamingContext ctx) {
        var sb = new StringBuilder(segment.Length + 16);
        var i = 0;
        while (i < segment.Length) {
            var start = segment.IndexOf('{', i);
            if (start < 0) { sb.Append(segment, i, segment.Length - i); break; }
            var end = segment.IndexOf('}', start + 1);
            if (end < 0) { sb.Append(segment, i, segment.Length - i); break; }
            sb.Append(segment, i, start - i);
            sb.Append(TokenValue(segment.Substring(start + 1, end - start - 1), ctx));
            i = end + 1;
        }
        return sb.ToString();
    }

    private static string TokenValue(string token, FrameNamingContext ctx) {
        var t = ctx.CapturedLocal;
        return token.Trim().ToLowerInvariant() switch {
            "date" => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "time" => t.ToString("HH-mm-ss", CultureInfo.InvariantCulture),
            "datetime" => t.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture),
            // The astronomer's night: everything before local noon belongs to
            // the evening it started.
            "night" => t.AddHours(-12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "dateutc" => t.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "timeutc" => t.ToUniversalTime().ToString("HH-mm-ss", CultureInfo.InvariantCulture),
            "type" => Capitalize(ctx.ImageType),
            "filter" => ctx.Filter ?? string.Empty,
            "exposure" => FormatExposure(ctx.ExposureSec),
            "gain" => ctx.Gain?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "offset" => ctx.Offset?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "binning" => string.Create(CultureInfo.InvariantCulture, $"{ctx.BinX}x{ctx.BinY}"),
            "temp" => ctx.SensorTemp is double temp
                ? Math.Round(temp).ToString("0", CultureInfo.InvariantCulture) + "C"
                : string.Empty,
            "camera" => ctx.CameraName ?? string.Empty,
            "target" => ctx.TargetName ?? string.Empty,
            "n" or "number" or "frame" => ctx.FrameNumber.ToString("0000", CultureInfo.InvariantCulture),
            // Unknown token: vanish rather than leak {x} into a filename.
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
