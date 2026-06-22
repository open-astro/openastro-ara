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
                () => { connectCalled = true; return Task.FromResult(Accepted); },
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
        public void An_already_cancelled_request_throws_before_connecting_and_remembers_nothing() {
            var store = new FakeSelectionStore();
            var connectCalled = false;
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAsync<OperationCanceledException>(() => EquipmentEndpoints.ConnectAndRememberAsync(
                Request(), store,
                () => { connectCalled = true; return Task.FromResult(Accepted); },
                cts.Token));

            Assert.Multiple(() => {
                Assert.That(connectCalled, Is.False, "a cancelled request must NOT dispatch a connect to hardware");
                Assert.That(store.Remembered, Is.Empty, "a cancelled request must NOT remember a device");
            });
        }

        [Test]
        public void A_failed_connect_does_not_remember_the_device() {
            var store = new FakeSelectionStore();

            Assert.ThrowsAsync<InvalidOperationException>(() => EquipmentEndpoints.ConnectAndRememberAsync(
                Request(), store,
                () => throw new InvalidOperationException("bridge unreachable"),
                CancellationToken.None));

            Assert.That(store.Remembered, Is.Empty, "a device that failed to connect must NOT be remembered");
        }

        private sealed class FakeSelectionStore : IEquipmentSelectionStore {
            public System.Collections.Generic.List<DiscoveredDeviceDto> Remembered { get; } = new();

            public Task RememberAsync(DiscoveredDeviceDto device, CancellationToken ct) {
                Remembered.Add(device);
                return Task.CompletedTask;
            }

            public Task<System.Collections.Generic.IReadOnlyDictionary<DeviceType, DiscoveredDeviceDto>> GetAllAsync(CancellationToken ct) =>
                Task.FromResult<System.Collections.Generic.IReadOnlyDictionary<DeviceType, DiscoveredDeviceDto>>(
                    new System.Collections.Generic.Dictionary<DeviceType, DiscoveredDeviceDto>());
        }
    }
}
