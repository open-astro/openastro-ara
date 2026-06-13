#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §36-2 seam over "open a byte stream for a sky-data package URL". Abstracting the HTTP fetch behind an
    /// interface lets the <see cref="DataManagerService"/> download worker be unit-tested with an in-memory archive
    /// (no network), while production uses <see cref="HttpSkyDataFetcher"/>.
    /// </summary>
    public interface ISkyDataFetcher {
        /// <summary>Open a read stream for <paramref name="source"/>. The returned <see cref="SkyDataFetch"/> owns the
        /// underlying transport and must be disposed by the caller once the stream is consumed.</summary>
        Task<SkyDataFetch> OpenAsync(Uri source, CancellationToken ct);
    }

    /// <summary>An open sky-data byte stream plus its advertised length (null if the server didn't send one). Disposing
    /// this disposes the stream and the transport objects kept alive behind it.</summary>
    public sealed class SkyDataFetch : IAsyncDisposable {
        private readonly IDisposable[] _ownedAfterContent;

        public SkyDataFetch(Stream content, long? totalBytes, params IDisposable[] ownedAfterContent) {
            Content = content ?? throw new ArgumentNullException(nameof(content));
            TotalBytes = totalBytes;
            _ownedAfterContent = ownedAfterContent ?? Array.Empty<IDisposable>();
        }

        public Stream Content { get; }

        public long? TotalBytes { get; }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Best-effort stream teardown: any error tearing down the content stream must not leak the response/client (disposed in the finally) nor turn an already-finished download into a failure.")]
        public async ValueTask DisposeAsync() {
            try {
                await Content.DisposeAsync().ConfigureAwait(false);
            } catch (Exception) {
                // Swallow any teardown error — see the justification above; cleanup of the transport still runs.
            } finally {
                // Dispose the response/client AFTER the stream — the stream reads through them. Dispose each
                // independently so a throw from one doesn't leak the rest.
                foreach (var owned in _ownedAfterContent) {
                    try {
                        owned.Dispose();
                    } catch (Exception) {
                        // best-effort — see the method-level CA1031 justification.
                    }
                }
            }
        }
    }

    /// <summary>Production <see cref="ISkyDataFetcher"/>: a streaming HTTP GET via the named <see cref="HttpClientName"/>
    /// client, returning the response body stream with headers read but the body left to stream lazily.</summary>
    public sealed class HttpSkyDataFetcher : ISkyDataFetcher {

        public const string HttpClientName = "sky-data";

        private readonly IHttpClientFactory _httpClientFactory;

        public HttpSkyDataFetcher(IHttpClientFactory httpClientFactory) {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        public async Task<SkyDataFetch> OpenAsync(Uri source, CancellationToken ct) {
            ArgumentNullException.ThrowIfNull(source);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            try {
                // ResponseHeadersRead so the (potentially multi-GB) body streams rather than buffering in memory.
                var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                try {
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength;
                    var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    // On success the fetch owns the stream + response + client and disposes them in that order.
                    // Disposing an IHttpClientFactory client is documented as safe — the factory owns the pooled
                    // message handler's lifetime separately, so this disposes only the thin client wrapper (and it
                    // keeps the analyzer's CA2000 "dispose before losing scope" happy without a suppression).
                    return new SkyDataFetch(stream, totalBytes, response, client);
                } catch {
                    response.Dispose();
                    throw;
                }
            } catch {
                client.Dispose();
                throw;
            }
        }
    }
}
