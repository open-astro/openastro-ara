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
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1134 — the opt-in that aims the live <c>*ConnectIntegrationTest</c> fixtures at a real Alpaca
    /// server (AlpacaBridge on a rig) instead of the OmniSim. Read from the environment:
    /// <list type="bullet">
    /// <item><c>ARA_ALPACA_URL</c> — <c>http://host:port</c> of the server. Unset: the fixtures keep the
    /// OmniSim path unchanged.</item>
    /// <item><c>ARA_ALPACA_&lt;TYPE&gt;</c> — optional device number per type (<c>ARA_ALPACA_CAMERA=1</c>);
    /// the lowest-numbered device of the type when unset. The flat panel is <c>ARA_ALPACA_COVERCALIBRATOR</c>.</item>
    /// <item><c>ARA_ALPACA_ALLOW_MOTION</c> — <c>1</c> lets the cases that move gear (mount, focuser,
    /// rotator, filter wheel, dome, cover) or write switch outputs run. Off by default.</item>
    /// </list>
    /// </summary>
    internal sealed class LiveAlpacaRig {

        public const string UrlVariable = "ARA_ALPACA_URL";
        public const string MotionVariable = "ARA_ALPACA_ALLOW_MOTION";

        private readonly IReadOnlyDictionary<DeviceType, int> _deviceNumbers;

        private LiveAlpacaRig(Uri url, bool allowMotion, IReadOnlyDictionary<DeviceType, int> deviceNumbers) {
            Url = url;
            AllowMotion = allowMotion;
            _deviceNumbers = deviceNumbers;
        }

        /// <summary>The server's base URL, as given.</summary>
        public Uri Url { get; }

        public string Host => Url.Host;

        public int Port => Url.Port;

        /// <summary>Whether cases that move gear or write switch outputs may run.</summary>
        public bool AllowMotion { get; }

        /// <summary>The device-number variable for a type: <c>ARA_ALPACA_CAMERA</c>, and
        /// <c>ARA_ALPACA_COVERCALIBRATOR</c> for both FlatDevice and CoverCalibrator.</summary>
        public static string DeviceVariable(DeviceType type) =>
            "ARA_ALPACA_" + type.Canonical().ToString().ToUpperInvariant();

        /// <summary>The rig named by the environment, or null when <c>ARA_ALPACA_URL</c> is unset or blank.
        /// A malformed value throws <see cref="ArgumentException"/> naming the variable, so a typo fails
        /// the fixture loudly instead of quietly running against the OmniSim.</summary>
        public static LiveAlpacaRig? FromEnvironment(Func<string, string?> env) {
            var raw = env(UrlVariable)?.Trim();
            if (string.IsNullOrEmpty(raw)) {
                return null;
            }
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttp || url.Host.Length == 0) {
                throw new ArgumentException($"{UrlVariable}='{raw}' is not an http://host:port URL (https is not supported).");
            }
            var numbers = new Dictionary<DeviceType, int>();
            foreach (var type in Enum.GetValues<DeviceType>().Where(t => t != DeviceType.Guider).Select(t => t.Canonical()).Distinct()) {
                var name = DeviceVariable(type);
                var value = env(name)?.Trim();
                if (string.IsNullOrEmpty(value)) {
                    continue;
                }
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) {
                    throw new ArgumentException($"{name}='{value}' is not an Alpaca device number (0, 1, 2 ...).");
                }
                numbers[type] = number;
            }
            return new LiveAlpacaRig(url, ParseMotion(env(MotionVariable)), numbers);
        }

        private static bool ParseMotion(string? value) {
            var v = value?.Trim() ?? string.Empty;
            if (v.Length == 0 || v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
            throw new ArgumentException($"{MotionVariable}='{v}' must be 1 (allow motion) or 0 (default).");
        }

        /// <summary>The type half of a <c>configureddevices</c> key ("camera", "covercalibrator").</summary>
        [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
            Justification = "Matches the lowercased keys AlpacaManagementClient.ParseConfiguredDevices builds; an ASCII enum name, never compared culture-sensitively.")]
        public static string TypeKey(DeviceType type) => type.Canonical().ToString().ToLowerInvariant();

        /// <summary>The device the fixture for <paramref name="type"/> connects to, picked from the server's
        /// <c>configureddevices</c> (keys <c>"camera/0"</c>, as <see cref="AlpacaManagementClient"/> reads
        /// them): the number from <see cref="DeviceVariable"/> when set, else the lowest-numbered device of
        /// the type. Null when the server has none.</summary>
        public DiscoveredDeviceDto? Pick(DeviceType type, IReadOnlyDictionary<string, AlpacaConfiguredDevice> configured) {
            var prefix = TypeKey(type) + "/";
            var candidates = configured
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kv => (Number: int.TryParse(kv.Key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1, Device: kv.Value))
                .Where(c => c.Number >= 0)
                .OrderBy(c => c.Number)
                .ToList();
            var wanted = _deviceNumbers.TryGetValue(type.Canonical(), out var pinned) ? (int?)pinned : null;
            var hit = candidates.FirstOrDefault(c => wanted is null || c.Number == wanted);
            if (hit.Device is null) {
                return null;
            }
            var uniqueId = hit.Device.UniqueId.Length > 0
                ? hit.Device.UniqueId
                : string.Create(CultureInfo.InvariantCulture, $"{Host}:{Port}/{prefix}{hit.Number}");
            return new DiscoveredDeviceDto(uniqueId, hit.Device.Name, type, HostName: Host, IpAddress: string.Empty,
                IpPort: Port, AlpacaDeviceNumber: hit.Number, UseHttps: false);
        }

        /// <summary>The skip message when the server has no device for the fixture's type.</summary>
        public string MissingDeviceMessage(DeviceType type) {
            var key = TypeKey(type);
            return _deviceNumbers.TryGetValue(type.Canonical(), out var n)
                ? $"The Alpaca server at {Url} has no {key}/{n} ({DeviceVariable(type)}={n}) — skipping live {type} test."
                : $"The Alpaca server at {Url} lists no {key} in configureddevices — skipping live {type} test.";
        }

        /// <summary>Null when <paramref name="action"/> may run; otherwise the skip message that says
        /// which opt-in would run it.</summary>
        public string? MotionSkipReason(string deviceName, string action) => AllowMotion
            ? null
            : $"Skipped on real gear: this case {action} on the {deviceName}. Set {MotionVariable}=1 to run it (watch the rig).";
    }
}
