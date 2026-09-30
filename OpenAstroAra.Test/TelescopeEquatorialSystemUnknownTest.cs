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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1124 — until the mount's EquatorialSystem has been read, the daemon does not know
    /// which frame the mount wants, and must not guess JNOW. A slew or sync issued in that window
    /// reads the system on demand; if the read still fails the op is refused (no coordinates reach
    /// the mount), and the capture path records no pointing rather than a mislabelled one. Each bench
    /// drives a real <see cref="TelescopeService"/> against a scripted loopback mount.</summary>
    [TestFixture]
    [Category("bench")] // loopback-only, runs in the default job too
    public class TelescopeEquatorialSystemUnknownTest {

        private const string EquatorialSystemUnreadable = "\"not-an-int\""; // client-side read throws
        private const string J2000 = "2"; // ASCOM EquatorialCoordinateType.J2000

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "mount-under-test", Name: "Bench mount", Type: DeviceType.Telescope,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        // A mount sitting still at (6h, +45°) — a slew there settles on the first poll.
        private static Func<string, string?> Mount(Func<string> equatorialSystem) => path =>
            path.EndsWith("/equatorialsystem", StringComparison.Ordinal) ? equatorialSystem()
            : path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/tracking", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/cansync", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/rightascension", StringComparison.Ordinal) ? "6.0"
            : path.EndsWith("/declination", StringComparison.Ordinal) ? "45.0"
            : null;

        private static Coordinates J2000Target() =>
            new(Angle.ByHours(6.0), Angle.ByDegree(45.0), Epoch.J2000);

        private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string failure) {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                if (condition()) {
                    return;
                }
                await Task.Delay(100);
            }
            Assert.Fail(failure);
        }

        // Connected AND the first refresh has landed (RA/Dec read), so the EquatorialSystem read on
        // that refresh has already happened — and, for an unreadable system, failed.
        private static async Task<TelescopeService> ConnectAsync(ScriptedAlpacaDevice box) {
            var svc = new TelescopeService();
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            await WaitForAsync(() => {
                var info = ((ITelescopeMediator)svc).GetInfo();
                return info.Connected && Math.Abs(info.RightAscension - 6.0) < 1e-9;
            }, TimeSpan.FromSeconds(15), "mount never connected and reported its position");
            return svc;
        }

        private static bool PutReached(ScriptedAlpacaDevice box, string method) =>
            box.Puts.Any(p => p.Path.EndsWith("/" + method, StringComparison.Ordinal));

        private static double FormValue(string body, string key) {
            foreach (var pair in body.Split('&')) {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && string.Equals(Uri.UnescapeDataString(kv[0]), key, StringComparison.OrdinalIgnoreCase)) {
                    return double.Parse(Uri.UnescapeDataString(kv[1]), CultureInfo.InvariantCulture);
                }
            }
            throw new AssertionException($"'{key}' missing from PUT body '{body}'");
        }

        [Test]
        public async Task A_slew_before_the_equatorial_system_is_known_is_refused_not_sent_as_JNOW() {
            await using var box = ScriptedAlpacaDevice.Start(Mount(() => EquatorialSystemUnreadable));
            using var svc = await ConnectAsync(box);

            await Assert.ThrowsAsync<SequenceEntityFailedException>(
                () => ((ITelescopeMediator)svc).SlewToCoordinatesAsync(J2000Target(), CancellationToken.None),
                "a slew whose target frame is unknown must fail the instruction, not guess JNOW");
            Assert.That(PutReached(box, "slewtocoordinatesasync"), Is.False,
                "no target may reach the mount while its coordinate system is unknown");
        }

        [Test]
        public async Task A_sync_before_the_equatorial_system_is_known_is_refused_not_sent_as_JNOW() {
            await using var box = ScriptedAlpacaDevice.Start(Mount(() => EquatorialSystemUnreadable));
            using var svc = await ConnectAsync(box);

            Assert.That(await ((ITelescopeMediator)svc).Sync(J2000Target()), Is.False,
                "an unknown frame is a clean 'not synced' (the centering loop offset-compensates)");
            Assert.That(PutReached(box, "synctocoordinates"), Is.False,
                "the pointing model must not be recalibrated in a guessed frame");
        }

        [Test]
        public async Task A_slew_reads_the_equatorial_system_on_demand_and_sends_a_J2000_target_untransformed() {
            var system = EquatorialSystemUnreadable;
            await using var box = ScriptedAlpacaDevice.Start(Mount(() => Volatile.Read(ref system)));
            using var svc = await ConnectAsync(box);
            Volatile.Write(ref system, J2000); // readable now; the next refresh is up to 2 s away

            Assert.That(await ((ITelescopeMediator)svc).SlewToCoordinatesAsync(J2000Target(), CancellationToken.None), Is.True);
            var put = box.Puts.Single(p => p.Path.EndsWith("/slewtocoordinatesasync", StringComparison.Ordinal));
            Assert.That(FormValue(put.Body, "RightAscension"), Is.EqualTo(6.0).Within(1e-9),
                "a J2000 mount gets the J2000 target as-is — no precession");
            Assert.That(FormValue(put.Body, "Declination"), Is.EqualTo(45.0).Within(1e-9));
        }

        [Test]
        public async Task A_sync_reads_the_equatorial_system_on_demand_and_sends_a_J2000_target_untransformed() {
            var system = EquatorialSystemUnreadable;
            await using var box = ScriptedAlpacaDevice.Start(Mount(() => Volatile.Read(ref system)));
            using var svc = await ConnectAsync(box);
            Volatile.Write(ref system, J2000);

            Assert.That(await ((ITelescopeMediator)svc).Sync(J2000Target()), Is.True);
            var put = box.Puts.Single(p => p.Path.EndsWith("/synctocoordinates", StringComparison.Ordinal));
            Assert.That(FormValue(put.Body, "RightAscension"), Is.EqualTo(6.0).Within(1e-9));
            Assert.That(FormValue(put.Body, "Declination"), Is.EqualTo(45.0).Within(1e-9));
        }

        [Test]
        public async Task Capture_pointing_is_omitted_until_the_equatorial_system_is_known() {
            var system = EquatorialSystemUnreadable;
            await using var box = ScriptedAlpacaDevice.Start(Mount(() => Volatile.Read(ref system)));
            using var svc = await ConnectAsync(box);

            Assert.That(((ITelescopeMediator)svc).GetInfo().Coordinates, Is.Null,
                "RA/Dec in an unknown frame must not be labelled JNOW and written as EQUINOX 2000");
            Assert.That(CameraService.PointingFrom(((ITelescopeMediator)svc).GetInfo()), Is.Null);

            Volatile.Write(ref system, J2000);
            await WaitForAsync(() => ((ITelescopeMediator)svc).GetInfo().Coordinates is not null,
                TimeSpan.FromSeconds(15), "the pointing never appeared once the system became readable");
            var coords = ((ITelescopeMediator)svc).GetInfo().Coordinates;
            Assert.That(coords.Epoch, Is.EqualTo(Epoch.J2000));
            Assert.That(coords.RA, Is.EqualTo(6.0).Within(1e-9));
        }
    }
}
