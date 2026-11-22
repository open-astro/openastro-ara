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
    /// that position unverified and refuses every move that trusts it (GoTo, Park, model Home, the flip, polar
    /// alignment's motor moves) until the user confirms Home; Sync, the pad and Stop still work. Against a fake
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

        private TelescopeService Build(IMountPoseStore? store = null, IProfileService? profile = null, TimeSpan? refresh = null, TimeProvider? clock = null) =>
            new(faults: _faults, profileService: profile, poseStore: store) {
                PoseClock = clock ?? TimeProvider.System,
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

        // #2173 review — the one mount whose FindHome is known to be its home-sensor search: the HAE29C on an AlpacaBridge
        // that runs :MSH# for it and keeps an unmoved search from reading as Home (6.0.0~beta3 and later).
        private static void AsHae29c(FakeTelescopeDevice f) {
            f.Name = "iOptron HAE29C EQ";
            f.DriverInfo = "AlpacaCore iOptron Driver v1.0";
            f.DriverVersion = "6.0.0~beta4";
        }

        // The RC51 case: the mount wakes at its false zero and Ara connects to it.
        private async Task<TelescopeService> ConnectedAtFalseZeroAsync(IMountPoseStore? store = null, IProfileService? profile = null,
                bool hae29c = false, TimeProvider? clock = null) {
            _configure = f => { f.ReportsAtHome = true; f.Dec = 90; if (hae29c) { AsHae29c(f); } };
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

        // #2173 — on the HAE29C, Home (the mount's home-sensor search) is the way out of an unverified position, so it
        // runs; GoTo and Park, which drive by the untrusted model, stay refused, and the Park refusal names Home.
        [Test]
        public async Task Home_runs_while_unverified_but_GoTo_and_Park_stay_refused() {
            using var svc = await ConnectedAtFalseZeroAsync(hae29c: true);

            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => Current.Homes == 1, "the Home search");

            var park = await RefusedAsync(() => svc.ParkAsync(new ParkRequestDto(), null, CancellationToken.None));
            Assert.That(park.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified));
            Assert.That(park.Message, Does.Contain("The safe way out is Home"));
            var goTo = await RefusedAsync(() => svc.SlewAsync(new SlewRequestDto(5.0, 30.0), null, CancellationToken.None));
            Assert.That(goTo.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified));
            await Task.Delay(100);
            Assert.That(Current.Parks, Is.Zero);
            Assert.That(Current.Slews, Is.Zero);
        }

        // #2173 review — any other mount's Home drives to where its model thinks Home is (counterweight up, or the pier,
        // when the model is wrong), so it stays refused while unverified, and the refusal sends the user to the hand
        // controller's search. Nothing reaches the mount.
        [Test]
        public async Task Home_stays_refused_while_unverified_on_a_mount_whose_home_is_no_sensor_search() {
            using var svc = await ConnectedAtFalseZeroAsync();

            var home = await RefusedAsync(() => svc.FindHomeAsync(null, CancellationToken.None));

            Assert.That(home.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified));
            Assert.That(home.Message, Does.Contain("hand controller").And.Not.Contain("The safe way out is Home"));
            await Task.Delay(100);
            Assert.That(Current.Homes, Is.Zero);
        }

        [TestCase("iOptron HAE29C EQ", "AlpacaCore iOptron Driver v1.0", "6.0.0~beta3", ExpectedResult = true)]
        [TestCase("iOptron HAE29C EQ", "AlpacaCore iOptron Driver v1.0", "6.0.0", ExpectedResult = true)]
        [TestCase("iOptron HAE29C EQ", "AlpacaCore iOptron Driver v1.0", "7.1.0~beta1", ExpectedResult = true)]
        [TestCase("iOptron HAE29C EQ", "AlpacaCore iOptron Driver v1.0", "6.0.0~beta2", ExpectedResult = false)]
        [TestCase("iOptron HAE29C EQ", "AlpacaCore iOptron Driver v1.0", "5.0.2", ExpectedResult = false)]
        [TestCase("iOptron HAE29C EQ", "AlpacaCore iOptron Driver v1.0", "", ExpectedResult = false)]
        [TestCase("iOptron CEM70", "AlpacaCore iOptron Driver v1.0", "6.0.0~beta4", ExpectedResult = false)]
        [TestCase("iOptron HAE29C EQ", "Some other HAE29C driver", "6.0.0~beta4", ExpectedResult = false)]
        public bool Home_is_a_sensor_search_only_on_the_HAE29C_on_AlpacaBridge_6_0_0_beta3_or_later(string name, string info, string version) =>
            TelescopeService.HomeIsSensorSearch(name, info, version);

        // #2173 — a HAE29C that ignored the search, sent Home at its false zero, goes nowhere and reports Home throughout:
        // that is no search, so the position stays unverified (the eye confirmation is still needed).
        [Test]
        public async Task A_home_that_never_leaves_a_false_zero_does_not_trust_the_position() {
            using var svc = await ConnectedAtFalseZeroAsync(hae29c: true);

            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => Current.Homes == 1, "the Home");
            await Task.Delay(300); // many watch polls at Home and still

            Assert.That(Runtime(svc)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
        }

        // #2173 review — the same Home at a false zero over the slow link: one AtHome read fails during the watch. A
        // failed read is no "left Home", so nothing was seen moving and the position stays unverified.
        [Test]
        public async Task A_failed_at_home_read_during_a_home_at_a_false_zero_stays_unverified() {
            using var svc = await ConnectedAtFalseZeroAsync(hae29c: true);

            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => Current.Homes == 1, "the Home");
            Current.AtHomeReadFails = true; // a few watch polls lose their AtHome read
            await Task.Delay(100);
            Current.AtHomeReadFails = false;
            await Task.Delay(300); // many watch polls at Home and still again

            Assert.That(Runtime(svc)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
        }

        // #2173 review — the search runs (seen slewing, away from Home), then the mount loses power while AlpacaBridge stays
        // connected: its reads fail, and it wakes reporting Home wherever it points. What was seen before the gap no longer
        // counts, so the Home it reports after it is not trusted.
        [Test]
        public async Task A_mount_that_resets_in_the_middle_of_the_search_is_not_trusted() {
            using var svc = await ConnectedAtFalseZeroAsync(hae29c: true);
            Current.ReportsAtHome = false; // the search: away from Home and slewing
            Current.IsSlewing = true;
            Current.Dec = 60;

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

            Assert.That(Runtime(svc)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
        }

        // #2173 (Joey, 2026-11-21) — "If we drove it home, shouldn't it say: yes, I am home?" The jump flagged the
        // position; Home runs the search, the mount lands at Home reading Dec +90.71°, and the position is trusted again
        // with no eye check, persisted, and shown on the runtime the client reads.
        [Test]
        public async Task A_home_search_that_ends_at_home_at_dec_90_71_trusts_the_position_again() {
            var store = new MemoryPoseStore();
            _configure = f => { f.ReportsAtHome = false; f.Dec = 23.52; AsHae29c(f); };
            using var svc = Build(store);
            await ConnectAsync(svc);
            await WaitForAsync(() => Runtime(svc)?.DeclinationDegrees is 23.52, "a position");
            svc.MarkPositionUnverified(MountPoseTrust.PoseJumpInSession);
            Assert.That(store.UnverifiedReason(Mount.UniqueId), Is.EqualTo(MountPoseTrust.PoseJumpInSession));

            Current.IsSlewing = true; // the search runs: slewing, away from Home
            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => Current.Homes == 1, "the Home search");
            await Task.Delay(100);
            Current.Dec = 90.71; // where the search lands: the Dec axis just past the pole
            Current.ReportsAtHome = true;
            Current.IsSlewing = false;

            await WaitForAsync(() => Runtime(svc)?.PositionUnverified == false, "the position trusted again after the search");
            Assert.That(Current.Homes, Is.EqualTo(1));
            Assert.That(store.UnverifiedReason(Mount.UniqueId), Is.Null, "persisted");
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
        // the mount's power" (M28): the mount wakes reporting Home wherever it points, and the flag stays stop_unconfirmed.
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
            Assert.That(Runtime(svc)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.StopUnconfirmed),
                "the power cycle's false Home neither clears nor replaces it");
            Assert.That(store.UnverifiedReason(Mount.UniqueId), Is.EqualTo(MountPoseTrust.StopUnconfirmed));
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
