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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§42.4 — the switch read-back watch: only written ports are checked, settle
    /// window, tolerance-of-range math, once-per-command-episode firing.</summary>
    [TestFixture]
    public class SwitchReadbackWatchTest {

        private static readonly DateTimeOffset T0 = new(2026, 7, 10, 5, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan PastSettle = SwitchReadbackWatch.DefaultSettleWindow + TimeSpan.FromSeconds(1);

        [Test]
        public void A_port_the_daemon_never_wrote_is_never_checked() {
            var w = new SwitchReadbackWatch();
            for (var i = 0; i < 10; i++) {
                Assert.That(w.Observe(3, readBack: 0, min: 0, max: 1, tolerancePct: 5, T0 + TimeSpan.FromSeconds(i * 2)),
                    Is.EqualTo(ReadbackVerdict.Idle),
                    "a user flipping ports at the power box is not a fault");
            }
        }

        [Test]
        public void The_settle_window_suppresses_early_readbacks() {
            var w = new SwitchReadbackWatch();
            w.Command(0, 1.0, T0);
            Assert.That(w.Observe(0, readBack: 0, 0, 1, 5, T0 + TimeSpan.FromSeconds(2)), Is.EqualTo(ReadbackVerdict.Settling),
                "a slow relay hasn't flipped yet — not a mismatch");
            Assert.That(w.Observe(0, readBack: 1, 0, 1, 5, T0 + PastSettle), Is.EqualTo(ReadbackVerdict.Idle),
                "settled into the commanded value");
        }

        [Test]
        public void A_persistent_mismatch_recommands_once_then_fires() {
            var w = new SwitchReadbackWatch();
            w.Command(0, 1.0, T0);
            var t = T0 + PastSettle;
            Assert.That(w.Observe(0, 0, 0, 1, 5, t), Is.EqualTo(ReadbackVerdict.Degraded));
            Assert.That(w.Observe(0, 0, 0, 1, 5, t + TimeSpan.FromSeconds(2)), Is.EqualTo(ReadbackVerdict.Degraded));
            // §42.2 rows 15/16 — the first exhausted streak asks for ONE re-command, not a fault…
            Assert.That(w.Observe(0, 0, 0, 1, 5, t + TimeSpan.FromSeconds(4)), Is.EqualTo(ReadbackVerdict.Recommand));
            // …and the re-command gets the same fair settle window as the original write.
            Assert.That(w.Observe(0, 0, 0, 1, 5, t + TimeSpan.FromSeconds(6)), Is.EqualTo(ReadbackVerdict.Settling));
            var t2 = t + TimeSpan.FromSeconds(4) + PastSettle;
            Assert.That(w.Observe(0, 0, 0, 1, 5, t2), Is.EqualTo(ReadbackVerdict.Degraded));
            Assert.That(w.Observe(0, 0, 0, 1, 5, t2 + TimeSpan.FromSeconds(2)), Is.EqualTo(ReadbackVerdict.Degraded));
            Assert.That(w.Observe(0, 0, 0, 1, 5, t2 + TimeSpan.FromSeconds(4)), Is.EqualTo(ReadbackVerdict.Mismatch),
                "still wrong after the one re-command — now it's a fault");
            Assert.That(w.Observe(0, 0, 0, 1, 5, t2 + TimeSpan.FromSeconds(6)), Is.EqualTo(ReadbackVerdict.Idle),
                "latched — still wrong, already reported");
        }

        [Test]
        public void A_successful_recommand_never_faults() {
            var w = new SwitchReadbackWatch();
            w.Command(0, 1.0, T0);
            var t = T0 + PastSettle;
            w.Observe(0, 0, 0, 1, 5, t);
            w.Observe(0, 0, 0, 1, 5, t + TimeSpan.FromSeconds(2));
            Assert.That(w.Observe(0, 0, 0, 1, 5, t + TimeSpan.FromSeconds(4)), Is.EqualTo(ReadbackVerdict.Recommand));
            // The re-issued write took: the port reads the commanded value from here on.
            var t2 = t + TimeSpan.FromSeconds(4) + PastSettle;
            Assert.That(w.Observe(0, 1, 0, 1, 5, t2), Is.EqualTo(ReadbackVerdict.Idle),
                "the recovery step worked — no fault, no notification");
            Assert.That(w.Observe(0, 1, 0, 1, 5, t2 + TimeSpan.FromSeconds(2)), Is.EqualTo(ReadbackVerdict.Idle));
        }

        [Test]
        public void A_single_flapped_read_never_accumulates() {
            var w = new SwitchReadbackWatch();
            w.Command(0, 1.0, T0);
            var t = T0 + PastSettle;
            for (var i = 0; i < 5; i++) {
                Assert.That(w.Observe(0, 0, 0, 1, 5, t), Is.EqualTo(ReadbackVerdict.Degraded));
                Assert.That(w.Observe(0, 0, 0, 1, 5, t), Is.EqualTo(ReadbackVerdict.Degraded));
                Assert.That(w.Observe(0, 1, 0, 1, 5, t), Is.EqualTo(ReadbackVerdict.Idle), "a good read clears the streak");
            }
        }

        // Drives a freshly written, stubbornly-wrong port through the full episode:
        // streak → Recommand → re-armed settle → streak → Mismatch. Returns the time of the fire.
        private static DateTimeOffset DriveToFire(SwitchReadbackWatch w, DateTimeOffset written) {
            var t = written + PastSettle;
            w.Observe(0, 0, 0, 1, 5, t);
            w.Observe(0, 0, 0, 1, 5, t);
            Assert.That(w.Observe(0, 0, 0, 1, 5, t), Is.EqualTo(ReadbackVerdict.Recommand));
            var t2 = t + PastSettle;
            w.Observe(0, 0, 0, 1, 5, t2);
            w.Observe(0, 0, 0, 1, 5, t2);
            Assert.That(w.Observe(0, 0, 0, 1, 5, t2), Is.EqualTo(ReadbackVerdict.Mismatch));
            return t2;
        }

        [Test]
        public void Recovery_after_a_fired_episode_clears_and_requires_a_fresh_write_to_rearm() {
            var w = new SwitchReadbackWatch();
            w.Command(0, 1.0, T0);
            var t = DriveToFire(w, T0);
            Assert.That(w.Observe(0, 1, 0, 1, 5, t + TimeSpan.FromSeconds(2)), Is.EqualTo(ReadbackVerdict.Cleared));
            // The record is gone: a port oscillating at the boundary can't churn out faults.
            for (var i = 0; i < 6; i++) {
                Assert.That(w.Observe(0, i % 2, 0, 1, 5, t + TimeSpan.FromSeconds(4 + i * 2)), Is.EqualTo(ReadbackVerdict.Idle));
            }
            // A fresh write re-arms (a fresh episode gets a fresh re-command budget too).
            var written2 = t + TimeSpan.FromSeconds(20);
            w.Command(0, 1.0, written2);
            DriveToFire(w, written2);
        }

        [Test]
        public void Tolerance_is_a_percentage_of_the_ports_range() {
            var w = new SwitchReadbackWatch();
            w.Command(7, 50.0, T0);
            var t = T0 + PastSettle;
            // Range 0..100, 5% → ±5 allowed.
            Assert.That(w.Observe(7, 55.0, 0, 100, 5, t), Is.EqualTo(ReadbackVerdict.Idle), "at the boundary is in tolerance");
            Assert.That(w.Observe(7, 55.2, 0, 100, 5, t), Is.EqualTo(ReadbackVerdict.Degraded), "past the boundary counts");
        }

        [Test]
        public void A_degenerate_range_falls_back_to_exact_matching() {
            var w = new SwitchReadbackWatch();
            w.Command(0, 1.0, T0);
            var t = T0 + PastSettle;
            // Some drivers report min == max for boolean ports — range 0 must still catch ON vs OFF.
            Assert.That(w.Observe(0, 0.0, 1, 1, 5, t), Is.EqualTo(ReadbackVerdict.Degraded));
            Assert.That(w.Observe(0, 1.0, 1, 1, 5, t), Is.EqualTo(ReadbackVerdict.Idle));
        }

        [Test]
        public void A_fresh_write_restarts_the_episode_even_onto_a_fired_one() {
            var w = new SwitchReadbackWatch();
            w.Command(0, 1.0, T0);
            var t = DriveToFire(w, T0); // recommand exhausted + fault fired
            w.Command(0, 0.0, t + TimeSpan.FromSeconds(2)); // the daemon now WANTS off
            Assert.That(w.Observe(0, 0, 0, 1, 5, t + TimeSpan.FromSeconds(2) + PastSettle), Is.EqualTo(ReadbackVerdict.Idle),
                "the read-back matches the NEW command — no stale mismatch");
            Assert.That(w.CommandedFor(0), Is.EqualTo(0.0));
        }

        [Test]
        public void Reset_forgets_every_written_port() {
            var w = new SwitchReadbackWatch();
            w.Command(0, 1.0, T0);
            w.Command(3, 0.5, T0);
            w.Reset();
            Assert.That(w.CommandedFor(0), Is.Null);
            Assert.That(w.Observe(0, 0, 0, 1, 5, T0 + PastSettle), Is.EqualTo(ReadbackVerdict.Idle));
        }

        // ── End-to-end: a real SwitchService against a scripted switch that ignores the write ──

        [Test]
        [Category("bench")] // §42.4 virtual-observatory bench — loopback-only, runs in the default job too
        public async Task A_switch_that_ignores_the_write_publishes_one_value_mismatch() {
            // The device answers every read but the heater port stubbornly reads 0 no matter
            // what is written — a stuck relay.
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/maxswitch", StringComparison.Ordinal) ? "1"
                : path.EndsWith("/getswitchname", StringComparison.Ordinal) ? "\"Heater\""
                : path.EndsWith("/getswitchdescription", StringComparison.Ordinal) ? "\"\""
                : path.EndsWith("/getswitchvalue", StringComparison.Ordinal) ? "0"
                : path.EndsWith("/minswitchvalue", StringComparison.Ordinal) ? "0"
                : path.EndsWith("/maxswitchvalue", StringComparison.Ordinal) ? "1"
                : path.EndsWith("/switchstep", StringComparison.Ordinal) ? "1"
                : path.EndsWith("/canwrite", StringComparison.Ordinal) ? "true"
                : null);
            var wsEvents = new List<(string Type, JsonElement Payload)>();
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<string, JsonElement, CancellationToken>((t, p, _) => { lock (wsEvents) { wsEvents.Add((t, p)); } })
                .Returns(Task.CompletedTask);
            var hub = new EquipmentFaultHub(ws.Object);
            var faults = new List<EquipmentFaultEvent>();
            hub.Subscribe(f => { lock (faults) { faults.Add(f); } });

            using var svc = new SwitchService(faults: hub, ws: ws.Object);
            var device = new DiscoveredDeviceDto(
                UniqueId: "switch-under-test", Name: "Bench Power Box", Type: DeviceType.Switch,
                HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
                AlpacaDeviceNumber: 0, UseHttps: false);
            await svc.ConnectAsync(new ConnectRequestDto(device), idempotencyKey: null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(0, CancellationToken.None))?.State == EquipmentConnectionState.Connected,
                TimeSpan.FromSeconds(15), "switch never connected");

            // Command the heater ON; the device accepts the PUT and keeps reading 0.
            await svc.SetValueAsync(0, new SwitchValueRequestDto(PortId: 0, Value: 1.0), CancellationToken.None);

            // Settle (5 s) + 3 bad ticks (~6 s) → the §42.2 re-command (which the stuck device
            // also ignores) → re-armed settle (5 s) + 3 bad ticks (~6 s) → the fault.
            await WaitForAsync(() => { lock (faults) { return Task.FromResult(faults.Count > 0); } },
                TimeSpan.FromSeconds(60), "the value-mismatch fault never published");
            await Task.Delay(TimeSpan.FromSeconds(5)); // several more mismatched ticks — no re-fire
            lock (faults) {
                Assert.That(faults, Has.Count.EqualTo(1), "exactly one fault per command episode");
                Assert.That(faults[0].Kind, Is.EqualTo(EquipmentFaultKind.ValueMismatch));
                Assert.That(faults[0].DeviceName, Is.EqualTo("Bench Power Box"));
            }
            lock (wsEvents) {
                var mismatches = wsEvents.Where(e => e.Type == WsEventCatalog.SwitchValueMismatch).ToList();
                Assert.That(mismatches, Has.Count.EqualTo(1), "the structured switch.value_mismatch fired once");
                Assert.That(mismatches[0].Payload.GetProperty("port_id").GetInt32(), Is.EqualTo(0));
                Assert.That(mismatches[0].Payload.GetProperty("commanded").GetDouble(), Is.EqualTo(1.0));
                Assert.That(mismatches[0].Payload.GetProperty("read_back").GetDouble(), Is.EqualTo(0.0));
            }
        }

        [Test]
        [Category("bench")] // §42.4 virtual-observatory bench — loopback-only, runs in the default job too
        public async Task A_failing_sequencer_write_publishes_an_op_error_fault() {
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/maxswitch", StringComparison.Ordinal) ? "1"
                : path.EndsWith("/getswitchname", StringComparison.Ordinal) ? "\"Heater\""
                : path.EndsWith("/getswitchdescription", StringComparison.Ordinal) ? "\"\""
                : path.EndsWith("/getswitchvalue", StringComparison.Ordinal) ? "0"
                : path.EndsWith("/minswitchvalue", StringComparison.Ordinal) ? "0"
                : path.EndsWith("/maxswitchvalue", StringComparison.Ordinal) ? "1"
                : path.EndsWith("/switchstep", StringComparison.Ordinal) ? "1"
                : path.EndsWith("/canwrite", StringComparison.Ordinal) ? "true"
                : null);
            await using var proxy = AlpacaFaultProxy.Start(box.BaseUri);
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var hub = new EquipmentFaultHub(ws.Object);
            var faults = new List<EquipmentFaultEvent>();
            hub.Subscribe(f => { lock (faults) { faults.Add(f); } });

            using var svc = new SwitchService(faults: hub);
            var device = new DiscoveredDeviceDto(
                UniqueId: "switch-under-test", Name: "Bench Power Box", Type: DeviceType.Switch,
                HostName: proxy.BaseUri.Host, IpAddress: proxy.BaseUri.Host, IpPort: proxy.BaseUri.Port,
                AlpacaDeviceNumber: 0, UseHttps: false);
            await svc.ConnectAsync(new ConnectRequestDto(device), idempotencyKey: null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(0, CancellationToken.None))?.Ports.Count > 0,
                TimeSpan.FromSeconds(15), "the port cache never populated");

            // The write path dies (connection dropped on the PUT only — reads stay healthy, so
            // this is NOT a disconnect); the mediator op must publish one op_error AND fail the
            // instruction (§42.2 — the throw is what engages Attempts retries + instruction_failed;
            // before it, a dead dew-heater write read as success to the sequence).
            proxy.InjectFault(new AlpacaFaultRule { Method = "setswitchvalue", Fault = AlpacaFault.Drop() });
            Assert.ThrowsAsync<SequenceEntityFailedException>(() =>
                ((ISwitchMediator)svc).SetSwitchValue(0, 1.0, progress: null!, CancellationToken.None));

            lock (faults) {
                Assert.That(faults, Has.Count.EqualTo(1), "one fault per failed op occurrence");
                Assert.That(faults[0].Kind, Is.EqualTo(EquipmentFaultKind.OpError));
                Assert.That(faults[0].DeviceName, Is.EqualTo("Bench Power Box"),
                    "the multi-instance identity resolution found the live connection");
            }
        }

        private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout, string failure) {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                if (await condition()) {
                    return;
                }
                await Task.Delay(200);
            }
            Assert.Fail(failure);
        }
    }
}
