#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1539 — the §42.3 ladder against a real rotator service, reconnector and fake device:
    /// a user disconnect that lands between the ladder's state check and its reconnect dispatch must
    /// not be undone by that dispatch.</summary>
    [TestFixture]
    public class RecoveryRespectsUserDisconnectTest {

        private static readonly DiscoveredDeviceDto Rotator = new(
            UniqueId: "rot-1", Name: "Bench Rotator", Type: DeviceType.Rotator,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        private static async Task WaitForAsync(Func<bool> condition, string what) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition()) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail($"timed out waiting for {what}");
                }
                await Task.Delay(10);
            }
        }

        [Test]
        public async Task A_user_disconnect_between_the_ladders_state_check_and_its_dispatch_is_not_undone() {
            var devices = new List<FakeRotatorDevice>();
            using var rotator = new RotatorService {
                RefreshPeriod = TimeSpan.FromMilliseconds(20),
                DeviceFactory = _ => { var d = new FakeRotatorDevice(); lock (devices) { devices.Add(d); } return d; },
            };
            await rotator.ConnectAsync(new ConnectRequestDto(Rotator), null, CancellationToken.None);
            await WaitForAsync(() => rotator.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Connected, "the connect");
            lock (devices) { devices[0].Answers = false; }
            await WaitForAsync(() => rotator.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Error, "the §42.3 trip");
            lock (devices) { devices[0].Answers = true; } // the device is back: a dispatched reconnect would succeed

            // The service the reconnector and the ladder reach. Its first state read after the trip
            // answers Error, and the user's Disconnect lands right after that read: the window
            // between the ladder's release check and its reconnect dispatch.
            var raced = false;
            var racing = new Mock<IRotatorService>();
            racing.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).Returns(async (CancellationToken ct) => {
                var dto = await rotator.GetAsync(ct);
                if (!raced && dto?.State == EquipmentConnectionState.Error) {
                    raced = true;
                    await rotator.DisconnectAsync(null, ct);
                }
                return dto;
            });
            racing.Setup(r => r.ConnectAsync(It.IsAny<ConnectRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns((ConnectRequestDto req, string? key, CancellationToken ct) => rotator.ConnectAsync(req, key, ct));
            racing.As<IRecoverableDevice>().Setup(r => r.TryRecover(It.IsAny<DiscoveredDeviceDto>()))
                .Returns((DiscoveredDeviceDto d) => rotator.TryRecover(d));
            racing.As<IRecoverableDevice>().Setup(r => r.DesiredConnection).Returns(() => rotator.DesiredConnection);
            var services = new Mock<IServiceProvider>();
            services.Setup(s => s.GetService(typeof(IRotatorService))).Returns(racing.Object);
            var store = new Mock<IEquipmentSelectionStore>();
            store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<DiscoveredDeviceDto> { Rotator });
            var reconnector = new EquipmentReconnector(services.Object, store.Object, NullLogger<EquipmentReconnector>.Instance);

            var actions = new List<string>();
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<string, JsonElement, CancellationToken>((t, p, _) => {
                    if (t == WsEventCatalog.EquipmentFaultActionTaken) { lock (actions) { actions.Add(p.GetProperty("action").GetString()!); } }
                })
                .Returns(Task.CompletedTask);
            using var reaction = new FaultReactionService(reconnector: reconnector, ws: ws.Object) {
                DelayShaper = _ => TimeSpan.Zero,
                ConnectConfirmTimeout = TimeSpan.FromMilliseconds(300),
                ConnectPollInterval = TimeSpan.FromMilliseconds(5),
                LingerMaxAttempts = 0,
            };

            reaction.OnFault(new EquipmentFaultEvent(DeviceType.Rotator, Rotator.UniqueId, Rotator.Name,
                EquipmentFaultKind.Disconnected, "3 probes failed", DateTimeOffset.UtcNow));
            await reaction.WhenIdleAsync();

            Assert.That(raced, Is.True, "the user's disconnect landed inside the ladder");
            var dto = await rotator.GetAsync(CancellationToken.None);
            List<string> seen;
            lock (actions) { seen = actions.ToList(); }
            Assert.That(dto?.State, Is.EqualTo(EquipmentConnectionState.Disconnected),
                $"the rotator the user disconnected stays disconnected (actions: {string.Join(",", seen)})");
            Assert.That(seen, Does.Contain("released").And.Not.Contain("recovered"));
            lock (devices) { Assert.That(devices, Has.Count.EqualTo(1), "no reconnect reached the hardware"); }
        }
    }
}
