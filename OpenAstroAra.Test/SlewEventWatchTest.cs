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
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§57.8 — the slew lifecycle watch (pure) + the observed-transition WS events
    /// end-to-end against a scripted mount: started carries the commanded target, complete
    /// carries the duration, and a §57.4 abort publishes its own event while suppressing the
    /// episode's complete.</summary>
    [TestFixture]
    public class SlewEventWatchTest {

        // ── SlewEventWatch (pure) ──

        [Test]
        public void A_slew_start_carries_the_noted_target_and_consumes_it() {
            var w = new SlewEventWatch();
            w.NoteSlewTarget(5.5, 20.0);
            var v = w.Observe(slewing: true);
            Assert.That(v.Kind, Is.EqualTo(SlewEventWatch.Kind.Started));
            Assert.That(v.TargetRaHours, Is.EqualTo(5.5));
            Assert.That(v.TargetDecDegrees, Is.EqualTo(20.0));

            Assert.That(w.Observe(true).Kind, Is.EqualTo(SlewEventWatch.Kind.None), "steady slewing is not a new episode");
            Assert.That(w.Observe(false).Kind, Is.EqualTo(SlewEventWatch.Kind.Completed));
            var next = w.Observe(true);
            Assert.That(next.TargetRaHours, Is.Null, "the target was consumed by the first episode");
        }

        [Test]
        public void A_park_style_slew_has_no_target_but_still_opens_an_episode() {
            var w = new SlewEventWatch();
            var v = w.Observe(slewing: true);
            Assert.That(v.Kind, Is.EqualTo(SlewEventWatch.Kind.Started));
            Assert.That(v.TargetRaHours, Is.Null);
            Assert.That(v.TargetDecDegrees, Is.Null);
        }

        [Test]
        public void Completion_measures_the_episode_duration_monotonically() {
            var now = 100_000L;
            var w = new SlewEventWatch { TickMs = () => now };
            w.Observe(true);
            now += 23_500;
            var v = w.Observe(false);
            Assert.That(v.Kind, Is.EqualTo(SlewEventWatch.Kind.Completed));
            Assert.That(v.DurationSeconds, Is.EqualTo(23.5));
        }

        [Test]
        public void An_abort_suppresses_the_episodes_completed_verdict() {
            var w = new SlewEventWatch();
            w.Observe(true);
            Assert.That(w.NoteAborted(), Is.True, "an open episode accepts the abort");
            Assert.That(w.Observe(false).Kind, Is.EqualTo(SlewEventWatch.Kind.None),
                "the abort path already published slew_aborted — no complete on top");
            w.Observe(true);
            Assert.That(w.Observe(false).Kind, Is.EqualTo(SlewEventWatch.Kind.Completed),
                "the abort verdict does not leak into the next episode");
        }

        [Test]
        public void An_idle_abort_is_refused_and_cannot_poison_a_later_park_style_slew() {
            // #836 r1 — a defensive Stop press with nothing slewing must not latch a flag that
            // would swallow the NEXT slew's complete; park/home/flip slews never call
            // NoteSlewTarget, so nothing else would ever clear it.
            var w = new SlewEventWatch();
            Assert.That(w.NoteAborted(), Is.False, "no episode → nothing to abort, nothing published");
            Assert.That(w.Observe(true).Kind, Is.EqualTo(SlewEventWatch.Kind.Started));
            Assert.That(w.Observe(false).Kind, Is.EqualTo(SlewEventWatch.Kind.Completed),
                "the park-style episode closes with a complete — no stale suppression");
        }

        [Test]
        public void A_retarget_racing_an_in_flight_abort_does_not_unsuppress_the_episode() {
            // #836 r4 — a new slew commanded while the abort's device call is still in flight
            // (WILMA re-targeting right after Stop) must not clear the abort: the aborted
            // episode already published slew_aborted, so its Completed stays suppressed.
            var w = new SlewEventWatch();
            w.Observe(true);
            Assert.That(w.NoteAborted(), Is.True);
            w.NoteSlewTarget(1.0, 2.0); // the racing re-target
            Assert.That(w.Observe(false).Kind, Is.EqualTo(SlewEventWatch.Kind.None),
                "the episode must not read as both aborted and completed");
            var next = w.Observe(true);
            Assert.That(next.TargetRaHours, Is.EqualTo(1.0), "the re-target rides ITS OWN episode");
        }

        [Test]
        public void A_rapid_double_abort_publishes_the_aborted_event_once() {
            // #836 r3 — the second Stop press on the same episode is a no-op for events (the
            // device call still re-fires; it's idempotent on the mount side).
            var w = new SlewEventWatch();
            w.Observe(true);
            Assert.That(w.NoteAborted(), Is.True, "first press publishes");
            Assert.That(w.NoteAborted(), Is.False, "second press on the same episode does not");
            Assert.That(w.Observe(false).Kind, Is.EqualTo(SlewEventWatch.Kind.None));
        }

        [Test]
        public void A_failed_dispatch_clears_its_target_so_a_later_park_slew_reports_none() {
            // #836 r2 — the command's dispatch failed (no episode ever opened); its coordinates
            // must not masquerade as the target of the next, unrelated slew.
            var w = new SlewEventWatch();
            w.NoteSlewTarget(5.5, 20.0);
            w.ClearPendingTarget(); // the dispatch-failure path
            var v = w.Observe(true);
            Assert.That(v.Kind, Is.EqualTo(SlewEventWatch.Kind.Started));
            Assert.That(v.TargetRaHours, Is.Null);
            Assert.That(v.TargetDecDegrees, Is.Null);
        }

        [Test]
        public void A_failed_abort_device_call_unwinds_the_note_so_the_slew_still_completes() {
            // #836 r2 — NoteAborted now lands BEFORE the device call (so the abort beats a racing
            // poll tick); if that call then throws, the slew is still running and its eventual
            // complete must not be suppressed.
            var w = new SlewEventWatch();
            w.Observe(true);
            Assert.That(w.NoteAborted(), Is.True);
            w.ClearAbortNote(); // the device-call-failed path
            Assert.That(w.Observe(false).Kind, Is.EqualTo(SlewEventWatch.Kind.Completed));
        }

        [Test]
        public void An_unconsumed_target_expires_instead_of_riding_a_much_later_episode() {
            // #836 r5 — a fast REST slew that settles entirely between poll ticks never opens an
            // episode; its target must age out rather than label a later park/home slew.
            var now = 100_000L;
            var w = new SlewEventWatch { TickMs = () => now };
            w.NoteSlewTarget(5.5, 20.0);
            now += (long)SlewEventWatch.PendingTargetTtl.TotalMilliseconds + 1;
            var v = w.Observe(true);
            Assert.That(v.Kind, Is.EqualTo(SlewEventWatch.Kind.Started));
            Assert.That(v.TargetRaHours, Is.Null, "the stale target aged out");
        }

        [Test]
        public void A_target_observed_within_the_ttl_still_rides_its_episode() {
            var now = 100_000L;
            var w = new SlewEventWatch { TickMs = () => now };
            w.NoteSlewTarget(5.5, 20.0);
            now += 2_000; // one normal poll tick later
            var v = w.Observe(true);
            Assert.That(v.TargetRaHours, Is.EqualTo(5.5));
        }

        [Test]
        public void Reset_clears_the_episode_and_the_pending_target() {
            var w = new SlewEventWatch();
            w.NoteSlewTarget(5.5, 20.0);
            w.Observe(true);
            w.Reset();
            Assert.That(w.Observe(false).Kind, Is.EqualTo(SlewEventWatch.Kind.None),
                "no episode survives a reconnect");
            var v = w.Observe(true);
            Assert.That(v.TargetRaHours, Is.Null, "no pending target survives a reconnect");
        }

        // ── End-to-end against a scripted mount ──

        private sealed class PayloadRecordingBroadcaster : IWsBroadcaster {
            private readonly List<(string Type, string Payload)> _events = new();
            public long CurrentSequence => 0;
            public (string Type, string Payload)[] Snapshot() { lock (_events) { return _events.ToArray(); } }
            public Task PublishAsync(string eventType, System.Text.Json.JsonElement payload, CancellationToken ct) {
                lock (_events) { _events.Add((eventType, payload.GetRawText())); }
                return Task.CompletedTask;
            }
        }

        [Test]
        [Category("bench")] // loopback-only, runs in the default job too
        public async Task Observed_slew_transitions_publish_started_then_aborted_suppresses_complete() {
            var slewingValue = "false";
            await using var mount = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/slewing", StringComparison.Ordinal) ? Volatile.Read(ref slewingValue)
                : path.EndsWith("/tracking", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/rightascension", StringComparison.Ordinal) ? "5.5"
                : path.EndsWith("/declination", StringComparison.Ordinal) ? "20.0"
                : null);
            var ws = new PayloadRecordingBroadcaster();

            using var svc = new TelescopeService(ws: ws);
            var device = new DiscoveredDeviceDto(
                UniqueId: "mount-under-test", Name: "Bench Mount", Type: DeviceType.Telescope,
                HostName: mount.BaseUri.Host, IpAddress: mount.BaseUri.Host, IpPort: mount.BaseUri.Port,
                AlpacaDeviceNumber: 0, UseHttps: false);
            await svc.ConnectAsync(new ConnectRequestDto(device), idempotencyKey: null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None))?.State == EquipmentConnectionState.Connected,
                TimeSpan.FromSeconds(15), "mount never connected");

            // The mount starts slewing (any source — the watch observes, it doesn't instrument).
            Volatile.Write(ref slewingValue, "true");
            await WaitForAsync(() => Task.FromResult(Array.Exists(ws.Snapshot(),
                    e => e.Type == "telescope.slew_started")),
                TimeSpan.FromSeconds(15), "slew_started never published");

            // §57.4 panic stop mid-slew: the aborted event fires immediately…
            await svc.AbortSlewAsync(CancellationToken.None);
            Volatile.Write(ref slewingValue, "false");
            await WaitForAsync(() => Task.FromResult(Array.Exists(ws.Snapshot(),
                    e => e.Type == "telescope.slew_aborted")),
                TimeSpan.FromSeconds(10), "slew_aborted never published");

            // …and the episode's would-be complete is suppressed (give the poll a few ticks).
            await Task.Delay(TimeSpan.FromSeconds(5));
            var events = ws.Snapshot();
            Assert.That(Array.Exists(events, e => e.Type == "telescope.slew_complete"), Is.False,
                "an aborted episode must not also read as completed");
            var aborted = Array.Find(events, e => e.Type == "telescope.slew_aborted");
            Assert.That(aborted.Payload, Does.Contain("\"reason\":\"user_request\""));
            Assert.That(aborted.Payload, Does.Contain("halted_ra_hours"));
        }

        private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout, string failure) {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                if (await condition().ConfigureAwait(false)) {
                    return;
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
            Assert.Fail(failure);
        }
    }
}
