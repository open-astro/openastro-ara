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
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1807 slice B — a profile section the daemon writes itself, outside the profile routes (time sync, plate
    /// solve, a connect-time fill, the flip executor, auto flats, the guider), publishes the same
    /// <c>profile.updated { section }</c> a route write does and moves the section's <c>ETag</c>, so a client
    /// holding the section reloads it instead of getting a false 412 on its next Save. A route write still
    /// publishes once; a whole-profile apply (select) and a daemon-only section publish nothing.
    /// </summary>
    [TestFixture]
    [Category("IO")] // loopback HTTP and a profile.json in a temp dir
    [NonParallelizable]
    public class DaemonProfileWriteEventTest {

        private sealed class RecordingWs : IWsBroadcaster {
            private readonly List<string> _sections = new();
            public long CurrentSequence => 0;
            public Task PublishAsync(OpenAstroAra.Server.Contracts.WsEvents.WsEvent eventType, JsonElement payload, CancellationToken ct) {
                if (eventType.Token == "profile.updated") {
                    lock (_sections) {
                        _sections.Add(payload.GetProperty("section").GetString()!);
                    }
                }
                return Task.CompletedTask;
            }
            public List<string> Sections() { lock (_sections) { return _sections.ToList(); } }
            public void Clear() { lock (_sections) { _sections.Clear(); } }
        }

        private string _dir = null!;
        private FileProfileStore _store = null!;
        private RecordingWs _ws = null!;
        private WebApplication _app = null!;
        private HttpClient _http = null!;

        [OneTimeSetUp]
        public async Task StartHost() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-1807b-{Guid.NewGuid():N}");
            _store = new FileProfileStore(_dir);
            _ws = new RecordingWs();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<IProfileStore>(_store);
            builder.Services.AddSingleton<IWsBroadcaster>(_ws);
            builder.Services.ConfigureHttpJsonOptions(opts => {
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter<FilterKind>(JsonNamingPolicy.SnakeCaseLower));
                opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            _app = builder.Build();
            _app.Use(RefusalHandler.Middleware); // #1589 — the confirm route's refusal, as Program.cs answers it
            _app.MapProfileEndpoints();
            _app.PublishDaemonProfileWrites();
            await _app.StartAsync();
            _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        }

        [OneTimeTearDown]
        public async Task StopHost() {
            _http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            try {
                Directory.Delete(_dir, recursive: true);
            } catch (IOException) {
            }
        }

        [SetUp]
        public void ClearEvents() {
            // An optics change also clears the flip confirmation (safety): start each test unconfirmed,
            // with no completed flip on record.
            _store.UpdateSafetyPolicies(current => current with { FirstFlipConfirmed = false, FirstFlipCompletedUtc = null, FirstFlipPierSideChanged = false });
            _ws.Clear();
        }

        private async Task<string> Tag(string route) {
            using var get = await _http.GetAsync(new Uri($"/api/v1/profile/{route}", UriKind.Relative));
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            return get.Headers.ETag!.ToString();
        }

        /// <summary>Runs a daemon write and checks it published <paramref name="route"/> once, nothing else
        /// unless listed, and moved the section's tag.</summary>
        private async Task AssertPublishes(string route, Action write, params string[] alsoPublished) {
            var before = await Tag(route);
            _ws.Clear();
            write();
            Assert.That(_ws.Sections(), Is.EquivalentTo(new[] { route }.Concat(alsoPublished)),
                $"a daemon write of {route} publishes profile.updated naming it, once");
            Assert.That(await Tag(route), Is.Not.EqualTo(before), $"the {route} ETag moves with the daemon write");
        }

        [Test]
        public async Task A_time_sync_location_publishes_site_and_moves_its_ETag() {
            var svc = new TimeSyncService(NullLogger<TimeSyncService>.Instance, new FakeClockSetter(), _store);
            var lat = Math.Round(_store.GetSiteSettings().LatitudeDeg + 1.5, 2) % 80;
            await AssertPublishes("site", () => svc.PushAsync(new TimeSyncPushRequestDto(
                "gps-client", DateTimeOffset.UtcNow, new TimeSyncLocationDto(lat, -77.04, 120), Trust: "high"),
                CancellationToken.None).GetAwaiter().GetResult());
        }

        [Test]
        public async Task A_plate_solve_focal_length_learning_publishes_optics_and_moves_its_ETag() {
            _store.PutOpticsSettings(_store.GetOpticsSettings() with { FocalLengthMm = 530, ReducerFactor = 1, PixelSizeUm = 3.76 });
            var sut = new PlateSolveService(Mock.Of<IProfileService>(), Mock.Of<IPlateSolverFactory>(), _store);
            var parameter = new PlateSolveParameter { FocalLength = 530, PixelSize = 3.76, Binning = 1 };
            await AssertPublishes("optics", () => sut.LearnFocalLength(parameter, new PlateSolveResult { Success = true, Pixscale = 1.0 }));
        }

        [Test]
        public async Task A_mount_connect_optics_fill_publishes_optics_and_moves_its_ETag() {
            _store.PutOpticsSettings(_store.GetOpticsSettings() with { FocalLengthMm = 0, ApertureMm = 0 });
            using var mount = new TelescopeService(profileStore: _store);
            await AssertPublishes("optics", () => mount.MaybeAutoPopulateOptics(new TelescopeCapabilitiesDto(
                CanSlew: true, CanSync: true, CanPark: true, CanUnpark: true, CanSetTracking: true, CanPulseGuide: true,
                CanFindHome: true, SupportedSiderealRates: [], FocalLengthMm: 480, ApertureDiameterMm: 80)));
        }

        [Test]
        public async Task A_locked_camera_electronics_update_publishes_camera_electronics() {
            // The store call CameraService.MaybeAutoPopulateElectronics makes on connect (the service itself
            // needs a live camera; this checks the store write it goes through).
            await AssertPublishes("camera-electronics", () => _store.UpdateCameraElectronics(current =>
                current with { SensorName = current.SensorName + "x" }));
        }

        [Test]
        public async Task A_locked_safety_update_publishes_safety() {
            // The store call MeridianFlipExecutor makes to record a completed flip (SetUp left none).
            await AssertPublishes("safety", () => _store.UpdateSafetyPolicies(current => current with { FirstFlipCompletedUtc = DateTimeOffset.UtcNow }));
        }

        [Test]
        public async Task A_whole_safety_put_outside_the_routes_publishes_safety() {
            // The store calls SequencerService.AutoFlats.PersistCaptureDefaultQuietly makes: a get, then a put of
            // the whole section.
            var policy = _store.GetSafetyPolicies();
            await AssertPublishes("safety", () => _store.PutSafetyPolicies(policy with {
                CalibrationCaptureDefault = policy.CalibrationCaptureDefault == "always" ? "never" : "always",
            }));
        }

        [Test]
        public async Task A_locked_phd2_update_publishes_phd2() {
            // The store call GuiderService.CameraReconcile makes for the guide optics and pixel size.
            await AssertPublishes("phd2", () => _store.UpdatePhd2Settings(now => now with { GuidePixelSize = now.GuidePixelSize + 0.5 }));
        }

        [Test]
        public async Task Two_section_puts_in_one_daemon_step_publish_phd2_and_guider_advanced() {
            // The store calls GuiderService.GuidingAssistant makes: both blocks, each put only when it changed.
            var phd2 = _store.GetPhd2Settings();
            var advanced = _store.GetGuiderAdvancedSettings();
            var before = await Tag("guider-advanced");
            await AssertPublishes("phd2", () => {
                _store.PutPhd2Settings(phd2 with { GuideExposureMs = phd2.GuideExposureMs + 500 });
                _store.PutGuiderAdvancedSettings(advanced with { DecAlgorithm = advanced.DecAlgorithm == "Resist Switch" ? "Lowpass" : "Resist Switch" });
            }, "guider-advanced");
            Assert.That(await Tag("guider-advanced"), Is.Not.EqualTo(before));
        }

        [Test]
        public void A_section_mapped_under_two_route_names_is_refused_at_startup() {
            var section = ProfileSections.All[0];
            static RouteEndpoint At(string path, object meta) => new(_ => System.Threading.Tasks.Task.CompletedTask,
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse(path), 0, new EndpointMetadataCollection(meta), path);
            var source = new DefaultEndpointDataSource(At("/api/v1/profile/alpha", section), At("/api/v1/profile/beta", section));
            Assert.Throws<InvalidOperationException>(() => ProfileEndpoints.SectionRoutes([source]));
            var same = new DefaultEndpointDataSource(At("/api/v1/profile/alpha", section), At("/api/v1/profile/alpha/rearm", section));
            Assert.That(ProfileEndpoints.SectionRoutes([same])[section], Is.EqualTo("alpha"));
        }

        [Test]
        public async Task Every_client_section_written_by_the_daemon_publishes_its_route_once() {
            // The table walk: any section a client can load (WithSection), whoever writes it outside the routes
            // (the filter-wheel label mirror, the dome follower, storage configure, ...).
            var routes = ProfileEndpoints.SectionRoutes(((IEndpointRouteBuilder)_app).DataSources);
            Assert.That(routes, Has.Count.GreaterThanOrEqualTo(24));
            var json = _app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            var failures = new List<string>();
            foreach (var (section, route) in routes) {
                using var get = await _http.GetAsync(new Uri($"/api/v1/profile/{route}{SubPath(section)}", UriKind.Relative));
                var before = get.Headers.ETag!.ToString();
                var body = JsonNode.Parse(await get.Content.ReadAsStringAsync())!.AsObject();
                ProfileSectionVersionTest.Change(section.Name, body);
                var value = body.Deserialize(section.DtoType, json);
                var put = typeof(IProfileStore).GetMethods()
                    .Single(m => m.Name.StartsWith("Put", StringComparison.Ordinal)
                        && m.GetParameters() is [var p] && Nullable.GetUnderlyingType(p.ParameterType) is null
                        && p.ParameterType == section.DtoType);
                _ws.Clear();
                put.Invoke(_store, [value]);
                var seen = _ws.Sections();
                if (!seen.SequenceEqual([route])) {
                    failures.Add($"{section.Name}: published [{string.Join(", ", seen)}], want [{route}]");
                }
                using var reread = await _http.GetAsync(new Uri($"/api/v1/profile/{route}{SubPath(section)}", UriKind.Relative));
                if (reread.Headers.ETag!.ToString() == before) {
                    failures.Add($"{section.Name}: the ETag did not move");
                }
            }
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }

        private static string SubPath(ProfileSection section) => section.Name switch {
            "FilterWheelLabels" => "/labels",
            "FilterWheelPolicy" => "/policy",
            _ => "",
        };

        [Test]
        public void A_daemon_only_section_write_publishes_nothing() {
            // Calibration records and the identity record have no route and no client copy (#1644).
            _store.PutFocusCalibration(null);
            _ws.Clear();
            var state = _store.GetCalibrationState();
            _store.PutCalibrationState(state with { DarkLibrary = state.DarkLibrary with { Valid = !state.DarkLibrary.Valid } });
            Assert.That(_ws.Sections(), Is.Empty);
        }

        [Test]
        public void A_daemon_write_that_changes_nothing_publishes_nothing() {
            _store.PutSiteSettings(_store.GetSiteSettings());
            _store.UpdateOpticsSettings(_ => null);
            Assert.That(_ws.Sections(), Is.Empty);
        }

        [Test]
        public async Task A_route_write_publishes_its_section_exactly_once() {
            var etag = await Tag("site");
            var site = _store.GetSiteSettings();
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri("/api/v1/profile/site", UriKind.Relative)) {
                Content = JsonContent(site with { BortleClass = site.BortleClass == 4 ? 5 : 4 }),
            };
            request.Headers.TryAddWithoutValidation("If-Match", etag);
            using var put = await _http.SendAsync(request);
            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(Seen(), Is.EqualTo("site"));
        }

        [Test]
        public async Task A_route_write_with_no_IfMatch_is_refused_and_publishes_nothing() {
            var etag = await Tag("site");
            var site = _store.GetSiteSettings();
            _ws.Clear();
            using var content = JsonContent(site with { BortleClass = site.BortleClass == 4 ? 5 : 4 });
            using var put = await _http.PutAsync(new Uri("/api/v1/profile/site", UriKind.Relative), content);
            Assert.That((int)put.StatusCode, Is.EqualTo(StatusCodes.Status428PreconditionRequired));
            Assert.That(Seen(), Is.Empty, "a refused write publishes no profile.updated");
            Assert.That(_store.GetSiteSettings(), Is.EqualTo(site), "nothing is written");
            Assert.That(await Tag("site"), Is.EqualTo(etag));
        }

        [TestCase(90.0)]
        [TestCase(0.0)]
        public async Task A_safety_PUT_with_a_clearance_limit_out_of_range_answers_400_and_writes_nothing(double limit) {
            // #1589 — the route itself refuses the limit, on or off, before the store sees the record.
            var etag = await Tag("safety");
            var safety = _store.GetSafetyPolicies();
            _ws.Clear();
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri("/api/v1/profile/safety", UriKind.Relative)) {
                Content = JsonContent(safety with { ClearanceMaxAltitudeDeg = limit, ResumeDelayMin = safety.ResumeDelayMin + 1 }),
            };
            request.Headers.TryAddWithoutValidation("If-Match", etag);
            using var put = await _http.SendAsync(request);
            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(put.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
            Assert.That(_store.GetSafetyPolicies(), Is.EqualTo(safety), "nothing is written");
            Assert.That(Seen(), Is.Empty, "a refused write publishes no profile.updated");
        }

        [Test]
        public async Task An_optics_route_write_that_clears_the_flip_confirmation_also_publishes_safety() {
            // The store clears first_flip_confirmed on a real optics change: the safety tag moves too.
            _store.UpdateSafetyPolicies(current => current with { FirstFlipConfirmed = true });
            var safety = await Tag("safety");
            _ws.Clear();
            var optics = _store.GetOpticsSettings();
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri("/api/v1/profile/optics", UriKind.Relative)) {
                Content = JsonContent(optics with { ApertureMm = optics.ApertureMm + 10 }),
            };
            request.Headers.TryAddWithoutValidation("If-Match", await Tag("optics"));
            _ws.Clear();
            using var put = await _http.SendAsync(request);
            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(Seen(), Is.EqualTo("optics,safety"));
            Assert.That(await Tag("safety"), Is.Not.EqualTo(safety));
        }

        [Test]
        public async Task The_first_flip_rearm_answers_the_safety_ETag_and_publishes_once() {
            _store.UpdateSafetyPolicies(current => current with { FirstFlipConfirmed = true });
            _ws.Clear();
            using var post = await _http.PostAsync(new Uri("/api/v1/profile/safety/first-flip/rearm", UriKind.Relative), null);
            Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(post.Headers.ETag?.ToString(), Is.EqualTo(await Tag("safety")), "the re-arm answers the stored section's tag");
            Assert.That(Seen(), Is.EqualTo("safety"));
        }

        [Test]
        public async Task The_first_flip_rearm_clears_the_completed_flip_record() {
            _store.UpdateSafetyPolicies(current => current with { FirstFlipCompletedUtc = DateTimeOffset.UtcNow, FirstFlipPierSideChanged = true });
            using var post = await _http.PostAsync(new Uri("/api/v1/profile/safety/first-flip/rearm", UriKind.Relative), null);
            Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(_store.GetSafetyPolicies().FirstFlipCompletedUtc, Is.Null, "re-arm asks for a new watched flip");
            Assert.That(_store.GetSafetyPolicies().FirstFlipPierSideChanged, Is.False);
        }

        [Test]
        public async Task Confirming_with_no_completed_flip_is_refused_first_flip_not_completed() {
            using var post = await _http.PostAsync(new Uri("/api/v1/profile/safety/first-flip/confirm", UriKind.Relative), null);
            Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            using var json = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
            Assert.That(json.RootElement.GetProperty("reason").GetString(), Is.EqualTo("first-flip-not-completed"));
            Assert.That(_store.GetSafetyPolicies().FirstFlipConfirmed, Is.False);
            Assert.That(_ws.Sections(), Is.Empty, "a refusal writes nothing");
        }

        [Test]
        public async Task Confirming_a_completed_flip_sets_the_flag_answers_the_safety_ETag_and_publishes_once() {
            var completed = new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);
            _store.UpdateSafetyPolicies(current => current with { FirstFlipCompletedUtc = completed, FirstFlipPierSideChanged = true });
            _ws.Clear();
            using var post = await _http.PostAsync(new Uri("/api/v1/profile/safety/first-flip/confirm", UriKind.Relative), null);
            Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var stored = _store.GetSafetyPolicies();
            Assert.That(stored.FirstFlipConfirmed, Is.True);
            Assert.That(stored.FirstFlipCompletedUtc, Is.EqualTo(completed), "the confirmed flip stays on record");
            Assert.That(post.Headers.ETag?.ToString(), Is.EqualTo(await Tag("safety")), "the confirm answers the stored section's tag");
            Assert.That(Seen(), Is.EqualTo("safety"));

            // Confirming again changes nothing and still answers 200.
            _ws.Clear();
            using var again = await _http.PostAsync(new Uri("/api/v1/profile/safety/first-flip/confirm", UriKind.Relative), null);
            Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(_store.GetSafetyPolicies(), Is.EqualTo(stored));
        }

        [Test]
        public async Task A_profile_select_apply_publishes_nothing_and_later_daemon_writes_still_do() {
            var snap = ProfileStoreSnapshot.Capture(_store);
            var site = snap.Site with { SiteName = snap.Site.SiteName + " (other rig)" };
            ProfileStoreSnapshot.Apply(_store, snap with { Site = site });
            Assert.That(_ws.Sections(), Is.Empty, "a whole-profile apply is a select, not a section edit");
            await AssertPublishes("site", () => _store.PutSiteSettings(_store.GetSiteSettings() with { SiteName = "Back garden" }));
        }

        [Test]
        public async Task A_daemon_write_that_outlives_its_route_request_still_publishes() {
            // A task started inside a route write carries the request's async context; once the route has
            // answered, its own write is the daemon's and must not be taken for the route's.
            var release = new TaskCompletionSource();
            Task later = Task.CompletedTask;
            var http = new DefaultHttpContext();
            http.Request.Method = "PUT";
            http.Request.Path = "/api/v1/profile/site";
            await ProfileEndpoints.PublishUpdatedAfterWrite(http, () => {
                later = Task.Run(async () => {
                    await release.Task;
                    _store.PutSiteSettings(_store.GetSiteSettings() with { ObserverName = Guid.NewGuid().ToString("N") });
                });
                return new ValueTask<object?>(Results.Ok());
            }, _ws);
            release.SetResult();
            await later;
            Assert.That(Seen(), Is.EqualTo("site,site"), "the route's event, then the daemon's");
        }

        private sealed class BrokenWs(bool hang) : IWsBroadcaster {
            public int Calls;
            public long CurrentSequence => 0;
            public Task PublishAsync(OpenAstroAra.Server.Contracts.WsEvents.WsEvent eventType, JsonElement payload, CancellationToken ct) {
                Interlocked.Increment(ref Calls);
                return hang ? new TaskCompletionSource().Task : throw new InvalidOperationException("broadcaster down");
            }
        }

        [Test]
        public void A_daemon_write_returns_when_the_broadcaster_throws_or_never_answers() {
            // The writer is a hardware path (camera connect, the flip executor): a dead or stuck broadcaster
            // must not fail or hold its store write.
            foreach (var hang in new[] { false, true }) {
                var store = new InMemoryProfileStore();
                var ws = new BrokenWs(hang);
                _ = new ProfileSectionChangePublisher(store, ws,
                    new Dictionary<ProfileSection, string> { [ProfileSections.Site] = "site" });
                var write = Task.Run(() => store.PutSiteSettings(store.GetSiteSettings() with { SiteName = "Field" }));
                Assert.That(write.Wait(TimeSpan.FromSeconds(5)), Is.True, $"the store write returns (hang: {hang})");
                Assert.That(store.GetSiteSettings().SiteName, Is.EqualTo("Field"));
                Assert.That(ws.Calls, Is.EqualTo(1), "the publish was attempted");
            }
        }

        private static readonly JsonSerializerOptions WireJson = new(AraJsonSerializerContext.Default.Options) {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

        private static StringContent JsonContent<T>(T value) =>
            new(JsonSerializer.Serialize(value, WireJson), Encoding.UTF8, "application/json");

        /// <summary>The published sections, sorted, comma-joined.</summary>
        private string Seen() => string.Join(",", _ws.Sections().Order(StringComparer.Ordinal));

        private sealed class FakeClockSetter : ISystemClockSetter {
            public bool TrySet(DateTimeOffset utc) => true;
        }
    }
}
