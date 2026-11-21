#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#2083 — <see cref="TelescopeService.SyncEpoch"/> counts the syncs the mount accepted: after a clutch turn,
    /// Polar Align refuses a motor start until it moves (or the mount reconnects). The centring loop's mediator Sync and the
    /// REST slew with <c>sync: true</c> both move it; a refused sync does not. Against a fake mount, with no time-sync
    /// service, so the mount-clock gate is off.</summary>
    [TestFixture]
    public class TelescopeSyncEpochTest {

        private static readonly DiscoveredDeviceDto Mount = new(
            UniqueId: "mount-1", Name: "Bench Mount", Type: DeviceType.Telescope,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        private FakeTelescopeDevice? _device;

        private TelescopeService Build() =>
            new() {
                RefreshPeriod = TimeSpan.FromMilliseconds(20),
                StopGuidingBeforeMoveTimeout = TimeSpan.FromMilliseconds(50),
                DeviceFactory = _ => _device = new FakeTelescopeDevice(),
            };

        private static async Task WaitForAsync(Func<bool> condition, string what) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition()) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail($"timed out waiting for {what}");
                }
                await Task.Delay(10);
            }
        }

        private async Task<TelescopeService> ConnectedAsync() {
            var svc = Build();
            await svc.ConnectAsync(new ConnectRequestDto(Mount), null, CancellationToken.None);
            await WaitForAsync(() => svc.IsCleanlyConnected && svc.GetAsync(CancellationToken.None).Result?.Capabilities is not null, "the first clean read");
            return svc;
        }

        [Test]
        public async Task An_accepted_mediator_sync_moves_the_sync_epoch() {
            using var svc = await ConnectedAsync();
            var before = svc.SyncEpoch;

            var synced = await ((ITelescopeMediator)svc).Sync(new Coordinates(Angle.ByHours(5.0), Angle.ByDegree(30.0), Epoch.JNOW), CancellationToken.None);

            Assert.That(synced, Is.True);
            Assert.That(_device!.Syncs, Is.EqualTo(1));
            Assert.That(svc.SyncEpoch, Is.EqualTo(before + 1), "a Center's sync clears a clutch turn");
        }

        [Test]
        public async Task A_rest_slew_with_sync_moves_the_sync_epoch() {
            using var svc = await ConnectedAsync();
            var before = svc.SyncEpoch;

            await svc.SlewAsync(new SlewRequestDto(5.0, 30.0, Sync: true), null, CancellationToken.None);

            await WaitForAsync(() => _device!.Syncs == 1, "the sync");
            await WaitForAsync(() => svc.SyncEpoch == before + 1, "the sync epoch to move");
            Assert.That(svc.SyncEpoch, Is.EqualTo(before + 1));
        }

        [Test]
        public async Task A_refused_mediator_sync_leaves_the_sync_epoch() {
            using var svc = await ConnectedAsync();
            var before = svc.SyncEpoch;

            var synced = await ((ITelescopeMediator)svc).Sync(new Coordinates(Angle.ByHours(5.0), Angle.ByDegree(90.6), Epoch.JNOW), CancellationToken.None);

            Assert.That(synced, Is.False, "Dec past the pole is refused before the mount");
            Assert.That(_device!.Syncs, Is.Zero);
            Assert.That(svc.SyncEpoch, Is.EqualTo(before), "nothing was synced, so a clutch turn stands");
        }

        [Test]
        public async Task A_rest_slew_without_sync_leaves_the_sync_epoch() {
            using var svc = await ConnectedAsync();
            var before = svc.SyncEpoch;

            await svc.SlewAsync(new SlewRequestDto(5.0, 30.0), null, CancellationToken.None);

            await WaitForAsync(() => _device!.Slews == 1, "the GoTo");
            Assert.That(svc.SyncEpoch, Is.EqualTo(before), "a GoTo is no sync");
        }
    }
}
