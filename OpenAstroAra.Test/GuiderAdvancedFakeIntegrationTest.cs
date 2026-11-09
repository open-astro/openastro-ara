#region "copyright"
/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Guider;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1364 item 3 — the guider's advanced settings against the bench's <see cref="FakeGuider"/>: the read-back
    /// over the wire, the connect push (multi-star in the profile setup, the runtime groups after it), the
    /// tuning push (runtime groups only), and that a refused read posts no inbox error.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — loopback sockets: not part of the quick unit run
    [Category("bench")]
    public class GuiderAdvancedFakeIntegrationTest {

        private sealed class NoFrames : IPolarAlignFrameFetcher {
            public Task FetchAsync(string host, int rpcPort, string filename, string destinationPath, CancellationToken ct) => Task.CompletedTask;
            public Task<byte[]> FetchLiveFrameJpegAsync(string host, int rpcPort, CancellationToken ct) => throw new System.Net.Http.HttpRequestException("404");
        }

        private sealed class RecordingSink : OpenAstroAra.Core.Utility.Notification.INotifierSink {
            public readonly ConcurrentQueue<string> Messages = new();
            public void Notify(bool isError, string? header, string message) => Messages.Enqueue(message);
        }

        private static readonly string[] RaAlgos = ["Hysteresis", "Predictive PEC"];
        private static readonly string[] DecAlgos = ["Resist Switch", "Lowpass"];
        private static readonly string[] OnlySnr = ["MinStarSNR"];
        private static readonly string[] AdvancedGetters = [
            "get_guider_options", "get_profile_setup", "get_star_detection", "get_auto_exposure", "get_noise_reduction",
            "get_use_subframes", "get_guide_limits", "get_backlash_comp", "get_mount_options", "get_variable_delay_settings",
            "get_dither_settings", "get_algo", "get_algos",
        ];

        private static readonly string[] Recorded = [
            "set_profile_setup", "set_star_detection", "set_guide_limits", "set_algo", "set_noise_reduction", "set_mount_options",
            "set_algo_param",
        ];

        private static async Task<(FakeGuider Fake, GuiderService Svc, ConcurrentQueue<(string Method, JsonNode? Params)> Sent)> ConnectedAsync(
                Func<GuiderAdvancedSettings?>? advanced = null, Action<FakeGuider>? setup = null, ISequencerService? sequencer = null, HeadlessProfileService? profile = null) {
            var sent = new ConcurrentQueue<(string, JsonNode?)>();
            var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_pixel_scale", JsonValue.Create(1.5));
            fake.OnRpc("get_profile", _ => new JsonObject { ["id"] = 1, ["name"] = "My Equipment" });
            fake.OnRpc("get_profiles", _ => new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "My Equipment" }));
            fake.OnRpc("create_profile", _ => new JsonObject { ["id"] = 5, ["name"] = "Ara", ["selected"] = true });
            fake.OnRpc("get_connected", JsonValue.Create(true));
            foreach (var method in Recorded) {
                var m = method;
                fake.OnRpc(m, req => { sent.Enqueue((m, req["params"]?.DeepClone())); return JsonValue.Create(0); });
            }
            setup?.Invoke(fake);
            var svc = new GuiderService(profile ?? new HeadlessProfileService(),
                new GuiderRecoveryCoordinator(Mock.Of<IGuiderProcessSupervisor>(), Mock.Of<INotificationService>(),
                    Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(), sequencerResolver: sequencer is null ? null : () => sequencer) {
                AdvancedSettings = advanced,
            };
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => svc.GetInfo().PixelScale > 0).ConfigureAwait(false), Is.True, "precondition: connected");
            return (fake, svc, sent);
        }

        [Test]
        public async Task The_read_back_maps_every_group_the_guider_answers() {
            var (fake, svc, _) = await ConnectedAsync(setup: f => {
                f.OnRpc("get_guider_options", _ => new JsonObject { ["FastRecenter"] = true, ["TolerateJumps"] = false, ["TolerateJumpsThreshold"] = 4.5 });
                f.OnRpc("get_profile_setup", _ => new JsonObject { ["multistar_enabled"] = true, ["focal_length"] = 120 });
                f.OnRpc("get_star_detection", _ => new JsonObject { ["MinStarSNR"] = 6.0, ["MinStarHFD"] = 1.5, ["MaxStarHFD"] = 10.0, ["SearchRegion"] = 15, ["MassChangeThresholdEnabled"] = true, ["MassChangeThreshold"] = 50.0 });
                f.OnRpc("get_auto_exposure", _ => new JsonObject { ["Enabled"] = false, ["MinExposure"] = 1000, ["MaxExposure"] = 5000, ["TargetSNR"] = 6.0 });
                f.OnRpc("get_noise_reduction", JsonValue.Create("2x2 Mean"));
                f.OnRpc("get_use_subframes", JsonValue.Create(true));
                f.OnRpc("get_guide_limits", _ => new JsonObject { ["MaxRaDuration"] = 2500, ["MaxDecDuration"] = 2500 });
                f.OnRpc("get_backlash_comp", _ => new JsonObject { ["Enabled"] = true, ["PulseWidth"] = 450, ["Floor"] = 20, ["Ceiling"] = 2000 });
                f.OnRpc("get_mount_options", _ => new JsonObject { ["AssumeOrthogonal"] = false, ["StopGuidingWhenSlewing"] = true, ["CalFlipRequiresDecFlip"] = false });
                f.OnRpc("get_variable_delay_settings", _ => new JsonObject { ["Enabled"] = false, ["ShortDelaySeconds"] = 1.0, ["LongDelaySeconds"] = 10.0 });
                f.OnRpc("get_dither_settings", _ => new JsonObject { ["ScaleFactor"] = 1.0, ["RaOnly"] = false });
                f.OnRpc("get_algo", req => JsonValue.Create(req["params"]?["axis"]?.GetValue<string>() == "ra" ? "Hysteresis" : "Resist Switch"));
                f.OnRpc("get_algos", req => req["params"]?["axis"]?.GetValue<string>() == "ra"
                    ? new JsonArray("Hysteresis", "Predictive PEC") : new JsonArray("Resist Switch", "Lowpass"));
            }).ConfigureAwait(false);
            await using var _f = fake;
            using var _s = svc;
            var view = await new GuiderLiveService(svc, new NoFrames()).GetAdvancedAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(view.Connected, Is.True);
            var c = view.Current;
            Assert.That(c.MultiStarEnabled, Is.True);
            Assert.That((c.FastRecenter, c.TolerateJumps, c.TolerateJumpsThresholdPx), Is.EqualTo(((bool?)true, (bool?)false, (double?)4.5)));
            Assert.That((c.MinStarSnr, c.MinStarHfd, c.MaxStarHfd, c.SearchRegionPx), Is.EqualTo(((double?)6, (double?)1.5, (double?)10, (int?)15)));
            Assert.That((c.AutoExposureMinMs, c.AutoExposureMaxMs, c.AutoExposureTargetSnr), Is.EqualTo(((int?)1000, (int?)5000, (double?)6)));
            Assert.That((c.NoiseReduction, c.UseSubframes), Is.EqualTo(("2x2 Mean", (bool?)true)));
            Assert.That((c.MaxRaDurationMs, c.BacklashPulseMs, c.BacklashCeilingMs), Is.EqualTo(((int?)2500, (int?)450, (int?)2000)));
            Assert.That((c.StopGuidingWhenSlewing, c.VariableDelayLongS, c.DitherScaleFactor), Is.EqualTo(((bool?)true, (double?)10, (double?)1)));
            Assert.That((c.RaAlgorithm, c.DecAlgorithm), Is.EqualTo(("Hysteresis", "Resist Switch")));
            Assert.That(view.RaAlgorithms, Is.EqualTo(RaAlgos));
            Assert.That(view.DecAlgorithms, Is.EqualTo(DecAlgos));
        }

        [Test]
        [NonParallelizable] // Notifier.Use is process-global
        public async Task Refused_advanced_reads_raise_no_notification() {
            var (fake, svc, _) = await ConnectedAsync(setup: f => {
                foreach (var m in AdvancedGetters) {
                    var method = m;
                    f.OnRpc(method, _ => throw new InvalidOperationException($"mount not defined (advanced {method})"));
                }
            }).ConfigureAwait(false);
            await using var _f = fake;
            using var _s = svc;
            var sink = new RecordingSink();
            var previous = OpenAstroAra.Core.Utility.Notification.Notifier.Current;
            OpenAstroAra.Core.Utility.Notification.Notifier.Use(sink);
            try {
                var view = await new GuiderLiveService(svc, new NoFrames()).GetAdvancedAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.That(view.Connected, Is.True);
                Assert.That(view.Current.MinStarSnr, Is.Null);
                Assert.That(view.RaAlgorithms, Is.Empty);
                Assert.That(sink.Messages.Where(m => m.Contains("(advanced ", StringComparison.Ordinal)), Is.Empty,
                    "opening Guider settings without a mount must not post an inbox Error per setting");
            } finally {
                OpenAstroAra.Core.Utility.Notification.Notifier.Use(previous);
            }
        }

        private static readonly GuiderAdvancedSettings Advanced = new() {
            MultiStarEnabled = false, MinStarSnr = 8, MaxDecDurationMs = 1500, RaAlgorithm = "Predictive PEC",
        };

        [Test]
        public async Task Connect_pushes_multi_star_in_the_setup_and_the_runtime_groups() {
            var (fake, svc, sent) = await ConnectedAsync(advanced: () => Advanced).ConfigureAwait(false);
            await using var _f = fake;
            using var _s = svc;
            Assert.That(await WaitUntilAsync(() => sent.Any(s => s.Method == "set_algo")).ConfigureAwait(false), Is.True, "the connect push ran");
            var setup = sent.Where(s => s.Method == "set_profile_setup").Select(s => s.Params).ToList();
            Assert.That(setup, Has.Count.EqualTo(1));
            Assert.That(setup[0]?["multistar_enabled"]?.GetValue<bool>(), Is.False);
            var detection = sent.Single(s => s.Method == "set_star_detection").Params!.AsObject();
            Assert.That(detection.Select(p => p.Key), Is.EquivalentTo(OnlySnr), "only what the profile set");
            Assert.That(detection["MinStarSNR"]!.GetValue<double>(), Is.EqualTo(8));
            Assert.That(sent.Single(s => s.Method == "set_guide_limits").Params!["MaxDecDuration"]!.GetValue<int>(), Is.EqualTo(1500));
            var algo = sent.Single(s => s.Method == "set_algo").Params!;
            Assert.That((algo["axis"]!.GetValue<string>(), algo["name"]!.GetValue<string>()), Is.EqualTo(("ra", "Predictive PEC")));
            var order = sent.Select(s => s.Method).ToList();
            var firstRaParam = order.FindIndex(m => m == "set_algo_param");
            Assert.That(firstRaParam, Is.GreaterThan(order.IndexOf("set_algo")),
                "the algorithm is set before Tune Guiding's params, so they land on the new algorithm: " + string.Join(", ", order));
            Assert.That(sent.Any(s => s.Method is "set_noise_reduction" or "set_mount_options"), Is.False, "groups with nothing set are not sent");
        }

        [Test]
        public async Task Apply_sends_the_runtime_groups_without_the_setup() {
            var current = Advanced;
            var (fake, svc, sent) = await ConnectedAsync(advanced: () => current).ConfigureAwait(false);
            await using var _f = fake;
            using var _s = svc;
            Assert.That(await WaitUntilAsync(() => sent.Any(s => s.Method == "set_algo")).ConfigureAwait(false), Is.True);
            sent.Clear();
            current = Advanced with { MinStarSnr = 10, NoiseReduction = "3x3 Median" };
            var methods = await svc.ConnectedGuiderOrNull()!.RepushGuiderTuningAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(methods, Does.Contain("set_star_detection").And.Contain("set_noise_reduction"));
            Assert.That(methods, Does.Not.Contain("set_profile_setup"), "Apply while guiding never opens the disconnect window");
            Assert.That(sent.Single(s => s.Method == "set_star_detection").Params!["MinStarSNR"]!.GetValue<double>(), Is.EqualTo(10));
            Assert.That(sent.Single(s => s.Method == "set_noise_reduction").Params!["method"]!.GetValue<string>(), Is.EqualTo("3x3 Median"));
        }

        [Test]
        public async Task Saving_multi_star_while_connected_sends_set_profile_setup_with_the_flag() {
            // #1592 — the flag used to ride only the connect-time push, so a save never reached a connected guider.
            var current = Advanced with { MultiStarEnabled = true };
            var (fake, svc, sent) = await ConnectedAsync(advanced: () => current).ConfigureAwait(false);
            await using var _f = fake;
            using var _s = svc;
            Assert.That(await WaitUntilAsync(() => sent.Any(s => s.Method == "set_algo")).ConfigureAwait(false), Is.True);
            sent.Clear();
            current = Advanced with { MultiStarEnabled = false };

            await svc.ApplyGuiderMultiStarAsync(CancellationToken.None).ConfigureAwait(false);

            var setup = sent.Single(s => s.Method == "set_profile_setup").Params!;
            Assert.That(setup["multistar_enabled"]!.GetValue<bool>(), Is.False);
            Assert.That(sent.Count, Is.EqualTo(1), "only the flag is written");
        }

        [Test]
        public async Task An_unset_multi_star_applies_as_on_and_guiding_refuses_the_apply() {
            var (fake, svc, sent) = await ConnectedAsync(advanced: () => new GuiderAdvancedSettings()).ConfigureAwait(false);
            await using var _f = fake;
            using var _s = svc;
            Assert.That(await WaitUntilAsync(() => sent.Any(s => s.Method == "set_profile_setup" || s.Method == "set_algo_param")).ConfigureAwait(false), Is.True);
            sent.Clear();
            await svc.ApplyGuiderMultiStarAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(sent.Single(s => s.Method == "set_profile_setup").Params!["multistar_enabled"]!.GetValue<bool>(), Is.True);

            sent.Clear();
            await fake.BroadcastAsync(PhdEvents.AppState("Guiding")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => svc.ConnectedGuiderOrNull()!.State == "Guiding").ConfigureAwait(false), Is.True);
            await Assert.ThrowsAsync<GuiderBusyException>(() => svc.ApplyGuiderMultiStarAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.That(sent, Is.Empty, "nothing is sent while guiding");
        }

        [Test]
        public async Task A_running_sequence_refuses_the_multi_star_apply_and_nothing_is_sent() {
            // #1592 review — between targets the guider is stopped, so only the run itself says the rig is busy.
            var sequencer = new Mock<ISequencerService>();
            sequencer.Setup(q => q.ActiveRunIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Guid.NewGuid()]);
            var (fake, svc, sent) = await ConnectedAsync(advanced: () => new GuiderAdvancedSettings(), sequencer: sequencer.Object).ConfigureAwait(false);
            await using var _f = fake;
            using var _s = svc;
            Assert.That(await WaitUntilAsync(() => sent.Any(s => s.Method == "set_algo_param" || s.Method == "set_profile_setup")).ConfigureAwait(false), Is.True);
            sent.Clear();
            var methods = fake.ReceivedMethods.Count;

            var ex = await Assert.ThrowsAsync<GuiderBusyException>(() => svc.ApplyGuiderMultiStarAsync(CancellationToken.None)).ConfigureAwait(false);

            Assert.That(ex!.Message, Does.Contain("sequence"));
            Assert.That(fake.ReceivedMethods.Count, Is.EqualTo(methods), "no disconnect, no set_profile_setup");
        }

        [Test]
        public async Task Connect_and_tuning_pushes_send_aras_auto_dec_mode() {
            // #1448 — Auto used to be skipped as "unset", so a North in the guider's profile outlived it.
            var modes = new ConcurrentQueue<string?>();
            var (fake, svc, _) = await ConnectedAsync(setup: f => f.OnRpc("set_dec_guide_mode", req => {
                modes.Enqueue(req["params"]?["mode"]?.GetValue<string>());
                return JsonValue.Create(0);
            })).ConfigureAwait(false);
            await using var _f = fake;
            using var _s = svc;
            Assert.That(await WaitUntilAsync(() => !modes.IsEmpty).ConfigureAwait(false), Is.True, "the connect push sent the Dec mode");
            Assert.That(modes, Is.All.EqualTo("Auto"));
            modes.Clear();

            var methods = await svc.ConnectedGuiderOrNull()!.RepushGuiderTuningAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.That(methods, Does.Contain("set_dec_guide_mode"));
            Assert.That(modes, Is.EqualTo(AutoOnly));
        }


        // #1748 — each algorithm's real get_algo_param_names, and refusal of any other name, as the Guider does.
        private static readonly Dictionary<string, string[]> AlgoParams = new() {
            ["Hysteresis"] = ["algorithmName", "minMove", "hysteresis", "aggression"],
            ["Resist Switch"] = ["algorithmName", "minMove", "fastSwitch", "aggression"],
            ["Predictive PEC"] = ["algorithmName", "minMove", "reactiveWeight", "periodLength"],
            ["Lowpass"] = ["algorithmName", "minMove", "slopeWeight"],
            ["Lowpass2"] = ["algorithmName", "minMove", "aggressiveness"],
            ["ZFilter"] = ["algorithmName", "minMove", "expFactor"],
        };

        private static void RunsAlgorithm(FakeGuider f, ConcurrentQueue<(string Axis, string Name, double Value)> sent, string algorithm) {
            f.OnRpc("get_algo_param_names", _ => new JsonArray(AlgoParams[algorithm].Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()));
            f.OnRpc("set_algo_param", req => {
                var p = req["params"]!;
                var name = p["name"]!.GetValue<string>();
                sent.Enqueue((p["axis"]!.GetValue<string>(), name, p["value"]!.GetValue<double>()));
                return AlgoParams[algorithm].Contains(name) ? JsonValue.Create(0) : throw new InvalidOperationException("could not set param");
            });
        }

        private static HeadlessProfileService TunedProfile() {
            var profile = new HeadlessProfileService();
            profile.ActiveProfile.GuiderSettings.RAAggressiveness = 0.6;
            profile.ActiveProfile.GuiderSettings.DecAggressiveness = 0.6;
            return profile;
        }

        private static async Task<List<(string Axis, string Name, double Value)>> TunedPushAsync(string algorithm) {
            var sent = new ConcurrentQueue<(string Axis, string Name, double Value)>();
            var (fake, svc, _) = await ConnectedAsync(setup: f => RunsAlgorithm(f, sent, algorithm), profile: TunedProfile()).ConfigureAwait(false);
            await using var _f = fake;
            using var _s = svc;
            sent.Clear();
            await svc.ConnectedGuiderOrNull()!.RepushGuiderTuningAsync(CancellationToken.None).ConfigureAwait(false);
            return sent.Where(s => s.Name != "minMove").ToList();
        }

        [TestCase("Hysteresis", "aggression", 0.6)]
        [TestCase("Resist Switch", "aggression", 0.6)]
        [TestCase("Predictive PEC", "reactiveWeight", 0.6)]
        [TestCase("Lowpass2", "aggressiveness", 60.0)]
        public async Task Aggressiveness_goes_under_the_name_and_scale_the_algorithm_has(string algorithm, string name, double value) {
            var sent = await TunedPushAsync(algorithm).ConfigureAwait(false);
            Assert.That(sent, Has.Count.EqualTo(2), "one per axis");
            Assert.That(sent.Select(s => s.Name), Is.All.EqualTo(name));
            Assert.That(sent.Select(s => s.Value), Is.All.EqualTo(value).Within(1e-9));
        }

        [TestCase("Lowpass")]
        [TestCase("ZFilter")]
        public async Task An_algorithm_with_no_aggressiveness_is_not_sent_one(string algorithm) =>
            Assert.That(await TunedPushAsync(algorithm).ConfigureAwait(false), Is.Empty);

        [Test]
        [NonParallelizable] // Notifier.Use is process-global
        public async Task A_refused_setting_raises_no_notification() {
            var (fake, svc, _) = await ConnectedAsync(advanced: () => new GuiderAdvancedSettings { MinStarSnr = 8 },
                setup: f => f.OnRpc("set_star_detection", _ => throw new InvalidOperationException("the guider said no"))).ConfigureAwait(false);
            await using var _f = fake;
            using var _s = svc;
            var sink = new RecordingSink();
            var previous = OpenAstroAra.Core.Utility.Notification.Notifier.Current;
            OpenAstroAra.Core.Utility.Notification.Notifier.Use(sink);
            try {
                await svc.ConnectedGuiderOrNull()!.RepushGuiderTuningAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.That(sink.Messages, Is.Empty, "a refused push is not an equipment error");
            } finally {
                OpenAstroAra.Core.Utility.Notification.Notifier.Use(previous);
            }
        }

        private static async Task<GuiderAdvancedViewDto> ViewAsync(GuiderService svc) =>
            await new GuiderLiveService(svc, new NoFrames()).GetAdvancedAsync(CancellationToken.None).ConfigureAwait(false);

        [Test]
        public async Task A_question_the_guider_cannot_answer_pushes_aggression_as_before() {
            var sent = new ConcurrentQueue<(string Axis, string Name, double Value)>();
            var (fake, svc, _) = await ConnectedAsync(profile: TunedProfile(), setup: f => {
                RunsAlgorithm(f, sent, "Hysteresis");
                f.OnRpc("get_algo_param_names", _ => throw new InvalidOperationException("no mount defined"));
            }).ConfigureAwait(false);
            await using var _f = fake;
            using var _s = svc;
            sent.Clear();

            await svc.ConnectedGuiderOrNull()!.RepushGuiderTuningAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.That(sent.Where(s => s.Name != "minMove").Select(s => s.Name), Is.EqualTo(TwoAggression));
        }

        [Test]
        public async Task The_settings_question_is_asked_after_the_algorithm_changes() {
            var sent = new ConcurrentQueue<(string Axis, string Name, double Value)>();
            var algorithm = "Hysteresis";
            var (fake, svc, _) = await ConnectedAsync(profile: TunedProfile(), advanced: () => new GuiderAdvancedSettings { RaAlgorithm = "Lowpass2" }, setup: f => {
                RunsAlgorithm(f, sent, "Lowpass2");
                f.OnRpc("set_algo", _ => { Volatile.Write(ref algorithm, "Lowpass2"); return JsonValue.Create(0); });
                f.OnRpc("get_algo_param_names", _ => new JsonArray(AlgoParams[Volatile.Read(ref algorithm)].Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()));
            }).ConfigureAwait(false);
            await using var _f = fake;
            using var _s = svc;
            Assert.That(await WaitUntilAsync(() => sent.Any(s => s.Axis == "dec" && s.Name != "minMove")).ConfigureAwait(false), Is.True);

            Assert.That(sent.First(s => s.Axis == "ra" && s.Name != "minMove").Name, Is.EqualTo("aggressiveness"), "the question follows set_algo");
        }

        [Test]
        public async Task The_view_lists_what_the_latest_push_did_not_land_and_the_next_push_replaces_it() {
            var sent = new ConcurrentQueue<(string Axis, string Name, double Value)>();
            var saved = new GuiderAdvancedSettings { SearchRegionPx = 60, MinStarSnr = 8, MinStarHfd = 2 };
            var (fake, svc, _) = await ConnectedAsync(profile: TunedProfile(), advanced: () => saved, setup: f => {
                RunsAlgorithm(f, sent, "Lowpass");
                f.OnRpc("set_star_detection", req => req["params"]?["MinStarHFD"] is null ? JsonValue.Create(0) : throw new InvalidOperationException("MinStarHFD must be below MaxStarHFD"));
            }).ConfigureAwait(false);
            await using var _f = fake;
            using var _s = svc;
            var guider = svc.ConnectedGuiderOrNull()!;

            await guider.RepushGuiderTuningAsync(CancellationToken.None).ConfigureAwait(false);

            var entries = (await ViewAsync(svc).ConfigureAwait(false)).NotApplied;
            Assert.That(entries.Select(e => e.Setting), Is.SupersetOf(ExpectedNotApplied));
            Assert.That(entries.Single(e => e.Setting == "Star detection").Reason, Does.Contain("MinStarHFD must be below"), "the Guider's own words");
            Assert.That(entries.Single(e => e.Setting == "RA aggressiveness").Reason, Does.Contain("no aggressiveness"));
            Assert.That(entries.Single(e => e.Setting == "Search region").Reason, Does.Contain("7 to 50"));

            saved = new GuiderAdvancedSettings { MinStarSnr = 8 };
            RunsAlgorithm(fake, sent, "Hysteresis");
            await guider.RepushGuiderTuningAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.That((await ViewAsync(svc).ConfigureAwait(false)).NotApplied, Is.Empty, "a problem that was fixed disappears");
        }

        private static readonly string[] AutoOnly = ["Auto"];
        private static readonly string[] TwoAggression = ["aggression", "aggression"];
        private static readonly string[] ExpectedNotApplied = ["Search region", "RA aggressiveness", "Dec aggressiveness", "Star detection"];

        private static async Task<bool> WaitUntilAsync(Func<bool> condition) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!cts.IsCancellationRequested) {
                if (condition()) {
                    return true;
                }
                try {
                    await Task.Delay(50, cts.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    break;
                }
            }
            return condition();
        }
    }
}
