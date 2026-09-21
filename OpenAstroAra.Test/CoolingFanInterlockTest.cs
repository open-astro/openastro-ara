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

        [Test]
        public void Fan_port_is_scoped_to_the_thermal_switch() {
            Assert.That(CoolingFanInterlock.IsThermalSwitchFanPort(Thermal(Fan()), Fan()), Is.True);
            // An unrelated switch with a port literally named "Fan" is never the cooling fan.
            Assert.That(CoolingFanInterlock.IsThermalSwitchFanPort(Thermal(Fan(), name: "Pegasus UPBv2"), Fan()), Is.False);
            Assert.That(CoolingFanInterlock.IsThermalSwitchFanPort(Thermal(Fan()), Fan() with { Name = "Heater" }), Is.False);
        }

        [Test]
        public void Find_skips_disconnected_and_read_only_fans() {
            Assert.That(CoolingFanInterlock.FindThermalSwitchFanPort(new List<SwitchDto>()), Is.Null);
            Assert.That(CoolingFanInterlock.FindThermalSwitchFanPort([Thermal(Fan(), EquipmentConnectionState.Error)]), Is.Null);
            Assert.That(CoolingFanInterlock.FindThermalSwitchFanPort([Thermal(Fan(canWrite: false))]), Is.Null);
            var found = CoolingFanInterlock.FindThermalSwitchFanPort([Thermal(Fan())]);
            Assert.That(found, Is.Not.Null);
            Assert.That(found!.Value.Port.Id, Is.EqualTo(1));
        }

        [Test]
        public void Sync_writes_the_ports_own_bounds_not_a_literal_1_0() {
            // PWM fan (0–100): cooler-on must be FULL fan, not ~1%.
            var pwm = Thermal(Fan(value: 50, min: 10, max: 100));
            var on = CoolingFanInterlock.FanSyncRequest([pwm], cooling: true);
            var off = CoolingFanInterlock.FanSyncRequest([pwm], cooling: false);
            Assert.That(on!.Value.DeviceId, Is.EqualTo("sw-5"));
            Assert.That(on.Value.Request, Is.EqualTo(new SwitchValueRequestDto(1, 100)));
            Assert.That(off!.Value.Request, Is.EqualTo(new SwitchValueRequestDto(1, 10)));
            // No fan-capable switch (most rigs) → nothing to write.
            Assert.That(CoolingFanInterlock.FanSyncRequest([], cooling: true), Is.Null);
            // Already at the target (the warm ramp's minute-by-minute cooler calls) → no redundant write.
            Assert.That(CoolingFanInterlock.FanSyncRequest([Thermal(Fan(value: 100, min: 10, max: 100))], cooling: true), Is.Null);
            Assert.That(CoolingFanInterlock.FanSyncRequest([Thermal(Fan(value: 0))], cooling: false), Is.Null);
        }

        [Test]
        public void Thermal_switch_device_rule_matches_the_port_rule() {
            Assert.That(CoolingFanInterlock.IsThermalSwitchDevice(Thermal(Fan())), Is.True);
            Assert.That(CoolingFanInterlock.IsThermalSwitchDevice(Thermal(Fan(), name: "Pegasus UPBv2")), Is.False);
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
        public void Plausible_fan_off_covers_the_unread_snapshot_window_but_only_for_a_connected_thermal_switch() {
            var unread = new SwitchDto("sw-5", 0, "ToupTek Thermal Switch", EquipmentConnectionState.Connected, Ports: []);
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(unread, new SwitchValueRequestDto(0, 0)), Is.True, "unread snapshot + value 0: could be the fan");
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(unread, new SwitchValueRequestDto(1, 1)), Is.False, "a fan ON / mid-range value is never held");
            // Error / Disconnected project empty ports too — the write path's "not connected" applies, never the fan refusal.
            var down = unread with { State = EquipmentConnectionState.Error };
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(down, new SwitchValueRequestDto(0, 0)), Is.False);
            var other = unread with { Name = "Pegasus UPBv2" };
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(other, new SwitchValueRequestDto(0, 0)), Is.False);
            // Snapshot read: only the identified Fan port at its floor.
            var known = Thermal(Fan(value: 1));
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(known, new SwitchValueRequestDto(1, 0)), Is.True);
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(known, new SwitchValueRequestDto(0, 0)), Is.False, "the heater port is not the fan");
            Assert.That(CoolingFanInterlock.IsPlausibleFanOff(known, new SwitchValueRequestDto(1, 1)), Is.False);
        }

        [Test]
        public async System.Threading.Tasks.Task A_thermal_switch_in_Error_gets_the_not_connected_refusal_not_the_fan_one() {
            using var svc = new SwitchService();
            var dead = new DiscoveredDeviceDto(UniqueId: "dead-thermal", Name: "ToupTek Thermal Switch", Type: DeviceType.Switch,
                HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 1, AlpacaDeviceNumber: 0, UseHttps: false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, System.Threading.CancellationToken.None);
            for (var i = 0; i < 200; i++) {
                var dto = await svc.GetAsync("dead-thermal", System.Threading.CancellationToken.None);
                if (dto?.State != EquipmentConnectionState.Connecting) break;
                await System.Threading.Tasks.Task.Delay(50);
            }
            var ex = Assert.ThrowsAsync<System.InvalidOperationException>(
                () => svc.SetValueAsync("dead-thermal", new SwitchValueRequestDto(0, 0), System.Threading.CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("not connected"));
            Assert.That(ex.Message, Does.Not.Contain("cooler"));
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
            var ex = Assert.ThrowsAsync<System.InvalidOperationException>(
                () => svc.SetValueAsync("nope", new SwitchValueRequestDto(0, 0), System.Threading.CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("not connected"));
            await System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
