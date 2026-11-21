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

    /// <summary>#1589 — the mediator's slew core (a sequence GoTo, a Center iteration, the meridian flip) refuses a
    /// target at or above the profile's clearance limit before anything reaches the mount, with no op fault, as it
    /// refuses an out-of-range one (#2075). Off (the default), the same GoTo goes to the mount. The site is 0.1° off
    /// the pole, so a target's altitude is its declination (±0.1°) at any time of day.</summary>
    [TestFixture]
    [Category("IO")] // loopback HTTP: not part of the quick unit run
    [Category("bench")]
    public class TelescopeClearanceSlewTest {

        private static Func<string, string?> Mount(double raHours, double decDeg) => path =>
            path.EndsWith("/equatorialsystem", StringComparison.Ordinal) ? "1"
            : path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/tracking", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/rightascension", StringComparison.Ordinal) ? raHours.ToString(CultureInfo.InvariantCulture)
            : path.EndsWith("/declination", StringComparison.Ordinal) ? decDeg.ToString(CultureInfo.InvariantCulture)
            : path.EndsWith("/sideofpier", StringComparison.Ordinal) ? "0"
            : path.EndsWith("/cansetpierside", StringComparison.Ordinal) ? "false"
            : null;

        private sealed class Rig : IAsyncDisposable {
            public required ScriptedAlpacaDevice Box { get; init; }
            public required TelescopeService Service { get; init; }
            public required List<EquipmentFaultEvent> Faults { get; init; }
            public required OpenAstroAra.Profile.Profile Profile { get; init; }

            public bool Put(string suffix) => Box.Puts.Any(p => p.Path.EndsWith(suffix, StringComparison.Ordinal));

            public List<EquipmentFaultEvent> FaultSnapshot() {
                lock (Faults) {
                    return [.. Faults];
                }
            }

            public async ValueTask DisposeAsync() {
                Service.Dispose();
                Profile.Dispose();
                await Box.DisposeAsync();
            }
        }

        private static SafetyPoliciesDto Safety(bool enabled) =>
            new(OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: true, MeridianPauseMin: 5, MeridianRecenter: true,
                MeridianRecalGuider: true, OnAltitudeLimit: "skip_target",
                ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: true) {
                ClearanceLimitEnabled = enabled, ClearanceMaxAltitudeDeg = 80,
            };

        private static async Task<Rig> StartAsync(bool limitOn) {
            var box = ScriptedAlpacaDevice.Start(Mount(6.0, 85.0));
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<OpenAstroAra.Server.Contracts.WsEvents.WsEvent>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var hub = new EquipmentFaultHub(ws.Object);
            var faults = new List<EquipmentFaultEvent>();
            hub.Subscribe(f => { lock (faults) { faults.Add(f); } });
            var profile = new OpenAstroAra.Profile.Profile();
            profile.AstrometrySettings.Longitude = 0;
            profile.TelescopeSettings.SettleTime = 0;
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns(profile);
            var store = new Mock<IProfileStore>();
            store.Setup(p => p.GetSafetyPolicies()).Returns(Safety(limitOn));
            store.Setup(p => p.GetSiteSettings()).Returns(new SiteSettingsDto("pole", 89.9, 0, 0, "UTC", false, 0, 4, 2.0, "astronomical"));
            var svc = new TelescopeService(faults: hub, profileService: profileService.Object, profileStore: store.Object) { SlewSettleMaxPolls = 30 };
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

        [Test]
        public async Task A_sequence_GoTo_above_the_clearance_limit_fails_before_the_mount_and_raises_no_fault() {
            await using var rig = await StartAsync(limitOn: true);
            var target = new Coordinates(Angle.ByHours(6.0), Angle.ByDegree(85.0), Epoch.JNOW);

            var ex = await Assert.ThrowsAsync<SequenceEntityFailedException>(
                () => rig.Service.SlewToCoordinatesAsync(target, CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("clearance limit").And.Contain("Nothing was sent to the mount"));
            Assert.That(rig.Put("/slewtocoordinatesasync"), Is.False, "no goto reached the mount");
            Assert.That(rig.FaultSnapshot(), Is.Empty, "a refused target must not feed abort_and_park");
        }

        [Test]
        public async Task A_flip_above_the_clearance_limit_is_refused_before_the_mount() {
            await using var rig = await StartAsync(limitOn: true);
            var target = new Coordinates(Angle.ByHours(6.0), Angle.ByDegree(85.0), Epoch.JNOW);

            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Service.MeridianFlip(target, CancellationToken.None));

            Assert.That(rig.Put("/slewtocoordinatesasync"), Is.False);
            Assert.That(rig.FaultSnapshot(), Is.Empty);
        }

        [Test]
        public async Task With_the_clearance_limit_off_a_sequence_GoTo_near_the_zenith_goes_to_the_mount() {
            await using var rig = await StartAsync(limitOn: false);
            var target = new Coordinates(Angle.ByHours(6.0), Angle.ByDegree(85.0), Epoch.JNOW);

            Assert.That(await rig.Service.SlewToCoordinatesAsync(target, CancellationToken.None), Is.True);
            Assert.That(rig.Put("/slewtocoordinatesasync"), Is.True, "the GoTo reached the mount");
        }
    }
}
