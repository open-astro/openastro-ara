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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Utility.Notification;
using OpenAstroAra.Equipment.Equipment.MyDome;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AscomShutter = ASCOM.Common.DeviceInterfaces.ShutterState;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1689 seam 1 — the dome follower, driven the way the rig does: a fake mount whose position, pier side
    /// and slewing flag change, a scripted Alpaca dome behind the real <see cref="DomeService"/>, and the
    /// commands the dome receives. The geometry math is the real <see cref="DomeSynchronization"/>
    /// (its own test is the reference); azimuths are compared to what it returns, never recomputed here.
    /// </summary>
    [TestFixture]
    [NonParallelizable] // Notifier.Use is process-global
    public class DomeFollowerTest {

        private sealed class ScriptedDome : FakeDeviceAdapter, IDomeDevice {
            public double Az = 10;
            public bool IsSlewing;
            public bool CanAz = true;
            public bool Parked;
            public AscomShutter Shutter = AscomShutter.Open;
            public bool FailSlews;
            public readonly List<double> Slews = new();
            public readonly List<string> Commands = new();
            public bool Slewing => IsSlewing;
            public double Azimuth => Az;
            public AscomShutter ShutterStatus => Shutter;
            public bool AtHome => false;
            public bool AtPark => Parked;
            public bool CanSetShutter => true;
            public bool CanSetAzimuth => CanAz;
            public bool CanSyncAzimuth => true;
            public bool CanPark => true;
            public bool CanFindHome => true;
            public bool CanSetPark => true;
            public void SlewToAzimuth(double azimuth) {
                if (FailSlews) {
                    throw new InvalidOperationException("dome motor fault");
                }
                Slews.Add(azimuth);
                Az = azimuth;
            }
            public void SyncToAzimuth(double azimuth) { }
            public void Park() { Commands.Add("park"); Parked = true; }
            public void SetPark() { }
            public void FindHome() { Commands.Add("home"); }
            public void OpenShutter() { }
            public void CloseShutter() { }
            public void AbortSlew() { Commands.Add("abort"); }
        }

        private sealed class RecordingSink : INotifierSink {
            public readonly List<(bool IsError, string? Header, string Message)> Seen = new();
            public void Notify(bool isError, string? header, string message) => Seen.Add((isError, header, message));
        }

        private static readonly DiscoveredDeviceDto Device = new("uid", "Roll dome", OpenAstroAra.Server.Contracts.DeviceType.Dome, "127.0.0.1", "127.0.0.1", 1, 0, false);

        private InMemoryProfileStore _store = null!;
        private StoreBackedProfileService _profile = null!;
        private ScriptedDome _dome = null!;
        private DomeService _svc = null!;
        private TelescopeInfo _mount = null!;
        private DomeFollower _follower = null!;

        [SetUp]
        public async Task SetUp() {
            _store = new InMemoryProfileStore();
            // Made-up site (the Sydney Opera House landmark), never a real one.
            _store.PutSiteSettings(_store.GetSiteSettings() with { LatitudeDeg = -33.856784, LongitudeDeg = 151.215297, ElevationM = 42 });
            _store.PutDomeSettings(new DomeSettingsDto(
                FollowMount: true, DomeRadiusMm: 2500, ScopePositionNorthSouthMm: 100, ScopePositionEastWestMm: 50,
                ScopePositionUpDownMm: 200, GemAxisMm: 250, LateralAxisMm: 30, DecOffsetHorizontalMm: 40,
                AzimuthToleranceDegrees: 2.0, DomeSyncTimeoutSeconds: 1));
            _profile = new StoreBackedProfileService(_store);
            _dome = new ScriptedDome();
            _svc = new DomeService { RefreshPeriod = TimeSpan.FromMilliseconds(20), DeviceFactory = _ => _dome };
            _mount = new TelescopeInfo {
                Connected = true,
                SideOfPier = PierSide.pierEast,
                Coordinates = new Coordinates(Angle.ByHours(10), Angle.ByDegree(-45), Epoch.JNOW),
            };
            var mount = new Mock<ITelescopeMediator>();
            mount.Setup(m => m.GetInfo()).Returns(() => _mount);
            _follower = new DomeFollower(_svc, mount.Object, new DomeSynchronization(_profile), _profile, _store) {
                WaitPollInterval = TimeSpan.FromMilliseconds(20),
            };
            await _svc.ConnectAsync(new ConnectRequestDto(Device), null, CancellationToken.None);
            await EventuallyAsync(() => ((IDomeMediator)_svc).GetInfo() is { Connected: true, CanSetAzimuth: true }, "the dome connect");
        }

        [TearDown]
        public void TearDown() {
            Notifier.Use(null);
            _follower.Dispose();
            _svc.Dispose();
            _dome.Dispose();
            _profile.Dispose();
        }

        private static async Task EventuallyAsync(Func<bool> condition, string what) {
            for (var i = 0; i < 300 && !condition(); i++) {
                await Task.Delay(20);
            }
            Assert.That(condition(), Is.True, "timed out waiting for " + what);
        }

        // The cached dome reading follows the device on the lifecycle's refresh tick.
        private Task CachedAzimuthAsync() =>
            EventuallyAsync(() => Math.Abs(((IDomeMediator)_svc).GetInfo().Azimuth - _dome.Az) < 0.001, "the dome reading");

        private double Target() => _follower.GetSynchronizedDomeCoordinates(_mount).Azimuth.Degree;

        private async Task PlaceDomeAtAsync(double azimuth) {
            _dome.Az = azimuth;
            await CachedAzimuthAsync();
        }

        [Test]
        public async Task Off_sends_no_dome_commands() {
            _store.PutDomeSettings(_store.GetDomeSettings() with { FollowMount = false });
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Is.Empty);
            Assert.That(_follower.IsFollowing, Is.False);
        }

        [Test]
        public async Task After_the_mounts_slew_ends_the_dome_turns_once_to_the_computed_azimuth() {
            await PlaceDomeAtAsync(Target());
            _mount.Slewing = true;
            _mount.Coordinates = new Coordinates(Angle.ByHours(14), Angle.ByDegree(-20), Epoch.JNOW);
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Is.Empty, "not during the slew");

            _mount.Slewing = false;
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Has.Count.EqualTo(1));
            Assert.That(_dome.Slews[0], Is.EqualTo(Target()).Within(0.5));

            await CachedAzimuthAsync();
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Has.Count.EqualTo(1), "the dome is there; nothing more to send");
        }

        [Test]
        public async Task During_the_mounts_slew_the_dome_turns_when_the_profile_says_so() {
            _store.PutDomeSettings(_store.GetDomeSettings() with { SynchronizeDuringMountSlew = true });
            await PlaceDomeAtAsync(Target());
            _mount.Slewing = true;
            _mount.Coordinates = new Coordinates(Angle.ByHours(14), Angle.ByDegree(-20), Epoch.JNOW);
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task A_drift_under_the_tolerance_sends_nothing_and_past_it_one_slew() {
            await PlaceDomeAtAsync(Target() + 1.0);
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Is.Empty);

            await PlaceDomeAtAsync(Target() + 5.0);
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Has.Count.EqualTo(1));
            Assert.That(_dome.Slews[0], Is.EqualTo(Target()).Within(0.5));
        }

        [Test]
        public async Task A_change_of_pier_side_sends_one_slew_even_inside_the_tolerance() {
            _store.PutDomeSettings(_store.GetDomeSettings() with { AzimuthToleranceDegrees = 10 });
            await PlaceDomeAtAsync(Target());
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Is.Empty);

            _mount.SideOfPier = PierSide.pierWest;
            Assert.That(Math.Abs(Target() - _dome.Az), Is.LessThan(10.0), "the flip moves the slit less than the tolerance");
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Has.Count.EqualTo(1));
            Assert.That(_dome.Slews[0], Is.EqualTo(Target()).Within(0.5));
        }

        [Test]
        public async Task A_parked_mount_sends_nothing() {
            await PlaceDomeAtAsync(Target() + 30);
            _mount.AtPark = true;
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Is.Empty);
        }

        [Test]
        public async Task A_dome_that_is_slewing_or_moving_its_shutter_is_left_alone() {
            await PlaceDomeAtAsync(Target() + 30);
            _dome.IsSlewing = true;
            await EventuallyAsync(() => ((IDomeMediator)_svc).GetInfo().Slewing, "the dome slewing");
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Is.Empty, "a dome already turning (parking, homing, or a slew)");

            _dome.IsSlewing = false;
            _dome.Shutter = AscomShutter.Opening;
            await EventuallyAsync(() => !((IDomeMediator)_svc).GetInfo().Slewing, "the slew ending");
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Is.Empty, "the shutter is moving");
        }

        [Test]
        public async Task A_disconnected_mount_or_dome_sends_nothing() {
            await PlaceDomeAtAsync(Target() + 30);
            _mount.Connected = false;
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Is.Empty);

            _mount.Connected = true;
            await _svc.DisconnectAsync(null, CancellationToken.None);
            await EventuallyAsync(() => !((IDomeMediator)_svc).GetInfo().Connected, "the disconnect");
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Is.Empty);
            Assert.That(_follower.IsFollowing, Is.False);
        }

        [Test]
        public async Task A_manual_slew_turns_following_off() {
            await _svc.SlewAsync(new DomeSlewRequestDto(120), null, CancellationToken.None);
            Assert.That(_store.GetDomeSettings().FollowMount, Is.False);
            Assert.That(_follower.IsFollowing, Is.False);
        }

        [Test]
        public async Task Park_home_abort_and_disconnect_turn_following_off() {
            var turnOn = () => _store.PutDomeSettings(_store.GetDomeSettings() with { FollowMount = true });
            await _svc.ParkAsync(null, CancellationToken.None);
            Assert.That(_store.GetDomeSettings().FollowMount, Is.False, "park");
            turnOn();
            await _svc.FindHomeAsync(null, CancellationToken.None);
            Assert.That(_store.GetDomeSettings().FollowMount, Is.False, "home");
            turnOn();
            await _svc.AbortSlewAsync(null, CancellationToken.None);
            Assert.That(_store.GetDomeSettings().FollowMount, Is.False, "abort");
            turnOn();
            await _svc.DisconnectAsync(null, CancellationToken.None);
            Assert.That(_store.GetDomeSettings().FollowMount, Is.False, "disconnect");
        }

        [Test]
        public async Task The_sequencers_dome_moves_turn_following_off_too() {
            var mediator = _svc;
            Assert.That(mediator.IsFollowingScope, Is.True);
            await mediator.SlewToAzimuth(200, CancellationToken.None);
            Assert.That(mediator.IsFollowingScope, Is.False, "a plan's Slew dome step");
        }

        [Test]
        public async Task An_unsafe_reaction_turns_following_off() {
            var monitor = new Mock<ISafetyMonitorService>();
            monitor.Setup(m => m.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SafetyMonitorDto("sm1", "Cloudwatcher", EquipmentConnectionState.Connected, false, DateTimeOffset.UtcNow.ToString("O")));
            using var safety = new SafetyReactionService(
                safetyMonitor: monitor.Object,
                profiles: _store,
                domeFollower: () => _follower);
            await safety.TickAsync();
            Assert.That(_store.GetDomeSettings().FollowMount, Is.False);
        }

        [Test]
        public void The_setting_is_restored_on_daemon_start() {
            // A second follower over the same store, as a restarted daemon builds it.
            using var restarted = new DomeFollower(_svc, new Mock<ITelescopeMediator>().Object,
                new DomeSynchronization(_profile), _profile, _store);
            Assert.That(restarted.IsFollowing, Is.True);
        }

        [Test]
        public async Task A_dome_that_cannot_set_its_azimuth_refuses_to_start() {
            _store.PutDomeSettings(_store.GetDomeSettings() with { FollowMount = false });
            _dome.CanAz = false;
            // CanSetAzimuth is read once per connection: reconnect so the service sees the new capability.
            await _svc.DisconnectAsync(null, CancellationToken.None);
            await _svc.ConnectAsync(new ConnectRequestDto(Device), null, CancellationToken.None);
            await EventuallyAsync(() => ((IDomeMediator)_svc).GetInfo().Connected, "the reconnect");
            await EventuallyAsync(() => !((IDomeMediator)_svc).GetInfo().CanSetAzimuth && ((IDomeMediator)_svc).GetInfo().CanSetShutter, "the capabilities");

            await Assert.ThatAsync(() => _svc.SetFollowingAsync(true, CancellationToken.None),
                Throws.InvalidOperationException.With.Message.Contains("cannot turn to an azimuth"));
            Assert.That(await ((IDomeMediator)_svc).EnableFollowing(CancellationToken.None), Is.False);
            Assert.That(_store.GetDomeSettings().FollowMount, Is.False);
        }

        [Test]
        public async Task Turning_it_on_without_a_dome_radius_is_refused_with_the_reason() {
            _store.PutDomeSettings(DomeSettingsDto.Default);
            await Assert.ThatAsync(() => _svc.SetFollowingAsync(true, CancellationToken.None),
                Throws.InvalidOperationException.With.Message.Contains("dome radius"));
        }

        [Test]
        public async Task The_command_turns_it_on_and_off_and_the_status_says_so() {
            _store.PutDomeSettings(_store.GetDomeSettings() with { FollowMount = false });
            Assert.That((await _svc.GetAsync(CancellationToken.None))!.Runtime.Following, Is.False);

            Assert.That(await _svc.SetFollowingAsync(true, CancellationToken.None), Is.True);
            Assert.That(_store.GetDomeSettings().FollowMount, Is.True);
            Assert.That((await _svc.GetAsync(CancellationToken.None))!.Runtime.Following, Is.True);

            Assert.That(await _svc.SetFollowingAsync(false, CancellationToken.None), Is.False);
            Assert.That((await _svc.GetAsync(CancellationToken.None))!.Runtime.Following, Is.False);
        }

        [Test]
        public async Task A_failed_follow_slew_shows_one_error_pill_naming_the_dome() {
            var sink = new RecordingSink();
            Notifier.Use(sink);
            await PlaceDomeAtAsync(Target() + 30);
            _dome.FailSlews = true;
            await _follower.TickAsync();
            await _follower.TickAsync();
            var errors = sink.Seen.Where(s => s.IsError).ToList();
            Assert.That(errors, Has.Count.EqualTo(1), "one pill, not one per retry");
            Assert.That(errors[0].Message, Does.Contain("Roll dome"));
            Assert.That(errors[0].Message, Does.Contain("follow the mount"));
        }

        [Test]
        public async Task An_exposure_waits_for_the_dome_to_arrive() {
            await PlaceDomeAtAsync(Target() + 30);
            var waiting = _follower.WaitForDomeSynchronization(CancellationToken.None);
            await Task.Delay(100);
            Assert.That(waiting.IsCompleted, Is.False, "the dome is 30 degrees off");

            await _follower.TickAsync();
            await CachedAzimuthAsync();
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(_dome.Slews, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task The_wait_gives_up_at_the_profiles_sync_timeout() {
            await PlaceDomeAtAsync(Target() + 30);
            var started = DateTime.UtcNow;
            await _follower.WaitForDomeSynchronization(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(DateTime.UtcNow - started, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900)));
        }

        [Test]
        public async Task Synchronize_dome_turns_the_dome_once_without_turning_following_on() {
            _store.PutDomeSettings(_store.GetDomeSettings() with { FollowMount = false });
            await PlaceDomeAtAsync(Target() + 30);
            Assert.That(await _follower.TriggerTelescopeSync(CancellationToken.None), Is.True);
            Assert.That(_dome.Slews, Has.Count.EqualTo(1));
            Assert.That(_dome.Slews[0], Is.EqualTo(Target()).Within(0.5));
            Assert.That(_store.GetDomeSettings().FollowMount, Is.False);
        }

        [Test]
        public void The_store_geometry_reaches_the_profile_the_math_reads() {
            Assert.That(_profile.ActiveProfile.DomeSettings.DomeRadiusMm, Is.EqualTo(2500));
            Assert.That(_profile.ActiveProfile.DomeSettings.AzimuthToleranceDegrees, Is.EqualTo(2.0));
        }

        [Test]
        public void A_scope_outside_the_dome_is_refused_with_a_reason() {
            var outside = new DomeSettingsDto(DomeRadiusMm: 1000, ScopePositionNorthSouthMm: 900, ScopePositionEastWestMm: 900);
            Assert.That(DomeSettingsValidator.Error(outside), Does.Contain("outside the dome"));
            Assert.That(DomeSettingsValidator.Error(outside with { ScopePositionEastWestMm = 100 }), Is.Null);
        }
    }
}