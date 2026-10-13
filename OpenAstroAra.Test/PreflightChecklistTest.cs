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
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§58.14 — the pre-sleep checklist's grading rules, one per item, with no rig.</summary>
    [TestFixture]
    public class PreflightChecklistTest {

        private static readonly string[] D50 = { "d50" };
        private static readonly string[] OneRun = { "M81 night" };
        private static readonly TelescopeStateDto Tracking = new("tracking", 5.0, 40.0, true, false, false);
        private static readonly CameraStateDto Cold = new("idle", -10.2, 45, true, null, CoolerSetpointC: -10);

        private static TelescopeDto Mount(EquipmentConnectionState s = EquipmentConnectionState.Connected, TelescopeStateDto? rt = null)
            => new("m", "Mount", s, null, rt ?? Tracking);

        private static CameraDto Camera(EquipmentConnectionState s = EquipmentConnectionState.Connected, CameraStateDto? rt = null, bool cooler = true)
            => new("c", "Cam", s, new CameraCapabilitiesDto(100, 100, 3.76, false, true, false, 0, 0, 0, 0, 1, 1, 1, 1, 0.001, 3600, HasCooler: cooler), rt ?? Cold);

        [Test]
        public void Equipment_needs_the_mount_and_camera_connected() {
            Assert.That(PreflightChecklistService.EquipmentItem(null, null, null, null, null, false, null).Status, Is.EqualTo("fail"));
            var noCam = PreflightChecklistService.EquipmentItem(null, Mount(), null, null, null, false, null);
            Assert.That(noCam.Status, Is.EqualTo("fail"));
            Assert.That(noCam.Detail, Does.Contain("No camera selected"));
            var dropped = PreflightChecklistService.EquipmentItem(Camera(), Mount(EquipmentConnectionState.Disconnected), null, null, null, false, null);
            Assert.That(dropped.Status, Is.EqualTo("fail"));
            Assert.That(dropped.Detail, Does.Contain("Mount disconnected"));
            Assert.That(PreflightChecklistService.EquipmentItem(Camera(), Mount(), null, null, null, false, 0).Status, Is.EqualTo("ok"));
        }

        [Test]
        public void Equipment_warns_on_an_unconnected_optional_device_or_an_unresolved_fault() {
            var focuser = new FocuserDto("f", "Focuser", EquipmentConnectionState.Error, null, new FocuserStateDto("error", null, null, false));
            var item = PreflightChecklistService.EquipmentItem(Camera(), Mount(), focuser, null, null, false, 0);
            Assert.That(item.Status, Is.EqualTo("warn"));
            Assert.That(item.Detail, Does.Contain("Focuser in error"));

            var faults = PreflightChecklistService.EquipmentItem(Camera(), Mount(), null, null, null, false, 2);
            Assert.That(faults.Status, Is.EqualTo("warn"));
            Assert.That(faults.Detail, Does.Contain("2 unresolved faults"));

            // A manual filter wheel is never "connected" to anything: not a warning.
            var manual = new FilterWheelDto("w", "Manual", EquipmentConnectionState.Disconnected, new FilterWheelStateDto("idle", 1), Array.Empty<FilterSlotDto>(), Manual: true);
            Assert.That(PreflightChecklistService.EquipmentItem(Camera(), Mount(), null, manual, null, false, 0).Status, Is.EqualTo("ok"));
        }

        [Test]
        public void Equipment_read_failure_is_unknown_not_ok() {
            Assert.That(PreflightChecklistService.EquipmentItem(Camera(), Mount(), null, null, null, readFailed: true, null).Status, Is.EqualTo("unknown"));
        }

        [Test]
        public void Storage_grades_by_the_profile_thresholds() {
            const long gib = 1024L * 1024 * 1024;
            Assert.That(PreflightChecklistService.StorageItem(new StorageSpaceDto("/d", false, 412 * gib, 1000 * gib), 10, 2).Status, Is.EqualTo("ok"));
            Assert.That(PreflightChecklistService.StorageItem(new StorageSpaceDto("/d", false, 412 * gib, 1000 * gib), 10, 2).Detail, Does.StartWith("412 GB free"));
            Assert.That(PreflightChecklistService.StorageItem(new StorageSpaceDto("/d", false, 9 * gib, 1000 * gib), 10, 2).Status, Is.EqualTo("warn"));
            Assert.That(PreflightChecklistService.StorageItem(new StorageSpaceDto("/d", false, 1 * gib, 1000 * gib), 10, 2).Status, Is.EqualTo("fail"));
            Assert.That(PreflightChecklistService.StorageItem(new StorageSpaceDto("/d", true, null, null), 10, 2).Status, Is.EqualTo("unknown"));
        }

        [Test]
        public void Solver_needs_the_binary_and_a_database_and_says_it_was_not_test_solved() {
            var ok = PreflightChecklistService.SolverItem(new PlateSolveDatabaseStatusDto("/idx", "/idx", 290, D50, "/usr/bin/astap", true));
            Assert.That(ok.Status, Is.EqualTo("ok"));
            Assert.That(ok.Detail, Does.Contain("d50").And.Contain("not test-solved"));
            Assert.That(PreflightChecklistService.SolverItem(new PlateSolveDatabaseStatusDto("/idx", null, 0, Array.Empty<string>(), "/usr/bin/astap", true)).Status, Is.EqualTo("fail"));
            Assert.That(PreflightChecklistService.SolverItem(new PlateSolveDatabaseStatusDto("/idx", "/idx", 290, D50, "/nope", false)).Status, Is.EqualTo("fail"));
            Assert.That(PreflightChecklistService.SolverItem(new PlateSolveDatabaseStatusDto("", null, 0, Array.Empty<string>(), "", false)).Detail, Is.EqualTo("No solver configured."));
        }

        [Test]
        public void Cooler_is_ok_within_a_degree_of_the_setpoint() {
            Assert.That(PreflightChecklistService.CoolerItem(Camera(), false).Status, Is.EqualTo("ok"));
            Assert.That(PreflightChecklistService.CoolerItem(Camera(), false).Detail, Is.EqualTo("At target (-10.0 °C)."));
            var settling = PreflightChecklistService.CoolerItem(Camera(rt: new CameraStateDto("idle", -4.0, 100, true, null, CoolerSetpointC: -10)), false);
            Assert.That(settling.Status, Is.EqualTo("warn"));
            Assert.That(settling.Detail, Does.Contain("still settling"));
            Assert.That(PreflightChecklistService.CoolerItem(Camera(rt: new CameraStateDto("idle", 12.0, 0, false, null)), false).Status, Is.EqualTo("warn"));
            Assert.That(PreflightChecklistService.CoolerItem(Camera(cooler: false), false).Status, Is.EqualTo("ok"));
            Assert.That(PreflightChecklistService.CoolerItem(Camera(rt: new CameraStateDto("idle", -10, 50, true, null, -10, CoolerStateKnown: false)), false).Status, Is.EqualTo("unknown"));
            Assert.That(PreflightChecklistService.CoolerItem(Camera(EquipmentConnectionState.Disconnected), false).Status, Is.EqualTo("unknown"));
            Assert.That(PreflightChecklistService.CoolerItem(null, readFailed: true).Status, Is.EqualTo("unknown"));
        }

        [Test]
        public void Mount_must_be_connected_unparked_and_tracking() {
            Assert.That(PreflightChecklistService.MountItem(Mount(), false).Status, Is.EqualTo("ok"));
            Assert.That(PreflightChecklistService.MountItem(Mount(rt: new TelescopeStateDto("parked", null, null, false, true, false)), false).Status, Is.EqualTo("fail"));
            Assert.That(PreflightChecklistService.MountItem(Mount(rt: new TelescopeStateDto("idle", 5, 40, false, false, false)), false).Status, Is.EqualTo("warn"));
            Assert.That(PreflightChecklistService.MountItem(null, false).Status, Is.EqualTo("fail"));
            Assert.That(PreflightChecklistService.MountItem(null, readFailed: true).Status, Is.EqualTo("unknown"));
        }

        [Test]
        public void Sequence_item_warns_when_nothing_is_running() {
            Assert.That(PreflightChecklistService.SequenceItem(Array.Empty<string>(), false).Status, Is.EqualTo("warn"));
            Assert.That(PreflightChecklistService.SequenceItem(OneRun, false).Detail, Is.EqualTo("M81 night is running."));
            Assert.That(PreflightChecklistService.SequenceItem(Array.Empty<string>(), readFailed: true).Status, Is.EqualTo("unknown"));
        }

        [Test]
        public void Flip_forecast_lands_on_the_meridian_crossing_and_grades_the_altitude() {
            // Site at lon 0: LST at 2026-07-18 00:00 UTC is about 19h40m. A target at RA 21h40m crosses in ~2 h.
            var now = new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero);
            var lst = SiteAstrometry.LocalSiderealTimeDeg(now, 0) / 15.0;
            var ra = (lst + 2.0) % 24.0;

            var high = new Coordinates(ra, 60.0, Epoch.J2000, Coordinates.RAType.Hours);
            var f = PreflightChecklistService.ForecastFlip("M81", high, 45.0, 0.0, 0.0, now, 30, 15)!;
            Assert.That((f.AtUtc - now).TotalHours, Is.EqualTo(2.0 * 0.99727).Within(0.01), "sidereal hours shrink to solar");
            Assert.That(f.AltitudeDeg, Is.EqualTo(75.0).Within(0.1), "alt at transit = 90 - |lat - dec|");
            Assert.That(f.Standing, Is.EqualTo("ok"));

            // The flip point is MinutesAfterMeridian past the meridian: 30 min later, and lower for a low target.
            var low = new Coordinates(ra, -20.0, Epoch.J2000, Coordinates.RAType.Hours);
            var g = PreflightChecklistService.ForecastFlip("NGC 6188", low, 45.0, 0.0, 30.0, now, 30, 15)!;
            Assert.That((g.AtUtc - now).TotalHours, Is.EqualTo(2.5 * 0.99727).Within(0.01));
            Assert.That(g.AltitudeDeg, Is.LessThan(25.0).And.GreaterThan(15.0));
            Assert.That(g.Standing, Is.EqualTo("soft_warning"));

            var sunk = new Coordinates(ra, -40.0, Epoch.J2000, Coordinates.RAType.Hours);
            Assert.That(PreflightChecklistService.ForecastFlip("x", sunk, 45.0, 0.0, 0.0, now, 30, 15)!.Standing, Is.EqualTo("below_floor"));

            // Crossed an hour ago: the next crossing is ~23 h out, past the horizon → not tonight.
            var past = new Coordinates((lst - 1.0 + 24.0) % 24.0, 60.0, Epoch.J2000, Coordinates.RAType.Hours);
            Assert.That(PreflightChecklistService.ForecastFlip("y", past, 45.0, 0.0, 0.0, now, 30, 15), Is.Null);
        }

        [Test]
        public void Unattended_window_prints_the_clock_window_or_defers_to_the_sun() {
            var safety = new InMemoryProfileStore().GetSafetyPolicies();
            Assert.That(PreflightChecklistService.UnattendedWindow(safety), Is.EqualTo(((string?)null, (string?)null, "sun")));
            Assert.That(PreflightChecklistService.UnattendedWindow(safety with { UnattendedWindowStart = "23:00", UnattendedWindowEnd = "6:30" }),
                Is.EqualTo(("23:00", "06:30", "clock")));
            Assert.That(PreflightChecklistService.UnattendedWindow(safety with { UnattendedWindowStart = "23:00", UnattendedWindowEnd = "25:00" }).Source, Is.EqualTo("sun"));
        }

        [Test]
        public void All_good_vetoes_on_a_failure_or_a_flip_below_the_floor_only() {
            var ok = new PreflightItemDto("a", "ok", "A", "");
            var warn = new PreflightItemDto("b", "warn", "B", "");
            var unknown = new PreflightItemDto("c", "unknown", "C", "");
            var fail = new PreflightItemDto("d", "fail", "D", "");
            var at = DateTimeOffset.UtcNow;
            Assert.That(PreflightChecklistService.IsAllGood(new[] { ok, warn, unknown }, new[] { new PreflightFlipDto("t", at, 20, "soft_warning") }), Is.True);
            Assert.That(PreflightChecklistService.IsAllGood(new[] { ok, fail }, Array.Empty<PreflightFlipDto>()), Is.False);
            Assert.That(PreflightChecklistService.IsAllGood(new[] { ok }, new[] { new PreflightFlipDto("t", at, 5, "below_floor") }), Is.False);
        }

        [Test]
        public async Task A_bare_host_answers_with_unknowns_and_fails_never_silently_ok() {
            // No device services wired at all: the equipment/mount items fail (nothing selected), the
            // cooler is unknown, storage reads the real fallback directory, and the sequence warns.
            var store = new InMemoryProfileStore();
            var svc = new PreflightChecklistService(store, fileExists: _ => false) { UtcNow = () => new DateTimeOffset(2026, 7, 18, 4, 0, 0, TimeSpan.Zero) };
            var dto = await svc.BuildAsync(CancellationToken.None);
            var byKey = dto.Items.ToDictionary(i => i.Key, i => i.Status);
            Assert.That(byKey["equipment"], Is.EqualTo("fail"));
            Assert.That(byKey["mount"], Is.EqualTo("fail"));
            Assert.That(byKey["cooler"], Is.EqualTo("unknown"));
            Assert.That(byKey["plate_solver"], Is.EqualTo("fail"));
            Assert.That(byKey["sequence"], Is.EqualTo("warn"));
            Assert.That(dto.AllGood, Is.False);
            Assert.That(dto.UnattendedWindowSource, Is.EqualTo("sun"));
            Assert.That(dto.AlarmDevice, Is.Null);
            Assert.That(dto.Flips, Is.Empty);
        }

        [Test]
        public async Task A_throwing_device_reader_grades_as_unknown() {
            var store = new InMemoryProfileStore();
            var svc = new PreflightChecklistService(store, camera: new ThrowingCamera(), telescope: new ThrowingTelescope(), fileExists: _ => false);
            var dto = await svc.BuildAsync(CancellationToken.None);
            Assert.That(dto.Items.Single(i => i.Key == "equipment").Status, Is.EqualTo("unknown"));
            Assert.That(dto.Items.Single(i => i.Key == "cooler").Status, Is.EqualTo("unknown"));
            Assert.That(dto.Items.Single(i => i.Key == "mount").Status, Is.EqualTo("unknown"));
        }

        private sealed class ThrowingCamera : ICameraService {
            public Task<CameraDto?> GetAsync(CancellationToken ct) => throw new InvalidOperationException("driver dead");
            public bool IsFreeToCapture(object consumer) => true;
            public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task<bool> ForgetAsync(CancellationToken ct) => throw new NotSupportedException();
            public Task<ExposureResponseDto> StartExposureAsync(ExposureRequestDto request, string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task AbortExposureAsync(CancellationToken ct) => throw new NotSupportedException();
            public Task SetCoolerAsync(bool enabled, double? targetTemperatureC, CancellationToken ct) => throw new NotSupportedException();
            public Task SetReadoutModeAsync(int modeIndex, CancellationToken ct) => throw new NotSupportedException();
            public Task StartLiveViewAsync(LiveViewStartRequestDto request, CancellationToken ct) => throw new NotSupportedException();
            public Task StopLiveViewAsync() => throw new NotSupportedException();
            public LiveViewStatusDto GetLiveViewStatus() => throw new NotSupportedException();
            public (ReadOnlyMemory<byte> Jpeg, long Seq, long SessionId)? GetLiveViewFrame() => null;
        }

        private sealed class ThrowingTelescope : ITelescopeService {
            public Task<TelescopeDto?> GetAsync(CancellationToken ct) => throw new InvalidOperationException("driver dead");
            public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task<bool> ForgetAsync(CancellationToken ct) => throw new NotSupportedException();
            public Task<OperationAcceptedDto> SlewAsync(SlewRequestDto request, string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task<OperationAcceptedDto> ParkAsync(ParkRequestDto request, string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task<OperationAcceptedDto> UnparkAsync(string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task<OperationAcceptedDto> FindHomeAsync(string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task SetTrackingAsync(bool enabled, CancellationToken ct) => throw new NotSupportedException();
            public Task MoveAxisAsync(int axis, double rate, CancellationToken ct) => throw new NotSupportedException();
            public Task AbortSlewAsync(CancellationToken ct) => throw new NotSupportedException();
            public bool HasCleanReadSinceConnect => false;
        }
    }

    /// <summary>§58.14 — <c>GET /api/v1/preflight/today</c> over a real Kestrel host, snake_case wire shape.</summary>
    [TestFixture]
    [Category("IO")]
    public class PreflightEndpointsTest {

        private static readonly string[] ItemKeys = { "equipment", "storage", "plate_solver", "cooler", "mount", "sequence" };
        private WebApplication app = null!;
        private HttpClient http = null!;

        [OneTimeSetUp]
        public async Task StartKestrel() {
            var store = new InMemoryProfileStore();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<IProfileStore>(store);
            builder.Services.AddSingleton(new PreflightChecklistService(store, fileExists: _ => false));
            builder.Services.AddProblemDetails();
            builder.Services.ConfigureHttpJsonOptions(opts => {
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            app = builder.Build();
            app.MapPreflightEndpoints();
            await app.StartAsync();
            http = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromMinutes(2) };
        }

        [OneTimeTearDown]
        public async Task StopKestrel() {
            http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }

        [Test]
        public async Task Today_returns_the_checklist_in_snake_case() {
            var res = await http.GetAsync(new Uri("/api/v1/preflight/today", UriKind.Relative));
            Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            Assert.That(root.GetProperty("all_good").GetBoolean(), Is.False);
            Assert.That(root.GetProperty("unattended_window_source").GetString(), Is.EqualTo("sun"));
            var keys = root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ToList();
            Assert.That(keys, Is.EqualTo(ItemKeys));
            Assert.That(root.GetProperty("items")[0].GetProperty("status").GetString(), Is.EqualTo("fail"));
        }
    }
}
