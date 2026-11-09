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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1732 — an iOptron reports system status 3 (guiding) during a pulse, so the bridge reads
    /// Tracking false for the length of it. Eleven false TrackingLost faults on 2026-11-07.</summary>
    [TestFixture]
    [Category("IO")] // loopback scripted Alpaca device
    public class TelescopeTrackingWhilePulseGuidingTest {

        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "mount-under-test", Name: "Bench mount", Type: DeviceType.Telescope,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private static Func<string, string?> Mount(Func<string> tracking, Func<string> pulseGuiding) => path =>
            path.EndsWith("/connected", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/tracking", StringComparison.Ordinal) ? tracking()
            : path.EndsWith("/ispulseguiding", StringComparison.Ordinal) ? pulseGuiding()
            : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/rightascension", StringComparison.Ordinal) ? "6.0"
            : path.EndsWith("/declination", StringComparison.Ordinal) ? "6.0"
            : null;

        private static int Reads(ScriptedAlpacaDevice box, string field) =>
            box.Gets.Count(p => p.EndsWith("/" + field, StringComparison.Ordinal));

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

        private static async Task<(TelescopeService Svc, List<EquipmentFaultEvent> Faults)> ArmedAsync(ScriptedAlpacaDevice box) {
            var hub = new EquipmentFaultHub(Mock.Of<IWsBroadcaster>());
            var faults = new List<EquipmentFaultEvent>();
            hub.Subscribe(f => { lock (faults) { faults.Add(f); } });
            var svc = new TelescopeService(faults: hub) { RefreshPeriod = Tick };
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.Runtime.Tracking == true, "tracking never read on");
            return (svc, faults);
        }

        private static bool Lost(List<EquipmentFaultEvent> faults) {
            lock (faults) {
                return faults.Any(f => f.Kind == EquipmentFaultKind.TrackingLost);
            }
        }

        [Test]
        public async Task Tracking_reading_off_during_a_guide_pulse_is_not_a_fault() {
            var tracking = "true";
            var pulsing = "false";
            await using var box = ScriptedAlpacaDevice.Start(Mount(() => Volatile.Read(ref tracking), () => Volatile.Read(ref pulsing)));
            var (svc, faults) = await ArmedAsync(box);
            using var _ = svc;

            Volatile.Write(ref pulsing, "true");
            Volatile.Write(ref tracking, "false");
            var since = Reads(box, "tracking");
            await WaitForAsync(() => Reads(box, "tracking") >= since + MountTrackingWatch.DefaultDropThreshold + 3, "the refresh stopped");

            Assert.That(Lost(faults), Is.False, "a guide pulse explains tracking reading off");
        }

        [Test]
        public async Task Tracking_off_with_no_pulse_is_still_lost() {
            var tracking = "true";
            await using var box = ScriptedAlpacaDevice.Start(Mount(() => Volatile.Read(ref tracking), () => "false"));
            var (svc, faults) = await ArmedAsync(box);
            using var _ = svc;

            Volatile.Write(ref tracking, "false");

            await WaitForAsync(() => Lost(faults), "a real drop must still fire");
        }

        [Test]
        public async Task A_mount_that_cannot_report_pulse_guiding_still_gets_the_watch() {
            var tracking = "true";
            await using var box = ScriptedAlpacaDevice.Start(Mount(() => Volatile.Read(ref tracking),
                () => ScriptedAlpacaDevice.Error("Property IsPulseGuiding is not implemented")));
            var (svc, faults) = await ArmedAsync(box);
            using var _ = svc;

            Volatile.Write(ref tracking, "false");
            var since = Reads(box, "tracking");

            await WaitForAsync(() => Lost(faults), "an unsupported read counts as no pulse");
            Assert.That(Reads(box, "tracking") - since, Is.LessThan(MountTrackingWatch.MaxPulseHoldTicks), "it fires at the normal streak, not after the pulse hold");
        }

        [Test]
        public async Task A_parked_mount_is_not_asked_about_pulses() {
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/connected", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/tracking", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/ispulseguiding", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/atpark", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/rightascension", StringComparison.Ordinal) ? "6.0"
                : path.EndsWith("/declination", StringComparison.Ordinal) ? "6.0"
                : null);
            var hub = new EquipmentFaultHub(Mock.Of<IWsBroadcaster>());
            using var svc = new TelescopeService(faults: hub) { RefreshPeriod = Tick };
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            await WaitForAsync(() => Reads(box, "tracking") >= 5, "the refresh never ran");

            Assert.That(Reads(box, "ispulseguiding"), Is.Zero);
        }

        [Test]
        public void A_pulse_tick_holds_the_streak_instead_of_clearing_it() {
            var watch = new MountTrackingWatch(threshold: 3, graceTicks: 0);
            watch.Observe(tracking: true, slewing: false, parked: false);

            Assert.That(watch.Observe(false, false, false), Is.EqualTo(TrackingWatchVerdict.Degraded));
            Assert.That(watch.Observe(false, false, false, pulseGuiding: true), Is.Not.EqualTo(TrackingWatchVerdict.Lost));
            Assert.That(watch.Observe(false, false, false), Is.EqualTo(TrackingWatchVerdict.Degraded));
            Assert.That(watch.Observe(false, false, false), Is.EqualTo(TrackingWatchVerdict.Lost), "a real drop under pulsing still adds up");
        }

        [Test]
        public void A_pulse_flag_that_never_clears_cannot_hide_a_drop_for_good() {
            var watch = new MountTrackingWatch(threshold: 3, graceTicks: 0);
            watch.Observe(tracking: true, slewing: false, parked: false);

            var verdicts = Enumerable.Range(0, MountTrackingWatch.MaxPulseHoldTicks + MountTrackingWatch.DefaultDropThreshold)
                .Select(_ => watch.Observe(false, false, false, pulseGuiding: true)).ToList();

            Assert.That(verdicts[^1], Is.EqualTo(TrackingWatchVerdict.Lost));
            Assert.That(verdicts.Take(MountTrackingWatch.MaxPulseHoldTicks), Has.None.EqualTo(TrackingWatchVerdict.Lost));
        }
    }
}
