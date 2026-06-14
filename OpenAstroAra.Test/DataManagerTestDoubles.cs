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
        public Task<SkyDataFetch> OpenAsync(Uri source, DateTimeOffset? ifModifiedSince, CancellationToken ct) =>
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

        public async Task<SkyDataFetch> OpenAsync(Uri source, DateTimeOffset? ifModifiedSince, CancellationToken ct) {
            var payload = await _open(ct).ConfigureAwait(false);
            return new SkyDataFetch(new MemoryStream(payload, writable: false), payload.LongLength);
        }
    }

    // Records the conditional validator it was called with and returns either a 304 NotModified result or a payload
    // carrying a Last-Modified — to exercise the §36 incremental-update path.
    internal sealed class ConditionalFetcher : ISkyDataFetcher {
        private readonly byte[] _payload;
        private readonly bool _notModified;
        private readonly DateTimeOffset? _lastModified;

        public ConditionalFetcher(byte[] payload, DateTimeOffset? lastModified) {
            _payload = payload;
            _lastModified = lastModified;
            _notModified = false;
        }

        private ConditionalFetcher() {
            _payload = Array.Empty<byte>();
            _notModified = true;
        }

        public static ConditionalFetcher NotModified() => new();

        public int Calls { get; private set; }
        public DateTimeOffset? LastIfModifiedSince { get; private set; }

        public Task<SkyDataFetch> OpenAsync(Uri source, DateTimeOffset? ifModifiedSince, CancellationToken ct) {
            Calls++;
            LastIfModifiedSince = ifModifiedSince;
            if (_notModified) {
                return Task.FromResult(new SkyDataFetch(Stream.Null, 0) { NotModified = true, LastModified = ifModifiedSince });
            }
            return Task.FromResult(
                new SkyDataFetch(new MemoryStream(_payload, writable: false), _payload.LongLength) { LastModified = _lastModified });
        }
    }

    // Opens a stream that never yields a byte (honoring its read token), to exercise the idle-progress watchdog.
    internal sealed class StallingFetcher : ISkyDataFetcher {
        public Task<SkyDataFetch> OpenAsync(Uri source, DateTimeOffset? ifModifiedSince, CancellationToken ct) =>
            Task.FromResult(new SkyDataFetch(new StallStream(), totalBytes: null));
    }

    // OpenAsync itself never returns (honoring its token) — a CDN that accepts the connection but never sends
    // headers. Exercises the watchdog arming the header-wait phase.
    internal sealed class StallingHeaderFetcher : ISkyDataFetcher {
        public async Task<SkyDataFetch> OpenAsync(Uri source, DateTimeOffset? ifModifiedSince, CancellationToken ct) {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new SkyDataFetch(Stream.Null, totalBytes: null); // unreachable
        }
    }

    // Yields the payload in small chunks with a fixed delay between reads, to simulate a slow-but-steady transfer.
    internal sealed class TricklingFetcher : ISkyDataFetcher {
        private readonly byte[] _payload;
        private readonly int _chunk;
        private readonly TimeSpan _gap;

        public TricklingFetcher(byte[] payload, int chunk, TimeSpan gap) {
            _payload = payload;
            _chunk = chunk;
            _gap = gap;
        }

        public Task<SkyDataFetch> OpenAsync(Uri source, DateTimeOffset? ifModifiedSince, CancellationToken ct) =>
            Task.FromResult(new SkyDataFetch(new TrickleStream(_payload, _chunk, _gap), _payload.LongLength));
    }

    internal sealed class TrickleStream : Stream {
        private readonly byte[] _data;
        private readonly int _chunk;
        private readonly TimeSpan _gap;
        private int _pos;

        public TrickleStream(byte[] data, int chunk, TimeSpan gap) {
            _data = data;
            _chunk = chunk;
            _gap = gap;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            if (_pos >= _data.Length) {
                return 0;
            }
            await Task.Delay(_gap, cancellationToken).ConfigureAwait(false);
            var n = Math.Min(Math.Min(_chunk, buffer.Length), _data.Length - _pos);
            _data.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async reads only");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A read stream whose async reads block forever but honor cancellation — so when the worker's idle CTS fires,
    // the in-flight read throws OperationCanceledException.
    internal sealed class StallStream : Stream {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async reads only");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
