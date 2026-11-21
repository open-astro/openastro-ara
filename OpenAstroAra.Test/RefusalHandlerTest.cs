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
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Endpoints;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>ADR 0022 — the cause-to-wire table, through the one Refusal handler: each declared cause
    /// answers its 4xx status with <c>reason</c> = the cause, its parameters as extensions and a human
    /// <c>title</c>. Asserts the wire, never the English sentence.</summary>
    [TestFixture]
    public class RefusalHandlerTest {

        // Every declared cause (slice 1 pointing, then GoTo, then End Night, then the unverified mount, then Polar Align's start, #2048, and its sweep bound, #2089, then the mount clock, #2071): token, status, parameter names. A row changed here is a wire change.
        private static readonly object[] Causes = {
            new object[] { "no-camera", 409, Array.Empty<string>() },
            new object[] { "camera-busy", 409, Array.Empty<string>() },
            new object[] { "gate-stuck", 409, Array.Empty<string>() },
            new object[] { "center-in-progress", 409, new[] { "job_id" } },
            new object[] { "optics-not-set", 422, new[] { "missing" } },
            new object[] { "solver-not-set", 422, Array.Empty<string>() },
            new object[] { "capture-settings-unsupported", 422, Array.Empty<string>() },
            new object[] { "storage-unavailable", 409, Array.Empty<string>() },
            new object[] { "below-horizon-limit", 409, new[] { "altitude_deg", "limit_deg" } },
            new object[] { "counterweight-limit", 409, new[] { "hour_angle_hours", "counterweight_angle_deg", "limit_deg" } },
            new object[] { "end-night-in-progress", 409, Array.Empty<string>() },
            new object[] { "mount-position-unverified", 409, new[] { "position_unverified_reason" } },
            new object[] { "polar-align-mount-parked", 409, Array.Empty<string>() },
            new object[] { "polar-align-unconfirmed", 409, new[] { "direction" } },
            new object[] { "polar-align-sweep-limit", 409, new[] { "offset_deg", "limit_deg" } },
            new object[] { "polar-align-moving-mount", 409, Array.Empty<string>() },
            new object[] { "polar-align-on-axis", 409, Array.Empty<string>() },
            new object[] { "polar-align-pointing-unknown", 409, Array.Empty<string>() },
            new object[] { "polar-align-pose-ambiguous", 409, Array.Empty<string>() },
            new object[] { "polar-align-mount-busy", 409, Array.Empty<string>() },
            new object[] { "first-flip-not-completed", 409, Array.Empty<string>() },
            new object[] { "mount-clock-not-verified", 409, Array.Empty<string>() },
            new object[] { "polar-align-already-running", 409, Array.Empty<string>() },
            new object[] { "polar-align-clutch-turned", 409, Array.Empty<string>() },
            new object[] { "above-clearance-limit", 409, new[] { "altitude_deg", "limit_deg" } },
        };

        private static readonly string[] BothOptics = { "focal_length", "pixel_size" };

        private static RefusalCause Cause(string token) => RefusalCatalog.All.Single(c => c.Token == token);

        private static DefaultHttpContext Http() {
            var services = new ServiceCollection().AddLogging();
            // The daemon's own JSON setup (Program.cs), so a parameter type the app cannot write fails here.
            services.ConfigureHttpJsonOptions(o => {
                o.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            return new DefaultHttpContext { RequestServices = services.BuildServiceProvider(), Response = { Body = new MemoryStream() } };
        }

        private static async Task<JsonElement> Answer(HttpContext http, Exception thrown) {
            await RefusalHandler.Middleware(http, _ => throw thrown);
            http.Response.Body.Position = 0;
            using var doc = await JsonDocument.ParseAsync(http.Response.Body);
            return doc.RootElement.Clone();
        }

        [Test]
        public void The_catalog_declares_exactly_the_tabled_causes_each_once() {
            Assert.That(RefusalCatalog.All.Select(c => c.Token),
                Is.EquivalentTo(Causes.Cast<object[]>().Select(r => (string)r[0])));
            Assert.That(RefusalCatalog.All.Select(c => c.Token), Is.Unique);
        }

        [TestCaseSource(nameof(Causes))]
        public async Task A_refusal_answers_its_status_with_the_cause_as_reason_and_its_parameters(
                string token, int status, string[] parameterNames) {
            var cause = Cause(token);
            Assert.That(cause.Status, Is.EqualTo(status));
            Assert.That(cause.ParameterNames, Is.EqualTo(parameterNames));

            var parameters = parameterNames.ToDictionary(n => n, n => (object?)$"value-of-{n}");
            var http = Http();
            var body = await Answer(http, new RefusalException(cause, "An English sentence for the log.", parameters));

            Assert.Multiple(() => {
                Assert.That(http.Response.StatusCode, Is.EqualTo(status));
                Assert.That(http.Response.ContentType, Does.StartWith("application/problem+json"));
                Assert.That(body.GetProperty("status").GetInt32(), Is.EqualTo(status));
                Assert.That(body.GetProperty("reason").GetString(), Is.EqualTo(token));
                foreach (var name in parameterNames) {
                    Assert.That(body.GetProperty(name).GetString(), Is.EqualTo($"value-of-{name}"), name);
                }
                var title = body.GetProperty("title").GetString();
                Assert.That(title, Is.Not.Null.And.Not.Empty);
                Assert.That(title, Is.Not.EqualTo(token));
                Assert.That(Regex.IsMatch(title!, "^[a-z0-9_.-]+$"), Is.False, $"title '{title}' is a token, not a human title");
                Assert.That(body.GetProperty("detail").GetString(), Is.EqualTo("An English sentence for the log."));
            });
        }

        [Test]
        public async Task The_optics_cause_carries_the_missing_values_as_a_list() {
            var http = Http();
            var body = await Answer(http, new RefusalException(RefusalCatalog.OpticsNotSet, "no optics",
                new Dictionary<string, object?> { ["missing"] = BothOptics }));
            Assert.That(body.GetProperty("missing").EnumerateArray().Select(e => e.GetString()),
                Is.EqualTo(BothOptics));
        }

        [Test]
        public void A_parameter_the_cause_does_not_declare_is_refused_at_the_throw_site() {
            Assert.Throws<ArgumentException>(() => _ = new RefusalException(RefusalCatalog.CameraBusy, "busy",
                new Dictionary<string, object?> { ["job_id"] = "x" }));
        }

        [Test]
        public void Every_declared_status_is_a_4xx() {
            Assert.That(RefusalCatalog.All.Select(c => c.Status), Has.All.InRange(400, 499));
        }

        [Test]
        public async Task A_fault_passes_through_the_handler_untouched() {
            // A device fault or any other exception keeps its own (5xx) path: the handler must not answer it.
            var fault = new InvalidOperationException("capture failed");
            var http = Http();
            Exception? thrown = null;
            try {
                await RefusalHandler.Middleware(http, _ => throw fault);
            } catch (InvalidOperationException ex) {
                thrown = ex;
            }
            Assert.That(thrown, Is.SameAs(fault));
            Assert.That(http.Response.Body.Length, Is.Zero);
        }

        private sealed class StartedResponse : HttpResponseFeature {
            public override bool HasStarted => true;
        }

        [Test]
        public async Task A_refusal_after_the_answer_has_started_is_rethrown() {
            // The headers are already sent, so no problem body can be written: the Refusal leaves as it came.
            var http = Http();
            http.Features.Set<IHttpResponseFeature>(new StartedResponse());
            var refusal = new RefusalException(RefusalCatalog.CameraBusy, "busy");
            Exception? thrown = null;
            try {
                await RefusalHandler.Middleware(http, _ => throw refusal);
            } catch (RefusalException ex) {
                thrown = ex;
            }
            Assert.That(thrown, Is.SameAs(refusal));
            Assert.That(http.Response.Body.Length, Is.Zero);
        }

        [Test]
        public async Task A_request_that_is_not_refused_is_passed_on() {
            var http = Http();
            var called = false;
            await RefusalHandler.Middleware(http, _ => { called = true; return Task.CompletedTask; });
            Assert.That(called, Is.True);
        }

        [Test]
        public void A_refusal_is_an_InvalidOperationException_so_endpoints_not_yet_moved_catch_it_as_today() {
            Assert.That(new RefusalException(RefusalCatalog.CameraBusy, "busy"), Is.InstanceOf<InvalidOperationException>());
        }
    }
}