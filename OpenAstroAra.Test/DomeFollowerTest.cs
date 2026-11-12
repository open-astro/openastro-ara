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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility.Notification;
using OpenAstroAra.Equipment.Equipment.MyDome;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer.SequenceItem.Dome;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
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
            // Set: a slew blocks in the driver until it is released (a move that holds the follower's gate).
            public ManualResetEventSlim? Hold;
            public int SlewCalls;
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
                Interlocked.Increment(ref SlewCalls);
                Hold?.Wait(TimeSpan.FromSeconds(10));
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

        private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<DomeFollower> {
            public readonly List<string> Lines = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                lock (Lines) { Lines.Add(formatter(state, exception)); }
            }
        }

        // A pinned clock (local sidereal time about 12 h at the site), so 10h/-45 and 14h/-20 sit either side
        // of the meridian, far more than the tolerance apart, whatever the time the suite runs.
        private static readonly DateTime Now = new(2026, 4, 20, 12, 0, 0, DateTimeKind.Utc);

        private static readonly DiscoveredDeviceDto Device = new("uid", "Roll dome", OpenAstroAra.Server.Contracts.DeviceType.Dome, "127.0.0.1", "127.0.0.1", 1, 0, false);

        private InMemoryProfileStore _store = null!;
        private StoreBackedProfileService _profile = null!;
        private ScriptedDome _dome = null!;
        private DomeService _svc = null!;
        private TelescopeInfo _mount = null!;
        private DomeFollower _follower = null!;
        private CapturingLogger _log = null!;

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
            _log = new CapturingLogger();
            _follower = new DomeFollower(_svc, mount.Object, new DomeSynchronization(_profile), _profile, _store, _log) {
                WaitPollInterval = TimeSpan.FromMilliseconds(20),
                UtcNow = () => Now,
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

        // The validator accepts it (the mount sits at the centre of a 1 m dome), but the mount's 1.5 m axis
        // puts the aperture outside the dome for every pointing.
        private DomeSettingsDto OutsideTheDome() => _store.GetDomeSettings() with {
            DomeRadiusMm = 1000,
            ScopePositionNorthSouthMm = 0,
            ScopePositionEastWestMm = 0,
            ScopePositionUpDownMm = 0,
            GemAxisMm = 1500,
            LateralAxisMm = 0,
            DecOffsetHorizontalMm = 0,
        };

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
            Assert.That(Math.Abs(Target() - _dome.Az), Is.GreaterThan(20.0), "the new pointing is past the tolerance");
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
        public async Task An_emergency_stop_turns_following_off_before_the_mounts_slew_ends() {
            await PlaceDomeAtAsync(Target());
            _mount.Slewing = true;
            await _follower.TickAsync();
            var estop = new EmergencyStopService(domeFollower: () => _follower);
            await estop.ExecuteAsync();
            _mount.Slewing = false; // the stop aborted the slew
            await _follower.TickAsync();
            Assert.That(_store.GetDomeSettings().FollowMount, Is.False);
            Assert.That(_dome.Slews, Is.Empty, "nothing turns after an emergency stop");
        }

        [Test]
        public async Task Turning_it_off_while_a_tick_waits_for_another_move_keeps_the_follower_alive() {
            await PlaceDomeAtAsync(Target() + 30);
            using var hold = new ManualResetEventSlim(false);
            _dome.Hold = hold;
            // A plan's Synchronize dome holds the move gate while the dome turns.
            var sync = _follower.TriggerTelescopeSync(CancellationToken.None);
            await EventuallyAsync(() => Volatile.Read(ref _dome.SlewCalls) == 1, "the sync slew");
            var tick = _follower.TickAsync();
            await Task.Delay(50);
            Assert.That(tick.IsCompleted, Is.False, "the tick waits for the gate");

            await _svc.SetFollowingAsync(false, CancellationToken.None);
            // An escaping cancellation would end the follower's loop for good.
            await Assert.ThatAsync(() => tick.WaitAsync(TimeSpan.FromSeconds(5)), Throws.Nothing);
            hold.Set();
            await sync.WaitAsync(TimeSpan.FromSeconds(5));
            _dome.Hold = null;

            await _svc.SetFollowingAsync(true, CancellationToken.None);
            await PlaceDomeAtAsync(Target() + 30);
            var before = _dome.Slews.Count;
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Has.Count.EqualTo(before + 1), "a later tick still turns the dome");
        }

        [Test]
        public async Task A_geometry_that_puts_the_scope_outside_the_dome_shows_one_pill_and_stops_following() {
            var sink = new RecordingSink();
            Notifier.Use(sink);
            _store.PutDomeSettings(OutsideTheDome());
            Assert.That(DomeSettingsValidator.Error(_store.GetDomeSettings()), Is.Null);
            await _follower.TickAsync();
            await _follower.TickAsync();
            _ = _follower.IsSynchronized; // an exposure's wait polls this
            Assert.That(sink.Seen.Count(s => s.IsError), Is.EqualTo(1), "one pill, not one per tick");
            Assert.That(_store.GetDomeSettings().FollowMount, Is.False);
            Assert.That(_dome.Slews, Is.Empty);
        }

        [Test]
        public async Task An_unknown_pier_side_keeps_following_and_sends_nothing() {
            // A German mount that reports no side of the pier (for a moment after a connect, or for good when its
            // driver has no SideOfPier) is not a geometry error: following stays on and the dome waits.
            Assert.That(_store.GetDomeSettings().MountType, Is.EqualTo("equatorial"));
            await PlaceDomeAtAsync(Target() + 30);
            _mount.SideOfPier = PierSide.pierUnknown;
            await _follower.TickAsync();
            Assert.That(_follower.IsSynchronized, Is.False, "an exposure keeps waiting");
            Assert.That(_store.GetDomeSettings().FollowMount, Is.True);
            Assert.That(_dome.Slews, Is.Empty);
        }

        [Test]
        public async Task Once_the_mount_reports_its_pier_side_the_next_tick_turns_the_dome() {
            await PlaceDomeAtAsync(Target() + 30);
            _mount.SideOfPier = PierSide.pierUnknown;
            await _follower.TickAsync();
            _ = _follower.IsSynchronized; // an exposure's wait polls this

            _mount.SideOfPier = PierSide.pierEast;
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Has.Count.EqualTo(1));
            Assert.That(_dome.Slews[0], Is.EqualTo(Target()).Within(0.5));
        }

        [Test]
        public async Task A_fork_mount_follows_without_a_pier_side() {
            // A fork on a wedge has no side of the pier, and its geometry does not ask for one.
            _store.PutDomeSettings(_store.GetDomeSettings() with { MountType = "fork_on_wedge" });
            _mount.SideOfPier = PierSide.pierUnknown;
            await PlaceDomeAtAsync(Target() + 30);
            await _follower.TickAsync();
            Assert.That(_dome.Slews, Has.Count.EqualTo(1));
            Assert.That(_dome.Slews[0], Is.EqualTo(Target()).Within(0.5));
        }

        [Test]
        public async Task A_geometry_error_other_than_the_scope_outside_the_dome_keeps_following() {
            // Only the radius check turns following off; any other error from the geometry waits for the next tick.
            var geometry = new Mock<IDomeSynchronization>();
            geometry.Setup(g => g.TargetDomeCoordinates(It.IsAny<Coordinates>(), It.IsAny<double>(), It.IsAny<Angle>(),
                    It.IsAny<Angle>(), It.IsAny<double>(), It.IsAny<PierSide>()))
                .Throws(new ArgumentException("bad input"));
            var mount = new Mock<ITelescopeMediator>();
            mount.Setup(m => m.GetInfo()).Returns(() => _mount);
            await PlaceDomeAtAsync(Target() + 30);
            using var follower = new DomeFollower(_svc, mount.Object, geometry.Object, _profile, _store) { UtcNow = () => Now };
            await follower.TickAsync();
            Assert.That(follower.IsSynchronized, Is.False, "an exposure keeps waiting");
            Assert.That(_store.GetDomeSettings().FollowMount, Is.True);
            Assert.That(_dome.Slews, Is.Empty);
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
        public async Task Synchronize_dome_without_a_dome_radius_sends_nothing() {
            // A profile that never set the geometry: a meridian flip's or a centering's dome sync sends nothing, and
            // the geometry's radius check (an error pill) is not reached.
            var sink = new RecordingSink();
            Notifier.Use(sink);
            _store.PutDomeSettings(DomeSettingsDto.Default);
            await PlaceDomeAtAsync(90);
            Assert.That(await _follower.TriggerTelescopeSync(CancellationToken.None), Is.False);
            Assert.That(_dome.Slews, Is.Empty);
            Assert.That(sink.Seen, Is.Empty, "a dome with no geometry set is not an error");
        }

        // Centering and the Synchronize dome item act only on a false result: a dome sync that cannot be worked
        // out, or that the dome does not finish, must not throw into them.
        [Test]
        public async Task Synchronize_dome_with_an_unknown_pier_side_is_false_and_logs_the_pier_side() {
            _store.PutDomeSettings(_store.GetDomeSettings() with { FollowMount = false });
            await PlaceDomeAtAsync(Target() + 30);
            _mount.SideOfPier = PierSide.pierUnknown;
            Assert.That(await _follower.TriggerTelescopeSync(CancellationToken.None), Is.False);
            Assert.That(_dome.Slews, Is.Empty);
            Assert.That(_log.Lines, Has.Some.Contains("side of the pier"), "the cause is the mount, not the geometry");
        }

        [Test]
        public async Task Synchronize_dome_with_the_scope_outside_the_dome_is_false_and_sends_nothing() {
            _store.PutDomeSettings(OutsideTheDome() with { FollowMount = false });
            Assert.That(await _follower.TriggerTelescopeSync(CancellationToken.None), Is.False);
            Assert.That(_dome.Slews, Is.Empty);
        }

        [Test]
        public async Task Synchronize_dome_whose_dome_slew_fails_is_false() {
            _store.PutDomeSettings(_store.GetDomeSettings() with { FollowMount = false });
            await PlaceDomeAtAsync(Target() + 30);
            _dome.FailSlews = true;
            Assert.That(await _follower.TriggerTelescopeSync(CancellationToken.None), Is.False);
        }

        [Test]
        public async Task The_Synchronize_dome_item_fails_with_its_own_message_when_the_dome_cannot_sync() {
            _store.PutDomeSettings(OutsideTheDome() with { FollowMount = false });
            var mount = new Mock<ITelescopeMediator>();
            mount.Setup(m => m.GetInfo()).Returns(() => _mount);
            var item = new SynchronizeDome(_svc, _follower, mount.Object);
            await Assert.ThatAsync(() => item.Execute(null!, CancellationToken.None),
                Throws.TypeOf<SequenceEntityFailedException>().With.Message.EqualTo("Synchronize dome operation didn't complete successfully"));
        }

        [Test]
        public async Task A_profile_brought_back_with_no_dome_radius_turns_following_off_and_sends_nothing() {
            // Start() refuses following without a dome radius, but selecting a profile restores the setting as saved.
            var sink = new RecordingSink();
            Notifier.Use(sink);
            _store.PutDomeSettings(DomeSettingsDto.Default with { FollowMount = true });
            await _follower.TickAsync();
            Assert.That(_store.GetDomeSettings().FollowMount, Is.False);
            Assert.That(_dome.Slews, Is.Empty);
            Assert.That(sink.Seen, Is.Empty, "a dome with no geometry set is not an error");
            Assert.That(_log.Lines, Has.Some.Contains("no dome radius"), "the log names the real cause");
        }

        [Test]
        public void The_store_geometry_reaches_the_profile_the_math_reads() {
            Assert.That(_profile.ActiveProfile.DomeSettings.DomeRadiusMm, Is.EqualTo(2500));
            Assert.That(_profile.ActiveProfile.DomeSettings.AzimuthToleranceDegrees, Is.EqualTo(2.0));
        }

        [Test]
        public void A_profile_put_keeps_the_stored_follow_setting() {
            // A panel opened while following was on saves geometry after the dome was parked.
            _store.PutDomeSettings(_store.GetDomeSettings() with { FollowMount = false });
            var stale = _store.GetDomeSettings() with { FollowMount = true, AzimuthToleranceDegrees = 3 };
            var result = ProfileEndpoints.PutDomeSettings(stale, _store);
            Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.Ok<DomeSettingsDto>>());
            Assert.That(_store.GetDomeSettings().FollowMount, Is.False, "only the follow command turns it on");
            Assert.That(_store.GetDomeSettings().AzimuthToleranceDegrees, Is.EqualTo(3), "the geometry is saved");

            _store.PutDomeSettings(_store.GetDomeSettings() with { FollowMount = true });
            ProfileEndpoints.PutDomeSettings(_store.GetDomeSettings() with { FollowMount = false }, _store);
            Assert.That(_store.GetDomeSettings().FollowMount, Is.True, "nor off");
        }

        [Test]
        public async Task A_profile_switch_that_drops_the_connected_dome_keeps_the_new_profiles_follow_setting() {
            // Profile A follows with this dome; the select has already put profile B's block (another dome,
            // saved following) in the store when the device switch drops A's dome.
            _store.PutDomeSettings(_store.GetDomeSettings() with { FollowMount = true, AzimuthToleranceDegrees = 3 });
            var otherDome = Device with { UniqueId = "uid-b", Name = "Dome B", HostName = "10.0.0.2", IpAddress = "10.0.0.2" };
            var selections = new Mock<IEquipmentSelectionStore>();
            selections.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { otherDome });
            var reconnector = new Mock<IEquipmentReconnector>();
            reconnector.Setup(r => r.GetConnectionStateAsync(OpenAstroAra.Server.Contracts.DeviceType.Dome, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ((IDomeMediator)_svc).GetInfo().Connected ? EquipmentConnectionState.Connected : EquipmentConnectionState.Disconnected);
            var services = new Mock<IServiceProvider>();
            services.Setup(s => s.GetService(typeof(IDomeService))).Returns(_svc);
            using var deviceSwitch = new ProfileDeviceSwitch(services.Object, selections.Object, reconnector.Object, _store,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ProfileDeviceSwitch>.Instance);

            await deviceSwitch.ApplyAsync(CancellationToken.None);

            await EventuallyAsync(() => !((IDomeMediator)_svc).GetInfo().Connected, "the switch's disconnect");
            Assert.That(_store.GetDomeSettings().FollowMount, Is.True, "profile B's saved Follow the mount survives the switch");
        }

        [Test]
        public void A_scope_outside_the_dome_is_refused_with_a_reason() {
            var outside = new DomeSettingsDto(DomeRadiusMm: 1000, ScopePositionNorthSouthMm: 900, ScopePositionEastWestMm: 900);
            Assert.That(DomeSettingsValidator.Error(outside), Does.Contain("outside the dome"));
            Assert.That(DomeSettingsValidator.Error(outside with { ScopePositionEastWestMm = 100 }), Is.Null);
        }
    }
}