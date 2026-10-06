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
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1230 — the capabilities are built on the first pass whose reads answer. If the secondary axis's
    /// AxisRates threw on that pass, the unclipped primary bands used to stay published for the whole
    /// session while the snap later used the real secondary bands, so a chip under the secondary's
    /// floor still 409'd. The caps must catch up once the band cache fills.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — loopback HTTP
    public class TelescopeAxisRatesRepublishTest {

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "mount-under-test", Name: "Bench mount", Type: DeviceType.Telescope,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private const string PrimaryBands = "[{\"Minimum\":0.002,\"Maximum\":4.0}]";
        private static readonly double[] RepublishedLegacyList = [0.5, 2.0, 4.0];
        private const string SecondaryBands = "[{\"Minimum\":0.5,\"Maximum\":0.5},{\"Minimum\":2.0,\"Maximum\":4.0}]";

        private static Func<string, string?> Mount() => path =>
            path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/tracking", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/canmoveaxis", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/rightascension", StringComparison.Ordinal) ? "6.0"
            : path.EndsWith("/declination", StringComparison.Ordinal) ? "45.0"
            : null;

        private static async Task<TelescopeCapabilitiesDto?> CapsAsync(TelescopeService svc) =>
            (await svc.GetAsync(CancellationToken.None))?.Capabilities;

        /// <summary>Polls the capabilities until <paramref name="ready"/> accepts them; the last read on timeout.</summary>
        private static async Task<TelescopeCapabilitiesDto?> WaitForCapsAsync(TelescopeService svc, Func<TelescopeCapabilitiesDto?, bool> ready, string failure) {
            var sw = Stopwatch.StartNew();
            TelescopeCapabilitiesDto? caps = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(15)) {
                caps = await CapsAsync(svc);
                if (ready(caps)) {
                    return caps;
                }
                await Task.Delay(50);
            }
            Assert.Fail(failure);
            return caps;
        }

        [Test]
        public async Task Capabilities_are_republished_once_a_late_secondary_AxisRates_answers() {
            var secondaryAnswers = 0;
            await using var box = ScriptedAlpacaDevice.Start(Mount());
            box.RespondWithQuery((path, query) => {
                if (!path.EndsWith("/axisrates", StringComparison.Ordinal)) {
                    return null;
                }
                if (query.Contains("axis=0", StringComparison.Ordinal)) {
                    return PrimaryBands;
                }
                // The secondary throws until the test flips it — the pass that builds the caps sees null.
                return Volatile.Read(ref secondaryAnswers) == 1 ? SecondaryBands : ScriptedAlpacaDevice.Error("AxisRates(Secondary) busy");
            });
            using var svc = new TelescopeService { RefreshPeriod = TimeSpan.FromMilliseconds(100) };
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);

            var caps = await WaitForCapsAsync(svc, c => c is not null, "capabilities never published");
            Assert.That(caps!.MoveAxisRateBandsDegPerSec, Is.EqualTo(new[] { new MoveAxisRateBandDto(0.002, 4.0) }),
                "with the secondary unread, the primary bands go out unclipped (as before)");

            Volatile.Write(ref secondaryAnswers, 1);
            caps = await WaitForCapsAsync(svc, c => c?.MoveAxisRateBandsDegPerSec?.Count == 2,
                "the capabilities were never re-published after the secondary answered");
            Assert.That(caps!.MoveAxisRateBandsDegPerSec, Is.EqualTo(new[] { new MoveAxisRateBandDto(0.5, 0.5), new MoveAxisRateBandDto(2.0, 4.0) }));
            Assert.That(caps.MoveAxisRatesDegPerSec, Is.EqualTo(RepublishedLegacyList), "the legacy list follows the re-published bands");

            // And the pad can now use a published rate on the secondary without a 409.
            await svc.MoveAxisAsync(1, 0.5, CancellationToken.None);
        }
    }
}
