#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1649 — Simple Mode's Tracking speed row: the mount's drive rate (Sidereal, Lunar,
    /// Solar, King) is read with the runtime and set through its own endpoint.</summary>
    [TestFixture]
    public class EquipmentEndpointsTrackingRateTest {

        private static int StatusOf(IResult r) => r switch {
            ProblemHttpResult p => p.StatusCode,
            Accepted => StatusCodes.Status202Accepted,
            _ => throw new InvalidOperationException(r.GetType().Name),
        };

        [Test]
        public async Task A_known_rate_is_forwarded_whatever_its_case() {
            var svc = new Mock<ITelescopeService>();
            var result = await EquipmentEndpoints.SetTrackingRateAsync(new TelescopeTrackingRateRequestDto("lunar"), svc.Object, CancellationToken.None);
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status202Accepted));
            svc.Verify(s => s.SetTrackingRateAsync("Lunar", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task An_unknown_rate_is_400_before_the_driver() {
            var svc = new Mock<ITelescopeService>();
            var result = await EquipmentEndpoints.SetTrackingRateAsync(new TelescopeTrackingRateRequestDto("ludicrous"), svc.Object, CancellationToken.None);
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status400BadRequest));
            svc.Verify(s => s.SetTrackingRateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task A_refusal_is_409_with_the_service_message() {
            var svc = new Mock<ITelescopeService>();
            svc.Setup(s => s.SetTrackingRateAsync("King", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("This mount doesn't offer the King rate."));
            var result = await EquipmentEndpoints.SetTrackingRateAsync(new TelescopeTrackingRateRequestDto("King"), svc.Object, CancellationToken.None);
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status409Conflict));
            Assert.That(((ProblemHttpResult)result).ProblemDetails.Detail, Is.EqualTo("This mount doesn't offer the King rate."));
        }
    }

    [TestFixture]
    [Category("IO")] // loopback scripted Alpaca device
    public class TelescopeTrackingRateTest {

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "mount-under-test", Name: "Bench mount", Type: DeviceType.Telescope,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private static readonly string[] ThreeRates = ["Sidereal", "Lunar", "Solar"];

        // Sidereal, Lunar and Solar (ASCOM DriveRate 0, 1, 2); currently Lunar.
        private static Func<string, string?> Mount() => path =>
            path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/tracking", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/trackingrates", StringComparison.Ordinal) ? "[0,1,2]"
            : path.EndsWith("/trackingrate", StringComparison.Ordinal) ? "1"
            : path.EndsWith("/rightascension", StringComparison.Ordinal) ? "6.0"
            : path.EndsWith("/declination", StringComparison.Ordinal) ? "45.0"
            : null;

        private static async Task<TelescopeDto> WaitForAsync(TelescopeService svc, Func<TelescopeDto?, bool> ready, string failure) {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(15)) {
                if (await svc.GetAsync(CancellationToken.None) is { } dto && ready(dto)) {
                    return dto;
                }
                await Task.Delay(50);
            }
            Assert.Fail(failure);
            return null!;
        }

        [Test]
        public async Task The_runtime_reports_the_rate_and_a_set_reaches_the_driver() {
            await using var box = ScriptedAlpacaDevice.Start(Mount());
            using var svc = new TelescopeService { RefreshPeriod = TimeSpan.FromMilliseconds(100) };
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);

            var dto = await WaitForAsync(svc, d => d?.Runtime.TrackingRate is not null && d.Capabilities is not null, "the rate was never read");
            Assert.That(dto.Runtime.TrackingRate, Is.EqualTo("Lunar"));
            Assert.That(dto.Capabilities!.SupportedSiderealRates, Is.EqualTo(ThreeRates));

            await svc.SetTrackingRateAsync("Solar", CancellationToken.None);
            var put = box.Puts.Single(p => p.Path.EndsWith("/trackingrate", StringComparison.Ordinal));
            Assert.That(put.Body, Does.Contain("TrackingRate=2"));
        }

        [Test]
        public async Task A_rate_the_mount_does_not_offer_is_refused() {
            await using var box = ScriptedAlpacaDevice.Start(Mount());
            using var svc = new TelescopeService { RefreshPeriod = TimeSpan.FromMilliseconds(100) };
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            await WaitForAsync(svc, d => d?.Capabilities is not null, "capabilities never published");

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetTrackingRateAsync("King", CancellationToken.None));
            Assert.That(box.Puts.Any(p => p.Path.EndsWith("/trackingrate", StringComparison.Ordinal)), Is.False);
        }
    }
}
