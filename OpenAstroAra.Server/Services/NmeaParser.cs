#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Globalization;

namespace OpenAstroAra.Server.Services;

/// <summary>A parsed NMEA fix: UTC time (always carried by RMC; date too) and, when the sentence
/// has a valid position, lat/long in signed degrees. Altitude only from GGA (meters).</summary>
public sealed record NmeaFix(DateTimeOffset? TimeUtc, double? LatitudeDeg, double? LongitudeDeg, double? AltitudeM);

/// <summary>
/// §31.4 — minimal NMEA 0183 parsing for the two sentences the USB-GPS self-sync needs:
/// <c>$GPRMC</c> (UTC time + date + position, with an A/V validity flag) and <c>$GPGGA</c>
/// (UTC time + position + altitude, with a fix-quality digit). Talker-agnostic (`$GNRMC` from a
/// multi-constellation receiver parses the same). Checksum-verified when the sentence carries
/// one; a bad checksum or an invalid/void fix parses to null rather than throwing — the serial
/// stream is untrusted line noise until proven otherwise.
/// </summary>
public static class NmeaSentenceParser {

    /// <summary>Parse one sentence. Null for anything that isn't a valid, checksummed RMC/GGA
    /// with an active fix.</summary>
    public static NmeaFix? Parse(string? sentence) {
        if (string.IsNullOrWhiteSpace(sentence)) {
            return null;
        }
        var s = sentence.Trim();
        if (s.Length < 7 || s[0] != '$') {
            return null;
        }
        // Optional *hh checksum: XOR of everything between '$' and '*'.
        var star = s.IndexOf('*', StringComparison.Ordinal);
        string body;
        if (star >= 0) {
            if (star + 3 > s.Length || !byte.TryParse(s.AsSpan(star + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var expected)) {
                return null;
            }
            byte actual = 0;
            for (var i = 1; i < star; i++) {
                actual ^= (byte)s[i];
            }
            if (actual != expected) {
                return null;
            }
            body = s[1..star];
        } else {
            body = s[1..];
        }

        var f = body.Split(',');
        if (f.Length < 1 || f[0].Length != 5) {
            return null;
        }
        var type = f[0][2..]; // drop the 2-char talker (GP/GN/GL/…)
        return type switch {
            "RMC" => ParseRmc(f),
            "GGA" => ParseGga(f),
            _ => null,
        };
    }

    // $GPRMC,hhmmss.sss,A,llll.ll,a,yyyyy.yy,a,speed,course,ddmmyy,…  (A = active, V = void)
    private static NmeaFix? ParseRmc(string[] f) {
        if (f.Length < 10 || !string.Equals(f[2], "A", StringComparison.Ordinal)) {
            return null;
        }
        var time = ParseUtc(f[1], f[9]);
        if (time is null) {
            return null;
        }
        var lat = ParseCoordinate(f[3], f[4], isLatitude: true);
        var lng = ParseCoordinate(f[5], f[6], isLatitude: false);
        return new NmeaFix(time, lat, lng, AltitudeM: null);
    }

    // $GPGGA,hhmmss.sss,llll.ll,a,yyyyy.yy,a,quality,sats,hdop,alt,M,…  (quality 0 = no fix)
    private static NmeaFix? ParseGga(string[] f) {
        if (f.Length < 10 || !int.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quality) || quality <= 0) {
            return null;
        }
        var lat = ParseCoordinate(f[2], f[3], isLatitude: true);
        var lng = ParseCoordinate(f[4], f[5], isLatitude: false);
        double? alt = double.TryParse(f[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : null;
        // GGA has no date field, so its time can't build a full instant on its own — position/alt
        // only; RMC supplies the timestamp for the sync.
        return new NmeaFix(TimeUtc: null, lat, lng, alt);
    }

    private static DateTimeOffset? ParseUtc(string hhmmss, string ddmmyy) {
        if (hhmmss.Length < 6 || ddmmyy.Length != 6) {
            return null;
        }
        if (!int.TryParse(hhmmss.AsSpan(0, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hh)
                || !int.TryParse(hhmmss.AsSpan(2, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm)
                || !int.TryParse(hhmmss.AsSpan(4, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ss)
                || !int.TryParse(ddmmyy.AsSpan(0, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dd)
                || !int.TryParse(ddmmyy.AsSpan(2, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mo)
                || !int.TryParse(ddmmyy.AsSpan(4, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var yy)) {
            return null;
        }
        double fractional = 0;
        var dot = hhmmss.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0 && !double.TryParse(hhmmss.AsSpan(dot), NumberStyles.Float, CultureInfo.InvariantCulture, out fractional)) {
            return null;
        }
        try {
            // NMEA's 2-digit year is unambiguous in practice: GPS predates 2000, ARA doesn't.
            var baseTime = new DateTimeOffset(2000 + yy, mo, dd, hh, mm, ss, TimeSpan.Zero);
            return baseTime.AddSeconds(fractional);
        } catch (ArgumentOutOfRangeException) {
            return null;
        }
    }

    // NMEA coordinates are ddmm.mmmm (lat) / dddmm.mmmm (lng) with a N/S/E/W hemisphere field.
    private static double? ParseCoordinate(string value, string hemisphere, bool isLatitude) {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(hemisphere)) {
            return null;
        }
        var degDigits = isLatitude ? 2 : 3;
        if (value.Length <= degDigits
                || !int.TryParse(value.AsSpan(0, degDigits), NumberStyles.Integer, CultureInfo.InvariantCulture, out var deg)
                || !double.TryParse(value.AsSpan(degDigits), NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)) {
            return null;
        }
        var result = deg + minutes / 60.0;
        return hemisphere switch {
            "N" or "E" => result,
            "S" or "W" => -result,
            _ => null,
        };
    }
}
