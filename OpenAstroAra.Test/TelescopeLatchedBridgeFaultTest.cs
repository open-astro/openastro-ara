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
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1193 — after a mount Wi-Fi blip AlpacaBridge latches "Mount communications
    /// compromised": it keeps answering <c>Connected=true</c> (so the §42.3 probe streak never
    /// trips) while every property read fails with that message, and only a disconnect + reconnect
    /// of the telescope clears it. The refresh tick now recognises the latched error on the reads
    /// and trips the mount to Error with a Disconnected fault, which is the §42.3 ladder's cue to
    /// do exactly that reconnect (bounded, logged, notified, on the card).</summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    public class TelescopeLatchedBridgeFaultTest {

        // #1265 — the service refreshes every 100 ms here instead of the production 2 s, so a
        // "several ticks" wait is a few hundred milliseconds. Scale every tick-counted wait by it.
        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
        private static TimeSpan Ticks(int n) => Tick * n;

        private const string Latched = "Mount communications compromised";

        [Test]
        public void The_matcher_sees_the_bridge_phrase_anywhere_in_the_exception_chain() {
            Assert.That(TelescopeService.IsLatchedBridgeFault(new InvalidOperationException("Mount communications compromised")), Is.True);
            Assert.That(TelescopeService.IsLatchedBridgeFault(new InvalidOperationException("outer",
                new InvalidOperationException("ASCOM: MOUNT COMMUNICATIONS COMPROMISED (retry)"))), Is.True, "case-insensitive, inner");
            Assert.That(TelescopeService.IsLatchedBridgeFault(new InvalidOperationException("Property not implemented")), Is.False);
            Assert.That(TelescopeService.IsLatchedBridgeFault(null), Is.False);
        }

        // ─── Bench: a scripted mount that latches ─────────────────────────────────────────────

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "mount-under-test", Name: "Bench mount", Type: DeviceType.Telescope,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        // Connected answers true throughout (the bridge's behaviour); the position reads are what
        // latch. `slewing` is scripted separately for the never-during-a-slew rule.
        private static Func<string, string?> Mount(Func<string> position, Func<string> slewing) => path =>
            path.EndsWith("/connected", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/slewing", StringComparison.Ordinal) ? slewing()
            : path.EndsWith("/tracking", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/rightascension", StringComparison.Ordinal) ? position()
            : path.EndsWith("/declination", StringComparison.Ordinal) ? position()
            : null;

        private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string failure) {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                if (condition()) {
                    return;
                }
                await Task.Delay(100);
            }
            Assert.Fail(failure);
        }

        /// <summary>TripLatchedBridgeFault sets Error under the gate and publishes the fault
        /// after releasing it, so a wait on the state alone can observe Error with the fault
        /// still in flight (#1255: 0 rows under CI load). Wait for the row, then the state.</summary>
        private static async Task WaitForTripAsync(TelescopeService svc, List<EquipmentFaultEvent> faults, int count, string failure) {
            await WaitForAsync(() => { lock (faults) { return faults.Count >= count; } }, TimeSpan.FromSeconds(15), failure);
            await WaitForAsync(() => StateOf(svc).Result == EquipmentConnectionState.Error, TimeSpan.FromSeconds(15), failure);
        }

        private static async Task<EquipmentConnectionState?> StateOf(TelescopeService svc) =>
            (await svc.GetAsync(CancellationToken.None))?.State;

        // #1255 — the refresh is single-flight, so once the cache reports a runtime state every
        // later tick started after the script change that produced it. Waiting for the committed
        // state (not N ticks of wall clock) is what makes "the mount is slewing when the latched
        // error appears" true under full-suite load, where a tick can stall between its Slewing
        // and RightAscension reads for longer than the old fixed window.
        private static Task WaitForRuntimeStateAsync(TelescopeService svc, string state) =>
            WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.Runtime.State == state,
                TimeSpan.FromSeconds(15), $"the runtime never reported '{state}'");

        private static int PositionReads(ScriptedAlpacaDevice box) =>
            box.Gets.Count(p => p.EndsWith("/rightascension", StringComparison.Ordinal));

        // "Several refresh ticks" counted at the device: the stub has answered N more position
        // reads since the mark, however long those ticks took.
        private static Task WaitForPositionReadsAsync(ScriptedAlpacaDevice box, int since, int count) =>
            WaitForAsync(() => PositionReads(box) >= since + count, TimeSpan.FromSeconds(15),
                $"the service never read the position {count} more times");

        [Test]
        [Category("bench")] // loopback-only, runs in the default job too
        public async Task A_latched_bridge_trips_the_mount_to_Error_with_a_Disconnected_fault_once_per_episode() {
            var position = "6.0";
            var slewing = "false";
            await using var box = ScriptedAlpacaDevice.Start(Mount(() => Volatile.Read(ref position), () => Volatile.Read(ref slewing)));
            var hub = new EquipmentFaultHub(Mock.Of<IWsBroadcaster>());
            var faults = new List<EquipmentFaultEvent>();
            hub.Subscribe(f => { lock (faults) { faults.Add(f); } });
            using var svc = new TelescopeService(faults: hub) { RefreshPeriod = Tick };
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            await WaitForAsync(() => StateOf(svc).Result == EquipmentConnectionState.Connected, TimeSpan.FromSeconds(15), "never connected");

            // The blip: the bridge keeps saying Connected but every read is the latched error.
            Volatile.Write(ref position, ScriptedAlpacaDevice.Error(Latched));
            await WaitForTripAsync(svc, faults, 1,
                "a latched bridge must trip the mount to Error (the probe streak alone never does — Connected stays true)");
            lock (faults) {
                Assert.That(faults, Has.Count.EqualTo(1));
                Assert.That(faults[0].DeviceType, Is.EqualTo(DeviceType.Telescope));
                Assert.That(faults[0].Kind, Is.EqualTo(EquipmentFaultKind.Disconnected), "the §42.3 ladder's reconnect cue");
                Assert.That(faults[0].Details, Does.Contain(Latched).And.Contain("#1193"));
            }

            // The ladder's reconnect: a fresh connect of the same device. The bridge is STILL
            // latched (reads keep failing), so the mount comes up Connected and must not trip
            // again within the same episode — one fault, not one per 2 s tick.
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            await WaitForAsync(() => StateOf(svc).Result == EquipmentConnectionState.Connected, TimeSpan.FromSeconds(15), "never reconnected");
            await WaitForPositionReadsAsync(box, PositionReads(box), 3); // refresh ticks on the still-latched bridge
            Assert.That(await StateOf(svc), Is.EqualTo(EquipmentConnectionState.Connected), "still latched after the reconnect is the same episode");
            lock (faults) {
                Assert.That(faults, Has.Count.EqualTo(1), "no second trip while the episode is open");
            }

            // The bridge recovers (reads answer), then latches again later: a NEW episode trips.
            Volatile.Write(ref position, "6.0");
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.Runtime.RightAscensionHours is 6.0,
                TimeSpan.FromSeconds(15), "the position never came back after the bridge recovered");
            Volatile.Write(ref position, ScriptedAlpacaDevice.Error(Latched));
            await WaitForTripAsync(svc, faults, 2, "a new episode must trip again");
            lock (faults) {
                Assert.That(faults, Has.Count.EqualTo(2));
            }
        }

        [Test]
        [Category("bench")]
        public async Task A_latched_bridge_is_not_tripped_while_the_mount_is_slewing() {
            var position = "6.0";
            var slewing = "false";
            await using var box = ScriptedAlpacaDevice.Start(Mount(() => Volatile.Read(ref position), () => Volatile.Read(ref slewing)));
            var hub = new EquipmentFaultHub(Mock.Of<IWsBroadcaster>());
            var faults = new List<EquipmentFaultEvent>();
            hub.Subscribe(f => { lock (faults) { faults.Add(f); } });
            using var svc = new TelescopeService(faults: hub) { RefreshPeriod = Tick };
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            await WaitForAsync(() => StateOf(svc).Result == EquipmentConnectionState.Connected, TimeSpan.FromSeconds(15), "never connected");

            // Start the goto, let a tick land so every later refresh reads Slewing = true, THEN latch
            // the position reads: flipping both at once let a tick straddle the two writes (Slewing
            // still false, position already latched) and trip the mount it was told not to (#1265).
            Volatile.Write(ref slewing, "true");
            await WaitForRuntimeStateAsync(svc, "slewing");
            Volatile.Write(ref position, ScriptedAlpacaDevice.Error(Latched));
            await WaitForPositionReadsAsync(box, PositionReads(box), 5); // several ticks: latched reads, but a goto in flight
            string published;
            lock (faults) { published = string.Join("; ", faults.Select(f => f.Details)); }
            Assert.That(await StateOf(svc), Is.EqualTo(EquipmentConnectionState.Connected),
                "a reconnect mid-goto would abandon a moving mount — the slew's own watchdog and the next tick own it"
                + (published.Length == 0 ? "" : $" (faults published: {published})"));
            lock (faults) {
                Assert.That(faults.Where(f => f.Details?.Contains("#1193", StringComparison.Ordinal) == true), Is.Empty);
            }

            // The slew ends with the bridge still latched: now it trips.
            Volatile.Write(ref slewing, "false");
            await WaitForTripAsync(svc, faults, 1, "trips once the slew is over");
        }
    }
}
