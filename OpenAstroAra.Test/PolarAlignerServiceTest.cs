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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1972/#1973 — which connected device is the Polar Aligner, what GET reports about it,
    /// Stop and Home, and the Switch and Telescope services lending their driver actions.</summary>
    [TestFixture]
    public class PolarAlignerServiceTest {

        private const string HealthyInfo = """
            {"tiers":[1],"deviceName":"PA","deviceVersion":"1.0","axes":{"az":{"maxMove":60,"resolution":0.05},"alt":{"maxMove":30,"resolution":0.1}},
             "simultaneousMoves":false,"movesWhileTracking":true,"movesWhileSlewing":false,"canStop":true,"settleSeconds":1.5}
            """;
        private const string HealthyStatus = """{"az":{"moving":true,"position":-4.5},"alt":{"moving":false,"position":2,"fault":"limit"}}""";
        private const string StillStatus = """{"az":{"moving":false,"position":-4.5},"alt":{"moving":false,"position":2}}""";

        /// <summary>A device's actions in memory: answers by action name, records every call.</summary>
        private sealed class ScriptedActions(params string[] supported) : IDriverActions {
            public Dictionary<string, Func<string>> Replies { get; } = new(StringComparer.Ordinal);
            public ConcurrentQueue<string> Calls { get; } = new();
            public IReadOnlyList<string> SupportedActions => supported;
            public string Action(string name, string parameters) {
                Calls.Enqueue(name);
                return Replies.TryGetValue(name, out var reply) ? reply() : string.Empty;
            }
        }

        private sealed class Carriers : IDriverActionsCarriers {
            public List<DriverActionsCarrier> Connected { get; } = [];
            public IReadOnlyList<DriverActionsCarrier> ConnectedActionCarriers() => Connected.ToList();
        }

        private static readonly string[] JustStop = ["PolarAlign:Stop"];
        private static readonly string[] StopThenHome = ["PolarAlign:Stop", "PolarAlign:Status", "PolarAlign:Home"];

        private Carriers _switches = null!;
        private Carriers _mount = null!;

        [SetUp]
        public void SetUp() {
            _switches = new Carriers();
            _mount = new Carriers();
        }

        // #2069 — the verified stop at bench speed.
        private PolarAlignerService Build() => new(_switches, _mount) { StopWindow = TimeSpan.FromMilliseconds(150), StopPoll = TimeSpan.FromMilliseconds(5) };

        private static ScriptedActions Healthy() {
            var actions = new ScriptedActions("PolarAlign:Info", "PolarAlign:Status");
            actions.Replies["PolarAlign:Info"] = () => HealthyInfo;
            actions.Replies["PolarAlign:Status"] = () => HealthyStatus;
            return actions;
        }

        // ─── Recognition ───

        [TestCase("PolarAlign:Info")]
        [TestCase("polaralign:info")]
        [TestCase("POLARALIGN:INFO")]
        public void A_device_listing_the_info_action_in_any_case_is_a_polar_aligner(string listed) {
            _switches.Connected.Add(new("sw", "Power box", new ScriptedActions(listed)));
            Assert.That(Build().Current(), Is.Not.Null, "switch");

            SetUp();
            _mount.Connected.Add(new("mount", "Mount", new ScriptedActions("Other", listed)));
            Assert.That(Build().Current(), Is.Not.Null, "mount");
        }

        [Test]
        public void A_device_without_the_info_action_is_not_a_polar_aligner_whatever_its_name() {
            _switches.Connected.Add(new("sw", "Polar Aligner", new ScriptedActions("PolarAlign:Move", "Other")));
            _mount.Connected.Add(new("mount", "Polar Aligner mount", new ScriptedActions()));

            var service = Build();
            Assert.Multiple(() => {
                Assert.That(service.Current(), Is.Null);
                Assert.That(service.IsPresent, Is.False);
            });
        }

        // ─── Derivation ───

        [Test]
        public void A_switch_wins_over_the_mount() {
            var sw = Healthy();
            _switches.Connected.Add(new("sw", "Base", sw));
            _mount.Connected.Add(new("mount", "Mount", Healthy()));

            Build().Current()!.Stop();

            Assert.That(sw.Calls, Is.EqualTo(JustStop));
        }

        [Test]
        public async Task Between_switches_the_lowest_unique_id_wins() {
            _switches.Connected.Add(new("b-box", "B", Healthy()));
            _switches.Connected.Add(new("a-box", "A", Healthy()));
            _switches.Connected.Add(new("0-plain", "Plain", new ScriptedActions()));

            var dto = await Build().GetAsync(CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(dto.Carrier, Is.EqualTo("switch"));
                Assert.That(dto.DeviceId, Is.EqualTo("a-box"));
                Assert.That(dto.Name, Is.EqualTo("A"));
            });
        }

        [Test]
        public async Task Nothing_connected_is_no_polar_aligner() {
            var service = Build();
            var dto = await service.GetAsync(CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(service.Current(), Is.Null);
                Assert.That(dto.Present, Is.False);
                Assert.That(dto.Carrier, Is.Null);
            });
        }

        [Test]
        public async Task The_mount_carries_it_when_no_switch_does() {
            _mount.Connected.Add(new("mount-uid", "Mount", Healthy()));

            var dto = await Build().GetAsync(CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(dto.Carrier, Is.EqualTo("mount"));
                Assert.That(dto.DeviceId, Is.Null, "the mount is not addressed by id");
                Assert.That(dto.Name, Is.EqualTo("Mount"));
            });
        }

        // ─── GET ───

        [Test]
        public async Task Get_reports_capabilities_and_state_of_a_healthy_device() {
            _switches.Connected.Add(new("sw", "Base", Healthy()));

            var dto = await Build().GetAsync(CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(dto.Present, Is.True);
                Assert.That(dto.Drivable, Is.True);
                Assert.That(dto.NotDrivableReason, Is.Null);
                Assert.That(dto.Capabilities, Is.EqualTo(new PolarAlignerCapabilitiesDto(
                    "PA", "1.0", 60, 0.05, 30, 0.1, false, true, false, true, 1.5)));
                Assert.That(dto.Moving, Is.True);
                Assert.That(dto.AzimuthPositionArcmin, Is.EqualTo(-4.5));
                Assert.That(dto.AltitudePositionArcmin, Is.EqualTo(2));
                Assert.That(dto.AzimuthFault, Is.Null);
                Assert.That(dto.AltitudeFault, Is.EqualTo("limit"));
            });
        }

        [Test]
        public async Task Get_reads_info_once_per_connection() {
            var actions = Healthy();
            _switches.Connected.Add(new("sw", "Base", actions));
            var service = Build();

            await service.GetAsync(CancellationToken.None);
            await service.GetAsync(CancellationToken.None);

            Assert.That(actions.Calls.Count(c => c == "PolarAlign:Info"), Is.EqualTo(1));
        }

        [Test]
        public async Task Get_when_info_fails_reports_the_device_as_unreadable_and_does_not_throw() {
            var actions = Healthy();
            actions.Replies["PolarAlign:Info"] = () => throw new ASCOM.DriverException("busy");
            _switches.Connected.Add(new("sw", "Base", actions));

            var dto = await Build().GetAsync(CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(dto.Present, Is.True);
                Assert.That(dto.Drivable, Is.False);
                Assert.That(dto.NotDrivableReason, Is.EqualTo("info_unreadable"));
                Assert.That(dto.Capabilities, Is.Null);
                Assert.That(dto.AzimuthPositionArcmin, Is.EqualTo(-4.5), "status is still read");
            });
        }

        [Test]
        public async Task Get_when_status_fails_leaves_the_positions_unknown() {
            var actions = Healthy();
            actions.Replies["PolarAlign:Status"] = () => "not json";
            _switches.Connected.Add(new("sw", "Base", actions));

            var dto = await Build().GetAsync(CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(dto.Drivable, Is.True);
                Assert.That(dto.Moving, Is.False);
                Assert.That(dto.AzimuthPositionArcmin, Is.Null);
                Assert.That(dto.AltitudePositionArcmin, Is.Null);
            });
        }

        [Test]
        public async Task Absent_positions_stay_null_never_zero() {
            var actions = Healthy();
            actions.Replies["PolarAlign:Status"] = () => """{"az":{"moving":false},"alt":{"moving":false}}""";
            _switches.Connected.Add(new("sw", "Base", actions));

            var dto = await Build().GetAsync(CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(dto.AzimuthPositionArcmin, Is.Null);
                Assert.That(dto.AltitudePositionArcmin, Is.Null);
            });
        }

        [Test]
        public async Task A_tier_2_only_device_is_shown_but_not_drivable_with_the_reason() {
            var actions = Healthy();
            actions.Replies["PolarAlign:Info"] = () => """{"tiers":[2],"axes":{"az":{"maxMove":60},"alt":{"maxMove":30}}}""";
            _switches.Connected.Add(new("sw", "Base", actions));

            var dto = await Build().GetAsync(CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(dto.Present, Is.True);
                Assert.That(dto.Drivable, Is.False);
                Assert.That(dto.NotDrivableReason, Is.EqualTo("device_runs_its_own_loop"));
                Assert.That(dto.Capabilities, Is.Not.Null);
            });
        }

        // ─── Stop / Home ───

        [Test]
        public async Task Stop_and_home_call_the_device() {
            var actions = Healthy();
            actions.Replies["PolarAlign:Status"] = () => StillStatus;
            _switches.Connected.Add(new("sw", "Base", actions));
            var service = Build();

            var stop = await service.StopAsync("k1", CancellationToken.None);
            var home = await service.HomeAsync(null, CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(actions.Calls, Is.EqualTo(StopThenHome));
                Assert.That(stop.OperationType, Is.EqualTo("polaraligner.stop"));
                Assert.That(stop.IdempotencyKey, Is.EqualTo("k1"));
                Assert.That(home.OperationType, Is.EqualTo("polaraligner.home"));
            });
        }

        private sealed class FakeRoutine(ScriptedActions device) : IPolarAlignerDriver {
            public bool DrivesPolarAligner { get; set; } = true;
            public Task StopPolarAlignerAsync() {
                device.Calls.Enqueue("routine stops its motor loop");
                DrivesPolarAligner = false;
                return Task.CompletedTask;
            }
        }

        private static readonly string[] RoutineThenDevice = ["routine stops its motor loop", "PolarAlign:Stop", "PolarAlign:Status"];

        [Test]
        public async Task Stop_ends_a_running_routines_motor_loop_before_it_stops_the_device() {
            // The emergency stop and the Stop route both come through here. A device Stop alone reads to
            // the routine as a finished move, and its next measurement would move the motors again.
            var actions = Healthy();
            actions.Replies["PolarAlign:Status"] = () => StillStatus;
            _switches.Connected.Add(new("sw", "Base", actions));
            var routine = new FakeRoutine(actions);
            var service = new PolarAlignerService(_switches, _mount, () => routine);

            await service.StopAsync(null, CancellationToken.None);

            Assert.That(actions.Calls, Is.EqualTo(RoutineThenDevice));
        }

        [Test]
        public async Task Home_is_refused_while_a_routine_moves_the_motors() {
            var actions = Healthy();
            _switches.Connected.Add(new("sw", "Base", actions));
            var service = new PolarAlignerService(_switches, _mount, () => new FakeRoutine(actions));

            await Assert.ThatAsync(() => service.HomeAsync(null, CancellationToken.None), Throws.InvalidOperationException);
            Assert.That(actions.Calls, Is.Empty, "nothing reached the device");
        }

        // ─── #2069 (ARA-19): the verified stop (the Stop route, Emergency Stop and the parks) ───

        private static int StopCalls(ScriptedActions actions) => actions.Calls.Count(c => c == "PolarAlign:Stop");

        [Test]
        public async Task Aligner_stop_is_retried_until_the_device_reports_still() {
            var actions = Healthy();
            // The first two Stops are lost: the device reads moving until the third arrives.
            actions.Replies["PolarAlign:Status"] = () => StopCalls(actions) < 3 ? HealthyStatus : StillStatus;
            _switches.Connected.Add(new("sw", "Base", actions));
            var service = Build();

            var stop = await service.StopAsync(null, CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(StopCalls(actions), Is.EqualTo(PolarAlignerVerifiedStop.Sends));
                Assert.That(stop.OperationType, Is.EqualTo("polaraligner.stop"));
                Assert.That(service.StopOwed, Is.False);
            });
        }

        [Test]
        public async Task A_stop_the_device_never_confirms_is_refused_and_owed_until_one_is() {
            var actions = Healthy(); // an axis reads moving whatever is sent
            _switches.Connected.Add(new("sw", "Base", actions));
            var service = Build();

            await Assert.ThatAsync(() => service.StopAsync(null, CancellationToken.None),
                Throws.InvalidOperationException.With.Message.StartsWith("stop_not_confirmed"));
            Assert.That(StopCalls(actions), Is.EqualTo(PolarAlignerVerifiedStop.Sends), "sent three times, then given up");
            Assert.That(service.StopOwed, Is.True);
            await Assert.ThatAsync(() => service.HomeAsync(null, CancellationToken.None), Throws.InvalidOperationException,
                "no Home while a Stop is owed");
            Assert.That(actions.Calls, Does.Not.Contain("PolarAlign:Home"));

            actions.Replies["PolarAlign:Status"] = () => StillStatus;
            await service.StopAsync(null, CancellationToken.None);
            Assert.That(service.StopOwed, Is.False);
            await service.HomeAsync(null, CancellationToken.None);
            Assert.That(actions.Calls, Does.Contain("PolarAlign:Home"));
        }

        [Test]
        public async Task A_stop_the_device_refuses_is_confirmed_by_a_still_read() {
            // A driver without PolarAlign:Stop answers it with an error: the device answered, so a still read confirms it,
            // and no Stop is owed for ever (it would refuse every later Home and run).
            var actions = Healthy();
            actions.Replies["PolarAlign:Stop"] = () => throw new ASCOM.ActionNotImplementedException("PolarAlign:Stop");
            actions.Replies["PolarAlign:Status"] = () => StillStatus;
            _switches.Connected.Add(new("sw", "Base", actions));
            var service = Build();

            var stop = await service.StopAsync(null, CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(stop.OperationType, Is.EqualTo("polaraligner.stop"));
                Assert.That(service.StopOwed, Is.False);
            });
            await service.HomeAsync(null, CancellationToken.None);
            Assert.That(actions.Calls, Does.Contain("PolarAlign:Home"));
        }

        [Test]
        public async Task Stop_with_nothing_connected_is_refused() {
            await Assert.ThatAsync(() => Build().StopAsync(null, CancellationToken.None),
                Throws.InvalidOperationException.With.Message.EqualTo("no Polar Aligner is connected"));
            await Assert.ThatAsync(() => Build().HomeAsync(null, CancellationToken.None), Throws.InvalidOperationException);
        }

        [Test]
        public async Task A_device_error_reaches_the_caller_with_the_devices_text() {
            var actions = Healthy();
            actions.Replies["PolarAlign:Home"] = () => throw new ASCOM.DriverException("home switch not found");
            _switches.Connected.Add(new("sw", "Base", actions));

            await Assert.ThatAsync(() => Build().HomeAsync(null, CancellationToken.None),
                Throws.InvalidOperationException.With.Message.Contains("home switch not found"));
        }

        // ─── Carriers: the Switch and Telescope services ───

        private static DiscoveredDeviceDto Device(string uid, DeviceType type) => new(
            UniqueId: uid, Name: $"{uid} name", Type: type,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private static async Task WaitForAsync(Func<bool> condition, string what) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition()) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail($"timed out waiting for {what}");
                }
                await Task.Delay(10);
            }
        }

        [Test]
        public async Task A_connected_switch_lends_its_actions_until_it_disconnects() {
            // The service disposes it too; a second Dispose is harmless.
            using var fake = new FakeSwitchDevice { ActionResponder = (_, _) => string.Empty };
            fake.Actions.Add("PolarAlign:Info");
            using var switches = new SwitchService { RefreshPeriod = TimeSpan.FromMilliseconds(20), DeviceFactory = _ => fake };
            using var telescope = new TelescopeService();
            var service = new PolarAlignerService(switches, telescope);
            await switches.ConnectAsync(new ConnectRequestDto(Device("pa-box", DeviceType.Switch)), null, CancellationToken.None);
            await WaitForAsync(() => switches.ActionsFor("pa-box") is not null, "the switch's actions");

            var carrier = switches.ActionsFor("pa-box")!;
            service.Current()!.Stop();
            Assert.That(fake.ActionCalls.Select(c => c.Name), Is.EqualTo(JustStop));

            await switches.DisconnectAsync("pa-box", null, CancellationToken.None);
            Assert.Multiple(() => {
                Assert.That(service.Current(), Is.Null, "a disconnected carrier is no Polar Aligner");
                Assert.That(switches.ActionsFor("pa-box"), Is.Null);
                Assert.Throws<ASCOM.NotConnectedException>(() => carrier.Action("PolarAlign:Stop", string.Empty),
                    "a held carrier refuses once its connection is gone");
            });
            Assert.That(fake.ActionCalls, Has.Count.EqualTo(1), "nothing reached the released device");
        }

        [Test]
        public async Task A_connected_mount_lends_its_actions_until_it_disconnects() {
            using var fake = new FakeTelescopeDevice { ActionResponder = (_, _) => string.Empty };
            fake.Actions.Add("polaralign:info");
            using var switches = new SwitchService();
            using var telescope = new TelescopeService { RefreshPeriod = TimeSpan.FromMilliseconds(20), DeviceFactory = _ => fake };
            var service = new PolarAlignerService(switches, telescope);
            await telescope.ConnectAsync(new ConnectRequestDto(Device("mount", DeviceType.Telescope)), null, CancellationToken.None);
            await WaitForAsync(() => telescope.MountActions() is not null, "the mount's actions");

            service.Current()!.Home();
            Assert.That(fake.ActionCalls.Select(c => c.Name), Does.Contain("PolarAlign:Home"));

            await telescope.DisconnectAsync(null, CancellationToken.None);
            Assert.Multiple(() => {
                Assert.That(service.Current(), Is.Null);
                Assert.That(telescope.MountActions(), Is.Null);
            });
        }

        [Test]
        public void A_driver_that_throws_from_SupportedActions_lists_nothing() {
            Assert.That(LiveDriverActions.Read(() => throw new ASCOM.NotImplementedException("SupportedActions")), Is.Empty);
        }
    }
}
