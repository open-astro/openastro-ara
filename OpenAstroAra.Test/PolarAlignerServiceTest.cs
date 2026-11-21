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
            // All three sends must start inside the window, also on a loaded CI runner (150 ms fitted only two there).
            service.StopWindow = TimeSpan.FromSeconds(1.5);

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
            // All three sends must start inside the window, also on a loaded CI runner (150 ms fitted only two there).
            service.StopWindow = TimeSpan.FromSeconds(1.5);

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
        public async Task A_verified_stop_on_a_slow_link_ends_within_its_window() {
            // #2069 — Emergency Stop's aligner step runs two verified stops inside its 10 s bound: a Status read that takes
            // longer than the window (a slow link) must not hold the stop past it.
            var actions = Healthy();
            actions.Replies["PolarAlign:Status"] = () => {
                Thread.Sleep(TimeSpan.FromSeconds(6));
                return StillStatus;
            };
            _switches.Connected.Add(new("sw", "Base", actions));
            var service = Build();
            var clock = System.Diagnostics.Stopwatch.StartNew();

            await Assert.ThatAsync(() => service.StopAsync(null, CancellationToken.None),
                Throws.InvalidOperationException.With.Message.StartsWith("stop_not_confirmed"));

            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)), "the 150 ms window bounds the stop, not the 6 s read");
            Assert.That(service.StopOwed, Is.True, "a read that did not answer in time confirms nothing");
        }

        /// <summary>Records each log entry's event id, level and text.</summary>
        private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<PolarAlignerService> {
            public ConcurrentQueue<(int Id, Microsoft.Extensions.Logging.LogLevel Level, string Text)> Entries { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                    TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                Entries.Enqueue((eventId.Id, logLevel, formatter(state, exception)));
        }

        [Test]
        public async Task A_verified_stop_that_leaves_a_device_call_running_logs_a_warning_with_the_count() {
            // #2069 — a call still waiting at the window's end keeps its own thread until it returns (a dead link: the HTTP
            // timeout). Each one the stop abandons is counted and logged, so a dead link holding threads shows in the log.
            // The Status read is held by the test (not timed): it is under way when the window ends and answers only once the
            // stop has returned. The window is long enough for the Stop send to answer on a loaded runner, so the read is the
            // one call left running.
            var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var actions = Healthy();
            actions.Replies["PolarAlign:Status"] = () => {
                readEntered.TrySetResult();
                release.Task.Wait(TimeSpan.FromSeconds(30));
                return StillStatus;
            };
            _switches.Connected.Add(new("sw", "Base", actions));
            var logger = new RecordingLogger();
            var service = new PolarAlignerService(_switches, _mount, logger: logger) {
                StopWindow = TimeSpan.FromSeconds(1.5), StopPoll = TimeSpan.FromMilliseconds(5),
            };

            try {
                await Assert.ThatAsync(() => service.StopAsync(null, CancellationToken.None),
                    Throws.InvalidOperationException.With.Message.StartsWith("stop_not_confirmed"));
                Assert.That(readEntered.Task.IsCompleted, Is.True, "the Stop answered and the Status read was under way at the window's end");
                Assert.That(PolarAlignerVerifiedStop.StillRunning, Is.GreaterThanOrEqualTo(1), "the Status read still holds its thread");
            } finally {
                release.TrySetResult();
            }

            var warnings = logger.Entries.Where(e => e.Id == 4644).ToList();
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.Multiple(() => {
                Assert.That(warnings[0].Level, Is.EqualTo(Microsoft.Extensions.Logging.LogLevel.Warning));
                Assert.That(warnings[0].Text, Does.Contain("1 device call(s) still waiting"), "the held read, and no call started after it");
                Assert.That(StopCalls(actions), Is.EqualTo(1), "no Stop resent once the read used up the window");
            });
        }

        [Test]
        public async Task A_stop_outcome_is_filed_under_the_device_it_went_to_not_the_newest_one_handed_out() {
            // #2069 — the routine's stop may go to the device object it read at the start of its run, older than the latest
            // one Current() handed out (another Polar Aligner connected in between): the outcome belongs to the older one.
            _switches.Connected.Add(new("sw-old", "Old base", Healthy()));
            var service = Build();
            var older = service.Current()!;

            _switches.Connected.Clear();
            _switches.Connected.Add(new("sw-new", "New base", Healthy()));
            Assert.That(service.Current(), Is.Not.Null);

            // Healthy reads an axis moving whatever is sent: the older device's stop is not confirmed.
            var outcome = await PolarAlignerVerifiedStop.StopAsync(older, service.StopWindow, service.StopPoll);
            Assert.That(outcome.Confirmed, Is.False);
            service.NoteStopOutcome(older, outcome.Confirmed);

            Assert.That(service.StopOwed, Is.False, "the newer device owes nothing");
            _switches.Connected.Clear();
            _switches.Connected.Add(new("sw-old", "Old base", Healthy()));
            Assert.That(service.StopOwed, Is.True, "the older device, which the Stop went to, owes it");
        }

        /// <summary>A device that never confirms a Stop (an axis reads moving) leaves one owed on <paramref name="service"/>.</summary>
        private static async Task OweAStopAsync(PolarAlignerService service) {
            await Assert.ThatAsync(() => service.StopAsync(null, CancellationToken.None),
                Throws.InvalidOperationException.With.Message.StartsWith("stop_not_confirmed"));
            Assert.That(service.StopOwed, Is.True);
        }

        [Test]
        public async Task A_different_polar_aligner_that_connects_is_not_held_by_the_old_ones_owed_stop() {
            // #2069 — the owed Stop belongs to the device it was sent to: a swapped-in device (another UniqueId) can be homed.
            _switches.Connected.Add(new("sw-old", "Old base", Healthy()));
            var service = Build();
            await OweAStopAsync(service);

            _switches.Connected.Clear();
            var replacement = Healthy();
            replacement.Replies["PolarAlign:Status"] = () => StillStatus;
            _switches.Connected.Add(new("sw-new", "New base", replacement));

            Assert.That(service.StopOwed, Is.False, "the new device owes nothing");
            await service.HomeAsync(null, CancellationToken.None);
            Assert.That(replacement.Calls, Does.Contain("PolarAlign:Home"));

            // The old device comes back: its Stop is still owed.
            _switches.Connected.Clear();
            _switches.Connected.Add(new("sw-old", "Old base", Healthy()));
            Assert.That(service.StopOwed, Is.True);
        }

        [Test]
        public async Task A_plain_reconnect_of_the_same_polar_aligner_keeps_its_owed_stop() {
            // #2069 — the same device (its UniqueId) on a new connection may still be moving: Home stays refused.
            _switches.Connected.Add(new("sw", "Base", Healthy()));
            var service = Build();
            await OweAStopAsync(service);

            _switches.Connected.Clear();
            var reconnected = Healthy();
            _switches.Connected.Add(new("sw", "Base", reconnected));

            Assert.That(service.StopOwed, Is.True);
            await Assert.ThatAsync(() => service.HomeAsync(null, CancellationToken.None), Throws.InvalidOperationException);
            Assert.That(reconnected.Calls, Does.Not.Contain("PolarAlign:Home"));
        }

        [Test]
        public async Task An_owed_stop_is_not_owed_while_no_polar_aligner_is_connected_and_returns_with_its_device() {
            // #2069 — the person cut the owing device's power (its carrier disconnected): nothing can be stopped or moved, so
            // nothing is owed now. The same device on a new connection owes it again.
            _switches.Connected.Add(new("sw", "Base", Healthy()));
            var service = Build();
            await OweAStopAsync(service);

            _switches.Connected.Clear();
            Assert.That(service.StopOwed, Is.False, "no device connected owes a Stop");

            _switches.Connected.Add(new("sw", "Base", Healthy()));
            Assert.That(service.StopOwed, Is.True, "the same device came back: still owed");
        }

        [Test]
        public async Task Home_with_no_polar_aligner_connected_says_so_while_a_stop_is_owed() {
            _switches.Connected.Add(new("sw", "Base", Healthy()));
            var service = Build();
            await OweAStopAsync(service);
            _switches.Connected.Clear();

            await Assert.ThatAsync(() => service.HomeAsync(null, CancellationToken.None),
                Throws.InvalidOperationException.With.Message.EqualTo("no Polar Aligner is connected"));
        }

        [Test]
        public async Task A_stop_delivered_after_a_failed_send_that_still_reads_moving_reports_the_axis_moving() {
            // #2069 — the first send is lost (link down), the second gets through but an axis still reads moving: the 409
            // says what the latest send found, not the first send's error.
            var actions = Healthy(); // an axis reads moving whatever is sent
            var sends = 0;
            actions.Replies["PolarAlign:Stop"] = () => ++sends == 1 ? throw new ASCOM.NotConnectedException("link down") : string.Empty;
            _switches.Connected.Add(new("sw", "Base", actions));
            var service = Build();
            // At least two sends must start inside the window, also on a loaded CI runner.
            service.StopWindow = TimeSpan.FromSeconds(1.5);

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StopAsync(null, CancellationToken.None));

            Assert.That(refused!.Message, Does.StartWith("stop_not_confirmed"));
            Assert.That(sends, Is.GreaterThanOrEqualTo(2));
            Assert.That(refused.Message, Does.Contain(PolarAlignerVerifiedStop.StillMoving));
            Assert.That(refused.Message, Does.Not.Contain("link down"));
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
