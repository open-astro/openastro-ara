#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1171 — the HTTP side of the conditional fetch: what goes on the wire and what comes back.</summary>
    [TestFixture]
    public class HttpSkyDataFetcherTest {

        private sealed class StubHandler : HttpMessageHandler {
            public readonly List<HttpRequestMessage> Requests = new();
            public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
                Requests.Add(request);
                return Task.FromResult(Respond(request));
            }
        }

        private sealed class StubFactory : IHttpClientFactory {
            private readonly HttpMessageHandler _handler;
            public StubFactory(HttpMessageHandler handler) => _handler = handler;
            public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
        }

        private static readonly Uri Source = new("https://cdn.example/sky/pkg.tar.gz");

        [Test]
        public async Task A_stored_etag_and_date_go_on_the_wire_and_a_200_hands_back_the_new_ones() {
            using var handler = new StubHandler();
            var lastMod = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
            handler.Respond = _ => {
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
                r.Headers.ETag = new EntityTagHeaderValue("\"new\"");
                r.Content.Headers.LastModified = lastMod.AddDays(1);
                return r;
            };
            var fetcher = new HttpSkyDataFetcher(new StubFactory(handler));

            await using var fetch = await fetcher.OpenAsync(Source, new SkyDataValidator(lastMod, "\"old\""), CancellationToken.None);

            var req = handler.Requests[0];
            Assert.That(req.Headers.IfModifiedSince, Is.EqualTo(lastMod));
            Assert.That(req.Headers.IfNoneMatch.ToString(), Is.EqualTo("\"old\""), "the stored ETag is sent as If-None-Match");
            Assert.That(fetch.NotModified, Is.False);
            Assert.That(fetch.TotalBytes, Is.EqualTo(3));
            Assert.That(fetch.ETag, Is.EqualTo("\"new\""), "the 200's ETag is what the install will record");
            Assert.That(fetch.LastModified, Is.EqualTo(lastMod.AddDays(1)));
            Assert.That(fetch.Validator, Is.EqualTo(new SkyDataValidator(lastMod.AddDays(1), "\"new\"")));
        }

        [Test]
        public async Task A_weak_tag_round_trips_and_a_304_echoes_the_validators() {
            using var handler = new StubHandler();
            handler.Respond = _ => {
                var r = new HttpResponseMessage(HttpStatusCode.NotModified);
                r.Headers.ETag = new EntityTagHeaderValue("\"weak\"", isWeak: true);
                return r;
            };
            var fetcher = new HttpSkyDataFetcher(new StubFactory(handler));

            await using var fetch = await fetcher.OpenAsync(Source, new SkyDataValidator(null, "W/\"weak\""), CancellationToken.None);

            Assert.That(handler.Requests[0].Headers.IfNoneMatch.ToString(), Is.EqualTo("W/\"weak\""), "a weak tag parses and is sent as given");
            Assert.That(handler.Requests[0].Headers.IfModifiedSince, Is.Null, "no stored date → no If-Modified-Since");
            Assert.That(fetch.NotModified, Is.True);
            Assert.That(fetch.ETag, Is.EqualTo("W/\"weak\""), "the 304's ETag comes back for the record");
            Assert.That(fetch.Content.Length, Is.EqualTo(0));
        }

        [Test]
        public async Task An_unparsable_stored_tag_is_left_off_the_request_and_the_fetch_still_works() {
            using var handler = new StubHandler();
            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            var fetcher = new HttpSkyDataFetcher(new StubFactory(handler));

            await using var fetch = await fetcher.OpenAsync(Source, new SkyDataValidator(null, "not an etag"), CancellationToken.None);

            Assert.That(handler.Requests[0].Headers.IfNoneMatch, Is.Empty, "garbage is not sent");
            Assert.That(handler.Requests[0].Headers.IfModifiedSince, Is.Null);
            Assert.That(fetch.NotModified, Is.False);
            Assert.That(fetch.Validator, Is.Null, "the server sent neither validator");
        }

        [Test]
        public async Task A_304_without_an_echoed_tag_keeps_the_stored_validators() {
            using var handler = new StubHandler();
            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified);
            var fetcher = new HttpSkyDataFetcher(new StubFactory(handler));
            var stored = new SkyDataValidator(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), "\"abc\"");

            await using var fetch = await fetcher.OpenAsync(Source, stored, CancellationToken.None);

            Assert.That(fetch.NotModified, Is.True);
            Assert.That(fetch.Validator, Is.EqualTo(stored), "nothing new was sent, so the install keeps what it had");
        }

        [Test]
        public async Task A_cleartext_source_is_refused_before_any_request() {
            using var handler = new StubHandler();
            var fetcher = new HttpSkyDataFetcher(new StubFactory(handler));
            await Assert.ThatAsync(() => fetcher.OpenAsync(new Uri("http://cdn.example/x"), null, CancellationToken.None), Throws.TypeOf<InvalidOperationException>());
            Assert.That(handler.Requests, Is.Empty);
        }

        // ── #1136: release assets redirect to GitHub's asset store ──

        private static readonly Uri Asset = new("https://github.com/open-astro/sky-data/releases/download/astap-2026.10/astap-d80.tar.gz");
        private static readonly Uri Store = new("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sig=x");

        private static HttpResponseMessage Redirect(HttpStatusCode code, string location) {
            var r = new HttpResponseMessage(code);
            r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return r;
        }

        [Test]
        public async Task A_302_to_the_github_asset_store_is_followed_and_the_body_comes_from_the_store() {
            using var handler = new StubHandler();
            handler.Respond = req => req.RequestUri == Asset
                ? Redirect(HttpStatusCode.Found, Store.ToString())
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 7, 7 }) };
            var fetcher = new HttpSkyDataFetcher(new StubFactory(handler));

            await using var fetch = await fetcher.OpenAsync(Asset, null, CancellationToken.None);

            Assert.That(handler.Requests.Count, Is.EqualTo(2), "one hop");
            Assert.That(handler.Requests[1].RequestUri, Is.EqualTo(Store));
            Assert.That(fetch.TotalBytes, Is.EqualTo(2));
        }

        [TestCase("http://release-assets.githubusercontent.com/x")]   // HTTPS→HTTP downgrade
        [TestCase("https://mirror.example/astap-d80.tar.gz")]          // another host
        [TestCase("https://githubusercontent.com.evil.example/x")]     // suffix trick
        public async Task An_untrusted_location_is_refused_and_not_requested(string location) {
            using var handler = new StubHandler();
            handler.Respond = _ => Redirect(HttpStatusCode.Found, location);
            var fetcher = new HttpSkyDataFetcher(new StubFactory(handler));

            await Assert.ThatAsync(() => fetcher.OpenAsync(Asset, null, CancellationToken.None),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("refusing to follow"));
            Assert.That(handler.Requests.Count, Is.EqualTo(1), "the untrusted target is never requested");
        }

        [Test]
        public async Task A_redirect_without_a_location_is_refused() {
            using var handler = new StubHandler();
            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Found);
            var fetcher = new HttpSkyDataFetcher(new StubFactory(handler));
            await Assert.ThatAsync(() => fetcher.OpenAsync(Asset, null, CancellationToken.None),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("no Location"));
        }

        [Test]
        public async Task A_redirect_loop_stops_at_the_cap() {
            using var handler = new StubHandler();
            // Every hop is trusted (github.com) and redirects again: the cap ends it, not a bad host.
            handler.Respond = req => Redirect(HttpStatusCode.TemporaryRedirect, $"https://github.com/loop/{handler.Requests.Count}");
            var fetcher = new HttpSkyDataFetcher(new StubFactory(handler));

            await Assert.ThatAsync(() => fetcher.OpenAsync(Asset, null, CancellationToken.None),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("redirected more than"));
            Assert.That(handler.Requests.Count, Is.EqualTo(HttpSkyDataFetcher.MaxRedirects + 1), "the first request plus MaxRedirects hops, then give up");
        }

        [Test]
        public async Task A_relative_location_is_resolved_against_the_current_url() {
            using var handler = new StubHandler();
            handler.Respond = req => req.RequestUri == Asset
                ? Redirect(HttpStatusCode.MovedPermanently, "/open-astro/sky-data/releases/download/astap-2026.10/astap-d80-v2.tar.gz")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            var fetcher = new HttpSkyDataFetcher(new StubFactory(handler));

            await using var fetch = await fetcher.OpenAsync(Asset, null, CancellationToken.None);

            Assert.That(handler.Requests[1].RequestUri, Is.EqualTo(new Uri("https://github.com/open-astro/sky-data/releases/download/astap-2026.10/astap-d80-v2.tar.gz")));
        }

        [Test]
        public async Task Validators_ride_every_hop_and_a_304_after_the_hop_is_not_modified() {
            using var handler = new StubHandler();
            var lastMod = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
            handler.Respond = req => req.RequestUri == Asset
                ? Redirect(HttpStatusCode.Found, Store.ToString())
                : new HttpResponseMessage(HttpStatusCode.NotModified);
            var fetcher = new HttpSkyDataFetcher(new StubFactory(handler));

            await using var fetch = await fetcher.OpenAsync(Asset, new SkyDataValidator(lastMod, "\"d80\""), CancellationToken.None);

            foreach (var req in handler.Requests) {
                Assert.That(req.Headers.IfModifiedSince, Is.EqualTo(lastMod), $"If-Modified-Since on {req.RequestUri}");
                Assert.That(req.Headers.IfNoneMatch.ToString(), Is.EqualTo("\"d80\""), $"If-None-Match on {req.RequestUri}");
            }
            Assert.That(fetch.NotModified, Is.True, "the store's 304 means the install is current");
            Assert.That(fetch.ETag, Is.EqualTo("\"d80\""), "no echoed tag → the stored one is kept");
        }
    }
}
