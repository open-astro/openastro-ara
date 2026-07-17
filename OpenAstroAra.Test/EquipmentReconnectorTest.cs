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
using Microsoft.Extensions.Logging.Abstractions;
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
            public Task<int> ForgetAsync(DeviceType type, CancellationToken ct) =>
                Task.FromResult(_devices.RemoveAll(d => d.Type == type));
        }

        // Build a reconnector whose IServiceProvider resolves the given mocked services.
        private static EquipmentReconnector Build(IEquipmentSelectionStore store,
                params (Type type, object impl)[] services) {
            var sp = new Mock<IServiceProvider>();
            foreach (var (t, impl) in services) {
                sp.Setup(s => s.GetService(t)).Returns(impl);
            }
            return new EquipmentReconnector(sp.Object, store, NullLogger<EquipmentReconnector>.Instance);
        }

        [Test]
        public async Task ReconnectAsync_returns_zero_when_nothing_remembered() {
            var r = Build(new FakeStore());
            var outcome = await r.ReconnectAsync(DeviceType.Camera, CancellationToken.None);
            Assert.That(outcome.Attempted, Is.EqualTo(0));
            Assert.That(outcome.Dispatched, Is.EqualTo(0));
        }

        [Test]
        public async Task ReconnectAsync_dispatches_a_single_instance_connect() {
            var cam = new Mock<ICameraService>();
            cam.Setup(s => s.ConnectAsync(It.IsAny<ConnectRequestDto>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Accepted());
            var r = Build(new FakeStore(Device(DeviceType.Camera, "cam-1")), (typeof(ICameraService), cam.Object));

            var outcome = await r.ReconnectAsync(DeviceType.Camera, CancellationToken.None);

            Assert.That(outcome.Attempted, Is.EqualTo(1));
            Assert.That(outcome.Dispatched, Is.EqualTo(1));
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

            var outcome = await r.ReconnectAsync(DeviceType.Switch, CancellationToken.None);

            Assert.That(outcome.Attempted, Is.EqualTo(2));
            Assert.That(outcome.Dispatched, Is.EqualTo(2));
            sw.Verify(s => s.ConnectAsync(It.IsAny<ConnectRequestDto>(), null, It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        [Test]
        public async Task ReconnectAsync_isolates_a_failing_device_and_attempts_the_rest() {
            // Rig-restart case: one switch's Alpaca server isn't up yet and its ConnectAsync throws.
            // The other switch must still be attempted, and the call must not propagate the failure.
            var sw = new Mock<ISwitchService>();
            var first = true;
            sw.Setup(s => s.ConnectAsync(It.IsAny<ConnectRequestDto>(), null, It.IsAny<CancellationToken>()))
                .Returns(() => {
                    if (first) { first = false; throw new InvalidOperationException("switch not ready"); }
                    return Task.FromResult(Accepted());
                });
            var r = Build(
                new FakeStore(Device(DeviceType.Switch, "sw-0", 0), Device(DeviceType.Switch, "sw-1", 1)),
                (typeof(ISwitchService), sw.Object));

            var outcome = await r.ReconnectAsync(DeviceType.Switch, CancellationToken.None);

            Assert.That(outcome.Attempted, Is.EqualTo(2)); // both attempted despite the first throwing
            Assert.That(outcome.Dispatched, Is.EqualTo(1)); // only the second dispatched cleanly
            sw.Verify(s => s.ConnectAsync(It.IsAny<ConnectRequestDto>(), null, It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        [Test]
        public async Task ReconnectAsync_reports_zero_dispatched_when_every_device_throws() {
            // Total synchronous failure (e.g. every switch's Alpaca server down on a rig restart):
            // each is attempted but none dispatches, so the endpoint can return 502 instead of 202.
            var sw = new Mock<ISwitchService>();
            sw.Setup(s => s.ConnectAsync(It.IsAny<ConnectRequestDto>(), null, It.IsAny<CancellationToken>()))
                .Throws(new InvalidOperationException("switch not ready"));
            var r = Build(
                new FakeStore(Device(DeviceType.Switch, "sw-0", 0), Device(DeviceType.Switch, "sw-1", 1)),
                (typeof(ISwitchService), sw.Object));

            var outcome = await r.ReconnectAsync(DeviceType.Switch, CancellationToken.None);

            Assert.That(outcome.Attempted, Is.EqualTo(2));
            Assert.That(outcome.Dispatched, Is.EqualTo(0));
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

            var outcome = await r.ReconnectAsync(DeviceType.CoverCalibrator, CancellationToken.None);

            Assert.That(outcome.Attempted, Is.EqualTo(1));
            Assert.That(outcome.Dispatched, Is.EqualTo(1));
        }

        [Test]
        public async Task ReconnectAsync_dispatches_the_guider_via_its_own_connect_path() {
            // Guider connects via PHD2 (JSON-RPC), not the remembered-Alpaca loop — the
            // reconnector dispatches IGuiderService.ConnectAsync with a NULL-field request
            // so the daemon uses the active profile's phd2 host/port (which may be a remote
            // SBC). This is what lets a meridian flip's guider re-cal heal a dropped guider.
            var guider = new Mock<IGuiderService>();
            guider.Setup(s => s.ConnectAsync(
                    It.Is<GuiderConnectRequestDto>(req => req.Host == null && req.Port == null),
                    null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Accepted());
            var r = Build(new FakeStore(), (typeof(IGuiderService), guider.Object));

            var outcome = await r.ReconnectAsync(DeviceType.Guider, CancellationToken.None);

            Assert.That(outcome.Attempted, Is.EqualTo(1));
            Assert.That(outcome.Dispatched, Is.EqualTo(1));
            guider.VerifyAll();
        }

        [Test]
        public async Task ReconnectAsync_reports_a_failed_guider_dispatch_as_attempted_not_dispatched() {
            var guider = new Mock<IGuiderService>();
            guider.Setup(s => s.ConnectAsync(It.IsAny<GuiderConnectRequestDto>(), null, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("phd2 unreachable"));
            var r = Build(new FakeStore(), (typeof(IGuiderService), guider.Object));

            var outcome = await r.ReconnectAsync(DeviceType.Guider, CancellationToken.None);

            Assert.That(outcome.Attempted, Is.EqualTo(1));
            Assert.That(outcome.Dispatched, Is.EqualTo(0));
        }
    }
}
