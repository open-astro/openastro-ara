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
    /// What a completed install remembers about the remote package so the next fetch can be conditional
    /// (§36 incremental update, #1171): the <c>Last-Modified</c> date and/or the <c>ETag</c>. Either may be
    /// null when the server did not send it; a validator with neither is treated as "no validator".
    /// </summary>
    public sealed record SkyDataValidator(DateTimeOffset? LastModified, string? ETag) {
        public bool IsEmpty => LastModified is null && string.IsNullOrWhiteSpace(ETag);
    }

    /// <summary>
    /// §36-2 seam over "open a byte stream for a sky-data package URL". Abstracting the HTTP fetch behind an
    /// interface lets the <see cref="DataManagerService"/> download worker be unit-tested with an in-memory archive
    /// (no network), while production uses <see cref="HttpSkyDataFetcher"/>.
    /// </summary>

    public interface ISkyDataFetcher {
        /// <summary>Open a read stream for <paramref name="source"/>. The returned <see cref="SkyDataFetch"/> owns the
        /// underlying transport and must be disposed by the caller once the stream is consumed.
        /// When <paramref name="validator"/> carries a value, the fetch is conditional (§36 incremental update):
        /// <c>If-None-Match</c> for the ETag and <c>If-Modified-Since</c> for the date. If the remote package is
        /// unchanged the server answers 304 and the result has <see cref="SkyDataFetch.NotModified"/> set with an
        /// empty body — the caller must check that flag before reading <see cref="SkyDataFetch.Content"/>.</summary>
        Task<SkyDataFetch> OpenAsync(Uri source, SkyDataValidator? validator, CancellationToken ct);
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

        /// <summary>True when a conditional fetch got 304 Not Modified — the remote package is unchanged per the
        /// caller's validator (ETag and/or Last-Modified), <see cref="Content"/> is empty, and the caller should keep
        /// the existing install rather than re-extract.</summary>
        public bool NotModified { get; init; }

        /// <summary>The remote <c>Last-Modified</c> for this package, when the server sent one — persisted alongside the
        /// install so a later fetch can be made conditional. Null when the server didn't advertise it.</summary>
        public DateTimeOffset? LastModified { get; init; }

        /// <summary>The remote <c>ETag</c> (as sent, quotes included), when the server sent one (#1171). A CDN
        /// that re-serves a byte-identical package with a fresh date keeps its ETag, so this is the validator
        /// that survives a re-upload.</summary>
        public string? ETag { get; init; }

        /// <summary>Both validators, or null when the server sent neither.</summary>
        public SkyDataValidator? Validator {
            get {
                var v = new SkyDataValidator(LastModified, ETag);
                return v.IsEmpty ? null : v;
            }
        }

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

        public async Task<SkyDataFetch> OpenAsync(Uri source, SkyDataValidator? validator, CancellationToken ct) {
            ArgumentNullException.ThrowIfNull(source);
            // Defence in depth: today the catalog hardcodes https URLs, but refuse to fetch a sky-data package over
            // cleartext if a future catalog entry ever carries an http (or other-scheme) source.
            if (!source.IsAbsoluteUri || !string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) {
                throw new InvalidOperationException($"Sky-data source must be an absolute https URL, got '{source}'.");
            }

            var client = _httpClientFactory.CreateClient(HttpClientName);
            try {
                using var request = new HttpRequestMessage(HttpMethod.Get, source);
                if (validator?.LastModified is { } since) {
                    // §36 incremental update: ask the CDN to answer 304 if the package is unchanged.
                    request.Headers.IfModifiedSince = since;
                }
                if (!string.IsNullOrWhiteSpace(validator?.ETag)) {
                    // #1171 — the ETag survives a re-upload of identical bytes where Last-Modified does not; an
                    // unparsable stored tag is not sent (the date alone, if any, still makes the GET conditional).
                    if (System.Net.Http.Headers.EntityTagHeaderValue.TryParse(validator.ETag, out var etag)) {
                        request.Headers.IfNoneMatch.Add(etag);
                    } else {
                        OpenAstroAra.Core.Utility.Logger.Debug($"sky-data fetch of {source}: stored ETag '{validator.ETag}' is not a valid entity tag; fetching without If-None-Match.");
                    }
                }
                // ResponseHeadersRead so the (potentially multi-GB) body streams rather than buffering in memory.
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                try {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotModified) {
                        // 304: nothing to download. Capture the validator (if echoed) and return an empty,
                        // not-modified result whose body is Stream.Null — so dispose the transport here, since
                        // unlike the success path no streamed body keeps it alive.
                        var lastModified304 = response.Content.Headers.LastModified ?? validator?.LastModified;
                        var etag304 = response.Headers.ETag?.ToString() ?? validator?.ETag;
                        response.Dispose();
                        client.Dispose();
                        return new SkyDataFetch(Stream.Null, 0) { NotModified = true, LastModified = lastModified304, ETag = etag304 };
                    }
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength;
                    var lastModified = response.Content.Headers.LastModified;
                    var etagHeader = response.Headers.ETag?.ToString();
                    var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    // On success the fetch owns the stream + response + client and disposes them in that order.
                    // Disposing an IHttpClientFactory client is documented as safe — the factory owns the pooled
                    // message handler's lifetime separately, so this disposes only the thin client wrapper (and it
                    // keeps the analyzer's CA2000 "dispose before losing scope" happy without a suppression).
                    return new SkyDataFetch(stream, totalBytes, response, client) { LastModified = lastModified, ETag = etagHeader };
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
