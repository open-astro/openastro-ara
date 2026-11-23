#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Base for the live <c>*ConnectIntegrationTest</c> fixtures: probes the ASCOM OmniSim
    /// management API on <c>:32323</c> once per fixture and <see cref="Assert.Ignore(string)"/>s
    /// when none answers, so the fixtures skip cleanly outside the <c>alpaca-sim-integration</c>
    /// CI job. A fixture that needs scratch disk calls <see cref="CreateTempDir"/>; the base
    /// tears it down safely — <see cref="Assert.Ignore(string)"/> fires BEFORE the directory
    /// exists, and <c>Directory.Delete("")</c> in a teardown threw <c>ArgumentException</c>,
    /// failing a local run with every test green (#1198, from the #1211 review).
    ///
    /// #1134 — with <c>ARA_ALPACA_URL</c> set (<see cref="LiveAlpacaRig"/>) the fixture probes that
    /// server instead, picks its device from <c>configureddevices</c> (no UDP discovery), and ignores
    /// when the server has no device of the type. Cases that move gear or write switch outputs call
    /// <see cref="RequireMotionOptIn"/>; OmniSim-only checks ask <see cref="OnRealGear"/>.
    /// </summary>
    public abstract class OmniSimIntegrationFixture {

        /// <summary>Discovery attempts the derived fixtures allow before failing.</summary>
        protected const int MaxDiscoveryAttempts = 6;

        private static readonly Uri ManagementProbeUri = new("http://127.0.0.1:32323/management/apiversions");

        /// <summary>Device name for the skip message ("skipping live Camera test").</summary>
        protected abstract string DeviceName { get; }

        /// <summary>The scratch directory from <see cref="CreateTempDir"/>, or "" when none was made.</summary>
        protected string TempDir { get; private set; } = string.Empty;

        /// <summary>The Alpaca type the fixture connects; the <see cref="DeviceName"/> token by default.</summary>
        protected virtual DeviceType DeviceType => Enum.Parse<DeviceType>(DeviceName);

        /// <summary>The real rig from <c>ARA_ALPACA_URL</c>, or null on the OmniSim path.</summary>
        private protected LiveAlpacaRig? Rig { get; private set; }

        /// <summary>Whether the fixture runs against real gear (<c>ARA_ALPACA_URL</c> set).</summary>
        protected bool OnRealGear => Rig is not null;

        private DiscoveredDeviceDto? _rigDevice;

        [OneTimeSetUp]
        public async Task ProbeOmniSim() {
            Rig = LiveAlpacaRig.FromEnvironment(Environment.GetEnvironmentVariable);
            if (Rig is not null) {
                await ProbeRig(Rig).ConfigureAwait(false);
                await OnOmniSimAvailable().ConfigureAwait(false);
                return;
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try {
                using var resp = await http.GetAsync(ManagementProbeUri).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) {
                    Assert.Ignore($"OmniSim management API returned {(int)resp.StatusCode} on :32323 — skipping live {DeviceName} test.");
                }
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
                Assert.Ignore("No ASCOM OmniSim answering on :32323 — start one (or run the alpaca-sim-integration CI job) to exercise this test.");
            }
            await OnOmniSimAvailable().ConfigureAwait(false);
        }

        private async Task ProbeRig(LiveAlpacaRig rig) {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            try {
                using var resp = await http.GetAsync(new Uri(rig.Url, "/management/apiversions")).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) {
                    Assert.Ignore($"The Alpaca server at {rig.Url} ({LiveAlpacaRig.UrlVariable}) returned {(int)resp.StatusCode} — skipping live {DeviceName} test.");
                }
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
                Assert.Ignore($"No Alpaca server answering at {rig.Url} ({LiveAlpacaRig.UrlVariable}) — skipping live {DeviceName} test.");
            }
            using var management = new AlpacaManagementClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var configured = await management.GetConfiguredDevicesAsync(rig.Host, rig.Port, cts.Token).ConfigureAwait(false);
            if (configured.Count == 0) {
                // The client reads a failed or unparsable configureddevices as empty: say so, not "no camera".
                Assert.Ignore($"The Alpaca server at {rig.Url} listed no devices (configureddevices empty, failed or unreadable) — skipping live {DeviceName} test.");
            }
            _rigDevice = rig.Pick(DeviceType, configured);
            if (_rigDevice is null) {
                Assert.Ignore(rig.MissingDeviceMessage(DeviceType));
            }
            await TestContext.Progress.WriteLineAsync(
                $"live {DeviceName} test on real gear: {_rigDevice.Name} ({LiveAlpacaRig.TypeKey(_rigDevice.Type)}/{_rigDevice.AlpacaDeviceNumber} at {rig.Url}), motion {(rig.AllowMotion ? "ALLOWED" : "off")}").ConfigureAwait(false);
        }

        /// <summary>Fixture-specific setup that runs only once a simulator (or the real rig) answered.</summary>
        protected virtual Task OnOmniSimAvailable() => Task.CompletedTask;

        /// <summary>Ignore the case on real gear unless <c>ARA_ALPACA_ALLOW_MOTION=1</c>: call it first in
        /// every case that moves gear or writes switch outputs. <paramref name="action"/> completes "this
        /// case ... on the {device}" ("slews the mount", "writes outputs"). No-op on the OmniSim.</summary>
        protected void RequireMotionOptIn(string action) {
            if (Rig?.MotionSkipReason(DeviceName, action) is { } reason) {
                Assert.Ignore(reason);
            }
        }

        /// <summary>The device to connect: the rig's pick on real gear, else the first one UDP discovery
        /// finds from the OmniSim (retried <see cref="MaxDiscoveryAttempts"/> times); null when none. #1134 —
        /// the OmniSim path keeps only devices on the OmniSim port, so a broadcast on a LAN that also has a
        /// rig never hands a motion case the real mount.</summary>
        protected async Task<DiscoveredDeviceDto?> DiscoverAsync() {
            if (_rigDevice is not null) {
                return _rigDevice;
            }
            var discovery = new AlpacaEquipmentDiscoveryService();
            for (var attempt = 1; attempt <= MaxDiscoveryAttempts; attempt++) {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var found = await discovery.DiscoverAsync(DeviceType, forceRefresh: true, cts.Token).ConfigureAwait(false);
                if (found.FirstOrDefault(d => d.IpPort == ManagementProbeUri.Port) is { } device) {
                    return device;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            return null;
        }

        /// <summary>Create a per-fixture scratch directory that <see cref="RemoveTempDir"/> deletes.</summary>
        protected string CreateTempDir(string prefix) {
            TempDir = Path.Combine(Path.GetTempPath(), $"oara-{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(TempDir);
            return TempDir;
        }

        [OneTimeTearDown]
        public void RemoveTempDir() {
            if (TempDir.Length == 0) {
                return;
            }
            try { Directory.Delete(TempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
