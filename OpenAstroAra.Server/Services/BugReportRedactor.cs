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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenAstroAra.Server.Services;

/// <summary>§54.3 sharing modes for a bug-report bundle: everything, coarse GPS (~111 km), or every
/// sensitive item redacted. The wire values are the snake_case strings.</summary>
public enum BugReportSharingMode {
    Full,
    CoarseGps,
    Redacted,
}

/// <summary>
/// §54.4 — the redaction applied while a bundle is built, server-side, so the ZIP the user
/// downloads never held the sensitive items in the chosen mode. What is scrubbed (per §54.1):
/// GPS coordinates and elevation, equipment serial numbers, hostnames, usernames in paths,
/// private IPv4 addresses, and notification tokens / secrets. The profile is walked as JSON
/// (so keys drive the decisions); logs and system-info are scrubbed by pattern. Nothing
/// here touches the daemon's own files: only the copies written into the bundle.
/// </summary>
public static partial class BugReportRedactor {

    public const string RedactedValue = "[redacted]";

    /// <summary>Parse a wire value; null/blank = <see cref="BugReportSharingMode.Full"/>; unknown = null.</summary>
    public static BugReportSharingMode? Parse(string? wire) => wire?.Trim().ToLowerInvariant() switch {
        null or "" or "full" => BugReportSharingMode.Full,
        "coarse_gps" => BugReportSharingMode.CoarseGps,
        "redacted" => BugReportSharingMode.Redacted,
        _ => null,
    };

    public static string ToWire(BugReportSharingMode mode) => mode switch {
        BugReportSharingMode.CoarseGps => "coarse_gps",
        BugReportSharingMode.Redacted => "redacted",
        _ => "full",
    };

    // Keys that hold a secret or a device identity, matched case-insensitively on the key name.
    [GeneratedRegex("token|secret|password|passphrase|api_?key|webhook", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKey();

    [GeneratedRegex("serial", RegexOptions.IgnoreCase)]
    private static partial Regex SerialKey();

    [GeneratedRegex("^(latitude|longitude)(_deg)?$|^(lat|lon|lng)$", RegexOptions.IgnoreCase)]
    private static partial Regex CoordinateKey();

    [GeneratedRegex("^elevation(_m)?$|altitude_m", RegexOptions.IgnoreCase)]
    private static partial Regex ElevationKey();

    [GeneratedRegex("host_?name|^host$|endpoint|address|_url$", RegexOptions.IgnoreCase)]
    private static partial Regex HostKey();

    // /home/<user>/, /Users/<user>/, C:\Users\<user>\ — keep the shape, drop the name.
    [GeneratedRegex(@"(?<=(^|[\s""'=:(\[,])/(?:home|Users)/)([^/\s""']+)(?=/)", RegexOptions.CultureInvariant)]
    private static partial Regex PosixUserPath();

    // One or two backslashes: a Windows path inside serialized JSON (system-info, a CLEF log line) is
    // escaped as C:\\Users\\name\\.
    [GeneratedRegex(@"(?<=[A-Za-z]:\\{1,2}Users\\{1,2})([^\\\s""']+)(?=\\)", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsUserPath();

    // RFC 1918 ranges only: a public address in a log is a deliberate remote-access config, not a LAN leak.
    [GeneratedRegex(@"\b(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateIpv4();

    // mDNS names (pi-observatory.local) — the one hostname form a LAN daemon logs routinely.
    [GeneratedRegex(@"\b[A-Za-z0-9](?:[A-Za-z0-9-]{0,62})\.local\b", RegexOptions.CultureInvariant)]
    private static partial Regex LocalHostname();

    /// <summary>Scrub free text (a log file, system-info.json) for <paramref name="mode"/>. Full and
    /// coarse-GPS leave text alone: coordinates in logs are not reliably recognisable, and coarse-GPS
    /// is a profile-only promise (§54.3).</summary>
    public static string RedactText(string text, BugReportSharingMode mode) {
        ArgumentNullException.ThrowIfNull(text);
        if (mode != BugReportSharingMode.Redacted) {
            return text;
        }
        var s = PosixUserPath().Replace(text, "[user]");
        s = WindowsUserPath().Replace(s, "[user]");
        s = PrivateIpv4().Replace(s, "[private-ip]");
        s = LocalHostname().Replace(s, "[host].local");
        return s;
    }

    /// <summary>Scrub a profile.json for <paramref name="mode"/>. Unparseable JSON is returned as text
    /// through <see cref="RedactText"/>, never dropped (the bundle must still say what the profile
    /// held). Coarse GPS rounds latitude/longitude to whole degrees (~111 km) and zeroes elevation;
    /// Redacted zeroes them and blanks secrets, serials and host-like keys too.</summary>
    public static string RedactProfileJson(string json, BugReportSharingMode mode) {
        ArgumentNullException.ThrowIfNull(json);
        if (mode == BugReportSharingMode.Full) {
            return json;
        }
        JsonNode? root;
        try {
            root = JsonNode.Parse(json);
        } catch (JsonException) {
            return RedactText(json, mode);
        }
        if (root is null) {
            return json;
        }
        Walk(root, mode);
        return root.ToJsonString(Indented);
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static void Walk(JsonNode node, BugReportSharingMode mode) {
        switch (node) {
            case JsonObject obj:
                // Snapshot the keys: values are replaced in place while iterating.
                foreach (var key in new System.Collections.Generic.List<string>(obj.Select(kv => kv.Key))) {
                    var value = obj[key];
                    if (value is null) {
                        continue;
                    }
                    if (CoordinateKey().IsMatch(key) && value is JsonValue cv && cv.TryGetValue<double>(out var deg)) {
                        obj[key] = mode == BugReportSharingMode.CoarseGps ? Math.Round(deg, 0, MidpointRounding.AwayFromZero) : 0.0;
                        continue;
                    }
                    if (ElevationKey().IsMatch(key) && value is JsonValue ev && ev.TryGetValue<double>(out _)) {
                        obj[key] = 0.0;
                        continue;
                    }
                    if (mode == BugReportSharingMode.Redacted && value is JsonValue sv) {
                        var sensitiveKey = SecretKey().IsMatch(key) || SerialKey().IsMatch(key) || HostKey().IsMatch(key);
                        if (sv.TryGetValue<string>(out var str)) {
                            // An empty secret stays empty: the reader learns it is unset.
                            obj[key] = sensitiveKey && str.Length > 0 ? RedactedValue : RedactText(str, mode);
                            continue;
                        }
                        if (sensitiveKey && sv.TryGetValue<double>(out _)) {
                            // A serial or token stored as a number is still a serial or token.
                            obj[key] = RedactedValue;
                            continue;
                        }
                    }
                    if (value is JsonObject or JsonArray) {
                        Walk(value, mode);
                    }
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++) {
                    var item = arr[i];
                    if (item is JsonObject or JsonArray) {
                        Walk(item, mode);
                    } else if (mode == BugReportSharingMode.Redacted && item is JsonValue v && v.TryGetValue<string>(out var s)) {
                        arr[i] = RedactText(s, mode);
                    }
                }
                break;
        }
    }
}
