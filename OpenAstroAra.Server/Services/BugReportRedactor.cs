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

    /// <summary>§54.5 placeholders. GPS is not a placeholder: coordinates stay numbers (zeroed, or rounded for
    /// coarse GPS) so the scrubbed profile.json still parses for a maintainer's tooling.</summary>
    public const string RedactedValue = "[REDACTED]";
    public const string RedactedSerial = "[REDACTED-SN]";
    public const string RedactedHost = "[REDACTED-HOST]";
    public const string RedactedUser = "[REDACTED-USER]";
    public const string RedactedIp = "[REDACTED-IP]";

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

    // LAN names (pi-observatory.local, astro.pi.lan): every label goes, the suffix stays so the reader sees it was a LAN name.
    [GeneratedRegex(@"\b(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,62})\.)+(?:local|lan)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LanHostname();

    // §54.6 always-blacklisted credential shapes, stripped in every mode: the daemon's own token header, bearer
    // tokens, GitHub / Stripe / Slack-style keys, and private-key blocks.
    [GeneratedRegex(@"(?i)(X-OpenAstroAra-Token:\s*|Bearer\s+)[A-Za-z0-9_\-.]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenHeader();

    [GeneratedRegex(@"\b(?:ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk_(?:live|test)_[A-Za-z0-9]{10,}|xox[abp]-[A-Za-z0-9-]{10,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex KnownKeyShape();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBlock();

    /// <summary>§54.6 — credential shapes no mode may pass through; applied to every text entry in every mode.</summary>
    public static string StripCredentials(string text) {
        ArgumentNullException.ThrowIfNull(text);
        var s = TokenHeader().Replace(text, m => m.Groups[1].Value + RedactedValue);
        s = KnownKeyShape().Replace(s, RedactedValue);
        s = PrivateKeyBlock().Replace(s, RedactedValue);
        return s;
    }

    /// <summary>Scrub free text (a log file, system-info.json) for <paramref name="mode"/>. Full and
    /// coarse-GPS leave text alone: coordinates in logs are not reliably recognisable, and coarse-GPS
    /// is a profile-only promise (§54.3).</summary>
    public static string RedactText(string text, BugReportSharingMode mode) {
        ArgumentNullException.ThrowIfNull(text);
        var s = StripCredentials(text);
        if (mode != BugReportSharingMode.Redacted) {
            return s;
        }
        s = PosixUserPath().Replace(s, RedactedUser);
        s = WindowsUserPath().Replace(s, RedactedUser);
        s = PrivateIpv4().Replace(s, RedactedIp);
        s = LanHostname().Replace(s, m => RedactedHost + m.Value[m.Value.LastIndexOf('.')..]);
        return s;
    }

    /// <summary>Scrub a profile.json for <paramref name="mode"/>. Unparseable JSON is returned as text
    /// through <see cref="RedactText"/>, never dropped (the bundle must still say what the profile
    /// held). Full strips §54.6 credential shapes only (and re-serializes, indented); coarse GPS also
    /// rounds latitude/longitude to whole degrees (~111 km) and zeroes elevation; Redacted zeroes them
    /// and blanks secrets, serials and host-like keys too.</summary>
    public static string RedactProfileJson(string json, BugReportSharingMode mode) {
        ArgumentNullException.ThrowIfNull(json);
        // No early return for Full: §54.6 credential shapes go in every mode, which needs the walk.
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

    // §54.5 placeholder for a sensitive key, or null for an ordinary one.
    private static string? PlaceholderFor(string key) =>
        SerialKey().IsMatch(key) ? RedactedSerial
        : HostKey().IsMatch(key) ? RedactedHost
        : SecretKey().IsMatch(key) ? RedactedValue
        : null;

    private static bool TryDouble(JsonValue value, out double d) {
        if (value.TryGetValue<double>(out d)) {
            return true;
        }
        return value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
    }

    private static void Walk(JsonNode node, BugReportSharingMode mode) {
        switch (node) {
            case JsonObject obj:
                // Snapshot the keys: values are replaced in place while iterating.
                foreach (var key in new System.Collections.Generic.List<string>(obj.Select(kv => kv.Key))) {
                    var value = obj[key];
                    if (value is null) {
                        continue;
                    }
                    if (mode != BugReportSharingMode.Full && CoordinateKey().IsMatch(key) && value is JsonValue cv && TryDouble(cv, out var deg)) {
                        // A coordinate stays a number (a string one is rewritten as a number) so the file still parses.
                        obj[key] = mode == BugReportSharingMode.CoarseGps ? Math.Round(deg, 0, MidpointRounding.AwayFromZero) : 0.0;
                        continue;
                    }
                    if (mode != BugReportSharingMode.Full && ElevationKey().IsMatch(key) && value is JsonValue ev && TryDouble(ev, out _)) {
                        obj[key] = 0.0; // a string elevation is an elevation too
                        continue;
                    }
                    if (value is JsonValue sv && sv.TryGetValue<string>(out var str)) {
                        if (mode == BugReportSharingMode.Redacted && str.Length > 0 && PlaceholderFor(key) is { } placeholder) {
                            obj[key] = placeholder; // an empty secret stays empty: the reader learns it is unset
                        } else {
                            obj[key] = RedactText(str, mode); // §54.6 credentials go in every mode
                        }
                        continue;
                    }
                    if (mode == BugReportSharingMode.Redacted && value is JsonValue nv && PlaceholderFor(key) is { } p && nv.TryGetValue<double>(out _)) {
                        obj[key] = p; // a serial or token stored as a number is still a serial or token
                        continue;
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
                    } else if (item is JsonValue v && v.TryGetValue<string>(out var s)) {
                        arr[i] = RedactText(s, mode);
                    }
                }
                break;
        }
    }
}
