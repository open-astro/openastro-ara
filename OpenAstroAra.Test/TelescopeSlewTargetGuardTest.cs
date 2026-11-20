#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#2075 — on rig A, Ara sent slews with Dec past ±90° to the mount; the mount refused them
    /// and each refusal became a telescope op fault that counts toward abort_and_park. The mediator slew
    /// core now refuses an out-of-range target before anything reaches the mount, with no fault, and
    /// the mount's over-the-pole reading is folded so the centering and flip targets stay in range and
    /// a goto to a folded target settles.</summary>
    [TestFixture]
    [Category("IO")] // loopback HTTP: not part of the quick unit run
    [Category("bench")]
    public class TelescopeSlewTargetGuardTest {

        private const double Longitude = 0.0;

        // A JNOW mount (no precession natives needed) that reports the given pointing.
        private static Func<string, string?> Mount(double raHours, double decDeg, string sideOfPier = "0", string canSetPierSide = "false") => path =>
            path.EndsWith("/equatorialsystem", StringComparison.Ordinal) ? "1"
            : path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/tracking", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/cansync", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/rightascension", StringComparison.Ordinal) ? raHours.ToString(CultureInfo.InvariantCulture)
            : path.EndsWith("/declination", StringComparison.Ordinal) ? decDeg.ToString(CultureInfo.InvariantCulture)
            : path.EndsWith("/sideofpier", StringComparison.Ordinal) ? sideOfPier
            : path.EndsWith("/cansetpierside", StringComparison.Ordinal) ? canSetPierSide
            : null;

        private sealed class Rig : IAsyncDisposable {
            public required ScriptedAlpacaDevice Box { get; init; }
            public required TelescopeService Service { get; init; }
            public required List<EquipmentFaultEvent> Faults { get; init; }
            public OpenAstroAra.Profile.Profile? Profile { get; init; }
            public ITelescopeMediator Mediator => Service;

            public List<EquipmentFaultEvent> FaultSnapshot() {
                lock (Faults) {
                    return [.. Faults];
                }
            }

            public bool Put(string suffix) => Box.Puts.Any(p => p.Path.EndsWith(suffix, StringComparison.Ordinal));

            public async ValueTask DisposeAsync() {
                Service.Dispose();
                Profile?.Dispose();
                await Box.DisposeAsync();
            }
        }

        private static async Task<Rig> StartAsync(double raHours, double decDeg, bool useSideOfPier = false, string canSetPierSide = "false") {
            var box = ScriptedAlpacaDevice.Start(Mount(raHours, decDeg, canSetPierSide: canSetPierSide));
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<OpenAstroAra.Server.Contracts.WsEvents.WsEvent>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var hub = new EquipmentFaultHub(ws.Object);
            var faults = new List<EquipmentFaultEvent>();
            hub.Subscribe(f => { lock (faults) { faults.Add(f); } });
            var profile = new OpenAstroAra.Profile.Profile();
            profile.AstrometrySettings.Longitude = Longitude;
            profile.MeridianFlipSettings.UseSideOfPier = useSideOfPier;
            profile.TelescopeSettings.SettleTime = 0;
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns(profile);
            // ~3 s settle bound: a guard that fails open would otherwise sit out the 10-minute default.
            var svc = new TelescopeService(faults: hub, profileService: profileService.Object) { SlewSettleMaxPolls = 30 };
            var device = new DiscoveredDeviceDto(
                UniqueId: "mount-under-test", Name: "Bench mount", Type: DeviceType.Telescope,
                HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
                AlpacaDeviceNumber: 0, UseHttps: false);
            await svc.ConnectAsync(new ConnectRequestDto(device), null, CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline) {
                var info = ((ITelescopeMediator)svc).GetInfo();
                if (info.Connected && info.Coordinates is not null) {
                    break;
                }
                await Task.Delay(50);
            }
            Assert.That(((ITelescopeMediator)svc).GetInfo().Coordinates, Is.Not.Null, "mount never connected and reported its position");
            return new Rig { Box = box, Service = svc, Faults = faults, Profile = profile };
        }

        [TestCase(90.61)]
        [TestCase(-90.6)]
        [TestCase(double.NaN)]
        public async Task A_slew_with_dec_past_the_pole_is_refused_before_the_mount_and_raises_no_fault(double dec) {
            await using var rig = await StartAsync(6.0, 45.0);
            var target = new Coordinates(Angle.ByHours(6.0), Angle.ByDegree(dec), Epoch.JNOW);

            var ex = await Assert.ThrowsAsync<SequenceEntityFailedException>(
                () => rig.Mediator.SlewToCoordinatesAsync(target, CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("Nothing was sent to the mount"));
            Assert.That(rig.Put("/slewtocoordinatesasync"), Is.False, "no goto reached the mount");
            Assert.That(rig.Put("/tracking"), Is.False, "the pre-slew tracking enable did not run either");
            Assert.That(rig.FaultSnapshot(), Is.Empty, "a refused target must not feed abort_and_park");
        }

        [Test]
        public async Task A_slew_to_the_pole_itself_goes_to_the_mount() {
            await using var rig = await StartAsync(6.0, 90.0);
            var target = new Coordinates(Angle.ByHours(6.0), Angle.ByDegree(90.0), Epoch.JNOW);

            Assert.That(await rig.Mediator.SlewToCoordinatesAsync(target, CancellationToken.None), Is.True);
            Assert.That(rig.Put("/slewtocoordinatesasync"), Is.True);
            Assert.That(rig.FaultSnapshot(), Is.Empty);
        }

        [Test]
        public async Task A_sync_with_dec_past_the_pole_is_not_synced_and_sends_nothing() {
            await using var rig = await StartAsync(6.0, 45.0);
            var target = new Coordinates(Angle.ByHours(6.0), Angle.ByDegree(90.6), Epoch.JNOW);

            Assert.That(await rig.Mediator.Sync(target, CancellationToken.None), Is.False);
            Assert.That(rig.Put("/synctocoordinates"), Is.False);
        }

        [Test]
        public async Task A_flip_with_dec_past_the_pole_is_refused_before_the_pier_side_write() {
            // Target 3 h east of the meridian on a mount that reports east: with side of pier on and a
            // capable mount, a valid flip writes SideOfPier before the goto. A refused one writes nothing.
            var ra = (SiteAstrometry.LocalSiderealTimeDeg(DateTimeOffset.UtcNow, Longitude) / 15.0 + 3.0) % 24.0;
            await using var rig = await StartAsync(ra, 45.0, useSideOfPier: true, canSetPierSide: "true");
            var target = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(90.6), Epoch.JNOW);

            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Mediator.MeridianFlip(target, CancellationToken.None));

            Assert.That(rig.Put("/sideofpier"), Is.False, "some mounts start moving on the pier-side write");
            Assert.That(rig.Put("/slewtocoordinatesasync"), Is.False);
            Assert.That(rig.FaultSnapshot(), Is.Empty);
        }

        [Test]
        public async Task A_goto_to_a_folded_target_settles_on_a_mount_that_reports_over_the_pole() {
            // The synced HAE29C reported its Dec axis past the pole. After a goto to (18h, 89.4°) it
            // reports (6h, 90.6°): the same sky point. A per-axis compare saw a 180° RA miss and
            // ended in a StallTimeout fault.
            await using var rig = await StartAsync(6.0, 90.6);
            var target = new Coordinates(Angle.ByHours(18.0), Angle.ByDegree(89.4), Epoch.JNOW);

            Assert.That(await rig.Mediator.SlewToCoordinatesAsync(target, CancellationToken.None), Is.True);
            Assert.That(rig.FaultSnapshot(), Is.Empty, "no StallTimeout fault");
        }

        [Test]
        public async Task GetCurrentPosition_folds_a_reading_over_the_pole() {
            await using var rig = await StartAsync(6.0, 90.9);

            var position = rig.Mediator.GetCurrentPosition();

            Assert.That(position.RA, Is.EqualTo(18.0).Within(1e-9));
            Assert.That(position.Dec, Is.EqualTo(89.1).Within(1e-9));
            Assert.That(position.Epoch, Is.EqualTo(Epoch.JNOW));
        }
    }
}
