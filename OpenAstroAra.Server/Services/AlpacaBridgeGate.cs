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

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §68.1 AlpacaBridge version-gate outcome. ARA depends on AlpacaBridge as its equipment hub;
    /// the daemon probes the bridge's <c>/version</c> on connect and classifies it so equipment
    /// operations can be blocked (or warned about) when the bridge is too old to be trusted.
    /// </summary>
    public enum AlpacaBridgeStatus {
        /// <summary>Version &gt;= 1.5.0 — full feature support.</summary>
        Ok,

        /// <summary>
        /// Version &gt;= 1.2.0 and &lt; 1.5.0 — usable, but a newer bridge is recommended. ARA
        /// surfaces the §68.1 non-blocking warning (<c>equipment.alpaca_bridge_outdated_warn</c>);
        /// equipment is NOT blocked.
        /// </summary>
        OutdatedWarn,

        /// <summary>
        /// Version &lt; 1.2.0 — below the minimum supported bridge. Equipment-dependent operations
        /// must refuse with HTTP 503 + <c>code: "alpaca_bridge_outdated"</c> until the bridge is
        /// upgraded.
        /// </summary>
        OutdatedBlock,

        /// <summary>
        /// The <c>/version</c> probe was unreachable, non-JSON, or missing the version field — the
        /// bridge is treated as not present (§68.2), distinct from "present but too old".
        /// </summary>
        Missing,
    }

    /// <summary>
    /// The pure §68.1 version-gate decision: parse an AlpacaBridge version string and classify it
    /// against the minimum (1.2.0) and recommended (1.5.0) versions. No I/O — the network probe lives
    /// in <see cref="AlpacaBridgeVersionProbe"/>, so this logic is deterministically unit-testable.
    /// </summary>
    public static class AlpacaBridgeGate {

        /// <summary>§68.1 minimum supported AlpacaBridge version; below this, equipment is blocked.</summary>
        public static readonly Version MinimumVersion = new(1, 2, 0);

        /// <summary>§68.1 recommended version; between minimum and this, a non-blocking warning is shown.</summary>
        public static readonly Version RecommendedVersion = new(1, 5, 0);

        private static readonly Version Zero = new(0, 0, 0);

        /// <summary>
        /// Classify a raw AlpacaBridge version string per the §68.1 table. A null/blank/unparseable
        /// value yields <see cref="AlpacaBridgeStatus.Missing"/> (the handshake couldn't read a
        /// version) — deliberately NOT <see cref="AlpacaBridgeStatus.OutdatedBlock"/>, since "we
        /// couldn't tell" is the missing-bridge case (§68.2), not a known-too-old bridge.
        /// </summary>
        public static AlpacaBridgeStatus Classify(string? versionString) {
            if (!TryParseVersion(versionString, out var version)) {
                return AlpacaBridgeStatus.Missing;
            }
            if (version >= RecommendedVersion) {
                return AlpacaBridgeStatus.Ok;
            }
            return version >= MinimumVersion ? AlpacaBridgeStatus.OutdatedWarn : AlpacaBridgeStatus.OutdatedBlock;
        }

        /// <summary>
        /// Leniently parse an AlpacaBridge version into a 3-component <see cref="Version"/>:
        /// accepts <c>"1.2.3"</c>, <c>"1.2"</c> (patch defaults to 0), a leading <c>v</c>, extra
        /// components (<c>"1.2.3.4"</c> → 1.2.3), and a pre-release/build suffix (<c>"1.2.3-beta"</c>
        /// → 1.2.3). Always normalizes to major.minor.patch so <c>"1.2"</c> compares equal to
        /// <c>1.2.0</c> rather than less-than (a bare <see cref="Version"/> leaves unspecified
        /// components at -1).
        /// </summary>
        internal static bool TryParseVersion(string? raw, out Version version) {
            version = Zero;
            if (string.IsNullOrWhiteSpace(raw)) {
                return false;
            }
            var trimmed = raw.Trim();
            if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V')) {
                trimmed = trimmed[1..];
            }
            // Take the leading run of digits and dots, dropping any "-beta"/"+build" tail.
            var end = 0;
            while (end < trimmed.Length && (char.IsAsciiDigit(trimmed[end]) || trimmed[end] == '.')) {
                end++;
            }
            var parts = trimmed[..end].Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) {
                return false;
            }
            if (!TryParseComponent(parts[0], out var major)) {
                return false;
            }
            var minor = 0;
            var patch = 0;
            if (parts.Length > 1 && !TryParseComponent(parts[1], out minor)) {
                return false;
            }
            if (parts.Length > 2 && !TryParseComponent(parts[2], out patch)) {
                return false;
            }
            version = new Version(major, minor, patch);
            return true;
        }

        private static bool TryParseComponent(string s, out int value) =>
            int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
