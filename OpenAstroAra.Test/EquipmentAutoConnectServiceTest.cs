#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    [TestFixture]
    [Category("IO")]
    public class EquipmentAutoConnectServiceTest {

        private static DiscoveredDeviceDto Camera(string id) =>
            new(UniqueId: id, Name: id, Type: DeviceType.Camera, HostName: id, IpAddress: "10.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        [Test]
        public async Task A_restart_connects_only_the_active_profiles_devices() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-autoconnect-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(dir, "profiles"));
            try {
                var a = Guid.NewGuid();
                var b = Guid.NewGuid();
                Guid active = b;
                using var selections = new EquipmentSelectionStore(dir, NullLogger<EquipmentSelectionStore>.Instance, _ => false, () => active);
                await selections.RememberAsync(Camera("real-cam"), CancellationToken.None);
                active = a;
                await selections.RememberAsync(Camera("sim-cam"), CancellationToken.None);
                active = b; // the rig restarts on the other profile, the one written first

                var connected = new TaskCompletionSource<string>();
                var reconnector = new Mock<IEquipmentReconnector>();
                reconnector.Setup(r => r.ResolveConnect(DeviceType.Camera, It.IsAny<DiscoveredDeviceDto>(), It.IsAny<CancellationToken>()))
                    .Returns<DeviceType, DiscoveredDeviceDto, CancellationToken>((_, d, _) => () => {
                        connected.TrySetResult(d.UniqueId);
                        return Task.CompletedTask;
                    });
                var profile = new Mock<IProfileStore>();
                profile.Setup(p => p.GetEquipmentConnection()).Returns(new EquipmentConnectionDto(
                    Camera: true, Mount: true, Focuser: true, FilterWheel: true, Rotator: true, Guider: false,
                    FlatPanel: true, Dome: true, Weather: true, SafetyMonitor: true));
                var services = new ServiceCollection()
                    .AddSingleton<IEquipmentSelectionStore>(selections)
                    .AddSingleton(reconnector.Object)
                    .AddSingleton(profile.Object)
                    .BuildServiceProvider();

                using var auto = new EquipmentAutoConnectService(services, NullLogger<EquipmentAutoConnectService>.Instance);
                await auto.StartAsync(CancellationToken.None);

                var who = await connected.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await auto.StopAsync(CancellationToken.None);

                Assert.That(who, Is.EqualTo("real-cam"));
                reconnector.Verify(r => r.ResolveConnect(DeviceType.Camera, It.Is<DiscoveredDeviceDto>(d => d.UniqueId == "sim-cam"), It.IsAny<CancellationToken>()), Times.Never);
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
