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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §43-2b(b) seam over "open a byte stream for a remote backup archive". Lets the
    /// <see cref="BackupService"/> remote-restore worker be unit-tested with an in-memory
    /// archive (no network); production uses <see cref="HttpBackupSourceFetcher"/>.
    /// Result type is shared with the sky-data fetcher — same "stream + owned transport"
    /// shape, different scheme policy (see below).
    /// </summary>
    public interface IBackupSourceFetcher {
        /// <summary>Open a read stream for <paramref name="source"/>. The returned fetch owns
        /// the underlying transport and must be disposed by the caller once consumed.</summary>
        Task<SkyDataFetch> OpenAsync(Uri source, CancellationToken ct);
    }

    /// <summary>
    /// Production <see cref="IBackupSourceFetcher"/>: a streaming HTTP GET. Unlike the sky-data
    /// fetcher this deliberately allows plain <c>http</c> alongside <c>https</c> — the §43 remote
    /// source is typically ANOTHER ARA daemon on the same LAN, and v0.0.1 daemons are LAN-only
    /// with no TLS (§2.3). Integrity does not ride on the transport: a remote restore requires
    /// the request's out-of-band SHA-256 and the archive is refused on any mismatch.
    /// </summary>
    public sealed class HttpBackupSourceFetcher : IBackupSourceFetcher {

        public const string HttpClientName = "backup-source";

        private readonly IHttpClientFactory _httpClientFactory;

        public HttpBackupSourceFetcher(IHttpClientFactory httpClientFactory) {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        public async Task<SkyDataFetch> OpenAsync(Uri source, CancellationToken ct) {
            ArgumentNullException.ThrowIfNull(source);
            if (!source.IsAbsoluteUri
                    || (!string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                        && !string.Equals(source.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))) {
                throw new InvalidOperationException($"Backup source must be an absolute http(s) URL, got '{source}'.");
            }

            var client = _httpClientFactory.CreateClient(HttpClientName);
            try {
                // ResponseHeadersRead so the body streams to the temp file rather than buffering in memory.
                var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                try {
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength;
                    var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    // On success the fetch owns stream + response + client, disposed in that order
                    // (mirrors HttpSkyDataFetcher — disposing an IHttpClientFactory client is safe;
                    // the factory owns the pooled handler's lifetime separately).
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
