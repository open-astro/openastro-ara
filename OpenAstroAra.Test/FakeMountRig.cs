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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2139 — a real Telescope service connected to the in-memory fake mount, with the person rules' inputs (the profile
    /// store, the sequencer, Polar Align): the seam a person's move is admitted at. <see cref="Fake"/> counts what reached
    /// the mount.
    /// </summary>
    internal sealed class FakeMountRig : IDisposable {
        private static readonly DiscoveredDeviceDto Mount = new(
            UniqueId: "mount-1", Name: "Bench Mount", Type: DeviceType.Telescope,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        public required TelescopeService Service { get; init; }
        public required FakeTelescopeDevice Fake { get; init; }

        public static async Task<FakeMountRig> ConnectAsync(IProfileStore? profiles = null, ISequencerService? sequencer = null,
                IPolarAlignService? polarAlign = null, Action<FakeTelescopeDevice>? configure = null) {
            FakeTelescopeDevice? fake = null;
            var svc = new TelescopeService(profileStore: profiles,
                    sequencerResolver: sequencer is null ? null : () => sequencer,
                    polarAlignResolver: polarAlign is null ? null : () => polarAlign) {
                RefreshPeriod = TimeSpan.FromMilliseconds(20),
                StopGuidingBeforeMoveTimeout = TimeSpan.FromMilliseconds(50),
                DeviceFactory = _ => {
                    fake = new FakeTelescopeDevice { ReportsArrival = true, ArrivesAtTarget = true };
                    configure?.Invoke(fake);
                    return fake;
                },
            };
            await svc.ConnectAsync(new ConnectRequestDto(Mount), null, CancellationToken.None);
            await WaitForAsync(() => svc.IsCleanlyConnected && svc.GetAsync(CancellationToken.None).Result?.Capabilities is not null, "the first clean read");
            return new FakeMountRig { Service = svc, Fake = fake! };
        }

        public static async Task WaitForAsync(Func<bool> condition, string what) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition()) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail($"timed out waiting for {what}");
                }
                await Task.Delay(10);
            }
        }

        /// <summary>A GoTo the service accepted reaches the mount in the background.</summary>
        public Task SlewedAsync() => WaitForAsync(() => Fake.Slews > 0, "the GoTo at the mount");

        public void Dispose() => Service.Dispose();
    }
}
