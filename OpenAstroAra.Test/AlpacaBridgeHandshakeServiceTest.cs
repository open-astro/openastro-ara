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
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §68.1 handshake cache: a probed classification is reused within the 30s TTL, re-probed after
    /// it, and keyed per bridge (scheme/host/port).
    /// </summary>
    [TestFixture]
    public class AlpacaBridgeHandshakeServiceTest {

        private static readonly Uri BridgeA = new("http://127.0.0.1:11111/");
        private static readonly Uri BridgeB = new("http://127.0.0.1:22222/");

        [Test]
        public async Task First_call_probes_and_returns_the_classification() {
            var probe = new CountingProbe(new AlpacaBridgeHandshake(AlpacaBridgeStatus.OutdatedWarn, "1.3.0"));
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var svc = new AlpacaBridgeHandshakeService(probe, clock);

            var result = await svc.HandshakeAsync(BridgeA, CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(result.Status, Is.EqualTo(AlpacaBridgeStatus.OutdatedWarn));
                Assert.That(result.Version, Is.EqualTo("1.3.0"));
                Assert.That(probe.Calls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task Second_call_within_the_ttl_serves_the_cache_without_reprobing() {
            var probe = new CountingProbe(new AlpacaBridgeHandshake(AlpacaBridgeStatus.Ok, "1.6.0"));
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var svc = new AlpacaBridgeHandshakeService(probe, clock);

            await svc.HandshakeAsync(BridgeA, CancellationToken.None);
            clock.Advance(AlpacaBridgeHandshakeService.CacheTtl - TimeSpan.FromSeconds(1)); // still fresh.
            var second = await svc.HandshakeAsync(BridgeA, CancellationToken.None);

            Assert.That(second.Status, Is.EqualTo(AlpacaBridgeStatus.Ok));
            Assert.That(probe.Calls, Is.EqualTo(1), "a hit within the TTL must not re-probe the bridge");
        }

        [Test]
        public async Task Call_after_the_ttl_reprobes() {
            var probe = new CountingProbe(new AlpacaBridgeHandshake(AlpacaBridgeStatus.Ok, "1.6.0"));
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var svc = new AlpacaBridgeHandshakeService(probe, clock);

            await svc.HandshakeAsync(BridgeA, CancellationToken.None);
            clock.Advance(AlpacaBridgeHandshakeService.CacheTtl + TimeSpan.FromSeconds(1)); // expired.
            await svc.HandshakeAsync(BridgeA, CancellationToken.None);

            Assert.That(probe.Calls, Is.EqualTo(2), "an expired entry must re-probe");
        }

        [Test]
        public async Task Distinct_bridges_are_cached_independently() {
            var probe = new CountingProbe(new AlpacaBridgeHandshake(AlpacaBridgeStatus.Ok, "1.6.0"));
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var svc = new AlpacaBridgeHandshakeService(probe, clock);

            await svc.HandshakeAsync(BridgeA, CancellationToken.None);
            await svc.HandshakeAsync(BridgeB, CancellationToken.None);

            Assert.That(probe.Calls, Is.EqualTo(2), "different bridges share no cache entry");
        }

        [Test]
        public async Task Device_uris_on_the_same_bridge_share_one_cache_entry() {
            var probe = new CountingProbe(new AlpacaBridgeHandshake(AlpacaBridgeStatus.Ok, "1.6.0"));
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var svc = new AlpacaBridgeHandshakeService(probe, clock);

            await svc.HandshakeAsync(new Uri("http://127.0.0.1:11111/api/v1/camera/0"), CancellationToken.None);
            await svc.HandshakeAsync(new Uri("http://127.0.0.1:11111/api/v1/telescope/0"), CancellationToken.None);

            Assert.That(probe.Calls, Is.EqualTo(1), "two devices on one bridge (differing only by path) must share the cache");
        }

        // Returns the supplied results in order (the last repeats once exhausted), counting calls via
        // Interlocked so the counter is safe even if a future concurrent test exercises it.
        [Test]
        public async Task Missing_is_not_cached_so_a_recovered_bridge_is_seen_immediately() {
            // First probe: the bridge is unreachable; second probe: it's back.
            var probe = new CountingProbe(
                new AlpacaBridgeHandshake(AlpacaBridgeStatus.Missing, null),
                new AlpacaBridgeHandshake(AlpacaBridgeStatus.Ok, "1.6.0"));
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var svc = new AlpacaBridgeHandshakeService(probe, clock);

            var first = await svc.HandshakeAsync(BridgeA, CancellationToken.None);  // Missing — not cached.
            var second = await svc.HandshakeAsync(BridgeA, CancellationToken.None); // re-probes within the TTL.

            Assert.Multiple(() => {
                Assert.That(first.Status, Is.EqualTo(AlpacaBridgeStatus.Missing));
                Assert.That(second.Status, Is.EqualTo(AlpacaBridgeStatus.Ok),
                    "a Missing result must not be cached, so a recovered bridge is picked up on the next call");
                Assert.That(probe.Calls, Is.EqualTo(2));
            });
        }

        [Test]
        public void HandshakeAsync_rejects_a_null_uri() {
            var svc = new AlpacaBridgeHandshakeService(
                new CountingProbe(new AlpacaBridgeHandshake(AlpacaBridgeStatus.Ok, "1.6.0")),
                new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

            Assert.That(async () => await svc.HandshakeAsync(null!, CancellationToken.None),
                Throws.ArgumentNullException);
        }

        // Returns the supplied results in order (the last repeats once exhausted), counting calls via
        // Interlocked so the counter is safe even if a future concurrent test exercises it.
        private sealed class CountingProbe : IAlpacaBridgeVersionProbe {
            private readonly AlpacaBridgeHandshake[] _results;
            private int _calls;

            public CountingProbe(params AlpacaBridgeHandshake[] results) => _results = results;

            public int Calls => Volatile.Read(ref _calls);

            public Task<AlpacaBridgeHandshake> ProbeAsync(Uri bridgeBaseUri, CancellationToken ct) {
                ct.ThrowIfCancellationRequested();
                var index = Interlocked.Increment(ref _calls) - 1;
                return Task.FromResult(_results[Math.Min(index, _results.Length - 1)]);
            }
        }

        private sealed class FakeTimeProvider : TimeProvider {
            private DateTimeOffset _now;
            public FakeTimeProvider(DateTimeOffset start) => _now = start;
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }
    }
}
