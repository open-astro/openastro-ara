#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Services;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    // No-op WS broadcaster for tests that don't assert on events.
    internal sealed class NullBroadcaster : IWsBroadcaster {
        public long CurrentSequence => 0;
        public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) => Task.CompletedTask;
    }

    // Records every published (eventType, payload) pair for download-worker assertions.
    internal sealed class CapturingBroadcaster : IWsBroadcaster {
        public ConcurrentQueue<(string EventType, JsonElement Payload)> Events { get; } = new();
        public long CurrentSequence => Events.Count;

        public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) {
            Events.Enqueue((eventType, payload.Clone()));
            return Task.CompletedTask;
        }
    }

    // A fetcher that should never be called (for inventory-only tests).
    internal sealed class UnusedFetcher : ISkyDataFetcher {
        public Task<SkyDataFetch> OpenAsync(Uri source, CancellationToken ct) =>
            throw new InvalidOperationException("fetch not expected in this test");
    }

    // Returns a caller-supplied byte payload (e.g. an in-memory .tar.gz) as the package stream, or throws/blocks
    // to exercise the worker's failure and mid-flight paths.
    internal sealed class FakeSkyDataFetcher : ISkyDataFetcher {
        private readonly Func<CancellationToken, Task<byte[]>> _open;

        public FakeSkyDataFetcher(byte[] payload) {
            _open = _ => Task.FromResult(payload);
        }

        public FakeSkyDataFetcher(Func<CancellationToken, Task<byte[]>> open) {
            _open = open ?? throw new ArgumentNullException(nameof(open));
        }

        public async Task<SkyDataFetch> OpenAsync(Uri source, CancellationToken ct) {
            var payload = await _open(ct).ConfigureAwait(false);
            return new SkyDataFetch(new MemoryStream(payload, writable: false), payload.LongLength);
        }
    }
}
