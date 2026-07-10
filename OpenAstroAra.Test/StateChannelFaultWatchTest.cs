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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§42.2 slice 4a — the state-channel fault watches: a mount silently dropping
    /// tracking and a camera cooler failing to hold set-point. Pure-class coverage for every
    /// decision, plus one end-to-end proof through a real TelescopeService.</summary>
    [TestFixture]
    public class StateChannelFaultWatchTest {

        // ── MountTrackingWatch (pure) ──

        [Test]
        public void An_armed_watch_fires_after_the_drop_streak_and_only_once() {
            var w = new MountTrackingWatch();
            Assert.That(w.Observe(tracking: true, slewing: false, parked: false), Is.EqualTo(TrackingWatchVerdict.Idle), "arms");
            Assert.That(w.Observe(false, false, false), Is.EqualTo(TrackingWatchVerdict.Degraded));
            Assert.That(w.Observe(false, false, false), Is.EqualTo(TrackingWatchVerdict.Degraded));
            Assert.That(w.Observe(false, false, false), Is.EqualTo(TrackingWatchVerdict.Lost));
            Assert.That(w.Observe(false, false, false), Is.EqualTo(TrackingWatchVerdict.Idle), "latched — no re-fire while still off");
        }

        [Test]
        public void A_watch_that_never_saw_tracking_never_accuses() {
            var w = new MountTrackingWatch();
            for (var i = 0; i < 10; i++) {
                Assert.That(w.Observe(tracking: false, slewing: false, parked: false), Is.EqualTo(TrackingWatchVerdict.Idle),
                    "a mount that was never tracking (e.g. connected parked-off) has nothing to lose");
            }
        }

        [Test]
        public void Flapping_reads_never_accumulate() {
            var w = new MountTrackingWatch();
            w.Observe(true, false, false);
            for (var i = 0; i < 5; i++) {
                Assert.That(w.Observe(false, false, false), Is.EqualTo(TrackingWatchVerdict.Degraded));
                Assert.That(w.Observe(false, false, false), Is.EqualTo(TrackingWatchVerdict.Degraded));
                Assert.That(w.Observe(true, false, false), Is.EqualTo(TrackingWatchVerdict.Idle), "a good read clears the streak");
            }
        }

        [Test]
        public void A_slewing_or_parked_mount_is_never_a_drop() {
            var w = new MountTrackingWatch();
            w.Observe(true, false, false); // armed
            for (var i = 0; i < 5; i++) {
                Assert.That(w.Observe(tracking: false, slewing: true, parked: false), Is.EqualTo(TrackingWatchVerdict.Idle));
            }
            // Parked also drops the expectation: tracking-off after a park is the normal end state.
            Assert.That(w.Observe(tracking: false, slewing: false, parked: true), Is.EqualTo(TrackingWatchVerdict.Idle));
            for (var i = 0; i < 5; i++) {
                Assert.That(w.Observe(false, false, false), Is.EqualTo(TrackingWatchVerdict.Idle),
                    "unparked with tracking off — expectation was cleared by the observed park");
            }
        }

        [Test]
        public void The_command_grace_window_suppresses_and_syncs_expectations() {
            var w = new MountTrackingWatch();
            w.Observe(true, false, false); // armed
            w.NoteMotionCommanded();
            for (var i = 0; i < MountTrackingWatch.DefaultCommandGraceTicks; i++) {
                Assert.That(w.Observe(false, false, false), Is.EqualTo(TrackingWatchVerdict.Suppressed),
                    "post-command reads inform, never accuse");
            }
            // Grace over, tracking still expected (nothing during grace said otherwise) — now it counts.
            Assert.That(w.Observe(false, false, false), Is.EqualTo(TrackingWatchVerdict.Degraded));
        }

        [Test]
        public void A_park_command_makes_tracking_off_expected() {
            var w = new MountTrackingWatch();
            w.Observe(true, false, false); // armed
            w.NoteParkCommanded();
            // Moving to park (grace), then parked, then later unparked-idle: tracking stays
            // off the whole way and must never fire.
            for (var i = 0; i < 20; i++) {
                var parked = i is >= 5 and < 10;
                Assert.That(w.Observe(tracking: false, slewing: false, parked: parked), Is.Not.EqualTo(TrackingWatchVerdict.Lost));
            }
        }

        [Test]
        public void A_deliberate_tracking_off_never_fires_and_reenabling_arms_fresh() {
            var w = new MountTrackingWatch();
            w.Observe(true, false, false);
            w.NoteTrackingCommanded(false);
            for (var i = 0; i < 10; i++) {
                Assert.That(w.Observe(false, false, false), Is.Not.EqualTo(TrackingWatchVerdict.Lost));
            }
            w.NoteTrackingCommanded(true);
            for (var i = 0; i < MountTrackingWatch.DefaultCommandGraceTicks; i++) {
                w.Observe(false, false, false); // grace
            }
            w.Observe(true, false, false); // the enable landed
            Assert.That(w.Observe(false, false, false), Is.EqualTo(TrackingWatchVerdict.Degraded), "armed again");
        }

        [Test]
        public void A_rejected_reenable_does_not_refire_but_an_observed_recovery_rearms() {
            var w = new MountTrackingWatch();
            w.Observe(true, false, false);
            w.Observe(false, false, false);
            w.Observe(false, false, false);
            Assert.That(w.Observe(false, false, false), Is.EqualTo(TrackingWatchVerdict.Lost));

            // §42.3 reaction re-enables; the mount rejects it (tracking stays off).
            w.NoteTrackingCommanded(true);
            for (var i = 0; i < MountTrackingWatch.DefaultCommandGraceTicks + 5; i++) {
                Assert.That(w.Observe(false, false, false), Is.Not.EqualTo(TrackingWatchVerdict.Lost),
                    "a rejected re-enable must not re-fire and loop the reaction service");
            }

            // The user fixes it → observed recovery re-arms for a genuine new episode.
            Assert.That(w.Observe(true, false, false), Is.EqualTo(TrackingWatchVerdict.Recovered));
            w.Observe(false, false, false);
            w.Observe(false, false, false);
            Assert.That(w.Observe(false, false, false), Is.EqualTo(TrackingWatchVerdict.Lost), "fresh episode fires");
        }

        [Test]
        public void Reset_clears_expectations_and_episodes() {
            var w = new MountTrackingWatch();
            w.Observe(true, false, false);
            w.Observe(false, false, false);
            w.Reset();
            for (var i = 0; i < 10; i++) {
                Assert.That(w.Observe(false, false, false), Is.EqualTo(TrackingWatchVerdict.Idle),
                    "no expectations carry across sessions");
            }
        }

        // ── CoolingDriftMonitor (pure) ──

        private static readonly DateTimeOffset T0 = new(2026, 7, 10, 4, 0, 0, TimeSpan.Zero);

        [Test]
        public void Sustained_drift_fires_once_after_the_window() {
            var m = new CoolingDriftMonitor();
            Assert.That(m.Observe(true, -10, -2, T0), Is.EqualTo(CoolingDriftVerdict.Drifting), "8°C off — accumulating");
            Assert.That(m.Observe(true, -10, -2, T0 + TimeSpan.FromMinutes(3)), Is.EqualTo(CoolingDriftVerdict.Drifting));
            Assert.That(m.Observe(true, -10, -2, T0 + TimeSpan.FromMinutes(5)), Is.EqualTo(CoolingDriftVerdict.Drifted));
            Assert.That(m.Observe(true, -10, -2, T0 + TimeSpan.FromMinutes(9)), Is.EqualTo(CoolingDriftVerdict.Idle), "latched");
        }

        [Test]
        public void The_window_is_wall_clock_so_download_gaps_count() {
            var m = new CoolingDriftMonitor();
            m.Observe(true, -10, -2, T0);
            // Only ONE more observation, 6 minutes later (a long download starved the ticks).
            Assert.That(m.Observe(true, -10, -2, T0 + TimeSpan.FromMinutes(6)), Is.EqualTo(CoolingDriftVerdict.Drifted));
        }

        [Test]
        public void A_brief_excursion_that_returns_in_band_never_fires() {
            var m = new CoolingDriftMonitor();
            m.Observe(true, -10, -2, T0);
            Assert.That(m.Observe(true, -10, -9, T0 + TimeSpan.FromMinutes(2)), Is.EqualTo(CoolingDriftVerdict.Idle), "back in band clears");
            Assert.That(m.Observe(true, -10, -2, T0 + TimeSpan.FromMinutes(3)), Is.EqualTo(CoolingDriftVerdict.Drifting), "window restarts");
            Assert.That(m.Observe(true, -10, -2, T0 + TimeSpan.FromMinutes(7)), Is.EqualTo(CoolingDriftVerdict.Drifting), "4 min into the NEW window");
        }

        [Test]
        public void Disarmed_states_stop_accumulation_but_keep_a_fired_episode_latched() {
            var m = new CoolingDriftMonitor();
            m.Observe(true, -10, -2, T0);
            // Cooler reads off for a tick at minute 4 — the accumulation clears...
            Assert.That(m.Observe(false, -10, -2, T0 + TimeSpan.FromMinutes(4)), Is.EqualTo(CoolingDriftVerdict.Idle));
            Assert.That(m.Observe(true, -10, -2, T0 + TimeSpan.FromMinutes(5)), Is.EqualTo(CoolingDriftVerdict.Drifting),
                "...so the window restarted rather than firing at minute 5");

            // Fire an episode, then flap the temperature sensor: the episode must stay latched.
            var fired = new CoolingDriftMonitor();
            fired.Observe(true, -10, -2, T0);
            fired.Observe(true, -10, -2, T0 + TimeSpan.FromMinutes(5));
            Assert.That(fired.Observe(true, -10, null, T0 + TimeSpan.FromMinutes(6)), Is.EqualTo(CoolingDriftVerdict.Idle));
            Assert.That(fired.Observe(true, -10, -2, T0 + TimeSpan.FromMinutes(12)), Is.EqualTo(CoolingDriftVerdict.Idle),
                "a flapping sensor must not clear-and-refire the episode");
        }

        [Test]
        public void Recovery_needs_the_hysteresis_band_and_rearms() {
            var m = new CoolingDriftMonitor();
            m.Observe(true, -10, -2, T0);
            Assert.That(m.Observe(true, -10, -2, T0 + TimeSpan.FromMinutes(5)), Is.EqualTo(CoolingDriftVerdict.Drifted));
            Assert.That(m.Observe(true, -10, -5.5, T0 + TimeSpan.FromMinutes(6)), Is.EqualTo(CoolingDriftVerdict.Idle),
                "4.5°C off is inside threshold but outside the clear band — still latched");
            Assert.That(m.Observe(true, -10, -6.5, T0 + TimeSpan.FromMinutes(7)), Is.EqualTo(CoolingDriftVerdict.Recovered),
                "3.5°C off clears");
            m.Observe(true, -10, -2, T0 + TimeSpan.FromMinutes(8));
            Assert.That(m.Observe(true, -10, -2, T0 + TimeSpan.FromMinutes(13)), Is.EqualTo(CoolingDriftVerdict.Drifted),
                "fresh episode fires");
        }

        [Test]
        public void Reset_clears_the_latch_and_the_accumulation() {
            var m = new CoolingDriftMonitor();
            m.Observe(true, -10, -2, T0);
            m.Observe(true, -10, -2, T0 + TimeSpan.FromMinutes(5)); // fired
            m.Reset(); // commanded cooler change
            Assert.That(m.Observe(true, -10, -2, T0 + TimeSpan.FromMinutes(6)), Is.EqualTo(CoolingDriftVerdict.Drifting),
                "fresh baseline — accumulating toward a new window, not latched");
        }

        // ── End-to-end: a real TelescopeService against a scripted mount ──

        private static (EquipmentFaultHub Hub, List<EquipmentFaultEvent> Faults) Hub() {
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var hub = new EquipmentFaultHub(ws.Object);
            var faults = new List<EquipmentFaultEvent>();
            hub.Subscribe(f => { lock (faults) { faults.Add(f); } });
            return (hub, faults);
        }

        [Test]
        [Category("bench")] // §42.2 virtual-observatory bench — loopback-only, runs in the default job too
        public async Task A_mount_that_silently_drops_tracking_publishes_one_tracking_lost_fault() {
            var trackingValue = "true";
            await using var mount = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/tracking", StringComparison.Ordinal) ? Volatile.Read(ref trackingValue)
                : path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
                : null);
            var (hub, faults) = Hub();

            using var svc = new TelescopeService(faults: hub);
            var device = new DiscoveredDeviceDto(
                UniqueId: "mount-under-test", Name: "Bench Mount", Type: DeviceType.Telescope,
                HostName: mount.BaseUri.Host, IpAddress: mount.BaseUri.Host, IpPort: mount.BaseUri.Port,
                AlpacaDeviceNumber: 0, UseHttps: false);
            await svc.ConnectAsync(new ConnectRequestDto(device), idempotencyKey: null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None))?.State == EquipmentConnectionState.Connected,
                TimeSpan.FromSeconds(15), "mount never connected");
            // Let at least one refresh observe tracking=on so the watch arms.
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None))?.Runtime.Tracking == true,
                TimeSpan.FromSeconds(10), "tracking never observed on");

            // The mount silently drops tracking — no daemon command.
            Volatile.Write(ref trackingValue, "false");
            await WaitForAsync(() => { lock (faults) { return Task.FromResult(faults.Count > 0); } },
                TimeSpan.FromSeconds(20), "the tracking-lost fault never published");

            // Several more ticks with tracking still off: the episode must not re-fire.
            await Task.Delay(TimeSpan.FromSeconds(5));
            lock (faults) {
                Assert.That(faults, Has.Count.EqualTo(1), "exactly one fault per episode");
                Assert.That(faults[0].Kind, Is.EqualTo(EquipmentFaultKind.TrackingLost));
                Assert.That(faults[0].DeviceType, Is.EqualTo(DeviceType.Telescope));
                Assert.That(faults[0].DeviceName, Is.EqualTo("Bench Mount"));
            }
        }

        private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout, string failure) {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                if (await condition()) {
                    return;
                }
                await Task.Delay(200);
            }
            Assert.Fail(failure);
        }
    }
}
