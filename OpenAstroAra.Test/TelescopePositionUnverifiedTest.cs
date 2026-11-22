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
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#2070 — after a power loss an iOptron HAE29C reports Home (Dec 90) wherever it points. Ara marks
    /// that position unverified and refuses every move that trusts it (GoTo, Park, the flip, polar alignment's
    /// motor moves) until Home is confirmed; Sync, the pad, Stop and (#2173) the user's Home still work. Against a fake
    /// mount: no test here dials a port.</summary>
    [TestFixture]
    public class TelescopePositionUnverifiedTest {

        private static readonly DiscoveredDeviceDto Mount = new(
            UniqueId: "mount-1", Name: "Bench Mount", Type: DeviceType.Telescope,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        private sealed class FaultSink : IEquipmentFaultSink {
            private readonly List<EquipmentFaultEvent> _faults = new();
            public void Publish(EquipmentFaultEvent fault) { lock (_faults) { _faults.Add(fault); } }
            public List<EquipmentFaultEvent> Faults { get { lock (_faults) { return _faults.ToList(); } } }
        }

        private sealed class MemoryPoseStore : IMountPoseStore {
            public readonly Dictionary<string, string> Unverified = new();
            public string? UnverifiedReason(string deviceId) { lock (Unverified) { return Unverified.GetValueOrDefault(deviceId); } }
            public void Store(string deviceId, string? reason) {
                lock (Unverified) {
                    if (reason is null) { Unverified.Remove(deviceId); } else { Unverified[deviceId] = reason; }
                }
            }
        }

        private readonly List<FakeTelescopeDevice> _devices = new();
        private FaultSink _faults = null!;
        private Action<FakeTelescopeDevice>? _configure;

        [SetUp]
        public void SetUp() {
            _devices.Clear();
            _faults = new FaultSink();
            _configure = null;
        }

        private FakeTelescopeDevice Current { get { lock (_devices) { return _devices[^1]; } } }

        private sealed class ManualClock : TimeProvider {
            private long _ticks = new DateTimeOffset(2026, 11, 19, 3, 0, 0, TimeSpan.Zero).UtcTicks;
            public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
            public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        }

        private TelescopeService Build(IMountPoseStore? store = null, IProfileService? profile = null, TimeSpan? refresh = null, TimeProvider? clock = null,
                TimeSpan? poseRead = null) =>
            new(faults: _faults, profileService: profile, poseStore: store) {
                PoseClock = clock ?? TimeProvider.System,
                PoseReadTimeout = poseRead ?? TimeSpan.FromSeconds(10),
                RefreshPeriod = refresh ?? TimeSpan.FromMilliseconds(20),
                StopGuidingBeforeMoveTimeout = TimeSpan.FromMilliseconds(50),
                HomeSearchPollInterval = TimeSpan.FromMilliseconds(20),
                DeviceFactory = _ => {
                    var fake = new FakeTelescopeDevice();
                    _configure?.Invoke(fake);
                    lock (_devices) { _devices.Add(fake); }
                    return fake;
                },
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

        private static TelescopeStateDto? Runtime(TelescopeService s) => s.GetAsync(CancellationToken.None).Result?.Runtime;

        private static async Task ConnectAsync(TelescopeService s) {
            await s.ConnectAsync(new ConnectRequestDto(Mount), null, CancellationToken.None);
            await WaitForAsync(() => s.IsCleanlyConnected && s.GetAsync(CancellationToken.None).Result?.Capabilities is not null, "the first clean read");
        }

        // The RC51 case: the mount wakes at its false zero and Ara connects to it.
        private async Task<TelescopeService> ConnectedAtFalseZeroAsync(IMountPoseStore? store = null, IProfileService? profile = null,
                TimeProvider? clock = null, Action<FakeTelescopeDevice>? also = null) {
            _configure = f => { f.ReportsAtHome = true; f.Dec = 90; also?.Invoke(f); };
            var svc = Build(store, profile, clock: clock);
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.PositionUnverified == true, "the unverified position");
            return svc;
        }

        private static async Task<RefusalException> RefusedAsync(Func<Task> move) {
            try {
                await move();
            } catch (RefusalException refusal) {
                return refusal;
            }
            Assert.Fail("the move was not refused");
            return null!;
        }

        private static readonly Coordinates Target = new(Angle.ByHours(5.0), Angle.ByDegree(30.0), Epoch.JNOW);

        [Test]
        public async Task The_runtime_reports_an_unverified_position_after_a_connect_at_home() {
            using var svc = await ConnectedAtFalseZeroAsync();

            var runtime = Runtime(svc)!;
            Assert.That(runtime.PositionUnverified, Is.True);
            Assert.That(runtime.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
        }

        [Test]
        public async Task A_rest_goto_is_refused_and_nothing_reaches_the_mount() {
            using var svc = await ConnectedAtFalseZeroAsync();

            var refusal = await RefusedAsync(() => svc.SlewAsync(new SlewRequestDto(5.0, 30.0), null, CancellationToken.None));

            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified));
            Assert.That(refusal.Parameters["position_unverified_reason"], Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
            Assert.That(refusal.Parameters["home_is_sensor_search"], Is.EqualTo(false), "always sent (#2173)");
            await Task.Delay(100);
            Assert.That(Current.Slews, Is.Zero);
        }

        [Test]
        public async Task Force_does_not_bypass_the_refusal() {
            using var svc = await ConnectedAtFalseZeroAsync();
            var sequencer = new Mock<ISequencerService>();
            sequencer.Setup(s => s.ActiveRunIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Guid> { Guid.NewGuid() });

            var refusal = await RefusedAsync(() => EquipmentEndpoints.SlewAsync(
                new SlewRequestDto(5.0, 30.0, Force: true), null, svc, sequencer.Object, profiles: null, CancellationToken.None));

            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified));
            Assert.That(Current.Slews, Is.Zero);
        }

        [Test]
        public async Task A_sync_is_still_allowed() {
            using var svc = await ConnectedAtFalseZeroAsync();

            await svc.SlewAsync(new SlewRequestDto(5.0, 30.0, Sync: true), null, CancellationToken.None);

            await WaitForAsync(() => Current.Syncs == 1, "the sync");
        }

        // #2173 — Home is the way out of an unverified position on any mount that can find Home (NINA allows Find Home at
        // any time), so it runs; GoTo and Park, which drive by the untrusted model, stay refused, and the Park refusal
        // names Home.
        [Test]
        public async Task Home_runs_while_unverified_but_GoTo_and_Park_stay_refused() {
            using var svc = await ConnectedAtFalseZeroAsync();

            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => Current.Homes == 1, "the Home search");

            var park = await RefusedAsync(() => svc.ParkAsync(new ParkRequestDto(), null, CancellationToken.None));
            Assert.That(park.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified));
            Assert.That(park.Message, Does.Contain("The way out is Home").And.Not.Contain("hand controller"));
            var goTo = await RefusedAsync(() => svc.SlewAsync(new SlewRequestDto(5.0, 30.0), null, CancellationToken.None));
            Assert.That(goTo.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified));
            await Task.Delay(100);
            Assert.That(Current.Parks, Is.Zero);
            Assert.That(Current.Slews, Is.Zero);
        }

        // #2173 — ADR 0003 rule 2: whether Home may run follows CanFindHome, never the brand or driver. A mount that is
        // no iOptron, on a driver that is no AlpacaBridge, runs its Home while unverified all the same.
        [Test]
        public async Task Home_is_allowed_while_unverified_on_a_mount_with_a_non_iOptron_name() {
            using var svc = await ConnectedAtFalseZeroAsync(also: f => {
                f.DriverInfo = "Generic Alpaca Telescope Driver";
                f.DriverVersion = "2.3.1";
            });

            await svc.FindHomeAsync(null, CancellationToken.None);

            await WaitForAsync(() => Current.Homes == 1, "the Home");
        }

        // #2173 — a mount whose Alpaca CanFindHome is false keeps the #2070 refusal while unverified, and the refusal sends
        // the user to the hand controller's search. Nothing reaches the mount.
        [Test]
        public async Task Home_is_refused_while_unverified_on_a_mount_that_cannot_find_home() {
            using var svc = await ConnectedAtFalseZeroAsync(also: f => f.FindHomeCapable = false);

            var home = await RefusedAsync(() => svc.FindHomeAsync(null, CancellationToken.None));

            Assert.That(home.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified));
            Assert.That(home.Message, Does.Contain("hand controller").And.Not.Contain("The way out is Home"));
            await Task.Delay(100);
            Assert.That(Current.Homes, Is.Zero);
        }

        // #2173 review — a mount at Home whose position is unverified only because a routine's stop was unconfirmed (the
        // one reason a completed Home may clear by itself): the guards of the Home watch still apply to it.
        private async Task<TelescopeService> AtHomeAfterAnUnconfirmedStopAsync() {
            var svc = await ConnectedAtFalseZeroAsync();
            await svc.ConfirmHomeAsync(CancellationToken.None); // really at Home
            svc.MarkPositionUnverified(MountPoseTrust.StopUnconfirmed);
            return svc;
        }

        // #2173 — a Home that goes nowhere (the mount reports Home throughout): no search was seen to run, so the position
        // stays unverified even for a reason a completed Home may clear. No brand knowledge is needed for that.
        [Test]
        public async Task Home_that_never_moves_clears_nothing() {
            using var svc = await AtHomeAfterAnUnconfirmedStopAsync();

            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => Current.Homes == 1, "the Home");
            await Task.Delay(300); // many watch polls at Home and still

            Assert.That(Runtime(svc)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.StopUnconfirmed));
        }

        // #2173 review — the same Home that never moves over the slow link: one AtHome read fails during the watch. A
        // failed read is no "left Home", so nothing was seen moving and the position stays unverified.
        [Test]
        public async Task A_failed_at_home_read_during_a_home_that_never_moves_stays_unverified() {
            using var svc = await AtHomeAfterAnUnconfirmedStopAsync();

            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => Current.Homes == 1, "the Home");
            Current.AtHomeReadFails = true; // a few watch polls lose their AtHome read
            await Task.Delay(100);
            Current.AtHomeReadFails = false;
            await Task.Delay(300); // many watch polls at Home and still again

            Assert.That(Runtime(svc)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.StopUnconfirmed));
        }

        // #2173 review — after an unconfirmed stop (a reason a completed Home may clear by itself) the search runs (seen
        // slewing, away from Home), then the mount loses power while AlpacaBridge stays connected: its reads fail, and it
        // wakes reporting Home wherever it points. What was seen before the gap no longer counts, so the Home it reports
        // after it is not trusted.
        [Test]
        public async Task A_mount_that_resets_in_the_middle_of_the_search_is_not_trusted() {
            _configure = f => { f.ReportsAtHome = false; f.Dec = 60; };
            using var svc = Build();
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is 60.0, "a position");
            svc.MarkPositionUnverified(MountPoseTrust.StopUnconfirmed);
            Current.IsSlewing = true; // the search: away from Home and slewing

            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => Current.Homes == 1, "the Home search");
            await Task.Delay(150); // watch polls see the search run
            Current.AtHomeReadFails = true; // the power loss: reads fail
            await Task.Delay(100);
            Current.IsSlewing = false; // it wakes at a false zero
            Current.ReportsAtHome = true;
            Current.Dec = 90;
            Current.AtHomeReadFails = false;
            await Task.Delay(300);

            Assert.That(Runtime(svc)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.StopUnconfirmed));
        }

        // #2173 review (the RC51 hazard) — the mount woke at its false zero; the pad nudges it off (allowed while
        // unverified), then Home on a mount whose Home drives by its model slews back to the model's zero: seen to run,
        // and it lands reporting Home. Ara cannot tell that from a sensor search, so after a power loss the position stays
        // unverified until the user confirms Home by eye.
        [Test]
        public async Task Pad_then_Home_from_a_false_zero_stays_unverified_until_Home_is_confirmed_by_eye() {
            using var svc = await ConnectedAtFalseZeroAsync();
            await svc.MoveAxisAsync(0, 1.0, null, CancellationToken.None); // the pad nudge
            Current.ReportsAtHome = false;
            Current.Dec = 60;
            await svc.MoveAxisAsync(0, 0, null, CancellationToken.None);
            await Task.Delay(100);

            Current.IsSlewing = true; // the model Home slews back
            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => Current.Homes == 1, "the Home");
            await Task.Delay(100);
            Current.Dec = 90; // the model's false zero
            Current.ReportsAtHome = true;
            Current.IsSlewing = false;
            await Task.Delay(400); // many watch polls at Home and still

            Assert.That(Runtime(svc)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
            var goTo = await RefusedAsync(() => svc.SlewAsync(new SlewRequestDto(5.0, 30.0), null, CancellationToken.None));
            Assert.That(goTo.Message, Does.Contain("confirm by eye that the mount is really at Home"));
            await svc.ConfirmHomeAsync(CancellationToken.None); // the user's eye check
            Assert.That(Runtime(svc)!.PositionUnverified, Is.False);
        }

        // #2173 review — the same pad-then-Home after an in-session jump (a blip, a reset, a slipped clutch): the mount's
        // own model may be wrong, so a Home seen to run that ends at Home does not trust it by itself.
        [Test]
        public async Task Pad_then_Home_after_an_in_session_jump_stays_unverified() {
            _configure = f => { f.ReportsAtHome = false; f.Dec = 23.52; };
            using var svc = Build();
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is 23.52, "a position");
            svc.MarkPositionUnverified(MountPoseTrust.PoseJumpInSession);
            await svc.MoveAxisAsync(1, 1.0, null, CancellationToken.None);
            Current.Dec = 40;
            await svc.MoveAxisAsync(1, 0, null, CancellationToken.None);

            Current.IsSlewing = true;
            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => Current.Homes == 1, "the Home");
            await Task.Delay(100);
            Current.Dec = 90.71;
            Current.ReportsAtHome = true;
            Current.IsSlewing = false;
            await Task.Delay(400);

            Assert.That(Runtime(svc)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.PoseJumpInSession));
        }

        // #2173 — the same pad-then-Home from a false zero, on a mount whose driver says its FindHome is a real home-sensor
        // search (AlpacaBridge's capability): the search finds the physical Home whatever the model said, so the Home seen
        // to run that ends at Home trusts the position by itself, with no eye check.
        private async Task<TelescopeService> SensorSearchPadThenHomeAsync(TelescopeService svc) {
            await svc.MoveAxisAsync(0, 1.0, null, CancellationToken.None); // the pad nudge
            Current.ReportsAtHome = false;
            Current.Dec = 60;
            await svc.MoveAxisAsync(0, 0, null, CancellationToken.None);
            await Task.Delay(100);
            Current.IsSlewing = true; // the sensor search runs
            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => Current.Homes == 1, "the Home");
            await Task.Delay(100);
            return svc;
        }

        private void LandsAtHome() {
            Current.Dec = 90.71;
            Current.ReportsAtHome = true;
            Current.IsSlewing = false;
        }

        [Test]
        public async Task A_sensor_search_Home_after_the_pad_from_a_false_zero_trusts_the_position() {
            using var svc = await ConnectedAtFalseZeroAsync(also: f => f.SaysHomeIsSensorSearch());
            Assert.That((await svc.GetAsync(CancellationToken.None))!.Capabilities!.HomeIsSensorSearch, Is.True);
            await SensorSearchPadThenHomeAsync(svc);
            LandsAtHome();

            await WaitForAsync(() => Runtime(svc)?.PositionUnverified == false, "the position trusted after the sensor search");
            await svc.SlewAsync(new SlewRequestDto(5.0, 30.0), null, CancellationToken.None);
            await WaitForAsync(() => Current.Slews == 1, "a GoTo goes again");
        }

        [Test]
        public async Task A_sensor_search_Home_after_an_in_session_jump_trusts_the_position() {
            _configure = f => { f.ReportsAtHome = false; f.Dec = 23.52; f.SaysHomeIsSensorSearch(); };
            using var svc = Build();
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is 23.52, "a position");
            svc.MarkPositionUnverified(MountPoseTrust.PoseJumpInSession);
            await svc.MoveAxisAsync(1, 1.0, null, CancellationToken.None);
            Current.Dec = 40;
            await svc.MoveAxisAsync(1, 0, null, CancellationToken.None);

            Current.IsSlewing = true;
            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => Current.Homes == 1, "the Home");
            await Task.Delay(100);
            LandsAtHome();

            await WaitForAsync(() => Runtime(svc)?.PositionUnverified == false, "the position trusted after the sensor search");
        }

        // #2173 — no start means no trust, even on a sensor-search mount (the HAE29C has ignored its search command over
        // Wi-Fi): a Home that never leaves Home and moves clears nothing.
        [Test]
        public async Task A_sensor_search_Home_that_never_moves_clears_nothing() {
            using var svc = await ConnectedAtFalseZeroAsync(also: f => f.SaysHomeIsSensorSearch());

            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => Current.Homes == 1, "the Home");
            await Task.Delay(400); // many watch polls at Home and still

            Assert.That(Runtime(svc)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
        }

        // #2173 — a sensor-search mount whose reads fail in the middle of the search (it may have lost power and woken at a
        // false zero): what was seen before the gap no longer counts, so the Home it reports after it is not trusted.
        [Test]
        public async Task A_sensor_search_Home_with_a_failed_read_mid_search_is_not_trusted() {
            using var svc = await ConnectedAtFalseZeroAsync(also: f => f.SaysHomeIsSensorSearch());
            await SensorSearchPadThenHomeAsync(svc);
            Current.AtHomeReadFails = true;
            await Task.Delay(100);
            LandsAtHome();
            Current.AtHomeReadFails = false;
            await Task.Delay(400);

            Assert.That(Runtime(svc)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
        }

        // #2173 — a FindHome that fails is no Home: on a sensor-search mount that lands at Home anyway, the reason stays.
        [Test]
        public async Task A_sensor_search_Home_whose_FindHome_fails_is_not_trusted() {
            using var svc = await ConnectedAtFalseZeroAsync(also: f => {
                f.SaysHomeIsSensorSearch();
                f.FindHomeThrows = new ASCOM.DriverException("FindHome failed");
            });
            await SensorSearchPadThenHomeAsync(svc);
            await Task.Delay(100);
            LandsAtHome();
            await Task.Delay(400);

            Assert.That(Runtime(svc)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
        }

        // #2173 — a reconnect between the FindHome and the check voids the search: the watch ends, and the Home the old
        // session's mount reports afterwards trusts nothing in the new session.
        [Test]
        public async Task A_reconnect_in_the_middle_of_a_sensor_search_Home_trusts_nothing() {
            using var svc = await ConnectedAtFalseZeroAsync(also: f => f.SaysHomeIsSensorSearch());
            await SensorSearchPadThenHomeAsync(svc);
            var searching = Current;

            _configure = f => { f.ReportsAtHome = true; f.Dec = 90.71; f.SaysHomeIsSensorSearch(); };
            await svc.DisconnectAsync(null, CancellationToken.None);
            await ConnectAsync(svc);
            searching.Dec = 90.71; // the old session's search lands at Home after the reconnect
            searching.ReportsAtHome = true;
            searching.IsSlewing = false;
            await Task.Delay(400);

            Assert.That(Runtime(svc)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
        }

        // #2173 — a refusal on a sensor-search mount carries the capability and says Home clears it by itself.
        [Test]
        public async Task A_refusal_on_a_sensor_search_mount_says_Home_clears_it_by_itself() {
            using var svc = await ConnectedAtFalseZeroAsync(also: f => f.SaysHomeIsSensorSearch());

            var goTo = await RefusedAsync(() => svc.SlewAsync(new SlewRequestDto(5.0, 30.0), null, CancellationToken.None));

            Assert.That(goTo.Parameters["home_is_sensor_search"], Is.EqualTo(true));
            Assert.That(goTo.Message, Does.Contain("trusted again once the mount is seen to move"));
            Assert.That(goTo.Message, Does.Not.Contain("confirm by eye"));
        }

        // #2173 — the capability read: listed (case-insensitive) and answered sensor_search true is a sensor search;
        // anything else, a failed read included, is not (fail safe).
        [Test]
        public void The_sensor_search_capability_needs_the_action_listed_and_a_true_reply() {
            using var listed = new FakeTelescopeDevice();
            listed.SaysHomeIsSensorSearch();
            listed.Actions[0] = "FindHomeIsSensorSearch";
            Assert.That(TelescopeService.ReadHomeIsSensorSearch(listed), Is.True);

            using var unlisted = new FakeTelescopeDevice { ActionResponder = (_, _) => "{\"sensor_search\": true}" };
            Assert.That(TelescopeService.ReadHomeIsSensorSearch(unlisted), Is.False, "the action is not listed");
            Assert.That(unlisted.ActionCalls, Is.Empty);
        }

        [Test]
        public void A_failed_supported_actions_read_means_not_a_sensor_search() {
            using var mount = new FakeTelescopeDevice { SupportedActionsReadFails = true };
            mount.SaysHomeIsSensorSearch();
            Assert.That(TelescopeService.ReadHomeIsSensorSearch(mount), Is.False);
        }

        [Test]
        public void A_not_connected_action_reply_means_not_a_sensor_search() {
            using var mount = new FakeTelescopeDevice();
            mount.SaysHomeIsSensorSearch();
            mount.ActionResponder = (_, _) => throw new ASCOM.NotConnectedException("not connected");
            Assert.That(TelescopeService.ReadHomeIsSensorSearch(mount), Is.False);
        }

        [Test]
        public void The_sensor_search_reply_true_is_a_sensor_search() =>
            Assert.That(TelescopeService.ParseHomeIsSensorSearchReply("{\"sensor_search\": true, \"method\": \"zero_sensor_search\", \"model\": \"0036\"}"), Is.True);

        [Test]
        public void The_sensor_search_reply_false_is_not_a_sensor_search() =>
            Assert.That(TelescopeService.ParseHomeIsSensorSearchReply("{\"sensor_search\": false, \"method\": \"goto_stored_home\", \"model\": \"\"}"), Is.False);

        [Test]
        public void A_malformed_sensor_search_reply_is_not_a_sensor_search() {
            Assert.That(TelescopeService.ParseHomeIsSensorSearchReply("{\"sensor_search\": tru"), Is.False);
            Assert.That(TelescopeService.ParseHomeIsSensorSearchReply("true"), Is.False, "not an object");
            Assert.That(TelescopeService.ParseHomeIsSensorSearchReply(""), Is.False);
        }

        [Test]
        public void A_sensor_search_reply_without_the_field_is_not_a_sensor_search() {
            Assert.That(TelescopeService.ParseHomeIsSensorSearchReply("{\"method\": \"zero_sensor_search\", \"model\": \"0036\"}"), Is.False);
            Assert.That(TelescopeService.ParseHomeIsSensorSearchReply("{\"sensor_search\": \"true\"}"), Is.False, "a string is not the literal");
        }

        // #2173 (Joey, 2026-11-21) — "If we drove it home, shouldn't it say: yes, I am home?" Polar alignment could not
        // confirm its stop (only Ara's knowledge is in doubt, the mount's model is intact); Home runs the search, the mount
        // lands at Home reading Dec +90.71°, and the position is trusted again with no eye check, persisted, and shown on
        // the runtime the client reads.
        [Test]
        public async Task A_home_search_that_ends_at_home_at_dec_90_71_trusts_the_position_again() {
            var store = new MemoryPoseStore();
            _configure = f => { f.ReportsAtHome = false; f.Dec = 23.52; };
            using var svc = Build(store);
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is 23.52, "a position");
            svc.MarkPositionUnverified(MountPoseTrust.StopUnconfirmed);
            Assert.That(store.UnverifiedReason(Mount.UniqueId), Is.EqualTo(MountPoseTrust.StopUnconfirmed));

            Current.IsSlewing = true; // the search runs: slewing, away from Home
            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => Current.Homes == 1, "the Home search");
            await Task.Delay(100);
            Current.Dec = 90.71; // where the search lands: the Dec axis just past the pole
            Current.ReportsAtHome = true;
            Current.IsSlewing = false;

            await WaitForAsync(() => Runtime(svc)?.PositionUnverified == false, "the position trusted again after the search");
            Assert.That(Current.Homes, Is.EqualTo(1));
            // The store write follows the trust under load (the tick can show the trust first), so wait for it.
            await WaitForAsync(() => store.UnverifiedReason(Mount.UniqueId) is null, "the trust persisted");
            await Task.Delay(200);
            Assert.That(Runtime(svc)!.PositionUnverified, Is.False, "Dec 90.71° at Home is no jump afterwards");
            await svc.SlewAsync(new SlewRequestDto(5.0, 30.0), null, CancellationToken.None);
            await WaitForAsync(() => Current.Slews == 1, "a GoTo goes again");
        }

        // #2173 (rig 2026-11-21, 05:07–05:13Z): an AlpacaBridge restart (a reconnect), then one bad read, then good
        // reads. The mount never moved, so it stays verified, both on the reconnect's first read and in session.
        [Test]
        public async Task An_alpacabridge_restart_then_one_bad_read_then_good_reads_stays_verified() {
            _configure = f => { f.ReportsAtHome = false; f.Dec = 23.52; };
            using var svc = Build();
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is 23.52, "a position");

            await svc.DisconnectAsync(null, CancellationToken.None);
            _configure = f => { f.ReportsAtHome = false; f.Dec = 23.52; f.DecReads.Enqueue(0.0); };
            await ConnectAsync(svc);
            await WaitForAsync(() => Current.DecReads.IsEmpty && Runtime(svc)?.DeclinationDegrees is 23.52, "good reads after the bad one");

            Current.DecReads.Enqueue(0.0); // and one more bad read in session, minutes of nothing commanded
            await WaitForAsync(() => Current.DecReads.IsEmpty, "the bad read");
            await Task.Delay(MountPoseTrust.ConfirmGap + TimeSpan.FromMilliseconds(500)); // good reads well past the confirm gap

            Assert.That(Runtime(svc)!.PositionUnverified, Is.False);
        }

        // #2173 (rig 2026-11-21, 06:20Z): the run resumed from Home (Dec +90.71°) and its re-center slewed to Merope
        // (Dec 24°). Over a slow link no tick read Slewing during the slew, which outlasted the quiet window. The
        // re-center's own GoTo must not read as a jump.
        [Test]
        public async Task A_resume_recenter_slew_from_the_pole_to_dec_24_stays_verified() {
            var clock = new ManualClock();
            _configure = f => { f.ReportsAtHome = false; f.Dec = 30.0; };
            using var svc = Build(clock: clock);
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is 30.0, "a still position");
            Current.ReportsAtHome = true; // at Home after the night's home search
            Current.Dec = 90.71;
            await svc.ConfirmHomeAsync(CancellationToken.None);
            clock.Advance(MountPoseTrust.QuietWindow + TimeSpan.FromSeconds(30));

            var mount = Current;
            mount.OnSlew = (ra, dec) => _ = Task.Run(async () => {
                mount.SlewingReadFails = true; // the slow link: every Slewing read times out while it slews
                mount.ReportsAtHome = false;
                await Task.Delay(200);
                clock.Advance(MountPoseTrust.QuietWindow + TimeSpan.FromSeconds(10)); // a slew longer than the window
                mount.Ra = ra;
                mount.Dec = dec;
                await Task.Delay(200);
                mount.SlewingReadFails = false;
            });
            var merope = new Coordinates(Angle.ByHours(3.77), Angle.ByDegree(24.0), Epoch.JNOW);
            Assert.That(await ((ITelescopeMediator)svc).SlewToCoordinatesAsync(merope, CancellationToken.None), Is.True);

            await Task.Delay(200);
            clock.Advance(MountPoseTrust.ConfirmGap + TimeSpan.FromSeconds(1));
            await Task.Delay(200);
            clock.Advance(MountPoseTrust.ConfirmGap + TimeSpan.FromSeconds(1));
            await Task.Delay(200);
            Assert.That(Runtime(svc)!.PositionUnverified, Is.False, "the re-center's GoTo is Ara's own move");
        }

        // #2173 review — a real jump (a power blip the session survived) waits about 2 s for its second reading. A GoTo in
        // that window must not slip through and drive by the reset model, nor make the jump read as its own move.
        [Test]
        public async Task A_goto_in_the_confirm_window_of_a_jump_is_refused_and_the_jump_still_flags() {
            var clock = new ManualClock();
            _configure = f => { f.ReportsAtHome = false; f.Dec = 30.0; };
            using var svc = Build(clock: clock);
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is 30.0, "a still position");
            clock.Advance(MountPoseTrust.QuietWindow + TimeSpan.FromSeconds(30));
            await Task.Delay(100);

            Current.Dec = 60.0; // the jump: the model reset with nothing commanded
            await WaitForAsync(() => svc.PoseTrust.PendingVerdict == MountPoseTrust.PoseJumpInSession, "the jump's first reading");
            var later = Task.Run(async () => {
                await Task.Delay(200);
                clock.Advance(MountPoseTrust.ConfirmGap + TimeSpan.FromSeconds(1)); // the next readings come
            });
            var goTo = await RefusedAsync(() => svc.SlewAsync(new SlewRequestDto(5.0, 30.0), null, CancellationToken.None));
            await later;

            Assert.That(goTo.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified));
            Assert.That(goTo.Parameters["position_unverified_reason"], Is.EqualTo(MountPoseTrust.PoseJumpInSession));
            await WaitForAsync(() => Runtime(svc)?.PositionUnverifiedReason == MountPoseTrust.PoseJumpInSession, "the jump flagged");
            Assert.That(Current.Slews, Is.Zero);
        }

        // #2173 review — a REST park whose call returns at once (many drivers), over a slow link where every Slewing read
        // fails, lands at the park position long after the quiet window. It is Ara's own move until the mount answers
        // that it stopped, so the landing is no jump.
        [Test]
        public async Task A_rest_park_that_outlasts_the_quiet_window_over_a_slow_link_stays_verified() {
            var clock = new ManualClock();
            _configure = f => { f.ReportsAtHome = false; f.Dec = 30.0; };
            using var svc = Build(clock: clock);
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is 30.0, "a still position");
            var mount = Current;

            mount.SlewingReadFails = true; // the slow link, from the park on
            await svc.ParkAsync(new ParkRequestDto(), null, CancellationToken.None);
            await WaitForAsync(() => mount.Parks == 1, "the park call");
            await Task.Delay(100);
            clock.Advance(MountPoseTrust.QuietWindow + TimeSpan.FromSeconds(30)); // the park runs on, unseen
            mount.Dec = 70.0; // where it parks
            await Task.Delay(100);
            mount.SlewingReadFails = false; // the link recovers: the mount is still at its park
            for (var i = 0; i < 3; i++) {
                await Task.Delay(150);
                clock.Advance(MountPoseTrust.ConfirmGap + TimeSpan.FromSeconds(1));
            }
            await Task.Delay(150);

            Assert.That(Runtime(svc)!.PositionUnverified, Is.False, "the park's own landing");
        }

        // #2173 review — after a GoTo's noted target no longer explains anything, a power blip that moves the Dec to about
        // that target must still flag. Shared by the GoTo-target tests below.
        private static async Task ABlipToDec24FlagsAsync(TelescopeService svc, ManualClock clock, FakeTelescopeDevice mount) {
            await Task.Delay(150);
            clock.Advance(MountPoseTrust.QuietWindow + TimeSpan.FromSeconds(30)); // nothing commanded for a while
            await Task.Delay(150);
            mount.Dec = 24.2; // the blip lands near the dropped GoTo's target
            for (var i = 0; i < 3; i++) {
                await Task.Delay(150);
                clock.Advance(MountPoseTrust.ConfirmGap + TimeSpan.FromSeconds(1));
            }
            await WaitForAsync(() => Runtime(svc)?.PositionUnverifiedReason == MountPoseTrust.PoseJumpInSession, "the blip flagged as a jump");
        }

        private async Task<(TelescopeService Svc, ManualClock Clock, FakeTelescopeDevice Mount)> TrustedAtDec30Async() {
            var clock = new ManualClock();
            _configure = f => { f.ReportsAtHome = false; f.Dec = 30.0; };
            var svc = Build(clock: clock);
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is 30.0, "a still position");
            return (svc, clock, Current);
        }

        // #2173 review — a REST GoTo to Dec 24 whose PUT throws never got there: its target must not excuse a later jump.
        [Test]
        public async Task A_rest_goto_whose_put_fails_excuses_no_later_jump_to_its_target() {
            var (svc, clock, mount) = await TrustedAtDec30Async();
            using var _ = svc;
            mount.OnSlew = (_, _) => throw new InvalidOperationException("the PUT failed");

            await svc.SlewAsync(new SlewRequestDto(3.77, 24.0), null, CancellationToken.None);
            await WaitForAsync(() => mount.Slews == 1, "the failed PUT");

            await ABlipToDec24FlagsAsync(svc, clock, mount);
        }

        // #2173 review — the same GoTo dropped because a routine took the mount before it went out.
        [Test]
        public async Task A_rest_goto_dropped_for_the_motion_owner_excuses_no_later_jump_to_its_target() {
            var (svc, clock, mount) = await TrustedAtDec30Async();
            using var _ = svc;
            using (svc.AcquireMotion(MotionOwners.PolarAlign)) {
                await svc.SlewAsync(new SlewRequestDto(3.77, 24.0), null, CancellationToken.None);
                await Task.Delay(200); // the background send sees the owner and drops it
            }
            Assert.That(mount.Slews, Is.Zero);

            await ABlipToDec24FlagsAsync(svc, clock, mount);
        }

        // #2173 review — the same GoTo dropped because a stop landed during its tracking enable.
        [Test]
        public async Task A_rest_goto_dropped_after_a_stop_excuses_no_later_jump_to_its_target() {
            var (svc, clock, mount) = await TrustedAtDec30Async();
            using var _ = svc;
            mount.TrackingOn = false;
            var fenced = 0;
            mount.OnTrackingWrite = on => {
                if (on && Interlocked.Exchange(ref fenced, 1) == 0) {
                    svc.FenceMotion("test_stop"); // a stop that races the GoTo's tracking enable
                }
            };

            await svc.SlewAsync(new SlewRequestDto(3.77, 24.0), null, CancellationToken.None);
            await WaitForAsync(() => Volatile.Read(ref fenced) == 1, "the stop during the enable");
            await Task.Delay(100);
            Assert.That(mount.Slews, Is.Zero, "dropped after the stop");

            await ABlipToDec24FlagsAsync(svc, clock, mount);
        }

        // #2173 review — a sequencer GoTo whose PUT throws: the mediator's end of the move drops its target too.
        [Test]
        public async Task A_sequence_goto_that_fails_excuses_no_later_jump_to_its_target() {
            var (svc, clock, mount) = await TrustedAtDec30Async();
            using var _ = svc;
            mount.OnSlew = (_, _) => throw new InvalidOperationException("the PUT failed");

            await Assert.ThrowsAsync<SequenceEntityFailedException>(() =>
                ((ITelescopeMediator)svc).SlewToCoordinatesAsync(new Coordinates(Angle.ByHours(3.77), Angle.ByDegree(24.0), Epoch.JNOW), CancellationToken.None));
            Assert.That(mount.Slews, Is.EqualTo(1));

            await ABlipToDec24FlagsAsync(svc, clock, mount);
        }

        // #2173 review — the user's Home on a mount whose CanFindHome is false, sent from a trusted pose: the FindHome call
        // returns at once and the mount moves on for longer than the quiet window over a link that drops the Slewing
        // reads. The move stays Ara's own until the mount reports Home, so the landing is no jump.
        [Test]
        public async Task A_home_that_returns_at_once_on_a_mount_that_cannot_find_home_lands_with_no_jump() {
            var clock = new ManualClock();
            _configure = f => { f.ReportsAtHome = false; f.Dec = 30.0; f.FindHomeCapable = false; };
            using var svc = Build(clock: clock);
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is 30.0, "a still position");
            var mount = Current;

            mount.SlewingReadFails = true; // the slow link, from the Home on
            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => mount.Homes == 1, "the Home call");
            await Task.Delay(100);
            clock.Advance(MountPoseTrust.QuietWindow + TimeSpan.FromSeconds(30)); // it moves on, unseen
            mount.Dec = 90.0; // where it homes
            mount.ReportsAtHome = true;
            await Task.Delay(100);
            mount.SlewingReadFails = false;
            for (var i = 0; i < 3; i++) {
                await Task.Delay(150);
                clock.Advance(MountPoseTrust.ConfirmGap + TimeSpan.FromSeconds(1));
            }
            await Task.Delay(150);

            Assert.That(Runtime(svc)!.PositionUnverified, Is.False, "the Home's own landing");
        }

        // #2173 review — an unverified mount over a slow link: the fresh CanFindHome read times out, and Home still runs on
        // the connect's capability read (the mount said it can find Home).
        [Test]
        public async Task Home_runs_while_unverified_when_the_fresh_can_find_home_read_times_out() {
            _configure = f => { f.ReportsAtHome = true; f.Dec = 90; };
            using var svc = Build(poseRead: TimeSpan.FromMilliseconds(200));
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.PositionUnverified == true, "the unverified position");
            using var hold = new ManualResetEventSlim(false);
            Current.CanFindHomeHold = hold; // the slow link: the read hangs past the timeout
            try {
                await svc.FindHomeAsync(null, CancellationToken.None);
                await WaitForAsync(() => Current.Homes == 1, "the Home");
            } finally {
                hold.Set();
            }
        }

        // #2173 review — the reverse: the connect read said the mount cannot find Home, and the fresh read fails. Home is
        // still refused, with the hand controller named.
        [Test]
        public async Task Home_stays_refused_while_unverified_when_the_connect_read_said_no_and_the_fresh_read_fails() {
            using var svc = await ConnectedAtFalseZeroAsync(also: f => f.FindHomeCapable = false);
            Current.CanFindHomeReadFails = true;

            var home = await RefusedAsync(() => svc.FindHomeAsync(null, CancellationToken.None));

            Assert.That(home.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified));
            Assert.That(home.Message, Does.Contain("hand controller"));
            await Task.Delay(100);
            Assert.That(Current.Homes, Is.Zero);
        }

        // #2173 review — a driver that still reads Slewing false right after Park returns, before its slew starts: that
        // read must not end the park's hold, or the long park over the slow link that follows lands as a jump.
        [Test]
        public async Task A_park_whose_driver_reads_not_slewing_before_its_slew_starts_stays_verified() {
            var clock = new ManualClock();
            _configure = f => { f.ReportsAtHome = false; f.Dec = 30.0; };
            using var svc = Build(clock: clock);
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is 30.0, "a still position");
            var mount = Current;

            await svc.ParkAsync(new ParkRequestDto(), null, CancellationToken.None); // Slewing still reads false
            await WaitForAsync(() => mount.Parks == 1, "the park call");
            await Task.Delay(150);
            mount.SlewingReadFails = true; // the slew starts, over the slow link
            clock.Advance(MountPoseTrust.QuietWindow + TimeSpan.FromSeconds(30));
            mount.Dec = 70.0;
            await Task.Delay(100);
            mount.SlewingReadFails = false;
            for (var i = 0; i < 3; i++) {
                await Task.Delay(150);
                clock.Advance(MountPoseTrust.ConfirmGap + TimeSpan.FromSeconds(1));
            }
            await Task.Delay(150);

            Assert.That(Runtime(svc)!.PositionUnverified, Is.False, "the park's own landing");
        }

        // #2173 review — right after a fresh daemon connects, AlpacaBridge may answer only its (0, 0) placeholder. The
        // guard's own read must not take that as a pose: the GoTo is refused as not read yet, and the first real reading
        // later is no jump from Dec 0.
        [Test]
        public async Task A_goto_while_the_mount_reads_only_the_placeholder_is_refused_and_no_jump_follows() {
            _configure = f => { f.ReportsAtHome = false; f.Ra = 0.0; f.Dec = 0.0; };
            using var svc = Build();
            await ConnectAsync(svc);

            var goTo = await RefusedAsync(() => svc.SlewAsync(new SlewRequestDto(5.0, 30.0), null, CancellationToken.None));
            Assert.That(goTo.Parameters["position_unverified_reason"], Is.EqualTo(MountPoseTrust.NotReadYet));
            Current.Ra = 5.0; // real readings now
            Current.Dec = 30.0;
            await Task.Delay(MountPoseTrust.ConfirmGap + TimeSpan.FromMilliseconds(500));

            Assert.That(Runtime(svc)!.PositionUnverified, Is.False);
            Assert.That(Current.Slews, Is.Zero);
        }

        [Test]
        public async Task The_pad_nudge_and_stop_still_work_but_a_routines_motor_move_is_refused() {
            using var svc = await ConnectedAtFalseZeroAsync();

            await svc.MoveAxisAsync(0, 1.0, null, CancellationToken.None);
            await svc.MoveAxisAsync(0, 0, null, CancellationToken.None);
            await svc.AbortSlewAsync(CancellationToken.None);
            Assert.That(Current.MoveAxisStarts, Is.EqualTo(1), "the pad backs the mount off");
            Assert.That(Current.Aborts, Is.EqualTo(1), "Stop always works");

            var refusal = await RefusedAsync(() => svc.MoveAxisAsync(0, 1.0, 1000, svc.AbortEpoch, CancellationToken.None));
            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified));
            Assert.That(Current.MoveAxisStarts, Is.EqualTo(1), "polar alignment's move never reached the mount");
        }

        [Test]
        public async Task A_sequence_goto_fails_the_instruction_without_an_op_fault() {
            using var svc = await ConnectedAtFalseZeroAsync();

            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => ((ITelescopeMediator)svc).SlewToCoordinatesAsync(Target, CancellationToken.None));

            Assert.That(Current.Slews, Is.Zero);
            Assert.That(_faults.Faults.Where(f => f.Kind is EquipmentFaultKind.OpError or EquipmentFaultKind.StallTimeout), Is.Empty);
        }

        [Test]
        public async Task A_sequence_park_or_find_home_is_refused_without_an_op_fault() {
            using var svc = await ConnectedAtFalseZeroAsync();

            Current.TrackingOn = true;
            var park = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => ((ITelescopeMediator)svc).ParkTelescope(new Progress<ApplicationStatus>(), CancellationToken.None));
            Assert.That(Current.TrackingOn, Is.False, "a refused park does not leave the mount tracking unattended");
            Assert.That(park!.Message, Does.Contain("NOT parked"));
            Current.TrackingOn = true;
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => ((ITelescopeMediator)svc).FindHome(new Progress<ApplicationStatus>(), CancellationToken.None));
            Assert.That(Current.TrackingOn, Is.True, "a refused Home changes nothing");

            Assert.That(Current.Parks, Is.Zero);
            Assert.That(Current.Homes, Is.Zero);
            Assert.That(_faults.Faults.Where(f => f.Kind is EquipmentFaultKind.OpError or EquipmentFaultKind.StallTimeout), Is.Empty,
                "a refusal must not count toward the op-fault abort-and-park");
        }

        [Test]
        public async Task A_meridian_flip_is_refused_before_the_pier_side_write() {
            using var profile = new OpenAstroAra.Profile.Profile();
            profile.MeridianFlipSettings.UseSideOfPier = true;
            var profiles = new Mock<IProfileService>();
            profiles.SetupGet(p => p.ActiveProfile).Returns(profile);
            _configure = f => { f.ReportsAtHome = true; f.Dec = 90; f.PierSideSettable = true; };
            using var svc = Build(profile: profiles.Object);
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.PositionUnverified == true, "the unverified position");

            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => ((ITelescopeMediator)svc).MeridianFlip(Target, CancellationToken.None));

            Assert.That(Current.PierSideWrites, Is.Zero, "some mounts start the flip slew on the pier-side write");
            Assert.That(Current.Slews, Is.Zero);
        }

        [Test]
        public async Task A_goto_before_the_first_position_read_reads_the_mount_and_is_refused() {
            using var hold = new ManualResetEventSlim(false);
            _configure = f => { f.ReportsAtHome = true; f.Dec = 90; f.HoldReads = hold; };
            using var svc = Build();
            await svc.ConnectAsync(new ConnectRequestDto(Mount), null, CancellationToken.None);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Connected, "the connect");
            Assert.That(svc.IsCleanlyConnected, Is.False, "the first refresh is still held");

            try {
                var refusal = await RefusedAsync(() => svc.SlewAsync(new SlewRequestDto(5.0, 30.0), null, CancellationToken.None));
                // #2173 — the guard's one on-demand read decides nothing alone: the mount is not trusted yet.
                Assert.That(refusal.Parameters["position_unverified_reason"], Is.EqualTo(MountPoseTrust.NotReadYet));
            } finally {
                hold.Set();
            }
            Assert.That(Current.Slews, Is.Zero);
        }

        [Test]
        public async Task Confirm_home_is_refused_while_the_mount_does_not_report_home() {
            using var svc = await ConnectedAtFalseZeroAsync();
            Current.ReportsAtHome = false; // the pad moved it off the false zero

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ConfirmHomeAsync(CancellationToken.None));
            Assert.That(Runtime(svc)!.PositionUnverified, Is.True);
        }

        // #2173 — confirm-home on a sensor-search mount that does not report Home: the error names Home as clearing the
        // flag by itself, not an eye check, even after a power loss.
        [Test]
        public async Task Confirm_home_off_home_on_a_sensor_search_mount_says_Home_clears_it_by_itself() {
            using var svc = await ConnectedAtFalseZeroAsync(also: f => f.SaysHomeIsSensorSearch());
            Current.ReportsAtHome = false;

            var error = (await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ConfirmHomeAsync(CancellationToken.None)))!;

            Assert.That(error.Message, Does.Contain("trusted again once the mount is seen to move"));
            Assert.That(error.Message, Does.Not.Contain("confirm by eye"));
        }

        // #2173 — the reverse: a mount whose driver does not say its Home is a sensor search keeps the eye check.
        [Test]
        public async Task Confirm_home_off_home_on_a_model_home_mount_asks_for_the_eye_check() {
            using var svc = await ConnectedAtFalseZeroAsync();
            Current.ReportsAtHome = false;

            var error = (await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ConfirmHomeAsync(CancellationToken.None)))!;

            Assert.That(error.Message, Does.Contain("confirm by eye"));
        }

        // #2173 — a sequence GoTo refused on a sensor-search mount fails the instruction with words that name Home as
        // clearing the flag by itself.
        [Test]
        public async Task A_sequence_goto_refused_on_a_sensor_search_mount_says_Home_clears_it_by_itself() {
            using var svc = await ConnectedAtFalseZeroAsync(also: f => f.SaysHomeIsSensorSearch());

            var failed = (await Assert.ThrowsAsync<SequenceEntityFailedException>(() => ((ITelescopeMediator)svc).SlewToCoordinatesAsync(Target, CancellationToken.None)))!;

            Assert.That(failed.Message, Does.Contain("trusted again once the mount is seen to move"));
            Assert.That(failed.Message, Does.Not.Contain("confirm by eye"));
            Assert.That(Current.Slews, Is.Zero);
        }

        [Test]
        public async Task Confirm_home_clears_at_once_and_a_goto_then_goes() {
            using var svc = await ConnectedAtFalseZeroAsync();

            await svc.ConfirmHomeAsync(CancellationToken.None);

            Assert.That(Runtime(svc)!.PositionUnverified, Is.False, "no tick needed");
            await svc.SlewAsync(new SlewRequestDto(5.0, 30.0), null, CancellationToken.None);
            await WaitForAsync(() => Current.Slews == 1, "the goto");
        }

        [Test]
        public async Task The_flag_survives_a_reconnect_and_a_daemon_restart() {
            var store = new MemoryPoseStore();
            using (var svc = await ConnectedAtFalseZeroAsync(store)) {
                Assert.That(store.UnverifiedReason(Mount.UniqueId), Is.EqualTo(MountPoseTrust.AtHomeOnConnect), "persisted");
                _configure = f => { f.ReportsAtHome = false; f.Dec = 30; }; // nudged off the false zero
                await svc.DisconnectAsync(null, CancellationToken.None);
                await ConnectAsync(svc);
                Assert.That(Runtime(svc)!.PositionUnverified, Is.True, "a reconnect keeps it");
            }

            using var restarted = Build(store);
            await ConnectAsync(restarted);
            Assert.That(Runtime(restarted)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect),
                "a restarted daemon reading the mount off Home still does not trust it");

            Current.ReportsAtHome = true; // the hand controller's zero search
            await restarted.ConfirmHomeAsync(CancellationToken.None);
            Assert.That(store.UnverifiedReason(Mount.UniqueId), Is.Null, "the confirm is persisted too");
        }

        // #2066, #2065 (ARA-10) — a routine that could not confirm its stop (Polar Align's mount_did_not_stop) marks the pose
        // unverified: the mount may have run on. GoTo is refused as after a power loss, the flag is kept across a reconnect
        // and in the store, and only a confirmed Home clears it. The reconnect is the power cycle the user makes after "cut
        // the mount's power" (M28): the mount wakes reporting Home wherever it points, and #2173 review that replaces
        // stop_unconfirmed with at_home_on_connect, so a later Home needs the eye check.
        [Test]
        public async Task A_routine_that_could_not_confirm_its_stop_leaves_the_position_unverified_until_Home_is_confirmed() {
            var store = new MemoryPoseStore();
            _configure = f => { f.ReportsAtHome = false; f.Dec = 30; };
            using var svc = Build(store);
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is not null, "a position");
            Assert.That(Runtime(svc)!.PositionUnverified, Is.False, "a trusted mount to begin with");

            svc.MarkPositionUnverified(MountPoseTrust.StopUnconfirmed);

            Assert.That(Runtime(svc)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.StopUnconfirmed));
            Assert.That(store.UnverifiedReason(Mount.UniqueId), Is.EqualTo(MountPoseTrust.StopUnconfirmed), "persisted");
            var refusal = await RefusedAsync(() => svc.SlewAsync(new SlewRequestDto(5.0, 30.0), null, CancellationToken.None));
            Assert.That(refusal.Parameters["position_unverified_reason"], Is.EqualTo(MountPoseTrust.StopUnconfirmed));
            Assert.That(refusal.Message, Does.Contain("could not confirm that the mount stopped"), "worded for the stop, not a power loss");

            await svc.DisconnectAsync(null, CancellationToken.None);
            _configure = f => { f.ReportsAtHome = true; f.Dec = 90; }; // power_cycle_reports_home: the false zero
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is > 89.0, "the first read after the power cycle");
            await Task.Delay(100);
            await WaitForAsync(() => Runtime(svc)?.PositionUnverifiedReason == MountPoseTrust.AtHomeOnConnect,
                "the power cycle's false Home replacing the reason (it does not clear it)");
            await WaitForAsync(() => store.UnverifiedReason(Mount.UniqueId) == MountPoseTrust.AtHomeOnConnect, "the new reason persisted");
            Assert.That(Current.Slews, Is.Zero);

            // The hand controller's zero search: the mount really is at Home now.
            await svc.ConfirmHomeAsync(CancellationToken.None);
            Assert.That(Runtime(svc)!.PositionUnverified, Is.False);
            Assert.That(store.UnverifiedReason(Mount.UniqueId), Is.Null);
        }

        [Test]
        public async Task Home_reported_in_session_with_nothing_commanded_makes_the_position_unverified() {
            using var svc = Build();
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is not null, "a position");
            Assert.That(Runtime(svc)!.PositionUnverified, Is.False);

            // A power blip the session survived: the mount comes back at its false zero.
            Current.Dec = 90;
            Current.ReportsAtHome = true;

            await WaitForAsync(() => Runtime(svc)?.PositionUnverified == true, "the in-session detection");
            Assert.That(Runtime(svc)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.AtHomeWithoutCommand));
        }

        [Test]
        public async Task A_sequence_sync_that_moves_the_reported_dec_is_not_a_jump() {
            // A plan's Center slews, exposes and solves for longer than the quiet window, then syncs: the sync
            // moves the reported Dec by more than 2° between two still ticks. That is Ara's own command, not a
            // power blip, and must not lock out every later GoTo of the run.
            var clock = new ManualClock();
            using var svc = Build(clock: clock);
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is 30.0, "a still position");
            clock.Advance(MountPoseTrust.QuietWindow + TimeSpan.FromSeconds(30));

            Assert.That(await ((ITelescopeMediator)svc).Sync(new Coordinates(Angle.ByHours(5.0), Angle.ByDegree(35.0), Epoch.JNOW)), Is.True);
            Assert.That(Current.Syncs, Is.EqualTo(1));
            Current.Dec = 35.0; // the mount now reports the synced position

            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is 35.0, "the synced position");
            await Task.Delay(200); // several still ticks after the change
            Assert.That(Runtime(svc)!.PositionUnverified, Is.False, "the sync explains the Dec change");
        }

        [Test]
        public void The_file_store_keeps_a_flag_across_instances() {
            var dir = Path.Combine(Path.GetTempPath(), $"oara-pose-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try {
                new FileMountPoseStore(dir).Store("mount-1", MountPoseTrust.AtHomeOnConnect);
                Assert.That(new FileMountPoseStore(dir).UnverifiedReason("mount-1"), Is.EqualTo(MountPoseTrust.AtHomeOnConnect));

                new FileMountPoseStore(dir).Store("mount-1", null);
                Assert.That(new FileMountPoseStore(dir).UnverifiedReason("mount-1"), Is.Null);
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
