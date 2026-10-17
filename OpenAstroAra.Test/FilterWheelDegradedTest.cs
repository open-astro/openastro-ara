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

    /// <summary>
    /// The 2026-10-16 filter wheel: the bridge answers Connected while every Position read throws
    /// <c>EFW_ERROR_REMOVED</c>. The probe alone kept the chip green for minutes; the health tracker
    /// raises a standing <c>degraded</c> fault after three failed ticks and clears it with a
    /// <c>recovered</c> action when the reads come back.
    /// </summary>
    [TestFixture]
    [Category("IO")] // loopback scripted Alpaca device
    public class FilterWheelDegradedTest {

        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
        private const string Removed = "EFWGetPosition: EFW_ERROR_REMOVED";

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "efw-under-test", Name: "Bench EFW", Type: DeviceType.FilterWheel,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private static Func<string, string?> Wheel(Func<string> position) => path =>
            path.EndsWith("/connected", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/position", StringComparison.Ordinal) ? position()
            : null;

        private static async Task WaitForAsync(Func<bool> condition, string failure) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline) {
                if (condition()) {
                    return;
                }
                await Task.Delay(50);
            }
            Assert.Fail(failure);
        }

        [Test]
        public async Task Position_reads_failing_while_the_probe_answers_raise_degraded_and_their_return_recovers() {
            var position = "2";
            await using var box = ScriptedAlpacaDevice.Start(Wheel(() => Volatile.Read(ref position)));
            var ws = new Mock<IWsBroadcaster>();
            var actions = new List<JsonElement>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<string, JsonElement, CancellationToken>((t, e, _) => {
                    if (t == WsEventCatalog.EquipmentFaultActionTaken) { lock (actions) { actions.Add(e); } }
                })
                .Returns(Task.CompletedTask);
            var hub = new EquipmentFaultHub(ws.Object);
            var faults = new List<EquipmentFaultEvent>();
            hub.Subscribe(f => { lock (faults) { faults.Add(f); } });
            using var svc = new FilterWheelService(faults: hub) { RefreshPeriod = Tick };
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Connected, "never connected");
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.Runtime.CurrentSlot == 2, "the position never read");

            // The wheel drops off the bus but the bridge keeps answering Connected (2026-10-16).
            Volatile.Write(ref position, ScriptedAlpacaDevice.Error(Removed));
            await WaitForAsync(() => { lock (faults) { return faults.Count >= 1; } }, "no degraded fault after three failed position reads");
            lock (faults) {
                Assert.That(faults, Has.Count.EqualTo(1), "one fault per episode, not one per tick");
                Assert.That(faults[0].Kind, Is.EqualTo(EquipmentFaultKind.Degraded));
                Assert.That(faults[0].DeviceType, Is.EqualTo(DeviceType.FilterWheel));
                Assert.That(faults[0].Details, Does.Contain("EFW_ERROR_REMOVED").And.Contain("3 ticks"));
            }
            Assert.That((await svc.GetAsync(CancellationToken.None))!.State, Is.EqualTo(EquipmentConnectionState.Connected),
                "the probe still answers: not a disconnect, a degraded device");
            await Task.Delay(Tick * 4);
            lock (faults) { Assert.That(faults, Has.Count.EqualTo(1), "still one fault while the episode continues"); }

            // The reads come back: the device's own recovery clears the standing fault.
            Volatile.Write(ref position, "2");
            await WaitForAsync(() => { lock (actions) { return actions.Count >= 1; } }, "no recovered action after the reads returned");
            JsonElement recovered;
            lock (actions) { recovered = actions[0]; }
            Assert.That(recovered.GetProperty("action").GetString(), Is.EqualTo("recovered"));
            Assert.That(recovered.GetProperty("kind").GetString(), Is.EqualTo("degraded"));
            Assert.That(recovered.GetProperty("device_type").GetString(), Is.EqualTo("filterwheel"));
            Assert.That(recovered.GetProperty("source").GetString(), Is.EqualTo("device_reads"));

            // A fresh streak is a fresh episode.
            Volatile.Write(ref position, ScriptedAlpacaDevice.Error(Removed));
            await WaitForAsync(() => { lock (faults) { return faults.Count >= 2; } }, "a second episode must raise again");
        }

        [Test]
        public async Task A_single_failed_read_is_a_blip_not_a_fault() {
            var position = "1";
            await using var box = ScriptedAlpacaDevice.Start(Wheel(() => Volatile.Read(ref position)));
            var hub = new EquipmentFaultHub(Mock.Of<IWsBroadcaster>());
            var faults = new List<EquipmentFaultEvent>();
            hub.Subscribe(f => { lock (faults) { faults.Add(f); } });
            using var svc = new FilterWheelService(faults: hub) { RefreshPeriod = Tick };
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.Runtime.CurrentSlot == 1, "the position never read");

            var reads = box.Gets.Count(p => p.EndsWith("/position", StringComparison.Ordinal));
            Volatile.Write(ref position, ScriptedAlpacaDevice.Error(Removed));
            await WaitForAsync(() => box.Gets.Count(p => p.EndsWith("/position", StringComparison.Ordinal)) >= reads + 1, "no position read");
            Volatile.Write(ref position, "1");
            await Task.Delay(Tick * 6);

            lock (faults) { Assert.That(faults, Is.Empty, "one failed tick must not raise a fault"); }
        }
    }
}
