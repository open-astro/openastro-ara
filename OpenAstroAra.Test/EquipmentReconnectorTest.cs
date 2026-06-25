#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §52.1 <see cref="EquipmentReconnector"/> — reconnects the remembered device(s) for a type
    /// without re-discovery (shared by auto-connect-on-boot and the manual /reconnect endpoints).
    /// </summary>
    [TestFixture]
    public class EquipmentReconnectorTest {

        private static DiscoveredDeviceDto Device(DeviceType type, string id, int number = 0) =>
            new(UniqueId: id, Name: id, Type: type, HostName: "h", IpAddress: "127.0.0.1",
                IpPort: 11111, AlpacaDeviceNumber: number, UseHttps: false);

        private static OperationAcceptedDto Accepted() =>
            new(Guid.Empty, "connect", DateTimeOffset.UnixEpoch, null);

        // A pure fake store returning a fixed remembered set.
        private sealed class FakeStore : IEquipmentSelectionStore {
            private readonly List<DiscoveredDeviceDto> _devices;
            public FakeStore(params DiscoveredDeviceDto[] devices) => _devices = new(devices);
            public Task RememberAsync(DiscoveredDeviceDto device, CancellationToken ct) => Task.CompletedTask;
            public Task<IReadOnlyList<DiscoveredDeviceDto>> GetAllAsync(CancellationToken ct) =>
                Task.FromResult<IReadOnlyList<DiscoveredDeviceDto>>(_devices);
        }

        // Build a reconnector whose IServiceProvider resolves the given mocked services.
        private static EquipmentReconnector Build(IEquipmentSelectionStore store,
                params (Type type, object impl)[] services) {
            var sp = new Mock<IServiceProvider>();
            foreach (var (t, impl) in services) {
                sp.Setup(s => s.GetService(t)).Returns(impl);
            }
            return new EquipmentReconnector(sp.Object, store);
        }

        [Test]
        public async Task ReconnectAsync_returns_zero_when_nothing_remembered() {
            var r = Build(new FakeStore());
            Assert.That(await r.ReconnectAsync(DeviceType.Camera, CancellationToken.None), Is.EqualTo(0));
        }

        [Test]
        public async Task ReconnectAsync_dispatches_a_single_instance_connect() {
            var cam = new Mock<ICameraService>();
            cam.Setup(s => s.ConnectAsync(It.IsAny<ConnectRequestDto>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Accepted());
            var r = Build(new FakeStore(Device(DeviceType.Camera, "cam-1")), (typeof(ICameraService), cam.Object));

            var n = await r.ReconnectAsync(DeviceType.Camera, CancellationToken.None);

            Assert.That(n, Is.EqualTo(1));
            cam.Verify(s => s.ConnectAsync(It.IsAny<ConnectRequestDto>(), null, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task ReconnectAsync_reconnects_every_remembered_switch() {
            var sw = new Mock<ISwitchService>();
            sw.Setup(s => s.ConnectAsync(It.IsAny<ConnectRequestDto>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Accepted());
            var r = Build(
                new FakeStore(Device(DeviceType.Switch, "sw-0", 0), Device(DeviceType.Switch, "sw-1", 1)),
                (typeof(ISwitchService), sw.Object));

            var n = await r.ReconnectAsync(DeviceType.Switch, CancellationToken.None);

            Assert.That(n, Is.EqualTo(2));
            sw.Verify(s => s.ConnectAsync(It.IsAny<ConnectRequestDto>(), null, It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        [Test]
        public async Task ReconnectAsync_matches_a_remembered_FlatDevice_for_a_CoverCalibrator_request() {
            // Discovery may remember the flat panel under either the ASCOM (CoverCalibrator) or
            // the NINA (FlatDevice) token — a reconnect for one must still find the other.
            var flat = new Mock<IFlatDeviceService>();
            flat.Setup(s => s.ConnectAsync(It.IsAny<ConnectRequestDto>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Accepted());
            var r = Build(new FakeStore(Device(DeviceType.FlatDevice, "flat-1")),
                (typeof(IFlatDeviceService), flat.Object));

            var n = await r.ReconnectAsync(DeviceType.CoverCalibrator, CancellationToken.None);

            Assert.That(n, Is.EqualTo(1));
        }

        [Test]
        public async Task ReconnectAsync_skips_a_type_with_no_alpaca_connect_path() {
            // Guider connects via PHD2, not this flow — even if one is remembered, nothing is dispatched.
            var r = Build(new FakeStore(Device(DeviceType.Guider, "phd2")));
            Assert.That(await r.ReconnectAsync(DeviceType.Guider, CancellationToken.None), Is.EqualTo(0));
        }
    }
}
