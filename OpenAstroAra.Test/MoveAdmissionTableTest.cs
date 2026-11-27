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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2139 — Move Admission, one table: for each asker's move and each pre-move rule, is the move refused when only that
    /// rule is broken. Written from the behaviour before the module existed and green on both sides of the move (record
    /// 0026). Driven through the real Telescope service on the in-memory fake mount, as each asker reaches it.
    /// </summary>
    [TestFixture]
    public class MoveAdmissionTableTest {

        /// <summary>An asker's move, as it reaches the Telescope service.</summary>
        public enum Entry { PersonSlew, PersonPark, PersonHome, SessionSlew, CenterSlew, FlipSlew, SessionPark, SessionHome, ShutdownPark, ShutdownHome, FlipPark, SafetyPark }

        /// <summary>The one rule broken in a case (None: nothing is).</summary>
        public enum Broken { None, Range, Parked, Pose, MotionOwner, PolarAlignMoving, MountHeld, Horizon, Counterweight }

        private static readonly Entry[] Slews = [Entry.PersonSlew, Entry.SessionSlew, Entry.CenterSlew, Entry.FlipSlew];

        /// <summary>The table (record 0026): the rules that refuse each asker's move. A rule not listed lets the move go.
        /// #2140 — the position limits (Horizon, Counterweight) bind the Session's, Center's and the flip's GoTo; a person's
        /// park and home are refused while something holds the mount (MountHeld, Rig Occupancy); a home is refused from
        /// park (Parked).</summary>
        private static readonly Dictionary<Entry, Broken[]> Refused = new() {
            [Entry.PersonSlew] = [Broken.Range, Broken.Pose, Broken.MotionOwner, Broken.PolarAlignMoving, Broken.MountHeld, Broken.Horizon, Broken.Counterweight],
            [Entry.PersonPark] = [Broken.Pose, Broken.MotionOwner, Broken.PolarAlignMoving, Broken.MountHeld],
            [Entry.PersonHome] = [Broken.Parked, Broken.Pose, Broken.MotionOwner, Broken.PolarAlignMoving, Broken.MountHeld],
            [Entry.SessionSlew] = [Broken.Range, Broken.Parked, Broken.Pose, Broken.MotionOwner, Broken.Horizon, Broken.Counterweight],
            [Entry.CenterSlew] = [Broken.Range, Broken.Parked, Broken.Pose, Broken.MotionOwner, Broken.Horizon, Broken.Counterweight],
            [Entry.FlipSlew] = [Broken.Range, Broken.Parked, Broken.Pose, Broken.MotionOwner, Broken.Horizon, Broken.Counterweight],
            [Entry.SessionPark] = [Broken.Pose, Broken.MotionOwner],
            [Entry.SessionHome] = [Broken.Parked, Broken.Pose, Broken.MotionOwner],
            [Entry.ShutdownPark] = [Broken.Pose, Broken.MotionOwner],
            [Entry.ShutdownHome] = [Broken.Parked, Broken.Pose, Broken.MotionOwner],
            [Entry.FlipPark] = [Broken.Pose, Broken.MotionOwner],
            [Entry.SafetyPark] = [Broken.Pose],
        };

        public static IEnumerable<TestCaseData> Cases() {
            foreach (var entry in Enum.GetValues<Entry>()) {
                foreach (var broken in Enum.GetValues<Broken>()) {
                    // A park or a home has no target: the target rules do not apply to it.
                    if (!Slews.Contains(entry) && broken is Broken.Range or Broken.Horizon or Broken.Counterweight) {
                        continue;
                    }
                    yield return new TestCaseData(entry, broken, Refused[entry].Contains(broken)).SetName($"{entry} with {broken} broken");
                }
            }
        }

        private static readonly DiscoveredDeviceDto Mount = new(
            UniqueId: "mount-1", Name: "Bench Mount", Type: DeviceType.Telescope,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        // Latitude 40°, longitude 0, a 20° floor.
        private static Mock<IProfileStore> Site() {
            var store = new Mock<IProfileStore>();
            store.Setup(p => p.GetSiteSettings()).Returns(new SiteSettingsDto("site", 40.0, 0, 0, "UTC", false, 20, 4, 2.0, "astronomical"));
            store.Setup(p => p.GetCustomHorizon()).Returns(new CustomHorizonDto(Array.Empty<CustomHorizonPointDto>()));
            return store;
        }

        private static double RaAtHourAngle(double hourAngleHours) {
            var lstHours = SiteAstrometry.LocalSiderealTimeDeg(DateTimeOffset.UtcNow, 0) / 15.0;
            return ((lstHours - hourAngleHours) % 24.0 + 24.0) % 24.0;
        }

        /// <summary>The target for a case: 70° up on the meridian; 10° up (under the floor); below the pole an hour from
        /// the lower meridian (30° up, counterweight up); or a declination that is not a number.</summary>
        private static (double RaHours, double DecDeg) Target(Broken broken) => broken switch {
            Broken.Horizon => (RaAtHourAngle(0), -40),
            Broken.Counterweight => (RaAtHourAngle(-11), 80),
            // Not finite: a Dec past the pole is folded back by the coordinate type before the mediator reads it.
            Broken.Range => (RaAtHourAngle(0), double.NaN),
            _ => (RaAtHourAngle(0), 60),
        };

        private static async Task WaitForAsync(Func<bool> condition, string what) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition()) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail($"timed out waiting for {what}");
                }
                await Task.Delay(10);
            }
        }

        [TestCaseSource(nameof(Cases))]
        public async Task The_move_is_refused_only_by_the_rules_its_asker_is_checked_for(Entry entry, Broken broken, bool refused) {
            FakeTelescopeDevice? fake = null;
            var occupancy = HeldBy.Mount(RigOccupancy.Session, () => broken == Broken.MountHeld);
            var polarAlign = new Mock<IPolarAlignService>();
            polarAlign.SetupGet(p => p.IsMovingMount).Returns(broken == Broken.PolarAlignMoving);
            using var svc = new TelescopeService(profileStore: Site().Object,
                    occupancyResolver: () => occupancy, polarAlignResolver: () => polarAlign.Object) {
                RefreshPeriod = TimeSpan.FromMilliseconds(20),
                StopGuidingBeforeMoveTimeout = TimeSpan.FromMilliseconds(50),
                DeviceFactory = _ => {
                    fake = new FakeTelescopeDevice { ReportsArrival = true, ArrivesAtTarget = true };
                    if (broken == Broken.Pose) { fake.ReportsAtHome = true; fake.Dec = 90; }
                    if (broken == Broken.Parked) { fake.AtParkScript = () => true; }
                    return fake;
                },
            };
            await svc.ConnectAsync(new ConnectRequestDto(Mount), null, CancellationToken.None);
            await WaitForAsync(() => svc.IsCleanlyConnected && svc.GetAsync(CancellationToken.None).Result?.Capabilities is not null, "the first clean read");
            if (broken == Broken.Pose) {
                await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.Runtime?.PositionUnverified == true, "the unverified position");
            }
            if (broken == Broken.Parked) {
                await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.Runtime?.Parked == true, "the parked mount");
            }
            using var hold = broken == Broken.MotionOwner ? svc.AcquireMotion(MotionOwners.PolarAlign) : null;
            var (ra, dec) = Target(broken);
            var before = Sent(entry, fake!);

            var answered = await RunAsync(entry, svc, ra, dec).WaitAsync(TimeSpan.FromSeconds(30));
            var sent = answered && await SentWithinAsync(() => Sent(entry, fake!) > before);

            Assert.That(!sent, Is.EqualTo(refused), refused ? "the move went out" : "the move was refused");
        }

        /// <summary>Runs the entry; false when it was refused (a throw, a false, or a 409).</summary>
        private static async Task<bool> RunAsync(Entry entry, TelescopeService svc, double ra, double dec) {
            var mediator = svc;
            var target = new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Hours);
            var progress = new Progress<ApplicationStatus>();
            try {
                switch (entry) {
                    case Entry.PersonSlew:
                        var result = await EquipmentEndpoints.SlewAsync(new SlewRequestDto(ra, dec), null, svc, CancellationToken.None);
                        return result is not Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult;
                    case Entry.PersonPark:
                        await EquipmentEndpoints.TelescopeParkAsync(null, null, svc, CancellationToken.None);
                        return true;
                    case Entry.PersonHome:
                        await EquipmentEndpoints.TelescopeFindHomeAsync(null, svc, CancellationToken.None);
                        return true;
                    case Entry.SessionSlew:
                        return await mediator.SlewToCoordinatesAsync(target, MoveAsker.Session, CancellationToken.None);
                    case Entry.CenterSlew:
                        return await mediator.SlewToCoordinatesAsync(target, MoveAsker.Center, CancellationToken.None);
                    case Entry.FlipSlew:
                        return await mediator.MeridianFlip(target, CancellationToken.None);
                    case Entry.SessionPark:
                        return await mediator.ParkTelescope(progress, MoveAsker.Session, CancellationToken.None);
                    case Entry.ShutdownPark:
                        return await mediator.ParkTelescope(progress, MoveAsker.Shutdown, CancellationToken.None);
                    case Entry.FlipPark:
                        return await mediator.ParkTelescope(progress, MoveAsker.MeridianFlip, CancellationToken.None);
                    case Entry.SessionHome:
                        return await mediator.FindHome(progress, MoveAsker.Session, CancellationToken.None);
                    case Entry.ShutdownHome:
                        return await mediator.FindHome(progress, MoveAsker.Shutdown, CancellationToken.None);
                    case Entry.SafetyPark:
                        await svc.ParkAsync(new ParkRequestDto("safety.unsafe"), null, CancellationToken.None);
                        return true;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(entry), entry, null);
                }
            } catch (Exception ex) when (ex is not AssertionException) {
                return false;
            }
        }

        private static int Sent(Entry entry, FakeTelescopeDevice fake) => entry switch {
            Entry.PersonSlew or Entry.SessionSlew or Entry.CenterSlew or Entry.FlipSlew => fake.Slews,
            Entry.PersonHome or Entry.SessionHome or Entry.ShutdownHome => fake.Homes,
            _ => fake.Parks,
        };

        /// <summary>True once the move reached the fake mount (a REST move is sent in the background), within 3 s.</summary>
        private static async Task<bool> SentWithinAsync(Func<bool> sent) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (!sent()) {
                if (DateTime.UtcNow > deadline) {
                    return false;
                }
                await Task.Delay(10);
            }
            return true;
        }
    }
}
