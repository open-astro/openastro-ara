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
    }
}
