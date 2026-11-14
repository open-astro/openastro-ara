#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>Counts every store call that can write a section, so a refused write is seen to write nothing
    /// even when the body equals the stored copy. Public and unsealed: DispatchProxy.Create subclasses it.</summary>
    public class SectionWriteCountingStore : DispatchProxy {
        private int _writeCalls;
        public IProfileStore Inner { get; set; } = null!;
        public int WriteCalls => Volatile.Read(ref _writeCalls);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
            if (targetMethod!.Name.StartsWith("Put", StringComparison.Ordinal)
                || targetMethod.Name.StartsWith("Update", StringComparison.Ordinal)) {
                Interlocked.Increment(ref _writeCalls);
            }
            try {
                return targetMethod.Invoke(Inner, args);
            } catch (TargetInvocationException e) when (e.InnerException is not null) {
                throw e.InnerException;
            }
        }
    }

    /// <summary>
    /// Record 0015 rule 5, built on the section table of record 0012: every profile section a client can write
    /// answers a read with an <c>ETag</c> derived from the stored section, refuses a write whose <c>If-Match</c>
    /// names another copy with a 412 that writes nothing, and (until the daemon requires a version) still
    /// accepts a write with no <c>If-Match</c>. A write that changes the section moves the tag, so the copy
    /// loaded before it is refused and a read answers the new tag. The test walks <see cref="ProfileSections.All"/> and finds each
    /// section's route by the DTO its PUT accepts, so a new section is covered without a new test.
    /// </summary>
    [TestFixture]
    [Category("IO")] // loopback HTTP: not part of the quick unit run
    public class ProfileSectionVersionTest {

        /// <summary>Sections only the daemon writes (calibration records, identity): no PUT route, so no
        /// client write to check.</summary>
        private static readonly string[] DaemonOnly = [
            "FocusCalibration", "CalibrationState", "TrackingCalibration",
            "SkyModelCalibration", "EquipmentIdentity", "FilterFocusOffsets",
        ];

        /// <summary>One valid edit per routed section, applied to the body its GET returned. The walk PUTs it so
        /// the stored copy really changes: a fixed tag, a tag hashed from the request body, or a route that reads
        /// another section's tag all keep the same tag across a real write and fail there.</summary>
        private static readonly Dictionary<string, Action<JsonObject>> Changes = new() {
            ["ImagingDefaults"] = o => Bump(o, "exposure_seconds"),
            ["Storage"] = o => Bump(o, "backup_retention_count"),
            ["Notifications"] = o => Flip(o, "in_app_banner"),
            ["Site"] = o => o["latitude_deg"] = -33.856784,
            ["Filenames"] = o => Flip(o, "compress_darks_and_bias"),
            ["SafetyPolicies"] = o => Flip(o, "auto_resume_when_safe"),
            ["Autofocus"] = o => Bump(o, "steps"),
            ["PlateSolve"] = o => Bump(o, "timeout_seconds"),
            ["DiagnosticsMode"] = o => o["mode"] = "pause_on_critical",
            ["Phd2"] = o => Flip(o, "dither_enabled"),
            ["EquipmentConnection"] = o => Flip(o, "camera"),
            ["StretchDefaults"] = o => Bump(o, "asinh_default_beta"),
            ["Optics"] = o => Bump(o, "focal_length_mm"),
            ["CameraElectronics"] = o => o["sensor_name"] = "IMX571",
            ["FilterSet"] = o => o["filters"] = new JsonArray(new JsonObject { ["name"] = "Lum", ["kind"] = "l" }),
            ["FilterWheelLabels"] = o => o["labels"] = new JsonArray("Lum", "Red", "Green", "Blue"),
            ["CustomHorizon"] = o => o["points"] = new JsonArray(
                new JsonObject { ["azimuth_deg"] = 0.0, ["altitude_deg"] = 12.0 },
                new JsonObject { ["azimuth_deg"] = 180.0, ["altitude_deg"] = 15.0 }),
            ["PolarAlign"] = o => Bump(o, "exposure_seconds"),
            ["Unguided"] = o => Flip(o, "guard_enabled"),
            ["GuiderAdvanced"] = o => Flip(o, "multi_star_enabled"),
            ["FilterWheelPolicy"] = o => Flip(o, "home_on_first_connect"),
            ["SavedTargets"] = o => o["targets"] = new JsonArray(new JsonObject {
                ["name"] = "M 31", ["aliases"] = new JsonArray(), ["ra_deg"] = 10.6847, ["dec_deg"] = 41.2689,
                ["source"] = "typed", ["saved_utc"] = "2026-11-14T00:00:00Z",
            }),
            ["CoolingFan"] = o => { o["device_id"] = "switch-1"; o["port_id"] = 2; },
            ["Dome"] = o => Bump(o, "dome_radius_mm"),
        };

        private static void Bump(JsonObject o, string key) => o[key] = o[key]!.GetValue<double>() + 1;
        private static void Flip(JsonObject o, string key) => o[key] = !(o[key]?.GetValue<bool>() ?? false);

        private sealed record Route(string Section, string Path);

        private WebApplication _app = null!;
        private HttpClient _http = null!;
        private SectionWriteCountingStore _counter = null!;
        private List<Route> _routes = null!;

        [OneTimeSetUp]
        public async Task StartHost() {
            var store = DispatchProxy.Create<IProfileStore, SectionWriteCountingStore>();
            _counter = (SectionWriteCountingStore)(object)store;
            _counter.Inner = new InMemoryProfileStore();

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store);
            // The daemon's AOT-safe JSON setup (Program.cs): the slim builder has no reflection serializer.
            builder.Services.ConfigureHttpJsonOptions(opts => {
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter<FilterKind>(JsonNamingPolicy.SnakeCaseLower));
                opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            _app = builder.Build();
            _app.MapProfileEndpoints();
            await _app.StartAsync();
            _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };

            // Each table section's route: the PUT whose body is the section's DTO.
            var puts = ((IEndpointRouteBuilder)_app).DataSources
                .SelectMany(d => d.Endpoints)
                .OfType<RouteEndpoint>()
                .Where(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("PUT") == true)
                .Select(e => (Type: e.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType, Path: e.RoutePattern.RawText!))
                .Where(p => p.Type is not null)
                .ToList();
            _routes = [
                .. ProfileSections.All
                    .Select(s => (s.Name, Paths: puts.Where(p => p.Type == s.DtoType).Select(p => p.Path).ToList()))
                    .Where(s => s.Paths.Count == 1)
                    .Select(s => new Route(s.Name, s.Paths[0])),
            ];
        }

        [OneTimeTearDown]
        public async Task StopHost() {
            _http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        [Test]
        public void Every_table_section_has_one_put_route_or_is_written_by_the_daemon_only() {
            var routed = _routes.Select(r => r.Section).ToHashSet();
            var neither = ProfileSections.All.Select(s => s.Name)
                .Where(n => !routed.Contains(n) && !DaemonOnly.Contains(n))
                .ToList();
            Assert.That(neither, Is.Empty, "a section with no PUT route must be listed as daemon-only, or the walk below skips it");
            Assert.That(routed.Intersect(DaemonOnly), Is.Empty, "a daemon-only section gained a PUT route: drop it from DaemonOnly");
            Assert.That(_routes, Has.Count.GreaterThanOrEqualTo(24));
            Assert.That(Changes.Keys, Is.EquivalentTo(routed), "every routed section needs one edit in Changes, and only those");
        }

        [Test]
        public async Task Every_section_route_answers_an_ETag_and_refuses_a_stale_IfMatch_with_nothing_written() {
            var failures = new List<string>();
            foreach (var (section, path) in _routes) {
                using var get = await _http.GetAsync(new Uri(path, UriKind.Relative));
                var body = await get.Content.ReadAsStringAsync();
                var etag = get.Headers.ETag?.ToString();
                if (get.StatusCode != HttpStatusCode.OK || string.IsNullOrEmpty(etag)) {
                    failures.Add($"{section} GET {path}: {(int)get.StatusCode}, ETag '{etag}'");
                    continue;
                }

                var writesBefore = _counter.WriteCalls;
                using (var stale = await Put(path, body, "\"stale\"")) {
                    if (stale.StatusCode != HttpStatusCode.PreconditionFailed) {
                        failures.Add($"{section} PUT {path} with a stale If-Match: {(int)stale.StatusCode}, want 412");
                    }
                }
                if (_counter.WriteCalls != writesBefore) {
                    failures.Add($"{section} PUT {path} with a stale If-Match wrote to the store");
                }

                using (var current = await Put(path, body, etag)) {
                    var after = current.Headers.ETag?.ToString();
                    if (current.StatusCode != HttpStatusCode.OK || string.IsNullOrEmpty(after)) {
                        failures.Add($"{section} PUT {path} with the current If-Match: {(int)current.StatusCode}, ETag '{after}'");
                    } else if (after != etag) {
                        // The same content gives the same tag: a hash of the stored section, not a write counter.
                        failures.Add($"{section} PUT {path} of the unchanged section moved the ETag {etag} -> {after}");
                    }
                }

                using (var unversioned = await Put(path, body, null)) {
                    if (unversioned.StatusCode != HttpStatusCode.OK) {
                        failures.Add($"{section} PUT {path} with no If-Match: {(int)unversioned.StatusCode}, want 200 until a version is required");
                    }
                }

                // A real write moves the tag; the copy loaded before it is then stale; a read answers the new tag.
                var changed = JsonNode.Parse(body)!.AsObject();
                Changes[section](changed);
                string? moved;
                using (var write = await Put(path, changed.ToJsonString(), etag)) {
                    moved = write.Headers.ETag?.ToString();
                    if (write.StatusCode != HttpStatusCode.OK || string.IsNullOrEmpty(moved) || moved == etag) {
                        failures.Add($"{section} PUT {path} of a changed section: {(int)write.StatusCode}, ETag {etag} -> '{moved}', want 200 and a new tag");
                        continue;
                    }
                }
                writesBefore = _counter.WriteCalls;
                using (var old = await Put(path, body, etag)) {
                    if (old.StatusCode != HttpStatusCode.PreconditionFailed) {
                        failures.Add($"{section} PUT {path} with the tag from before the write: {(int)old.StatusCode}, want 412");
                    }
                }
                if (_counter.WriteCalls != writesBefore) {
                    failures.Add($"{section} PUT {path} with the tag from before the write wrote to the store");
                }
                using (var reread = await _http.GetAsync(new Uri(path, UriKind.Relative))) {
                    var now = reread.Headers.ETag?.ToString();
                    if (now != moved) {
                        failures.Add($"{section} GET {path} after the write: ETag '{now}', want {moved}");
                    }
                    if (await reread.Content.ReadAsStringAsync() == body) {
                        failures.Add($"{section}: the edit in Changes did not change the stored section");
                    }
                }
                using (var any = await Put(path, body, "*")) {
                    if (any.StatusCode != HttpStatusCode.OK) {
                        failures.Add($"{section} PUT {path} with If-Match *: {(int)any.StatusCode}, want 200");
                    }
                }
            }
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }

        private async Task<HttpResponseMessage> Put(string path, string json, string? ifMatch) {
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative)) {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (ifMatch is not null) {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }
            return await _http.SendAsync(request);
        }
    }
}