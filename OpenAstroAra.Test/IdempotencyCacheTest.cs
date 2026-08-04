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

namespace OpenAstroAra.Test;

[TestFixture]
public sealed class IdempotencyCacheTest {
    private sealed record ResultBox(int Value);

    [Test]
    public async Task Concurrent_same_key_and_fingerprint_executes_factory_once() {
        var cache = new IdempotencyCache<ResultBox>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<ResultBox> Factory() {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return new ResultBox(42);
        }

        var first = cache.GetOrRunAsync(" key ", "same-request", Factory);
        await entered.Task.ConfigureAwait(false);
        var second = cache.GetOrRunAsync("key", "same-request", Factory);
        release.TrySetResult();

        var results = await Task.WhenAll(first, second).ConfigureAwait(false);
        Assert.Multiple(() => {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(results[0], Is.SameAs(results[1]));
            Assert.That(results[0].Value, Is.EqualTo(42));
        });
    }

    [Test]
    public async Task Same_key_with_different_fingerprint_is_conflict() {
        var cache = new IdempotencyCache<ResultBox>();
        await cache.GetOrRunAsync("key", "request-a",
            () => Task.FromResult(new ResultBox(1))).ConfigureAwait(false);

        Assert.ThrowsAsync<IdempotencyKeyConflictException>(() =>
            cache.GetOrRunAsync("key", "request-b",
                () => Task.FromResult(new ResultBox(2))));
    }

    [Test]
    public async Task Faulted_flight_is_evicted_and_retry_runs() {
        var cache = new IdempotencyCache<ResultBox>();
        var calls = 0;
        Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrRunAsync("key", "request", () => {
                calls++;
                throw new InvalidOperationException("expected");
            }));

        var retry = await cache.GetOrRunAsync("key", "request", () => {
            calls++;
            return Task.FromResult(new ResultBox(7));
        }).ConfigureAwait(false);

        Assert.Multiple(() => {
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(retry.Value, Is.EqualTo(7));
        });
    }

    [Test]
    public async Task Missing_key_does_not_deduplicate() {
        var cache = new IdempotencyCache<ResultBox>();
        var calls = 0;
        await cache.GetOrRunAsync(null, "request",
            () => Task.FromResult(new ResultBox(++calls))).ConfigureAwait(false);
        await cache.GetOrRunAsync(" ", "request",
            () => Task.FromResult(new ResultBox(++calls))).ConfigureAwait(false);
        Assert.That(calls, Is.EqualTo(2));
    }

    [Test]
    public void Oversized_key_is_rejected_before_factory() {
        var cache = new IdempotencyCache<ResultBox>();
        var called = false;
        Assert.ThrowsAsync<ArgumentException>(() => cache.GetOrRunAsync(
            new string('x', 257), "request", () => {
                called = true;
                return Task.FromResult(new ResultBox(1));
            }));
        Assert.That(called, Is.False);
    }
}