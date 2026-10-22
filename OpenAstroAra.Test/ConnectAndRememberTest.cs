#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Http.HttpResults;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §52.1 connect-and-remember (<see cref="EquipmentEndpoints.ConnectAndRememberAsync"/>): the
    /// shared chokepoint every Alpaca <c>/connect</c> handler routes through after the §68.1 version
    /// gate was removed (Alpaca is open by design). It connects the device, then remembers it for
    /// auto-connect-on-boot — and short-circuits an already-cancelled request before doing either, so
    /// an abandoned connect neither pokes hardware nor persists a selection.
    /// </summary>
    [TestFixture]
    public class ConnectAndRememberTest {

        private static ConnectRequestDto Request(string uniqueId = "cam-1") =>
            new(new DiscoveredDeviceDto(
                UniqueId: uniqueId, Name: "Camera", Type: DeviceType.Camera,
                HostName: "bridge.local", IpAddress: "192.168.1.50", IpPort: 11111,
                AlpacaDeviceNumber: 0, UseHttps: false));

        private static readonly OperationAcceptedDto Accepted =
            new(Guid.NewGuid(), "camera.connect", DateTimeOffset.UnixEpoch, "idem-1");

        [Test]
        public async Task Connects_then_remembers_the_device() {
            var store = new FakeSelectionStore();
            var connectCalled = false;

            var result = await EquipmentEndpoints.ConnectAndRememberAsync(
                Request(), store,
                _ => { connectCalled = true; return Task.FromResult(Accepted); },
                CancellationToken.None);

            var accepted = result as Accepted<OperationAcceptedDto>;
            Assert.Multiple(() => {
                Assert.That(accepted, Is.Not.Null, "an open connect must yield an Accepted result");
                Assert.That(accepted!.Value, Is.SameAs(Accepted));
                Assert.That(connectCalled, Is.True);
                Assert.That(store.Remembered, Has.Count.EqualTo(1), "a connected device must be remembered for auto-connect");
                Assert.That(store.Remembered[0].UniqueId, Is.EqualTo("cam-1"));
            });
        }

        [Test]
        public async Task An_already_cancelled_request_throws_before_connecting_and_remembers_nothing() {
            var store = new FakeSelectionStore();
            var connectCalled = false;
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAsync<OperationCanceledException>(() => EquipmentEndpoints.ConnectAndRememberAsync(
                Request(), store,
                _ => { connectCalled = true; return Task.FromResult(Accepted); },
                cts.Token));

            Assert.Multiple(() => {
                Assert.That(connectCalled, Is.False, "a cancelled request must NOT dispatch a connect to hardware");
                Assert.That(store.Remembered, Is.Empty, "a cancelled request must NOT remember a device");
            });
        }

        [Test]
        public async Task A_failed_connect_does_not_remember_the_device() {
            var store = new FakeSelectionStore();

            await Assert.ThrowsAsync<InvalidOperationException>(() => EquipmentEndpoints.ConnectAndRememberAsync(
                Request(), store,
                _ => throw new InvalidOperationException("bridge unreachable"),
                CancellationToken.None));

            Assert.That(store.Remembered, Is.Empty, "a device that failed to connect must NOT be remembered");
        }

        private static ConnectRequestDto At(string host, string ip) =>
            new(new DiscoveredDeviceDto(
                UniqueId: "ZWO_EAF_0", Name: "ZWO EAF", Type: DeviceType.Focuser,
                HostName: host, IpAddress: ip, IpPort: 6800, AlpacaDeviceNumber: 0, UseHttps: false));

        [Test]
        public async Task A_device_on_this_machine_is_dialled_and_remembered_as_localhost() {
            // Any loopback address is this machine under the production rule, whatever the NICs are.
            var store = new FakeSelectionStore();
            ConnectRequestDto? dialled = null;

            await EquipmentEndpoints.ConnectAndRememberAsync(At("127.0.0.2", "127.0.0.2"), store,
                r => { dialled = r; return Task.FromResult(Accepted); }, CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(dialled?.Device.HostName, Is.EqualTo("localhost"));
                Assert.That(dialled?.Device.IpAddress, Is.EqualTo("127.0.0.1"));
                Assert.That(store.Remembered.Single().HostName, Is.EqualTo("localhost"));
                Assert.That(store.Remembered.Single().IpAddress, Is.EqualTo("127.0.0.1"));
            });
        }

        [Test]
        public async Task A_device_on_another_machine_is_dialled_and_remembered_as_given() {
            var store = new FakeSelectionStore();
            ConnectRequestDto? dialled = null;

            await EquipmentEndpoints.ConnectAndRememberAsync(At("192.0.2.10", "192.0.2.10"), store,
                r => { dialled = r; return Task.FromResult(Accepted); }, CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(dialled?.Device.IpAddress, Is.EqualTo("192.0.2.10"));
                Assert.That(store.Remembered.Single().IpAddress, Is.EqualTo("192.0.2.10"));
            });
        }

        private sealed class FakeSelectionStore : IEquipmentSelectionStore {
            public System.Collections.Generic.List<DiscoveredDeviceDto> Remembered { get; } = new();

            public Task RememberAsync(DiscoveredDeviceDto device, CancellationToken ct) {
                Remembered.Add(device);
                return Task.CompletedTask;
            }

            public Task<System.Collections.Generic.IReadOnlyList<DiscoveredDeviceDto>> GetAllAsync(CancellationToken ct) =>
                Task.FromResult<System.Collections.Generic.IReadOnlyList<DiscoveredDeviceDto>>(Remembered);

            public Task<int> ForgetAsync(DeviceType type, CancellationToken ct) =>
                Task.FromResult(Remembered.RemoveAll(d => d.Type == type));

        public Task<int> ForgetSwitchAsync(string uniqueId, CancellationToken ct) => Task.FromResult(0);

            public Task RelocateAsync(DiscoveredDeviceDto from, DiscoveredDeviceDto moved, CancellationToken ct) => RememberAsync(moved, ct);
        }
    }
}
