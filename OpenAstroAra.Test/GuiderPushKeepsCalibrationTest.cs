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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile;
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
    /// #2074 — the §63.5 push against the bench's <see cref="FakeGuider"/>: a guider that already holds the
    /// profile's selections and setup keeps its equipment connected (and so its calibration), a multi-star
    /// change opens the window for <c>set_profile_setup</c> alone, and a calibration the reconnect lost over
    /// unchanged gear is put back.
    /// </summary>
    [TestFixture]
    [Category("IO")]
    [Category("bench")]
    public class GuiderPushKeepsCalibrationTest {

        private const string Camera = "Alpaca Camera [bridge.local:11111/0]";
        private const string Mount = "Alpaca Telescope [bridge.local:11111/0]";

        /// <summary>A guider holding <see cref="Camera"/> / <see cref="Mount"/> on bridge.local, 240 mm and
        /// 2.9 µm, calibrated, equipment connected. It records every call in order, with set_connected's
        /// flag, and answers the getters from its state.</summary>
        private sealed class Rig : IAsyncDisposable {
            public readonly FakeGuider Fake = FakeGuider.Start();
            public readonly ConcurrentQueue<string> Calls = new();
            public volatile string HeldCamera = Camera;
            public volatile bool MultiStar = true;
            public volatile bool Connected = true;
            public volatile bool Calibrated = true;
            /// <summary>The latched-guider case: a disconnect clears the calibration.</summary>
            public volatile bool DisconnectDropsCalibration;

            public Rig() {
                Fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
                Fake.OnRpc("get_pixel_scale", JsonValue.Create(1.5));
                Fake.OnRpc("get_app_state", JsonValue.Create("Stopped"));
                Fake.OnRpc("get_profile", _ => new JsonObject { ["id"] = 1, ["name"] = "Ara" });
                Fake.OnRpc("get_profiles", _ => new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "Ara" }));
                Fake.OnRpc("get_connected", _ => JsonValue.Create(Connected));
                Fake.OnRpc("set_connected", req => {
                    var on = (bool?)req["params"]?[0] ?? false;
                    Calls.Enqueue(on ? "set_connected(true)" : "set_connected(false)");
                    Connected = on;
                    if (!on && DisconnectDropsCalibration) {
                        Calibrated = false;
                    }
                    return JsonValue.Create(0);
                });
                Fake.OnRpc("get_selected_camera", _ => JsonValue.Create(HeldCamera));
                Fake.OnRpc("get_selected_camera_id", JsonValue.Create("Default"));
                Fake.OnRpc("get_selected_mount", JsonValue.Create(Mount));
                Fake.OnRpc("get_selected_rotator", JsonValue.Create("None"));
                Fake.OnRpc("get_alpaca_server", _ => new JsonObject {
                    ["host"] = "bridge.local", ["port"] = 11111, ["camera_device"] = 0, ["telescope_device"] = 0, ["rotator_device"] = 0,
                });
                Fake.OnRpc("get_profile_setup", _ => new JsonObject {
                    ["focal_length"] = 240, ["pixel_size"] = 2.9, ["multistar_enabled"] = MultiStar,
                });
                Fake.OnRpc("get_calibrated", _ => JsonValue.Create(Connected && Calibrated));
                Fake.OnRpc("restore_calibration", _ => {
                    Calls.Enqueue("restore_calibration");
                    Calibrated = true;
                    return JsonValue.Create(true);
                });
                foreach (var method in new[] { "stop_capture", "set_profile_setup", "set_alpaca_server", "set_selected_camera",
                    "set_selected_camera_id", "set_selected_mount", "set_selected_rotator", "set_dec_guide_mode", "set_camera_gain" }) {
                    var m = method;
                    Fake.OnRpc(m, _ => { Calls.Enqueue(m); return JsonValue.Create(0); });
                }
            }

            public string[] Since(int mark) => Calls.Skip(mark).ToArray();

            public ValueTask DisposeAsync() => Fake.DisposeAsync();
        }

        private static async Task<GuiderService> ConnectAsync(Rig rig, Func<bool> araMultiStar) {
            var profiles = new HeadlessProfileService();
            var s = profiles.ActiveProfile.GuiderSettings;
            s.GuiderCamera = Camera;
            s.GuiderMount = Mount;
            s.GuideFocalLength = 240;
            s.GuidePixelSize = 2.9;
            var svc = new GuiderService(profiles,
                new GuiderRecoveryCoordinator(Mock.Of<IGuiderProcessSupervisor>(), Mock.Of<INotificationService>(),
                    Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(),
                araProfileResolver: () => (Guid.NewGuid(), "Ara")) {
                AdvancedSettings = () => new GuiderAdvancedSettings { MultiStarEnabled = araMultiStar() },
            };
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", rig.Fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Calls.Contains("set_camera_gain")).ConfigureAwait(false), Is.True,
                "precondition: the connect ran to the gain push");
            return svc;
        }

        private static readonly string[] WindowMethods = [
            "set_connected(false)", "stop_capture", "set_profile_setup", "set_alpaca_server",
            "set_selected_camera", "set_selected_camera_id", "set_selected_mount", "set_selected_rotator",
        ];

        [Test]
        public async Task A_guider_that_holds_the_profile_keeps_its_equipment_connected_on_connect_and_push() {
            await using var rig = new Rig();
            using var svc = await ConnectAsync(rig, () => true).ConfigureAwait(false);

            Assert.That(rig.Calls.Intersect(WindowMethods), Is.Empty, "the connect push left the equipment alone: " + string.Join(", ", rig.Calls));
            Assert.That(rig.Calls, Does.Contain("set_dec_guide_mode"), "the runtime-safe messages still went out");

            var mark = rig.Calls.Count;
            await svc.PushGuiderProfileAsync("push", CancellationToken.None).ConfigureAwait(false);

            var sent = rig.Since(mark);
            Assert.That(sent.Intersect(WindowMethods), Is.Empty, "the explicit push left the equipment alone: " + string.Join(", ", sent));
            Assert.That(sent, Does.Contain("set_dec_guide_mode").And.Contain("set_camera_gain"));
            Assert.That(rig.Calls, Does.Not.Contain("restore_calibration"));
            Assert.That(rig.Calibrated && rig.Connected, Is.True);
        }

        private static readonly string[] SetupOnlyWindow = ["set_connected(false)", "set_profile_setup", "set_connected(true)"];

        [Test]
        public async Task A_multi_star_change_sends_the_setup_alone_and_needs_no_restore_when_the_calibration_survives() {
            await using var rig = new Rig { MultiStar = false };
            using var svc = await ConnectAsync(rig, () => true).ConfigureAwait(false);

            var calls = rig.Calls.ToArray();
            Assert.That(calls.Where(c => c is "set_connected(false)" or "set_profile_setup" or "set_connected(true)"),
                Is.EqualTo(SetupOnlyWindow), string.Join(", ", calls));
            Assert.That(calls.Where(c => c == "set_alpaca_server" || c.StartsWith("set_selected", StringComparison.Ordinal)), Is.Empty,
                "unchanged selections are not sent, so the guider's mount is not rebuilt");
            Assert.That(calls, Does.Not.Contain("restore_calibration"), "get_calibrated still read true after the reconnect");
        }

        [Test]
        public async Task A_calibration_the_connect_reconnect_lost_over_unchanged_gear_is_restored() {
            await using var rig = new Rig { MultiStar = false, DisconnectDropsCalibration = true };
            using var svc = await ConnectAsync(rig, () => true).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => rig.Calls.Contains("restore_calibration")).ConfigureAwait(false), Is.True,
                "the lost calibration was restored: " + string.Join(", ", rig.Calls));
            var calls = rig.Calls.ToList();
            Assert.That(calls.IndexOf("restore_calibration"), Is.GreaterThan(calls.IndexOf("set_connected(true)")),
                "the restore follows the reconnect");
            Assert.That(rig.Calibrated, Is.True);
        }

        [Test]
        public async Task A_calibration_the_push_reconnect_lost_over_unchanged_gear_is_restored() {
            await using var rig = new Rig { DisconnectDropsCalibration = true };
            var araMultiStar = true;
            using var svc = await ConnectAsync(rig, () => araMultiStar).ConfigureAwait(false);
            Assert.That(rig.Calls, Does.Not.Contain("set_connected(false)"), "precondition: the connect opened no window");

            araMultiStar = false;
            var mark = rig.Calls.Count;
            await svc.PushGuiderProfileAsync("push", CancellationToken.None).ConfigureAwait(false);

            var sent = rig.Since(mark).ToList();
            Assert.That(sent, Does.Contain("set_profile_setup"));
            Assert.That(sent, Does.Contain("restore_calibration"), string.Join(", ", sent));
            Assert.That(sent.IndexOf("restore_calibration"), Is.GreaterThan(sent.IndexOf("set_connected(true)")));
            Assert.That(rig.Calibrated, Is.True);
        }

        [Test]
        public async Task A_changed_camera_sends_every_selection_and_restores_nothing() {
            await using var rig = new Rig { HeldCamera = "Alpaca Camera [bridge.local:11111/1]", DisconnectDropsCalibration = true };
            using var svc = await ConnectAsync(rig, () => true).ConfigureAwait(false);

            var calls = rig.Calls.ToArray();
            Assert.That(calls, Does.Contain("set_connected(false)"));
            Assert.That(calls, Does.Contain("set_alpaca_server").And.Contain("set_selected_camera").And.Contain("set_selected_mount")
                .And.Contain("set_selected_rotator"));
            Assert.That(calls, Does.Not.Contain("restore_calibration"), "another camera needs a new calibration");
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
