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
using System.Collections.Generic;

namespace OpenAstroAra.Test {

    // §25.5.6 / #1065 — the cooling-fan interlock moved from the Flutter client to the daemon.
    // These pin the pure decision helpers CameraService (fan follows cooler) and SwitchService
    // (no fan-off while cooling) rely on.
    [TestFixture]
    public class CoolingFanInterlockTest {

        private static SwitchPortDto Fan(double value = 1, double min = 0, double max = 1, bool canWrite = true) =>
            new(Id: 1, Name: "Fan", Value: value, Min: min, Max: max, CanWrite: canWrite);

        private static SwitchDto Thermal(SwitchPortDto port, EquipmentConnectionState state = EquipmentConnectionState.Connected, string name = "ToupTek Thermal Switch") =>
            new(DeviceId: "sw-5", AlpacaDeviceNumber: 0, Name: name, State: state, Ports: [new SwitchPortDto(0, "Heater", 0, 0, 1, true), port]);

        // The designation the rewritten cases use: "sw-5" (Thermal's id), port 1 (Fan()'s id).
        private static readonly CoolingFanDto Designated = new("sw-5", 1);

        [Test]
        public void A_designated_port_is_the_fan_on_any_device_and_only_that_port() {
            Assert.That(CoolingFanInterlock.IsDesignatedFanPort(Thermal(Fan(), name: "Pegasus UPBv2"), Fan(), Designated), Is.True, "any driver, whatever the device is called");
            Assert.That(CoolingFanInterlock.IsDesignatedFanPort(Thermal(Fan(), name: "Pegasus UPBv2"), Fan() with { Name = "Output 3" }, Designated), Is.True, "the port's name doesn't matter either");
            Assert.That(CoolingFanInterlock.IsDesignatedFanPort(Thermal(Fan()), new SwitchPortDto(0, "Heater", 0, 0, 1, true), Designated), Is.False, "another port of the same switch");
            Assert.That(CoolingFanInterlock.IsDesignatedFanPort(Thermal(Fan()) with { DeviceId = "sw-6" }, Fan(), Designated), Is.False, "the same port id on another switch");
        }

        [Test]
        public void A_Thermal_Switch_with_a_Fan_port_is_not_the_fan_unless_designated() {
            Assert.That(CoolingFanInterlock.IsDesignatedFanPort(Thermal(Fan()), Fan(), CoolingFanDto.None), Is.False);
            Assert.That(CoolingFanInterlock.FindFanPort([Thermal(Fan())], CoolingFanDto.None), Is.Null);
            Assert.That(CoolingFanInterlock.FanSyncRequest([Thermal(Fan())], CoolingFanDto.None, cooling: true), Is.Null, "never actuated");
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(Thermal(Fan()), CoolingFanDto.None, new SwitchValueRequestDto(1, 0)), Is.False, "never blocked");
            var unread = Thermal(Fan()) with { Ports = [] };
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(unread, CoolingFanDto.None, new SwitchValueRequestDto(1, 0)), Is.False, "never blocked while unread either");
        }

        [Test]
        public void Renaming_a_designated_device_does_not_change_the_interlock() {
            var renamed = Thermal(Fan(), name: "Scope fan box");
            Assert.That(CoolingFanInterlock.FindFanPort([renamed], Designated), Is.Not.Null);
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(renamed, Designated, new SwitchValueRequestDto(1, 0)), Is.True);
        }

        [Test]
        public void Find_skips_disconnected_and_read_only_fans() {
            Assert.That(CoolingFanInterlock.FindFanPort(new List<SwitchDto>(), Designated), Is.Null);
            Assert.That(CoolingFanInterlock.FindFanPort([Thermal(Fan(), EquipmentConnectionState.Error)], Designated), Is.Null);
            Assert.That(CoolingFanInterlock.FindFanPort([Thermal(Fan(canWrite: false))], Designated), Is.Null);
            var found = CoolingFanInterlock.FindFanPort([Thermal(Fan())], Designated);
            Assert.That(found, Is.Not.Null);
            Assert.That(found!.Value.Port.Id, Is.EqualTo(1));
        }

        [Test]
        public void Sync_writes_the_ports_own_bounds_not_a_literal_1_0() {
            // PWM fan (0–100): cooler-on must be FULL fan, not ~1%.
            var pwm = Thermal(Fan(value: 50, min: 10, max: 100));
            var on = CoolingFanInterlock.FanSyncRequest([pwm], Designated, cooling: true);
            var off = CoolingFanInterlock.FanSyncRequest([pwm], Designated, cooling: false);
            Assert.That(on!.Value.DeviceId, Is.EqualTo("sw-5"));
            Assert.That(on.Value.Request, Is.EqualTo(new SwitchValueRequestDto(1, 100)));
            Assert.That(off!.Value.Request, Is.EqualTo(new SwitchValueRequestDto(1, 10)));
            // No fan-capable switch (most rigs) → nothing to write.
            Assert.That(CoolingFanInterlock.FanSyncRequest([], Designated, cooling: true), Is.Null);
            // #1076 — cooler-ON always writes (a stale cached value must never skip the fan-on);
            // cooler-OFF skips when the cached port already holds the floor (the warm ramp's
            // minute-by-minute cooler calls).
            Assert.That(CoolingFanInterlock.FanSyncRequest([Thermal(Fan(value: 100, min: 10, max: 100))], Designated, cooling: true), Is.Not.Null);
            Assert.That(CoolingFanInterlock.FanSyncRequest([Thermal(Fan(value: 0))], Designated, cooling: false), Is.Null);
        }

        [Test]
        public void Fan_off_is_range_aware() {
            var pwm = Fan(value: 50, min: 10, max: 100);
            Assert.That(CoolingFanInterlock.IsFanOff(pwm, 10), Is.True);
            Assert.That(CoolingFanInterlock.IsFanOff(pwm, 11), Is.False);
            Assert.That(CoolingFanInterlock.IsFanOff(Fan(), 0), Is.True);
            Assert.That(CoolingFanInterlock.IsFanOff(Fan(), 1), Is.False);
        }

        [Test]
        public void Plausible_fan_off_covers_the_unread_snapshot_window_but_only_for_the_designated_connected_port() {
            var unread = new SwitchDto("sw-5", 0, "Any Box", EquipmentConnectionState.Connected, Ports: []);
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(unread, Designated, new SwitchValueRequestDto(1, 0)), Is.True, "unread snapshot + value 0 on the fan port");
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(unread, Designated, new SwitchValueRequestDto(0, 0)), Is.False, "another port is never held, even unread");
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(unread, Designated, new SwitchValueRequestDto(1, 1)), Is.False, "a fan ON / mid-range value is never held");
            // Error / Disconnected project empty ports too — the write path's "not connected" applies, never the fan refusal.
            var down = unread with { State = EquipmentConnectionState.Error };
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(down, Designated, new SwitchValueRequestDto(1, 0)), Is.False);
            var other = unread with { DeviceId = "sw-6" };
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(other, Designated, new SwitchValueRequestDto(1, 0)), Is.False);
            // Snapshot read: only the designated port at its floor.
            var known = Thermal(Fan(value: 1));
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(known, Designated, new SwitchValueRequestDto(1, 0)), Is.True);
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(known, Designated, new SwitchValueRequestDto(0, 0)), Is.False, "the heater port is not the fan");
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(known, Designated, new SwitchValueRequestDto(1, 1)), Is.False);
        }

        [Test]
        public void A_snapshot_that_skipped_the_fan_port_still_holds_a_fan_off_to_it() {
            // ReadPorts drops a port whose read threw and keeps the rest, so "ports read" doesn't mean "fan read".
            var partial = new SwitchDto("sw-5", 0, "Any Box", EquipmentConnectionState.Connected, Ports: [new SwitchPortDto(0, "Heater", 0, 0, 1, true)]);
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(partial, Designated, new SwitchValueRequestDto(1, 0)), Is.True);
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(partial, Designated, new SwitchValueRequestDto(1, 1)), Is.False);
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(partial, Designated, new SwitchValueRequestDto(0, 0)), Is.False);
        }

        [Test]
        public void A_designation_is_both_fields_or_neither() {
            Assert.That(CoolingFanInterlock.DesignationError(CoolingFanDto.None), Is.Null);
            Assert.That(CoolingFanInterlock.DesignationError(Designated), Is.Null);
            Assert.That(CoolingFanInterlock.DesignationError(new CoolingFanDto("sw-5", null)), Is.Not.Null);
            Assert.That(CoolingFanInterlock.DesignationError(new CoolingFanDto(null, 1)), Is.Not.Null);
            Assert.That(CoolingFanInterlock.DesignationError(new CoolingFanDto("", 1)), Is.Not.Null);
            Assert.That(CoolingFanInterlock.DesignationError(new CoolingFanDto("sw-5", -1)), Is.Not.Null);
            Assert.That(CoolingFanInterlock.DesignationError(new CoolingFanDto("sw-5", 40000)), Is.Not.Null);
        }

        [Test]
        public async System.Threading.Tasks.Task A_designated_switch_in_Error_gets_the_not_connected_refusal_not_the_fan_one() {
            var store = new InMemoryProfileStore();
            store.PutCoolingFan(new CoolingFanDto("dead-thermal", 0));
            using var svc = new SwitchService(profileStore: store);
            var dead = new DiscoveredDeviceDto(UniqueId: "dead-thermal", Name: "ToupTek Thermal Switch", Type: DeviceType.Switch,
                HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 1, AlpacaDeviceNumber: 0, UseHttps: false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, System.Threading.CancellationToken.None);
            for (var i = 0; i < 200; i++) {
                var dto = await svc.GetAsync("dead-thermal", System.Threading.CancellationToken.None);
                if (dto?.State != EquipmentConnectionState.Connecting) break;
                await System.Threading.Tasks.Task.Delay(50);
            }
            var ex = await Assert.ThrowsAsync<System.InvalidOperationException>(
                () => svc.SetValueAsync("dead-thermal", new SwitchValueRequestDto(0, 0), System.Threading.CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("not connected"));
            Assert.That(ex.Message, Does.Not.Contain("cooler"));
        }

        [Test]
        public void Cooler_state_for_the_interlock_is_unknown_on_error_or_an_unreadable_read_and_off_when_disconnected() {
            static CameraDto Cam(EquipmentConnectionState state, bool on, bool known = true) =>
                new("cam", "Cam", state, Capabilities: null, Runtime: new CameraStateDto("idle", null, null, on, null, CoolerStateKnown: known));
            Assert.That(CoolingFanInterlock.CoolerStateFor(null), Is.False, "no DTO (no camera device ever configured) → no TEC this daemon started");
            Assert.That(CoolingFanInterlock.CoolerStateFor(Cam(EquipmentConnectionState.Connected, true)), Is.True);
            Assert.That(CoolingFanInterlock.CoolerStateFor(Cam(EquipmentConnectionState.Connected, false)), Is.False);
            Assert.That(CoolingFanInterlock.CoolerStateFor(Cam(EquipmentConnectionState.Connected, false, known: false)), Is.Null, "CoolerOn read threw → unknown");
            Assert.That(CoolingFanInterlock.CoolerStateFor(new CameraDto("cam", "Cam", EquipmentConnectionState.Connected,
                    Capabilities: new CameraCapabilitiesDto(100, 100, 3.76, false, true, false, 0, 0, 0, 0, 1, 1, 1, 1, 0.001, 3600, HasCooler: false),
                    Runtime: new CameraStateDto("idle", null, null, false, null, CoolerStateKnown: false))),
                Is.False, "an uncooled camera's CoolerOn always throws — no cooler is definitively no TEC, not unknown");
            Assert.That(CoolingFanInterlock.CoolerStateFor(Cam(EquipmentConnectionState.Error, false)), Is.Null, "camera just dropped → the TEC may still run → unknown");
            Assert.That(CoolingFanInterlock.CoolerStateFor(Cam(EquipmentConnectionState.Disconnected, false)), Is.False, "no camera connected → no TEC this daemon started");
        }

        [Test]
        public void Fan_off_refusal_fails_closed_on_unknown_cooler_state() {
            Assert.That(CoolingFanInterlock.FanOffRefusal(false), Is.Null);
            Assert.That(CoolingFanInterlock.FanOffRefusal(true), Does.Contain("damage the camera"));
            Assert.That(CoolingFanInterlock.FanOffRefusal(null), Does.Contain("cooler state is unknown"));
        }

        [Test]
        public async System.Threading.Tasks.Task Switch_write_with_no_connected_switch_still_refuses_as_not_connected() {
            // The interlock only engages for a known fan port; an unknown device keeps the
            // existing "not connected" refusal (→ 409) so nothing regresses for plain switches.
            using var svc = new SwitchService();
            var ex = await Assert.ThrowsAsync<System.InvalidOperationException>(
                () => svc.SetValueAsync("nope", new SwitchValueRequestDto(0, 0), System.Threading.CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("not connected"));
            await System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
