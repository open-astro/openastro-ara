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
using System.Linq;
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
                new("u1", "ZWO ASI533MC Pro", DeviceType.Camera, "rig.local", "192.0.2.10", 6800, 1, false),
            });
            var alpaca = new Mock<IAlpacaManagementClient>();
            alpaca.Setup(a => a.GetConfiguredDevicesAsync("192.0.2.10", 6800, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AlpacaManagementClient.ParseConfiguredDevices(Devices));
            alpaca.Setup(a => a.GetDriverVersionAsync("192.0.2.10", 6800, "camera", 1, It.IsAny<CancellationToken>()))
                .ReturnsAsync("6.0.0~nightly");

            var result = await EquipmentEndpoints.GetDeviceDriverAsync("camera", selections.Object, alpaca.Object, CancellationToken.None);

            var ok = result as Ok<DeviceDriverDto>;
            Assert.That(ok?.Value, Is.EqualTo(new DeviceDriverDto("6.0.0~nightly", "1.41.0.0", null, "0c190e111d020900")));
        }

        [Test]
        public async Task A_flat_device_selection_asks_alpaca_for_its_covercalibrator() {
            // FlatDevice is NINA's name for the device; Alpaca only knows covercalibrator.
            var selections = new Mock<IEquipmentSelectionStore>();
            selections.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<DiscoveredDeviceDto> {
                new("u3", "Flat panel", DeviceType.FlatDevice, "rig.local", "192.0.2.10", 6800, 0, false),
            });
            var alpaca = new Mock<IAlpacaManagementClient>();
            alpaca.Setup(a => a.GetConfiguredDevicesAsync("192.0.2.10", 6800, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AlpacaManagementClient.ParseConfiguredDevices(Devices));
            alpaca.Setup(a => a.GetDriverVersionAsync("192.0.2.10", 6800, "covercalibrator", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync("1.2.0");

            var result = await EquipmentEndpoints.GetDeviceDriverAsync("flatdevice", selections.Object, alpaca.Object, CancellationToken.None);

            Assert.That((result as Ok<DeviceDriverDto>)?.Value?.DriverVersion, Is.EqualTo("1.2.0"));
        }

        [Test]
        public async Task The_guide_camera_route_answers_the_guiders_alpaca_camera_with_its_name() {
            var profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetPhd2Settings()).Returns(new Phd2SettingsDto(
                "localhost", 4400, "Default", true, 3, 5.0, 1.5, 10, 60, false, GuiderCamera: "Alpaca Camera [192.0.2.10:6800/1]"));
            var alpaca = new Mock<IAlpacaManagementClient>();
            alpaca.Setup(a => a.GetConfiguredDevicesAsync("192.0.2.10", 6800, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AlpacaManagementClient.ParseConfiguredDevices(Devices));
            alpaca.Setup(a => a.GetDriverVersionAsync("192.0.2.10", 6800, "camera", 1, It.IsAny<CancellationToken>()))
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
        public async Task A_host_both_lookups_refuse_leaves_no_unobserved_task() {
            // #1485: the driver lookup started beside the device list was never awaited when the list threw.
            const string Marker = "not a host (#1485)";
            var unobserved = 0;
            void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e) {
                if (e.Exception.InnerExceptions.Any(x => x.Message.Contains(Marker, System.StringComparison.Ordinal))) {
                    Interlocked.Increment(ref unobserved);
                }
            }
            TaskScheduler.UnobservedTaskException += OnUnobserved;
            try {
                await Lookups().ConfigureAwait(false);
                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                System.GC.Collect();
                Assert.That(unobserved, Is.Zero);
            } finally {
                TaskScheduler.UnobservedTaskException -= OnUnobserved;
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            static async Task Lookups() {
                var selections = new Mock<IEquipmentSelectionStore>();
                selections.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<DiscoveredDeviceDto> {
                    new("u1", "Cam", DeviceType.Camera, "bad host", "", 6800, 0, false),
                });
                var profiles = new Mock<IProfileStore>();
                profiles.Setup(p => p.GetPhd2Settings()).Returns(new InMemoryProfileStore().GetPhd2Settings() with {
                    GuiderCamera = "Alpaca Camera [bad host:6800/0]",
                });
                var alpaca = new Mock<IAlpacaManagementClient>();
                alpaca.Setup(a => a.GetConfiguredDevicesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .Returns(() => Task.FromException<IReadOnlyDictionary<string, AlpacaConfiguredDevice>>(new System.ArgumentException(Marker)));
                alpaca.Setup(a => a.GetDriverVersionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .Returns(() => Task.FromException<string?>(new System.ArgumentException(Marker)));
                var device = await EquipmentEndpoints.GetDeviceDriverAsync("camera", selections.Object, alpaca.Object, CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.That(device, Is.InstanceOf<Ok<DeviceDriverDto>>());
                var guide = await EquipmentEndpoints.GetGuideCameraDriverAsync(profiles.Object, alpaca.Object, Mock.Of<IGuiderService>(),
                    CancellationToken.None).ConfigureAwait(false);
                Assert.That(guide, Is.InstanceOf<Ok<DeviceDriverDto>>());
            }
        }

        [Test]
        public async Task The_switch_route_answers_that_switchs_driver_by_its_id() {
            // Switches are many at once (one per power box), so they are asked by id, not by the one selection.
            var switches = new Mock<ISwitchService>();
            switches.Setup(s => s.GetDevice("box")).Returns(
                new DiscoveredDeviceDto("box", "Pegasus Powerbox", DeviceType.Switch, "rig.local", "192.0.2.10", 6800, 0, false));
            var alpaca = new Mock<IAlpacaManagementClient>();
            alpaca.Setup(a => a.GetConfiguredDevicesAsync("192.0.2.10", 6800, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AlpacaManagementClient.ParseConfiguredDevices(
                    "{\"Value\":[{\"DeviceType\":\"Switch\",\"DeviceNumber\":0,\"DeviceName\":\"Pegasus Powerbox\",\"UniqueID\":\"box\",\"Firmware\":\"1.4\"}]}"));
            alpaca.Setup(a => a.GetDriverVersionAsync("192.0.2.10", 6800, "switch", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync("2.1");

            var result = await EquipmentEndpoints.GetSwitchDriverAsync("box", switches.Object, alpaca.Object, CancellationToken.None);
            Assert.That((result as Ok<DeviceDriverDto>)?.Value, Is.EqualTo(new DeviceDriverDto("2.1", null, "1.4")));

            var unknown = await EquipmentEndpoints.GetSwitchDriverAsync("gone", switches.Object, alpaca.Object, CancellationToken.None);
            Assert.That((unknown as IStatusCodeHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        }

        [Test]
        public async Task No_selected_device_is_404_and_an_unknown_type_400() {
            var selections = new Mock<IEquipmentSelectionStore>();
            selections.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<DiscoveredDeviceDto>());
            var alpaca = new Mock<IAlpacaManagementClient>();
            var none = await EquipmentEndpoints.GetDeviceDriverAsync("focuser", selections.Object, alpaca.Object, CancellationToken.None);
            Assert.That((none as IStatusCodeHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            // Only a type's name: Enum parsing would also take a number or a comma list.
            foreach (var type in new[] { "toaster", "3", "99", "camera,focuser" }) {
                var bad = await EquipmentEndpoints.GetDeviceDriverAsync(type, selections.Object, alpaca.Object, CancellationToken.None);
                Assert.That((bad as IStatusCodeHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest), type);
            }
        }
    }
}
