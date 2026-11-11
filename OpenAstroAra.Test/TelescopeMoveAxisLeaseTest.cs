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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1649 — a direction-pad press carries a lease; when the renewals stop (a sleeping iPad,
    /// dropped Wi-Fi) the daemon stops that axis by itself. A request without a lease behaves as before.</summary>
    [TestFixture]
    public class MoveAxisLeaseTest {

        private static readonly DateTimeOffset T0 = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
        private static readonly int[] Primary = [0];
        private static readonly int[] Secondary = [1];

        [Test]
        public void A_lease_is_clamped_to_250_to_5000_ms() {
            Assert.That(MoveAxisLease.Clamp(10), Is.EqualTo(250));
            Assert.That(MoveAxisLease.Clamp(1000), Is.EqualTo(1000));
            Assert.That(MoveAxisLease.Clamp(60_000), Is.EqualTo(5000));
        }

        [Test]
        public void An_unrenewed_axis_expires_after_its_lease_and_only_once() {
            var lease = new MoveAxisLease();
            lease.Renew(1, 1.0, 1000, T0);
            Assert.That(lease.TakeExpired(T0.AddMilliseconds(999)), Is.Empty);
            Assert.That(lease.TakeExpired(T0.AddMilliseconds(1000)), Is.EqualTo(Secondary));
            Assert.That(lease.TakeExpired(T0.AddSeconds(5)), Is.Empty, "an expired axis is stopped once");
            Assert.That(lease.Any, Is.False);
        }

        [Test]
        public void Renewals_keep_an_axis_moving() {
            var lease = new MoveAxisLease();
            lease.Renew(0, 1.0, 1000, T0);
            lease.Renew(0, 1.0, 1000, T0.AddMilliseconds(300));
            lease.Renew(0, 1.0, 1000, T0.AddMilliseconds(600));
            Assert.That(lease.TakeExpired(T0.AddMilliseconds(1500)), Is.Empty);
            Assert.That(lease.TakeExpired(T0.AddMilliseconds(1600)), Is.EqualTo(Primary));
        }

        [Test]
        public void Only_a_live_lease_at_the_same_rate_is_a_renewal() {
            var lease = new MoveAxisLease();
            Assert.That(lease.TryRenew(0, 1.0, 1000, T0), Is.False, "nothing to renew yet");
            lease.Renew(0, 1.0, 1000, T0);
            Assert.That(lease.TryRenew(0, 1.0, 1000, T0.AddMilliseconds(500)), Is.True);
            Assert.That(lease.TryRenew(0, 2.0, 1000, T0.AddMilliseconds(600)), Is.False, "another speed");
            Assert.That(lease.TryRenew(1, 1.0, 1000, T0.AddMilliseconds(600)), Is.False, "another axis");
            Assert.That(lease.TryRenew(0, 1.0, 1000, T0.AddMilliseconds(1600)), Is.False, "the lease ran out");
        }

        [Test]
        public void Each_axis_has_its_own_deadline_and_cancel_clears_it() {
            var lease = new MoveAxisLease();
            lease.Renew(0, 1.0, 1000, T0);
            lease.Renew(1, 1.0, 1000, T0);
            lease.Cancel(1);
            Assert.That(lease.TakeExpired(T0.AddSeconds(2)), Is.EqualTo(Primary));
            lease.Renew(0, 1.0, 1000, T0);
            lease.Renew(1, 1.0, 1000, T0);
            lease.CancelAll();
            Assert.That(lease.TakeExpired(T0.AddSeconds(2)), Is.Empty);
        }
    }

    /// <summary>#1649 — the lease end to end on the telescope service, against a scripted Alpaca mount
    /// and a hand-advanced clock (no sleeps for the lease itself).</summary>
    [TestFixture]
    [Category("IO")] // loopback scripted Alpaca device
    public class TelescopeMoveAxisLeaseTest {

        private sealed class ManualClock : TimeProvider {
            public DateTimeOffset Now { get; set; } = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
        }

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "mount-under-test", Name: "Bench mount", Type: DeviceType.Telescope,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private static Func<string, string?> Mount() => path =>
            path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/tracking", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/canmoveaxis", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/rightascension", StringComparison.Ordinal) ? "6.0"
            : path.EndsWith("/declination", StringComparison.Ordinal) ? "45.0"
            : null;

        private static async Task<(ScriptedAlpacaDevice Box, TelescopeService Svc, ManualClock Clock)> ArmedAsync(
                Func<string, string?>? putResponder = null) {
            var box = ScriptedAlpacaDevice.Start(Mount(), putResponder);
            box.RespondWithQuery((path, _) => path.EndsWith("/axisrates", StringComparison.Ordinal)
                ? "[{\"Minimum\":0.002,\"Maximum\":4.0}]" : null);
            var clock = new ManualClock();
            var svc = new TelescopeService { RefreshPeriod = TimeSpan.FromMilliseconds(100), LeaseClock = clock, LeaseCheckInterval = Timeout.InfiniteTimeSpan };
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            var sw = Stopwatch.StartNew();
            while ((await svc.GetAsync(CancellationToken.None))?.Capabilities?.MoveAxisRateBandsDegPerSec is not { Count: > 0 }) {
                if (sw.Elapsed > TimeSpan.FromSeconds(15)) {
                    Assert.Fail("capabilities never published");
                }
                await Task.Delay(50);
            }
            return (box, svc, clock);
        }

        private static string[] MoveAxisPuts(ScriptedAlpacaDevice box) =>
            [.. box.Puts.Where(p => p.Path.EndsWith("/moveaxis", StringComparison.Ordinal)).Select(p => p.Body)];

        private static bool IsStop(string body, int axis) =>
            body.Contains($"Axis={axis}", StringComparison.Ordinal) && body.Contains("Rate=0&", StringComparison.Ordinal)
            || body.Contains($"Axis={axis}", StringComparison.Ordinal) && body.EndsWith("Rate=0", StringComparison.Ordinal);

        [Test]
        public async Task A_leased_move_with_no_renewal_is_stopped_after_the_lease() {
            var (box, svc, clock) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;

            await svc.MoveAxisAsync(1, 1.0, 1000, CancellationToken.None);
            clock.Now = clock.Now.AddMilliseconds(999);
            svc.CheckLeases();
            Assert.That(MoveAxisPuts(box).Count(b => IsStop(b, 1)), Is.Zero, "still inside the lease");

            clock.Now = clock.Now.AddMilliseconds(2);
            svc.CheckLeases();
            Assert.That(MoveAxisPuts(box).Count(b => IsStop(b, 1)), Is.EqualTo(1), "the daemon stops the axis itself");
        }

        [Test]
        public async Task A_renewal_at_the_same_rate_only_extends_the_lease_and_never_reaches_the_driver() {
            // On the rig (EQMOD through AlpacaBridge, 2026-11-11) a MoveAxis repeated every 300 ms
            // came back "Motion superseded before dispatch" (500): the pad stopped after a second.
            var (box, svc, clock) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;

            await svc.MoveAxisAsync(0, 1.0, 1000, CancellationToken.None);
            clock.Now = clock.Now.AddMilliseconds(300);
            await svc.MoveAxisAsync(0, 1.0, 1000, CancellationToken.None);
            clock.Now = clock.Now.AddMilliseconds(300);
            await svc.MoveAxisAsync(0, 1.0, 1000, CancellationToken.None);
            Assert.That(MoveAxisPuts(box), Has.Length.EqualTo(1), "one move reaches the driver; renewals stay in the daemon");

            clock.Now = clock.Now.AddMilliseconds(900);
            svc.CheckLeases();
            Assert.That(MoveAxisPuts(box).Count(b => IsStop(b, 0)), Is.Zero, "the last renewal extended the lease");

            await svc.MoveAxisAsync(0, 2.0, 1000, CancellationToken.None);
            Assert.That(MoveAxisPuts(box), Has.Length.EqualTo(2), "a new speed is a new move");
        }

        [Test]
        public async Task A_move_the_driver_superseded_with_a_newer_one_is_not_a_failure() {
            // AlpacaBridge queues motion: a press let go at once has its start replaced by the stop
            // before it reaches the mount, and answers the start with this DriverException (rig,
            // 2026-11-11: every short press showed "Couldn't move the mount").
            var superseded = 1;
            var (box, svc, _) = await ArmedAsync(path => path.EndsWith("/moveaxis", StringComparison.Ordinal)
                && Interlocked.Exchange(ref superseded, 0) == 1
                ? ScriptedAlpacaDevice.Error("Motion superseded before dispatch") : null);
            await using var _ = box;
            using var __ = svc;

            await Assert.DoesNotThrowAsync(() => svc.MoveAxisAsync(0, 1.0, 1000, CancellationToken.None));
            Assert.That(svc.Lease.Any, Is.False, "the newer command decides; no lease for the replaced start");
            await svc.MoveAxisAsync(0, 1.0, 1000, CancellationToken.None);
            Assert.That(MoveAxisPuts(box), Has.Length.EqualTo(2), "the next press still reaches the driver");
        }

        [Test]
        public async Task A_request_without_a_lease_never_auto_stops_and_a_stop_cancels_the_lease() {
            var (box, svc, clock) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;

            await svc.MoveAxisAsync(0, 1.0, null, CancellationToken.None);
            clock.Now = clock.Now.AddMinutes(1);
            svc.CheckLeases();
            Assert.That(MoveAxisPuts(box).Count(b => IsStop(b, 0)), Is.Zero, "the Full UI's pad is unchanged");

            await svc.MoveAxisAsync(1, 1.0, 1000, CancellationToken.None);
            await svc.MoveAxisAsync(1, 0, null, CancellationToken.None);
            var stops = MoveAxisPuts(box).Count(b => IsStop(b, 1));
            clock.Now = clock.Now.AddSeconds(5);
            svc.CheckLeases();
            Assert.That(MoveAxisPuts(box).Count(b => IsStop(b, 1)), Is.EqualTo(stops), "a rate-0 release cancels the deadline");

            await svc.MoveAxisAsync(1, 1.0, 1000, CancellationToken.None);
            await svc.AbortSlewAsync(CancellationToken.None);
            stops = MoveAxisPuts(box).Count(b => IsStop(b, 1));
            clock.Now = clock.Now.AddSeconds(5);
            svc.CheckLeases();
            Assert.That(MoveAxisPuts(box).Count(b => IsStop(b, 1)), Is.EqualTo(stops), "AbortSlew cancels the deadline");
        }
    }
}
