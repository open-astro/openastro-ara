#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>GET /api/v1/equipment/driver/{type}: a device's driver and vendor-SDK versions (and its
    /// firmware when the Alpaca server reports one), for Simple Mode's device windows.</summary>
    [TestFixture]
    public class DeviceDriverInfoTest {

        private const string Devices = """
            {"Value":[
              {"DeviceType":"Camera","DeviceNumber":1,"DeviceName":"ZWO ASI533MC Pro","UniqueID":"u1","SdkVersion":"1.41.0.0","Vendor":"zwo","Config":{"serialNumber":"0c190e111d020900"}},
              {"DeviceType":"Telescope","DeviceNumber":0,"DeviceName":"EQM-35 Pro","UniqueID":"u2","Firmware":"3.47"}
            ]}
            """;

        [Test]
        public void Configured_devices_carry_the_sdk_version_and_firmware_when_reported() {
            var d = AlpacaManagementClient.ParseConfiguredDevices(Devices);
            Assert.That(d["camera/1"].SdkVersion, Is.EqualTo("1.41.0.0"));
            Assert.That(d["camera/1"].Firmware, Is.Null);
            Assert.That(d["telescope/0"].Firmware, Is.EqualTo("3.47"));
            Assert.That(d["camera/1"].SerialNumber, Is.EqualTo("0c190e111d020900"));
            Assert.That(d["telescope/0"].SerialNumber, Is.Null);
        }

        [Test]
        public async Task The_route_answers_the_selected_devices_driver_sdk_and_firmware() {
            var selections = new Mock<IEquipmentSelectionStore>();
            selections.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<DiscoveredDeviceDto> {
                new("u1", "ZWO ASI533MC Pro", DeviceType.Camera, "rig.local", "10.0.30.223", 6800, 1, false),
            });
            var alpaca = new Mock<IAlpacaManagementClient>();
            alpaca.Setup(a => a.GetConfiguredDevicesAsync("10.0.30.223", 6800, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AlpacaManagementClient.ParseConfiguredDevices(Devices));
            alpaca.Setup(a => a.GetDriverVersionAsync("10.0.30.223", 6800, "camera", 1, It.IsAny<CancellationToken>()))
                .ReturnsAsync("6.0.0~nightly");

            var result = await EquipmentEndpoints.GetDeviceDriverAsync("camera", selections.Object, alpaca.Object, CancellationToken.None);

            var ok = result as Ok<DeviceDriverDto>;
            Assert.That(ok?.Value, Is.EqualTo(new DeviceDriverDto("6.0.0~nightly", "1.41.0.0", null, "0c190e111d020900")));
        }

        [Test]
        public async Task The_guide_camera_route_answers_the_guiders_alpaca_camera_with_its_name() {
            var profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetPhd2Settings()).Returns(new Phd2SettingsDto(
                "localhost", 4400, "Default", true, 3, 5.0, 1.5, 10, 60, false, GuiderCamera: "Alpaca Camera [10.0.30.223:6800/1]"));
            var alpaca = new Mock<IAlpacaManagementClient>();
            alpaca.Setup(a => a.GetConfiguredDevicesAsync("10.0.30.223", 6800, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AlpacaManagementClient.ParseConfiguredDevices(Devices));
            alpaca.Setup(a => a.GetDriverVersionAsync("10.0.30.223", 6800, "camera", 1, It.IsAny<CancellationToken>()))
                .ReturnsAsync("6.0.0");

            var result = await EquipmentEndpoints.GetGuideCameraDriverAsync(profiles.Object, alpaca.Object, Mock.Of<IGuiderService>(), CancellationToken.None);

            Assert.That((result as Ok<DeviceDriverDto>)?.Value,
                Is.EqualTo(new DeviceDriverDto("6.0.0", "1.41.0.0", null, "0c190e111d020900", "ZWO ASI533MC Pro")));
        }

        [Test]
        public async Task A_guide_camera_that_is_not_an_alpaca_pick_and_a_guider_that_cannot_say_is_404() {
            var profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetPhd2Settings()).Returns(new Phd2SettingsDto(
                "localhost", 4400, "Default", true, 3, 5.0, 1.5, 10, 60, false, GuiderCamera: "ZWO ASI Camera (1)"));
            var result = await EquipmentEndpoints.GetGuideCameraDriverAsync(profiles.Object, new Mock<IAlpacaManagementClient>().Object,
                Mock.Of<IGuiderService>(), CancellationToken.None);
            Assert.That((result as IStatusCodeHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        }

        [Test]
        public async Task The_guiders_own_alpaca_camera_is_looked_up_like_a_profile_pick() {
            // What the rig's guider really answers: its Alpaca selection, with the server in the middle.
            var profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetPhd2Settings()).Returns(new Phd2SettingsDto(
                "localhost", 4400, "Default", true, 3, 5.0, 1.5, 10, 60, false, GuiderCamera: ""));
            var guider = new Mock<IGuiderService>();
            guider.Setup(g => g.TryGetGuideCameraNameAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync("Alpaca Camera [127.0.0.1:6800/1] - ZWO ASI120MM Mini (ZWO ASI Camera Driver)");
            var alpaca = new Mock<IAlpacaManagementClient>();
            alpaca.Setup(a => a.GetConfiguredDevicesAsync("127.0.0.1", 6800, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AlpacaManagementClient.ParseConfiguredDevices(Devices));
            alpaca.Setup(a => a.GetDriverVersionAsync("127.0.0.1", 6800, "camera", 1, It.IsAny<CancellationToken>()))
                .ReturnsAsync("6.0.0");

            var result = await EquipmentEndpoints.GetGuideCameraDriverAsync(profiles.Object, alpaca.Object, guider.Object, CancellationToken.None);

            Assert.That((result as Ok<DeviceDriverDto>)?.Value,
                Is.EqualTo(new DeviceDriverDto("6.0.0", "1.41.0.0", null, "0c190e111d020900", "ZWO ASI533MC Pro")));
        }

        [Test]
        public async Task A_guider_camera_its_alpaca_server_does_not_list_is_named_by_its_model_only() {
            var profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetPhd2Settings()).Returns(new Phd2SettingsDto(
                "localhost", 4400, "Default", true, 3, 5.0, 1.5, 10, 60, false, GuiderCamera: ""));
            var guider = new Mock<IGuiderService>();
            guider.Setup(g => g.TryGetGuideCameraNameAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync("Alpaca Camera [127.0.0.1:6800/0] - ZWO ASI120MM Mini (ZWO ASI Camera Driver)");
            var alpaca = new Mock<IAlpacaManagementClient>();
            alpaca.Setup(a => a.GetConfiguredDevicesAsync("127.0.0.1", 6800, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AlpacaManagementClient.ParseConfiguredDevices(Devices));
            alpaca.Setup(a => a.GetDriverVersionAsync("127.0.0.1", 6800, "camera", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            var result = await EquipmentEndpoints.GetGuideCameraDriverAsync(profiles.Object, alpaca.Object, guider.Object, CancellationToken.None);

            Assert.That((result as Ok<DeviceDriverDto>)?.Value?.Name, Is.EqualTo("ZWO ASI120MM Mini"));
        }

        [Test]
        public async Task Without_an_alpaca_pick_the_connected_guider_names_its_camera() {
            // The rig's profile leaves the guide camera blank; the guider still knows what it opened.
            var profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetPhd2Settings()).Returns(new Phd2SettingsDto(
                "localhost", 4400, "Default", true, 3, 5.0, 1.5, 10, 60, false, GuiderCamera: ""));
            var guider = new Mock<IGuiderService>();
            guider.Setup(g => g.TryGetGuideCameraNameAsync(It.IsAny<CancellationToken>())).ReturnsAsync("ZWO ASI120MM Mini");
            var result = await EquipmentEndpoints.GetGuideCameraDriverAsync(profiles.Object, new Mock<IAlpacaManagementClient>().Object,
                guider.Object, CancellationToken.None);
            Assert.That((result as Ok<DeviceDriverDto>)?.Value,
                Is.EqualTo(new DeviceDriverDto(null, null, null, null, "ZWO ASI120MM Mini")));
        }

        [Test]
        public async Task No_selected_device_is_404_and_an_unknown_type_400() {
            var selections = new Mock<IEquipmentSelectionStore>();
            selections.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<DiscoveredDeviceDto>());
            var alpaca = new Mock<IAlpacaManagementClient>();
            var none = await EquipmentEndpoints.GetDeviceDriverAsync("focuser", selections.Object, alpaca.Object, CancellationToken.None);
            Assert.That((none as IStatusCodeHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            var bad = await EquipmentEndpoints.GetDeviceDriverAsync("toaster", selections.Object, alpaca.Object, CancellationToken.None);
            Assert.That((bad as IStatusCodeHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        }
    }
}
