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
            public List<DiscoveredDeviceDto> Remembered { get; } = new();
            public List<string> ForgottenSwitches { get; } = new();
            public Task RememberAsync(DiscoveredDeviceDto device, CancellationToken ct) {
                Remembered.Add(device);
                return Task.CompletedTask;
            }
            public Task<IReadOnlyList<DiscoveredDeviceDto>> GetAllAsync(CancellationToken ct) =>
                Task.FromResult<IReadOnlyList<DiscoveredDeviceDto>>(_devices);
            public Task<int> ForgetAsync(DeviceType type, CancellationToken ct) =>
                Task.FromResult(_devices.RemoveAll(d => d.Type == type));

            public Task<int> ForgetSwitchAsync(string uniqueId, CancellationToken ct) {
                ForgottenSwitches.Add(uniqueId);
                return Task.FromResult(0);
            }
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

        // #1127 — a mocked service that also exposes a retained device.
        private static Mock<T> Retaining<T>(DiscoveredDeviceDto? retained) where T : class {
            var mock = new Mock<T>();
            mock.As<IRetainedDeviceSource>().SetupGet(s => s.RetainedDevice).Returns(retained);
            return mock;
        }

        [Test]
        public async Task ReconnectAsync_falls_back_to_the_retained_device_when_nothing_is_remembered() {
            // The wizard's store-only DELETE …/remembered leaves the service holding the device the
            // card names; Reconnect must dispatch THAT record, not answer 404.
            var retained = Device(DeviceType.Focuser, "foc-retained");
            var foc = Retaining<IFocuserService>(retained);
            foc.Setup(s => s.ConnectAsync(It.IsAny<ConnectRequestDto>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Accepted());
            var r = Build(new FakeStore(), (typeof(IFocuserService), foc.Object));

            var outcome = await r.ReconnectAsync(DeviceType.Focuser, CancellationToken.None);

            Assert.That(outcome.Attempted, Is.EqualTo(1));
            Assert.That(outcome.Dispatched, Is.EqualTo(1));
            foc.Verify(s => s.ConnectAsync(It.Is<ConnectRequestDto>(c => c.Device.UniqueId == "foc-retained"), null, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task ReconnectAsync_prefers_the_remembered_entry_over_the_retained_device() {
            // Both present (and diverged): the remembered entry is the auto-connect truth, so it wins
            // and the retained record is NOT dispatched on top of it.
            var foc = Retaining<IFocuserService>(Device(DeviceType.Focuser, "foc-retained"));
            foc.Setup(s => s.ConnectAsync(It.IsAny<ConnectRequestDto>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Accepted());
            var r = Build(new FakeStore(Device(DeviceType.Focuser, "foc-remembered")), (typeof(IFocuserService), foc.Object));

            var outcome = await r.ReconnectAsync(DeviceType.Focuser, CancellationToken.None);

            Assert.That(outcome.Attempted, Is.EqualTo(1));
            foc.Verify(s => s.ConnectAsync(It.Is<ConnectRequestDto>(c => c.Device.UniqueId == "foc-remembered"), null, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task ReconnectAsync_still_404s_when_neither_remembered_nor_retained() {
            var foc = Retaining<IFocuserService>(null);
            var r = Build(new FakeStore(), (typeof(IFocuserService), foc.Object));
            var outcome = await r.ReconnectAsync(DeviceType.Focuser, CancellationToken.None);
            Assert.That(outcome.Attempted, Is.EqualTo(0));
            foc.Verify(s => s.ConnectAsync(It.IsAny<ConnectRequestDto>(), null, It.IsAny<CancellationToken>()), Times.Never);
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
        // Only 127.0.0.1 answers: the saved address is gone.
        private sealed class LocalOnlyAlpaca(string key, AlpacaConfiguredDevice device) : IAlpacaManagementClient {
            public Task<IReadOnlyDictionary<string, string>> GetConfiguredDeviceNamesAsync(string host, int port, CancellationToken ct) =>
                throw new NotSupportedException();
            public Task<IReadOnlyDictionary<string, AlpacaConfiguredDevice>> GetConfiguredDevicesAsync(string host, int port, CancellationToken ct) =>
                Task.FromResult<IReadOnlyDictionary<string, AlpacaConfiguredDevice>>(host == "127.0.0.1"
                    ? new Dictionary<string, AlpacaConfiguredDevice> { [key] = device }
                    : new Dictionary<string, AlpacaConfiguredDevice>());
        }

        [Test]
        public async Task A_switch_saved_under_a_lost_address_is_dialled_and_resaved_as_localhost() {
            // 2026-10-22: the CM4 came home with its switches saved under the field router's address.
            var saved = new DiscoveredDeviceDto("ZWO_ASIAIR_2", "ZWO ASIAIR Plus (Pi CM4) Switch", DeviceType.Switch,
                "192.168.8.118", "192.168.8.118", 6800, 2, false);
            var store = new FakeStore(saved);
            var sw = new Mock<ISwitchService>();
            sw.Setup(s => s.ConnectAsync(It.IsAny<ConnectRequestDto>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(Accepted());
            var sp = new Mock<IServiceProvider>();
            sp.Setup(s => s.GetService(typeof(ISwitchService))).Returns(sw.Object);
            var alpaca = new LocalOnlyAlpaca("switch/2", new AlpacaConfiguredDevice("ZWO ASIAIR Plus (Pi CM4) Switch", "ZWO_ASIAIR_2"));
            var r = new EquipmentReconnector(sp.Object, store, NullLogger<EquipmentReconnector>.Instance, alpaca, _ => false);

            var outcome = await r.ReconnectAsync(DeviceType.Switch, CancellationToken.None);

            Assert.That(outcome.Dispatched, Is.EqualTo(1));
            sw.Verify(s => s.ConnectAsync(It.Is<ConnectRequestDto>(c => c.Device.HostName == "localhost" && c.Device.IpAddress == "127.0.0.1"),
                null, It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(store.ForgottenSwitches, Has.Count.EqualTo(1).And.Contains("ZWO_ASIAIR_2"), "the old address's entry goes");
            Assert.That(store.Remembered, Has.Count.EqualTo(1));
            Assert.That(store.Remembered[0].HostName, Is.EqualTo("localhost"));
        }

        [Test]
        public async Task Without_a_management_client_the_saved_record_is_dialled_as_it_is() {
            var saved = new DiscoveredDeviceDto("ZWO_EAF_0", "ZWO EAF", DeviceType.Focuser, "192.168.8.118", "192.168.8.118", 6800, 0, false);
            var foc = new Mock<IFocuserService>();
            foc.Setup(s => s.ConnectAsync(It.IsAny<ConnectRequestDto>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(Accepted());
            var store = new FakeStore(saved);
            var r = Build(store, (typeof(IFocuserService), foc.Object));

            await r.ReconnectAsync(DeviceType.Focuser, CancellationToken.None);

            foc.Verify(s => s.ConnectAsync(It.Is<ConnectRequestDto>(c => c.Device.IpAddress == "192.168.8.118"), null, It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(store.Remembered, Is.Empty);
        }
    }
}
