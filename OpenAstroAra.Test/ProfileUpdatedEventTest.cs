#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1644 — a successful profile-section write publishes <c>profile.updated { section }</c>; reads and
    /// rejected writes publish nothing.</summary>
    [TestFixture]
    public class ProfileUpdatedEventTest {

        private sealed class RecordingWs : IWsBroadcaster {
            public readonly List<(string Type, JsonElement Payload)> Published = new();
            public bool Throw { get; set; }
            public long CurrentSequence { get; private set; }
            public Task PublishAsync(OpenAstroAra.Server.Contracts.WsEvents.WsEvent eventType, JsonElement payload, CancellationToken ct) {
                if (Throw) {
                    throw new InvalidOperationException("broadcaster is down");
                }
                Published.Add((eventType.Token, payload.Clone()));
                return Task.CompletedTask;
            }
        }

        private static DefaultHttpContext Http(string method, string path) {
            var http = new DefaultHttpContext();
            http.Request.Method = method;
            http.Request.Path = path;
            return http;
        }

        private static Func<ValueTask<object?>> Returns(IResult result) => () => new ValueTask<object?>(result);

        [Test]
        public async Task Successful_put_publishes_the_section() {
            var ws = new RecordingWs();
            await ProfileEndpoints.PublishUpdatedAfterWrite(Http("PUT", "/api/v1/profile/site"), Returns(Results.Ok()), ws);
            Assert.That(ws.Published, Has.Count.EqualTo(1));
            Assert.That(ws.Published[0].Type, Is.EqualTo("profile.updated"));
            Assert.That(ws.Published[0].Payload.GetProperty("section").GetString(), Is.EqualTo("site"));
        }

        [Test]
        public async Task A_nested_route_publishes_its_first_segment() {
            var ws = new RecordingWs();
            await ProfileEndpoints.PublishUpdatedAfterWrite(Http("PUT", "/api/v1/profile/filter-wheel/labels"), Returns(Results.Ok()), ws);
            Assert.That(ws.Published[0].Payload.GetProperty("section").GetString(), Is.EqualTo("filter-wheel"));
        }

        [Test]
        public async Task A_rejected_write_publishes_nothing() {
            var ws = new RecordingWs();
            await ProfileEndpoints.PublishUpdatedAfterWrite(Http("PUT", "/api/v1/profile/site"),
                Returns(Results.Problem(statusCode: 422)), ws);
            Assert.That(ws.Published, Is.Empty);
        }

        [Test]
        public async Task A_read_publishes_nothing() {
            var ws = new RecordingWs();
            await ProfileEndpoints.PublishUpdatedAfterWrite(Http("GET", "/api/v1/profile/site"), Returns(Results.Ok()), ws);
            Assert.That(ws.Published, Is.Empty);
        }

        [Test]
        public async Task A_throwing_write_publishes_nothing() {
            var ws = new RecordingWs();
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await ProfileEndpoints.PublishUpdatedAfterWrite(Http("PUT", "/api/v1/profile/site"),
                    () => throw new InvalidOperationException("boom"), ws));
            Assert.That(ws.Published, Is.Empty);
        }

        [Test]
        public async Task A_dead_broadcaster_does_not_fail_the_write() {
            var ws = new RecordingWs { Throw = true };
            var result = await ProfileEndpoints.PublishUpdatedAfterWrite(Http("PUT", "/api/v1/profile/site"), Returns(Results.Ok()), ws);
            Assert.That(result, Is.Not.Null);
        }
    }
}