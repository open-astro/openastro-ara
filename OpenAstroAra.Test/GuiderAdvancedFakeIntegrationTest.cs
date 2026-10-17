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
                Func<GuiderAdvancedSettings?>? advanced = null, Action<FakeGuider>? setup = null) {
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
            var svc = new GuiderService(new HeadlessProfileService(),
                new GuiderRecoveryCoordinator(Mock.Of<IGuiderProcessSupervisor>(), Mock.Of<INotificationService>(),
                    Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>()) {
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
