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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Equipment.Equipment.MyGuider;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// A REST Park or Find Home stops guiding first, like the sequencer's Park and Find Home
    /// instructions already did. On 2026-10-16 the client's Home button moved the mount under a
    /// guiding PHD2, which chased a lost star for nine minutes.
    /// </summary>
    [TestFixture]
    [Category("IO")] // loopback scripted Alpaca device
    public class TelescopeHomeParkStopsGuidingTest {

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "mount-under-test", Name: "Bench mount", Type: DeviceType.Telescope,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private static string? Mount(string path) =>
            path.EndsWith("/connected", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/tracking", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/canfindhome", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/canpark", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/rightascension", StringComparison.Ordinal) ? "6.0"
            : path.EndsWith("/declination", StringComparison.Ordinal) ? "6.0"
            : null;

        private static async Task WaitForAsync(Func<bool> condition, string failure) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline) {
                if (condition()) {
                    return;
                }
                await Task.Delay(50);
            }
            Assert.Fail(failure);
        }

        private static readonly string[] StopOnly = { "stop_guiding" };

        private static bool PutSeen(ScriptedAlpacaDevice box, string verb) =>
            box.Puts.Any(p => p.Path.EndsWith("/" + verb, StringComparison.Ordinal));

        private static (Mock<IGuiderMediator> Guider, List<string> Order) GuidingGuider(bool connected = true) {
            var order = new List<string>();
            var guider = new Mock<IGuiderMediator>();
            guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = connected });
            guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>())).Returns(() => {
                lock (order) { order.Add("stop_guiding"); }
                return Task.FromResult(true);
            });
            return (guider, order);
        }

        private static async Task<TelescopeService> ConnectedAsync(ScriptedAlpacaDevice box, Func<IGuiderMediator?>? guider) {
            var svc = new TelescopeService(guiderResolver: guider);
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Connected, "never connected");
            return svc;
        }

        [Test]
        public async Task Find_home_stops_guiding_before_the_mount_moves() {
            await using var box = ScriptedAlpacaDevice.Start(Mount);
            var (guider, order) = GuidingGuider();
            using var svc = await ConnectedAsync(box, () => guider.Object);

            await svc.FindHomeAsync(idempotencyKey: null, CancellationToken.None);

            // The home PUT is issued only after the awaited stop returned, so "stop" is already on the
            // record the moment the device sees the PUT.
            lock (order) { Assert.That(order, Is.EqualTo(StopOnly)); }
            await WaitForAsync(() => PutSeen(box, "findhome"), "the mount never received findhome");
            guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Park_stops_guiding_before_the_mount_moves() {
            await using var box = ScriptedAlpacaDevice.Start(Mount);
            var (guider, order) = GuidingGuider();
            using var svc = await ConnectedAsync(box, () => guider.Object);

            await svc.ParkAsync(new ParkRequestDto(), idempotencyKey: null, CancellationToken.None);

            lock (order) { Assert.That(order, Is.EqualTo(StopOnly)); }
            await WaitForAsync(() => PutSeen(box, "park"), "the mount never received park");
            guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task A_guider_that_is_not_connected_is_left_alone_and_the_move_still_happens() {
            await using var box = ScriptedAlpacaDevice.Start(Mount);
            var (guider, _) = GuidingGuider(connected: false);
            using var svc = await ConnectedAsync(box, () => guider.Object);

            await svc.FindHomeAsync(idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => PutSeen(box, "findhome"), "the mount never received findhome");
            guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task A_guider_that_will_not_stop_does_not_block_the_move() {
            await using var box = ScriptedAlpacaDevice.Start(Mount);
            var guider = new Mock<IGuiderMediator>();
            guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = true });
            guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("guider busy"));
            using var svc = await ConnectedAsync(box, () => guider.Object);

            await svc.ParkAsync(new ParkRequestDto(), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => PutSeen(box, "park"), "a failing guider stop must not block the park");
        }

        [Test]
        public async Task Without_a_guider_in_the_composition_the_move_is_unchanged() {
            await using var box = ScriptedAlpacaDevice.Start(Mount);
            using var svc = await ConnectedAsync(box, guider: null);

            await svc.FindHomeAsync(idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => PutSeen(box, "findhome"), "the mount never received findhome");
        }
    }
}
