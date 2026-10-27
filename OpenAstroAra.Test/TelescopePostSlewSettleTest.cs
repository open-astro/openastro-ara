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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1445 — a sequence autofocus started in the same second its slew finished, and its first
    /// frames walked the coarse search away from focus. A mediator slew now settles for
    /// <c>TelescopeSettings.SettleTime</c> (NINA's post-slew settle, default 5 s) once the mount has
    /// arrived; the flip goto does not, since <c>MeridianFlipExecutor</c> settles with its own setting.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    [Category("bench")] // loopback-only, runs in the default job too
    public class TelescopePostSlewSettleTest {

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "mount-under-test", Name: "Bench mount", Type: DeviceType.Telescope,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        // A JNOW mount already on the target, so the goto arrives on the first poll.
        private static Func<string, string?> Mount() => path =>
            path.EndsWith("/equatorialsystem", StringComparison.Ordinal) ? "1"
            : path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/tracking", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/rightascension", StringComparison.Ordinal) ? "6.0"
            : path.EndsWith("/declination", StringComparison.Ordinal) ? "30.0"
            : path.EndsWith("/sideofpier", StringComparison.Ordinal) ? "0"
            : path.EndsWith("/cansetpierside", StringComparison.Ordinal) ? "false"
            : null;

        private static Coordinates Target() => new(Angle.ByHours(6.0), Angle.ByDegree(30.0), Epoch.JNOW);

        private static (OpenAstroAra.Profile.Profile Profile, IProfileService Service) ProfileWith(int? settleSeconds) {
            var profile = new OpenAstroAra.Profile.Profile();
            if (settleSeconds is int seconds) {
                profile.TelescopeSettings.SettleTime = seconds;
            }
            var svc = new Mock<IProfileService>();
            svc.SetupGet(p => p.ActiveProfile).Returns(profile);
            return (profile, svc.Object);
        }

        private static async Task<TelescopeService> ConnectAsync(ScriptedAlpacaDevice box, IProfileService profile, List<TimeSpan> settles) {
            var svc = new TelescopeService(profileService: profile) {
                SettleDelay = (wait, _) => {
                    lock (settles) {
                        settles.Add(wait);
                    }
                    return Task.CompletedTask;
                },
            };
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline) {
                var info = ((ITelescopeMediator)svc).GetInfo();
                if (info.Connected && info.Coordinates is not null) {
                    return svc;
                }
                await Task.Delay(100);
            }
            Assert.Fail("mount never connected and reported its position");
            return svc;
        }

        [Test]
        public async Task A_slew_settles_for_the_profile_default_of_five_seconds_after_arrival() {
            await using var box = ScriptedAlpacaDevice.Start(Mount());
            var (profile, profileService) = ProfileWith(settleSeconds: null);
            using (profile) {
                var settles = new List<TimeSpan>();
                using var svc = await ConnectAsync(box, profileService, settles);

                Assert.That(await ((ITelescopeMediator)svc).SlewToCoordinatesAsync(Target(), CancellationToken.None), Is.True);
                Assert.That(settles, Is.EqualTo(new[] { TimeSpan.FromSeconds(5) }),
                    "the next frame must not start while the mount is still ringing from the goto");
            }
        }

        [Test]
        public async Task A_zero_settle_time_skips_the_wait() {
            await using var box = ScriptedAlpacaDevice.Start(Mount());
            var (profile, profileService) = ProfileWith(settleSeconds: 0);
            using (profile) {
                var settles = new List<TimeSpan>();
                using var svc = await ConnectAsync(box, profileService, settles);

                Assert.That(await ((ITelescopeMediator)svc).SlewToCoordinatesAsync(Target(), CancellationToken.None), Is.True);
                Assert.That(settles, Is.Empty);
            }
        }

        [Test]
        public async Task A_slew_that_fails_does_not_settle() {
            var (profile, profileService) = ProfileWith(settleSeconds: 5);
            using (profile) {
                var settles = new List<TimeSpan>();
                using var svc = new TelescopeService(profileService: profileService) {
                    SettleDelay = (wait, _) => { settles.Add(wait); return Task.CompletedTask; },
                };

                Assert.That(await ((ITelescopeMediator)svc).SlewToCoordinatesAsync(Target(), CancellationToken.None), Is.False);
                Assert.That(settles, Is.Empty, "a disconnected mount went nowhere; there is nothing to settle");
            }
        }

        [Test]
        public async Task The_flip_goto_leaves_the_settle_to_the_flip_executor() {
            await using var box = ScriptedAlpacaDevice.Start(Mount());
            var (profile, profileService) = ProfileWith(settleSeconds: 5);
            using (profile) {
                var settles = new List<TimeSpan>();
                using var svc = await ConnectAsync(box, profileService, settles);

                Assert.That(await ((ITelescopeMediator)svc).MeridianFlip(Target(), CancellationToken.None), Is.True);
                Assert.That(settles, Is.Empty,
                    "MeridianFlipExecutor settles after the flip with MeridianFlipSettings.SettleTime; a second wait would double it");
            }
        }
    }
}
