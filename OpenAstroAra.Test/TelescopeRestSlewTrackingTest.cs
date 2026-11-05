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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>A go-to from the Planning page (the REST slew) on a mount at home with tracking off:
    /// the bridge refuses SlewToCoordinatesAsync until tracking is on, so the slew turns it on first.
    /// A sync moves nothing and leaves tracking alone.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — loopback HTTP: not part of the quick unit run
    [Category("bench")]
    public class TelescopeRestSlewTrackingTest {

        private static async Task<ScriptedAlpacaDevice> RunAsync(bool sync) {
            var tracking = false;
            var box = ScriptedAlpacaDevice.Start(
                path => path.EndsWith("/equatorialsystem", StringComparison.Ordinal) ? "2"
                    : path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
                    : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
                    : path.EndsWith("/tracking", StringComparison.Ordinal) ? (tracking ? "true" : "false")
                    : path.EndsWith("/rightascension", StringComparison.Ordinal) ? "6.0"
                    : path.EndsWith("/declination", StringComparison.Ordinal) ? "90.0"
                    : null,
                path => {
                    if (path.EndsWith("/tracking", StringComparison.Ordinal)) {
                        tracking = true;
                        return null;
                    }
                    return path.EndsWith("/slewtocoordinatesasync", StringComparison.Ordinal) && !tracking
                        ? ScriptedAlpacaDevice.Error("SlewToCoordinatesAsync requires Tracking to be true")
                        : null;
                });
            using var svc = new TelescopeService();
            var device = new DiscoveredDeviceDto(
                UniqueId: "mount-under-test", Name: "Bench mount", Type: DeviceType.Telescope,
                HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
                AlpacaDeviceNumber: 0, UseHttps: false);
            await svc.ConnectAsync(new ConnectRequestDto(device), null, CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (Math.Abs(((ITelescopeMediator)svc).GetInfo().RightAscension - 6.0) > 1e-9 && DateTime.UtcNow < deadline) {
                await Task.Delay(50);
            }
            await svc.SlewAsync(new SlewRequestDto(12.0, 45.0, sync), null, CancellationToken.None);
            var wanted = sync ? "/synctocoordinates" : "/slewtocoordinatesasync";
            while (!box.Puts.Any(p => p.Path.EndsWith(wanted, StringComparison.Ordinal)) && DateTime.UtcNow < deadline) {
                await Task.Delay(20);
            }
            return box;
        }

        [Test]
        public async Task A_goto_turns_tracking_on_before_it_slews() {
            await using var box = await RunAsync(sync: false);
            var paths = box.Puts.Select(p => p.Path).ToList();
            var trackingAt = paths.FindIndex(p => p.EndsWith("/tracking", StringComparison.Ordinal));
            var slewAt = paths.FindIndex(p => p.EndsWith("/slewtocoordinatesasync", StringComparison.Ordinal));
            Assert.That(trackingAt, Is.GreaterThanOrEqualTo(0), "tracking must be written");
            Assert.That(trackingAt, Is.LessThan(slewAt), "tracking goes on before the slew");
        }

        [Test]
        public async Task A_sync_leaves_tracking_alone() {
            await using var box = await RunAsync(sync: true);
            Assert.That(box.Puts.Select(p => p.Path).Any(p => p.EndsWith("/tracking", StringComparison.Ordinal)), Is.False);
        }
    }
}
