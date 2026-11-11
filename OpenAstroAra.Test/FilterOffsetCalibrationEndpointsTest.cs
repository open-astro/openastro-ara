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
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem.Autofocus;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1675 — the focus offset calibration routes end to end over loopback HTTP: a fake motorised
    /// wheel and focuser, and an autofocus executor that scripts a best position (or a failure) per filter and
    /// writes the run record the way the real sweep does.</summary>
    [TestFixture]
    [Category("IO")] // loopback HTTP: not part of the quick unit run
    public class FilterOffsetCalibrationEndpointsTest {

        private const string Base = "/api/v1/equipment/filterwheel/offsets";
        private static readonly string[] LrgbThenL = ["L", "R", "G", "B", "L"];
        private static readonly int[] Untouched4 = [0, 10, 20, 30];
        private static readonly int[] Untouched2 = [0, 10];

        /// <summary>The rig behind the routes. Script: per slot, the best positions its autofocus runs return
        /// in order (null = the run fails).</summary>
        private sealed class Rig : IAsyncDisposable {
            public string[] Names = ["L", "R", "G", "B"];
            public Dictionary<int, Queue<int?>> Script = new();
            public int? BlockOnSlot;
            public int CurrentSlot = 3;
            public int FocuserPosition = 5000;
            public bool Manual;
            public bool Absolute = true;
            public bool CameraConnected = true;
            public string AfFilter = "L";
            public OpticsSettingsDto? Optics;
            public readonly List<(int Slot, string? Filter, string? Trigger)> AutofocusRuns = new();
            public readonly AutofocusRunTracker Tracker = new();
            public readonly ActiveRunSessionRegistry Runs = new();
            public readonly InMemoryProfileStore Store = new();
            public readonly OpenAstroAra.Profile.Profile Profile = new();
            public readonly Mock<IBahtinovFocusService> Bahtinov = new();
            public readonly Mock<IGuideFocusService> GuideFocus = new();
            public readonly TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public WebApplication App = null!;
            public HttpClient Http = null!;
            public FilterOffsetCalibrationService Service = null!;

            public void Seed(params int?[][] perSlot) {
                for (var i = 0; i < perSlot.Length; i++) {
                    Script[i] = new Queue<int?>(perSlot[i]);
                }
            }

            public async Task StartAsync() {
                Store.PutAutofocusSettings(Store.GetAutofocusSettings() with { AfFilter = AfFilter });
                if (Optics is not null) {
                    Store.PutOpticsSettings(Optics);
                }
                var slots = Names.Select((n, i) => new FilterSlotDto(i, n, 0)).ToList();
                var wheel = new Mock<IFilterWheelService>();
                wheel.Setup(w => w.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
                    new FilterWheelDto("fw", "Wheel", EquipmentConnectionState.Connected, new FilterWheelStateDto("idle", CurrentSlot), slots, Manual));
                var wheelMediator = new Mock<IFilterWheelMediator>();
                wheelMediator.Setup(w => w.ChangeFilter(It.IsAny<FilterInfo>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((FilterInfo f, IProgress<ApplicationStatus>? _, CancellationToken _) => { CurrentSlot = f.Position; return f; });
                var focuser = new Mock<IFocuserService>();
                focuser.Setup(f => f.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
                    new FocuserDto("foc", "Focuser", EquipmentConnectionState.Connected,
                        new FocuserCapabilitiesDto(0, 100_000, 2.0, false, Absolute), new FocuserStateDto("idle", FocuserPosition, null, false)));
                var focuserMediator = new Mock<IFocuserMediator>();
                focuserMediator.Setup(f => f.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((int p, CancellationToken _) => FocuserPosition = p);
                var camera = new Mock<ICameraService>();
                camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
                    new CameraDto("cam", "Camera", CameraConnected ? EquipmentConnectionState.Connected : EquipmentConnectionState.Disconnected,
                        null, new CameraStateDto("idle", null, null, false, null)));
                var profile = new Mock<IProfileService>();
                profile.SetupGet(p => p.ActiveProfile).Returns(Profile);

                var builder = WebApplication.CreateSlimBuilder();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Services.AddSingleton<IAutofocusExecutor>(new ScriptedAutofocus(this));
                builder.Services.AddSingleton(Tracker);
                builder.Services.AddSingleton<IBatchJobService>(new InMemoryBatchJobService(null));
                builder.Services.AddSingleton(camera.Object);
                builder.Services.AddSingleton(focuser.Object);
                builder.Services.AddSingleton(focuserMediator.Object);
                builder.Services.AddSingleton(wheel.Object);
                builder.Services.AddSingleton(wheelMediator.Object);
                builder.Services.AddSingleton<IProfileStore>(Store);
                builder.Services.AddSingleton(profile.Object);
                builder.Services.AddSingleton(Runs);
                builder.Services.AddSingleton(Bahtinov.Object);
                builder.Services.AddSingleton(GuideFocus.Object);
                builder.Services.AddSingleton<FilterOffsetCalibrationService>();
                builder.Services.ConfigureHttpJsonOptions(opts => {
                    opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                    opts.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower; // as Program.cs
                });
                App = builder.Build();
                EquipmentEndpoints.MapOffsetCalibration(App.MapGroup("/api/v1/equipment/filterwheel"));
                await App.StartAsync();
                Http = new HttpClient { BaseAddress = new Uri(App.Urls.First()) };
                Service = App.Services.GetRequiredService<FilterOffsetCalibrationService>();
            }

            public async Task<HttpResponseMessage> CalibrateAsync(params int[] positions) =>
                await Http.PostAsJsonAsync<object>(new Uri(Base + "/calibrate", UriKind.Relative),
                    positions.Length == 0 ? new { } : new { positions });

            public async Task<int> PostAsync(string action) {
                using var response = await Http.PostAsync(new Uri($"{Base}/calibration/{action}", UriKind.Relative), null);
                return (int)response.StatusCode;
            }

            public async Task<JsonElement?> GetAsync() {
                using var response = await Http.GetAsync(new Uri(Base + "/calibration", UriKind.Relative));
                if ((int)response.StatusCode == 404) {
                    return null;
                }
                Assert.That((int)response.StatusCode, Is.EqualTo(200));
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
            }

            public async Task<JsonElement> WaitForEndAsync() {
                for (var i = 0; i < 250; i++) {
                    var state = await GetAsync();
                    if (state?.GetProperty("state").GetString() != "running") {
                        return state!.Value;
                    }
                    await Task.Delay(20);
                }
                Assert.Fail("the calibration never ended");
                return default;
            }

            public async ValueTask DisposeAsync() {
                Http?.Dispose();
                if (App is not null) {
                    await App.StopAsync();
                    await App.DisposeAsync();
                }
                Profile.Dispose();
            }
        }

        /// <summary>Writes the run record the way AutofocusSweepService does (Begin with the filter in place,
        /// then Complete with the fit, or Fail), and lands the focuser on the scripted best.</summary>
        private sealed class ScriptedAutofocus(Rig rig) : IAutofocusExecutor {
            public async Task<bool> RunAutofocusAsync(IProgress<ApplicationStatus> progress, CancellationToken token) {
                var slot = rig.CurrentSlot;
                using var runCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                rig.Tracker.Begin("classic", rig.FocuserPosition, 9, rig.Names[slot], null, runCts);
                var record = rig.Tracker.Snapshot();
                rig.AutofocusRuns.Add((slot, record.Filter, record.Trigger));
                if (rig.BlockOnSlot == slot) {
                    rig.Blocked.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                if (rig.Script[slot].Dequeue() is int best) {
                    rig.FocuserPosition = best;
                    rig.Tracker.SetFit(new FocusCurveFitResult { BestPosition = best, PredictedHfr = 2, RSquared = 0.97, IsUsable = true, WithinSampledRange = true },
                        best - 100, best + 100);
                    rig.Tracker.Complete(best, 2.0, 40);
                    return true;
                }
                rig.Tracker.Fail("only 2 of 9 probes had measurable stars", null);
                return false;
            }
        }

        private static JsonElement Row(JsonElement calibration, int position) =>
            calibration.GetProperty("filters").EnumerateArray().Single(f => f.GetProperty("position").GetInt32() == position);

        private static int? IntOrNull(JsonElement e, string name) =>
            e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetInt32();

        private static void SeedProfileOffsets(Rig rig, params int[] offsets) {
            for (var i = 0; i < offsets.Length; i++) {
                rig.Profile.FilterWheelSettings.FilterWheelFilters.Add(new FilterInfo(rig.Names[i], offsets[i], (short)i));
            }
        }

        [Test]
        public async Task Offsets_are_measured_against_the_mean_of_both_reference_runs() {
            await using var rig = new Rig();
            rig.Seed([1000, 1010], [1050], [990], [1100]);
            await rig.StartAsync();

            using var started = await rig.CalibrateAsync(0, 1, 2, 3);
            Assert.That((int)started.StatusCode, Is.EqualTo(202));
            var job = await started.Content.ReadFromJsonAsync<JsonElement>();
            Assert.That(job.GetProperty("job_type").GetString(), Is.EqualTo("filter_offset_calibration"));

            var result = await rig.WaitForEndAsync();
            Assert.That(result.GetProperty("state").GetString(), Is.EqualTo("complete"));
            Assert.That(result.GetProperty("reference_position").GetInt32(), Is.EqualTo(0));
            Assert.That(result.GetProperty("reference_first_position").GetInt32(), Is.EqualTo(1000));
            Assert.That(result.GetProperty("reference_second_position").GetInt32(), Is.EqualTo(1010));
            Assert.That(IntOrNull(Row(result, 0), "offset"), Is.EqualTo(0), "the reference stays 0");
            Assert.That(IntOrNull(Row(result, 1), "offset"), Is.EqualTo(45), "1050 − 1005");
            Assert.That(IntOrNull(Row(result, 2), "offset"), Is.EqualTo(-15), "990 − 1005");
            Assert.That(IntOrNull(Row(result, 3), "offset"), Is.EqualTo(95), "1100 − 1005");
            Assert.That(Row(result, 1).GetProperty("r_squared").GetDouble(), Is.EqualTo(0.97));
            Assert.That(rig.CurrentSlot, Is.EqualTo(0), "the reference filter is back in place");
            Assert.That(rig.FocuserPosition, Is.EqualTo(1010), "the focuser sits at the reference's latest best");
        }

        [Test]
        public async Task Each_autofocus_is_recorded_as_an_offset_calibration_run_with_its_filter() {
            await using var rig = new Rig();
            rig.Seed([1000, 1000], [1050], [990], [1100]);
            await rig.StartAsync();

            using var started = await rig.CalibrateAsync(0, 1, 2, 3);
            await rig.WaitForEndAsync();

            Assert.That(rig.AutofocusRuns.Select(r => r.Filter), Is.EqualTo(LrgbThenL));
            Assert.That(rig.AutofocusRuns.Select(r => r.Trigger), Is.All.EqualTo("offset calibration"));
        }

        [TestCase(1000, 1100, true, TestName = "Drift_over_the_CFZ_is_flagged")]
        [TestCase(1000, 1050, false, TestName = "Drift_inside_the_CFZ_is_not_flagged")]
        public async Task Drift_is_checked_against_the_reference_CFZ(int first, int second, bool warned) {
            await using var rig = new Rig {
                // f/8: CFZ ≈ 2.2 µm × 64 = 140.8 µm; at 2 µm/step, half is 35 steps, so the CFZ is 70.
                Optics = new OpticsSettingsDto(FocalLengthMm: 800, ReducerFactor: 1, SensorWidthPx: 0, SensorHeightPx: 0, PixelSizeUm: 0, ApertureMm: 100),
            };
            rig.Seed([first, second], [1050]);
            await rig.StartAsync();

            using var started = await rig.CalibrateAsync(0, 1);
            var result = await rig.WaitForEndAsync();

            Assert.That(result.GetProperty("cfz_steps").GetInt32(), Is.EqualTo(70));
            Assert.That(result.GetProperty("drift_checked").GetBoolean(), Is.True);
            Assert.That(result.GetProperty("drift_warning").GetBoolean(), Is.EqualTo(warned));
            Assert.That(result.GetProperty("state").GetString(), Is.EqualTo("complete"), "a drift warning still completes");
        }

        [Test]
        public async Task Without_optics_or_a_second_reference_the_drift_is_not_checked() {
            await using var rig = new Rig();
            rig.Seed([1000, null], [1050]);
            await rig.StartAsync();

            using var started = await rig.CalibrateAsync(0, 1);
            var result = await rig.WaitForEndAsync();

            Assert.That(result.GetProperty("drift_checked").GetBoolean(), Is.False);
            Assert.That(IntOrNull(Row(result, 1), "offset"), Is.EqualTo(50), "the first reference alone is the baseline");
        }

        [Test]
        public async Task A_failed_filter_keeps_its_previous_offset_after_save() {
            await using var rig = new Rig();
            rig.Seed([1000, 1000], [1040], [null]);
            await rig.StartAsync();
            SeedProfileOffsets(rig, 0, 10, 20);

            using var started = await rig.CalibrateAsync(0, 1, 2);
            var result = await rig.WaitForEndAsync();
            Assert.That(Row(result, 2).GetProperty("status").GetString(), Is.EqualTo("failed"));
            Assert.That(Row(result, 2).GetProperty("failure_reason").GetString(), Does.Contain("measurable stars"));
            Assert.That(Row(result, 2).GetProperty("previous_offset").GetInt32(), Is.EqualTo(20));

            Assert.That(await rig.PostAsync("save"), Is.EqualTo(204));
            var offsets = rig.Profile.FilterWheelSettings.FilterWheelFilters.ToDictionary(f => (int)f.Position, f => f.FocusOffset);
            Assert.That(offsets[1], Is.EqualTo(40), "the measured filter is written");
            Assert.That(offsets[2], Is.EqualTo(20), "the failed filter keeps its offset");
            Assert.That(offsets[0], Is.EqualTo(0));
            Assert.That(await rig.GetAsync(), Is.Null, "Save clears the results");
        }

        [Test]
        public async Task A_reference_that_fails_both_times_fails_the_run_with_a_reason() {
            await using var rig = new Rig();
            rig.Seed([null, null], [1050]);
            await rig.StartAsync();

            using var started = await rig.CalibrateAsync(0, 1);
            var result = await rig.WaitForEndAsync();

            Assert.That(result.GetProperty("state").GetString(), Is.EqualTo("failed"));
            Assert.That(result.GetProperty("reason").GetString(), Does.Contain("reference filter L failed both autofocus runs"));
            Assert.That(Row(result, 1).GetProperty("offset").ValueKind, Is.EqualTo(JsonValueKind.Null), "no offset against nothing");
            Assert.That(Row(result, 0).GetProperty("status").GetString(), Is.EqualTo("failed"));
        }

        [Test]
        public async Task Cancel_mid_run_leaves_the_offsets_untouched_and_the_reference_in_place() {
            await using var rig = new Rig { BlockOnSlot = 2 };
            rig.Seed([1000, 1000], [1040], [990], [1100]);
            await rig.StartAsync();
            SeedProfileOffsets(rig, 0, 10, 20, 30);

            using var started = await rig.CalibrateAsync(0, 1, 2, 3);
            await rig.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(await rig.PostAsync("cancel"), Is.EqualTo(204));
            var result = await rig.WaitForEndAsync();

            Assert.That(result.GetProperty("state").GetString(), Is.EqualTo("cancelled"));
            Assert.That(Row(result, 3).GetProperty("status").GetString(), Is.EqualTo("skipped"));
            Assert.That(rig.Profile.FilterWheelSettings.FilterWheelFilters.Select(f => f.FocusOffset), Is.EqualTo(Untouched4));
            Assert.That(rig.CurrentSlot, Is.EqualTo(0), "the reference filter is back in place");
            Assert.That(rig.FocuserPosition, Is.EqualTo(1000), "at the reference's best");
            Assert.That(await rig.PostAsync("cancel"), Is.EqualTo(409), "nothing left to cancel");
        }

        [TestCase("plan", "a plan is running")]
        [TestCase("autofocus", "an autofocus run is in progress")]
        [TestCase("bahtinov", "the Bahtinov focus readout is running")]
        [TestCase("guide_focus", "guide-camera focus is running")]
        [TestCase("calibration", "an offset calibration is already running")]
        [TestCase("manual_wheel", "motorised filter wheel")]
        [TestCase("relative_focuser", "absolute focuser")]
        [TestCase("no_camera", "Connect the camera")]
        [TestCase("nothing_picked", "besides the reference")]
        public async Task A_precondition_that_fails_is_a_409_with_the_reason(string precondition, string reason) {
            await using var rig = new Rig {
                Manual = precondition == "manual_wheel",
                Absolute = precondition != "relative_focuser",
                CameraConnected = precondition != "no_camera",
                BlockOnSlot = 1,
            };
            rig.Seed([1000, 1000], [1040]);
            await rig.StartAsync();
            using var runCts = new CancellationTokenSource();
            switch (precondition) {
                case "plan": rig.Runs.Enter(Guid.NewGuid()); break;
                case "autofocus": rig.Tracker.Begin("classic", 0, 9, "L", null, runCts); break;
                case "bahtinov": rig.Bahtinov.SetupGet(b => b.IsActive).Returns(true); break;
                case "guide_focus": rig.GuideFocus.SetupGet(g => g.IsActive).Returns(true); break;
                case "calibration":
                    using (var first = await rig.CalibrateAsync(0, 1)) {
                        Assert.That((int)first.StatusCode, Is.EqualTo(202));
                    }
                    await rig.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    break;
            }

            using var response = precondition == "nothing_picked" ? await rig.CalibrateAsync(0) : await rig.CalibrateAsync(0, 1);
            Assert.That((int)response.StatusCode, Is.EqualTo(409));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain(reason));
            if (precondition == "calibration") {
                Assert.That(await rig.PostAsync("cancel"), Is.EqualTo(204));
                await rig.WaitForEndAsync();
            }
        }

        [Test]
        public async Task Discard_clears_the_results() {
            await using var rig = new Rig();
            rig.Seed([1000, 1000], [1040]);
            await rig.StartAsync();
            SeedProfileOffsets(rig, 0, 10);

            using var started = await rig.CalibrateAsync(0, 1);
            await rig.WaitForEndAsync();

            Assert.That(await rig.PostAsync("discard"), Is.EqualTo(204));
            Assert.That(await rig.GetAsync(), Is.Null);
            Assert.That(rig.Profile.FilterWheelSettings.FilterWheelFilters.Select(f => f.FocusOffset), Is.EqualTo(Untouched2));
            Assert.That(await rig.PostAsync("discard"), Is.EqualTo(409), "nothing left to discard");
            Assert.That(await rig.PostAsync("save"), Is.EqualTo(409), "nothing left to save");
        }

        [TestCase(new[] { "Ha", "Dark", "OIII", "" }, "", 0, new[] { 0, 2 }, TestName = "Default_picks_skip_dark_and_unnamed_slots_and_the_lowest_pick_is_the_reference")]
        [TestCase(new[] { "L", "R", "OIII" }, "OIII", 2, new[] { 0, 1, 2 }, TestName = "The_profiles_autofocus_filter_is_the_reference")]
        [TestCase(new[] { "R", "lum", "G" }, "", 1, new[] { 0, 1, 2 }, TestName = "Without_an_autofocus_filter_L_or_Lum_is_the_reference")]
        public async Task Default_picks_and_the_reference_choice(string[] names, string afFilter, int reference, int[] measured) {
            await using var rig = new Rig { Names = names, AfFilter = afFilter };
            rig.Seed([1000, 1000], [1000, 1000], [1000, 1000], [1000, 1000]);
            await rig.StartAsync();

            using var started = await rig.CalibrateAsync(); // no positions: the default picks
            Assert.That((int)started.StatusCode, Is.EqualTo(202));
            var result = await rig.WaitForEndAsync();

            Assert.That(result.GetProperty("reference_position").GetInt32(), Is.EqualTo(reference));
            Assert.That(result.GetProperty("filters").EnumerateArray().Select(f => f.GetProperty("position").GetInt32()), Is.EqualTo(measured));
        }

        [Test]
        public async Task Autofocus_is_refused_while_a_calibration_runs() {
            await using var rig = new Rig { BlockOnSlot = 1 };
            rig.Seed([1000, 1000], [1040]);
            await rig.StartAsync();
            using var started = await rig.CalibrateAsync(0, 1);
            await rig.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var jobs = new Mock<IBatchJobService>(MockBehavior.Strict);
            var result = EquipmentEndpoints.RunAutofocus(new Mock<IAutofocusExecutor>().Object, jobs.Object, rig.Store, rig.Tracker,
                rig.Bahtinov.Object, rig.Service);

            Assert.That((result as ProblemHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
            jobs.VerifyNoOtherCalls();
            Assert.That(await rig.PostAsync("cancel"), Is.EqualTo(204));
            await rig.WaitForEndAsync();
        }
    }
}
