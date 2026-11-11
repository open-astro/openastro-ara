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
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    // #1663 — the main camera's own heater is the connected switch with the same serial after "_SN_"
    // and a DewHeater port; never by name or order.
    [TestFixture]
    public class DewHeaterPairingTest {

        private static SwitchDto Heater(string id, string name = "Heater switch", int heaterPort = 2,
            EquipmentConnectionState state = EquipmentConnectionState.Connected, string portName = "DewHeater") =>
            new(id, 0, name, state, [new SwitchPortDto(0, "Other", 0, 0, 1, true), new SwitchPortDto(heaterPort, portName, 0, 0, 1, true)]);

        [TestCase("ZWO_SN_A1", "ZWO_DEW_SN_A1")]
        [TestCase("TOUPTEK_SN_B2", "TOUPTEK_THERMALSW_SN_B2")]
        [TestCase("PLAYERONE_SN_C3", "PLAYERONE_SW_SN_C3")]
        public void Each_vendor_camera_pairs_with_its_own_heater_switch(string camera, string heater) {
            var found = DewHeaterPairing.Find(camera, [Heater("ZWO_DEW_SN_other"), Heater(heater, heaterPort: 3)]);
            Assert.That(found?.Device.DeviceId, Is.EqualTo(heater));
            Assert.That(found?.Port.Id, Is.EqualTo(3));
        }

        [Test]
        public void Two_same_brand_cameras_pair_by_serial_not_by_order() {
            var switches = new[] { Heater("ZWO_DEW_SN_B"), Heater("ZWO_DEW_SN_A") };
            Assert.That(DewHeaterPairing.Find("ZWO_SN_A", switches)?.Device.DeviceId, Is.EqualTo("ZWO_DEW_SN_A"));
            Assert.That(DewHeaterPairing.Find("ZWO_SN_B", switches)?.Device.DeviceId, Is.EqualTo("ZWO_DEW_SN_B"));
        }

        [Test]
        public void The_guide_cameras_heater_is_not_the_main_cameras() {
            // Only the guide camera's heater is on the rig: the main camera (serial A) gets none.
            Assert.That(DewHeaterPairing.Find("ZWO_SN_A", [Heater("ZWO_DEW_SN_GUIDE")]), Is.Null);
        }

        [TestCase("ZWO_1", "ZWO_DEW_SN_A", Description = "no serial on the camera")]
        [TestCase("ZWO_SN_A", "ZWO_DEW_1", Description = "no serial on the switch (a ZWO body without one)")]
        [TestCase("ZWO_1", "ZWO_DEW_1", Description = "no serial on either side")]
        [TestCase("ZWO_SN_", "ZWO_DEW_SN_", Description = "an empty serial is no serial")]
        [TestCase(null, "ZWO_DEW_SN_A")]
        public void Without_a_serial_on_both_sides_there_is_no_pairing(string? camera, string heater) =>
            Assert.That(DewHeaterPairing.Find(camera, [Heater(heater)]), Is.Null);

        [Test]
        public void A_matching_switch_without_a_DewHeater_port_pairs_with_nothing() =>
            Assert.That(DewHeaterPairing.Find("ZWO_SN_A", [Heater("ZWO_DEW_SN_A", portName: "Fan")]), Is.Null);

        [Test]
        public void A_disconnected_switch_pairs_with_nothing() {
            Assert.That(DewHeaterPairing.Find("ZWO_SN_A", [Heater("ZWO_DEW_SN_A", state: EquipmentConnectionState.Disconnected)]), Is.Null);
            Assert.That(DewHeaterPairing.Find("ZWO_SN_A", [Heater("ZWO_DEW_SN_A", state: EquipmentConnectionState.Error)]), Is.Null);
        }

        [Test]
        public void The_switchs_name_does_not_matter() =>
            Assert.That(DewHeaterPairing.Find("ZWO_SN_A", [Heater("ZWO_DEW_SN_A", name: "Scope box")]), Is.Not.Null);

        private static CameraDto Camera(string id = "ZWO_SN_A", EquipmentConnectionState state = EquipmentConnectionState.Connected) =>
            new(id, "Cam", state, null, null!);

        private static Mock<ICameraService> CameraService(CameraDto? dto) {
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(dto);
            return camera;
        }

        private static Mock<ISwitchService> Switches(params SwitchDto[] list) {
            var sw = new Mock<ISwitchService>();
            sw.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(list);
            return sw;
        }

        [Test]
        public async Task The_camera_status_names_the_paired_heater() {
            var dto = await EquipmentEndpoints.GetCameraWithHeaterAsync(
                CameraService(Camera()).Object, Switches(Heater("ZWO_DEW_SN_A", heaterPort: 4)).Object, CancellationToken.None);
            Assert.That(dto!.DewHeater, Is.EqualTo(new DewHeaterDto("ZWO_DEW_SN_A", 4)));
        }

        [Test]
        public async Task The_camera_status_heater_is_null_when_none_pairs() {
            var dto = await EquipmentEndpoints.GetCameraWithHeaterAsync(
                CameraService(Camera()).Object, Switches(Heater("ZWO_DEW_SN_B")).Object, CancellationToken.None);
            Assert.That(dto!.DewHeater, Is.Null);
        }

        [Test]
        public async Task A_failing_switch_list_leaves_the_camera_status_whole() {
            var sw = new Mock<ISwitchService>();
            sw.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));
            var dto = await EquipmentEndpoints.GetCameraWithHeaterAsync(CameraService(Camera()).Object, sw.Object, CancellationToken.None);
            Assert.That(dto, Is.Not.Null);
            Assert.That(dto!.DewHeater, Is.Null);
        }

        [Test]
        public async Task No_camera_is_no_status() =>
            Assert.That(await EquipmentEndpoints.GetCameraWithHeaterAsync(CameraService(null).Object, Switches().Object, CancellationToken.None), Is.Null);
    }
}
