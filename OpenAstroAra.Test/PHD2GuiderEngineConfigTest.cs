#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Profile;
using OpenAstroAra.Profile.Interfaces;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §63.5 (guider-e-2) — <see cref="PHD2Guider.BuildGuiderEngineConfigMessages"/> + dec-mode mapping. The
    /// socket-free message builder is the testable core of the on-connect push; verifies the profile maps onto
    /// the right set_profile_setup / set_algo_param / set_dec_guide_mode RPC objects.
    /// </summary>
    [TestFixture]
    public class PHD2GuiderEngineConfigTest {

        private static IGuiderSettings Settings(int focal = 250, double pixel = 2.9, double ra = 0.7,
                double dec = 0.65, double minMove = 0.15, string mode = "north", int exposureMs = 0,
                double? decMinMove = null) {
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuideFocalLength).Returns(focal);
            s.SetupGet(x => x.GuidePixelSize).Returns(pixel);
            s.SetupGet(x => x.RAAggressiveness).Returns(ra);
            s.SetupGet(x => x.DecAggressiveness).Returns(dec);
            s.SetupGet(x => x.MinimumMove).Returns(minMove);
            s.SetupGet(x => x.DecMinimumMove).Returns(decMinMove ?? minMove);
            s.SetupGet(x => x.DecGuideMode).Returns(mode);
            s.SetupGet(x => x.GuideExposureMs).Returns(exposureMs);
            return s.Object;
        }

        // ── guide exposure (set_exposure, runtime-safe) + the tuning-only subset ──

        private static readonly int[] ExpectedExposureParams = { 500 };
        private static readonly string[] ExpectedTuningMethods = {
            "set_algo_param", "set_algo_param", "set_algo_param", "set_algo_param", "set_dec_guide_mode", "set_exposure",
        };

        [Test]
        public void Build_pushes_set_exposure_only_when_a_guide_exposure_is_set() {
            // 0 = unset → never sent (the guider keeps its own duration).
            Assert.That(PHD2Guider.BuildGuiderEngineConfigMessages(Settings()).OfType<Phd2SetExposure>(), Is.Empty);

            var msgs = PHD2Guider.BuildGuiderEngineConfigMessages(Settings(exposureMs: 500));
            var exposure = msgs.OfType<Phd2SetExposure>().Single();
            // Positional params, as PHD2's set_exposure expects: [500].
            Assert.That(exposure.Parameters, Is.EqualTo(ExpectedExposureParams));
            // It applies on the next frame — it must never open the disconnect window.
            Assert.That(PHD2Guider.RequiresDisconnectedEquipment(exposure), Is.False);
        }

        [TestCase(50, 50)]
        [TestCase(0, 0)]
        [TestCase(100, 100)]
        [TestCase(-5, 0)]
        [TestCase(250, 100)]
        public void Camera_gain_message_carries_the_profile_gain_clamped_to_the_daemon_range(int gain, int expected) {
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuideCameraGain).Returns(gain);
            var msg = PHD2Guider.BuildCameraGainMessage(s.Object);
            Assert.That(msg.Method, Is.EqualTo("set_camera_gain"));
            Assert.That(msg.Parameters, Is.EqualTo(new[] { expected }));
        }

        [Test]
        public void Camera_gain_never_rides_the_disconnected_engine_config_window() {
            // The daemon refuses set_camera_gain while the camera is off ("camera has no gain control"), so the
            // gain is pushed after the reconnect, not in the engine-config list.
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuiderCamera).Returns("Alpaca Camera [localhost:6800/0]");
            s.SetupGet(x => x.GuideCameraGain).Returns(50);
            Assert.That(PHD2Guider.BuildGuiderEngineConfigMessages(s.Object).OfType<Phd2SetCameraGain>(), Is.Empty);
        }

        [Test]
        public void Tuning_messages_keep_runtime_safe_setters_and_drop_setup_and_selections() {
            // A configured profile always carries selections + set_profile_setup, which the full push sends
            // inside a stop_capture / set_connected(false) window — that is what stopped guiding when the
            // Tune Guiding dialog applied an aggressiveness change. The tuning subset must contain only what
            // applies while guiding continues.
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuiderCamera).Returns("Alpaca Camera [localhost:6800/1]");
            s.SetupGet(x => x.GuiderMount).Returns("Alpaca Mount [localhost:6800/0]");
            s.SetupGet(x => x.GuideFocalLength).Returns(120);
            s.SetupGet(x => x.GuidePixelSize).Returns(3.75);
            s.SetupGet(x => x.RAAggressiveness).Returns(0.5);
            s.SetupGet(x => x.DecAggressiveness).Returns(0.85);
            s.SetupGet(x => x.MinimumMove).Returns(0.15);
            s.SetupGet(x => x.DecMinimumMove).Returns(0.25);
            s.SetupGet(x => x.DecGuideMode).Returns("north");
            s.SetupGet(x => x.GuideExposureMs).Returns(500);

            var full = PHD2Guider.BuildGuiderEngineConfigMessages(s.Object);
            Assert.That(full.Any(PHD2Guider.RequiresDisconnectedEquipment), Is.True, "precondition: the full push would disconnect");

            var tuning = PHD2Guider.BuildGuiderTuningMessages(s.Object);
            Assert.That(tuning, Is.Not.Empty);
            Assert.That(tuning.Any(PHD2Guider.RequiresDisconnectedEquipment), Is.False);
            Assert.That(tuning.OfType<Phd2SetAlgoParam>().Count(), Is.EqualTo(4));
            Assert.That(tuning.OfType<Phd2SetDecGuideMode>().Count(), Is.EqualTo(1));
            Assert.That(tuning.OfType<Phd2SetExposure>().Count(), Is.EqualTo(1));
            Assert.That(tuning.Select(m => m.Method), Is.EquivalentTo(ExpectedTuningMethods));
        }

        // §63.17 — a settings mock with equipment selections set (the plain Settings() mock leaves them at
        // Moq's null defaults, which the builder must treat as unset).
        private static IGuiderSettings SettingsWithSelections() {
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuiderCamera).Returns("  Alpaca Camera ");
            s.SetupGet(x => x.GuiderCameraId).Returns("cam-01");
            s.SetupGet(x => x.GuiderMount).Returns("On-camera");
            s.SetupGet(x => x.GuiderAuxMount).Returns("Alpaca Telescope");
            s.SetupGet(x => x.GuiderRotator).Returns("Alpaca Rotator");
            s.SetupGet(x => x.GuiderSetupType).Returns(GuiderSettings.OagSetupType);
            s.SetupGet(x => x.GuiderAlpacaHost).Returns("192.168.1.20");
            s.SetupGet(x => x.GuiderAlpacaPort).Returns(11111);
            return s.Object;
        }

        // ── §63.19: the guide train decides whether PHD2 gets a rotator at all ──

        [Test]
        public void Build_withholds_the_rotator_and_resets_the_guider_to_None_on_a_separate_guide_scope() {
            // The night of 2026-10-16: the imaging CAA was the guider's rotator, so a 49° Center-and-Rotate
            // rotated PHD2's calibration and every dither spiralled. A guide-scope train never pushes a rotator.
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuiderCamera).Returns("Alpaca Camera [bridge.local:11111/2]");
            s.SetupGet(x => x.GuiderRotator).Returns("Alpaca Rotator [bridge.local:11111/0]");
            s.SetupGet(x => x.GuiderSetupType).Returns(GuiderSettings.GuideScopeSetupType);
            var msgs = PHD2Guider.BuildGuiderEngineConfigMessages(s.Object);

            Assert.That(msgs.OfType<Phd2SetSelectedRotator>().Single().Parameters!.Rotator, Is.EqualTo(PHD2Guider.NoRotatorChoice),
                "the guider's own stored rotator must be cleared, not merely left alone");
            Assert.That(msgs.OfType<Phd2SetAlpacaServer>().Single().Parameters!.RotatorDevice, Is.Null,
                "no rotator device number rides along either");
        }

        [Test]
        public void Build_treats_a_missing_setup_type_as_a_separate_guide_scope() {
            // Moq's null default = a profile from before the field existed: the safe reading is guide scope.
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuiderCamera).Returns("Alpaca Camera");
            s.SetupGet(x => x.GuiderRotator).Returns("Alpaca Rotator");
            var rotator = PHD2Guider.BuildGuiderEngineConfigMessages(s.Object).OfType<Phd2SetSelectedRotator>().Single();
            Assert.That(rotator.Parameters!.Rotator, Is.EqualTo(PHD2Guider.NoRotatorChoice));
            // …but only once Ara owns the guider's equipment: with no guide camera picked nothing is sent.
            var unowned = new Mock<IGuiderSettings>();
            unowned.SetupGet(x => x.GuiderRotator).Returns("Alpaca Rotator");
            Assert.That(PHD2Guider.BuildGuiderEngineConfigMessages(unowned.Object).OfType<Phd2SetSelectedRotator>(), Is.Empty);
        }

        [Test]
        public void Build_pushes_the_rotator_only_for_an_off_axis_guider() {
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuiderRotator).Returns(" Alpaca Rotator ");
            s.SetupGet(x => x.GuiderSetupType).Returns("OAG");
            var rotator = PHD2Guider.BuildGuiderEngineConfigMessages(s.Object).OfType<Phd2SetSelectedRotator>().Single();
            Assert.That(rotator.Parameters!.Rotator, Is.EqualTo("Alpaca Rotator"));
            // An OAG with no rotator picked keeps the guider's own choice — "None" is only forced on a guide scope.
            var none = new Mock<IGuiderSettings>();
            none.SetupGet(x => x.GuiderSetupType).Returns(GuiderSettings.OagSetupType);
            Assert.That(PHD2Guider.BuildGuiderEngineConfigMessages(none.Object).OfType<Phd2SetSelectedRotator>(), Is.Empty);
        }

        // ── §63.17: equipment-selection messages ──

        [Test]
        public void Build_maps_equipment_selections_trimmed_and_ordered_first() {
            var msgs = PHD2Guider.BuildGuiderEngineConfigMessages(SettingsWithSelections());

            var alpaca = msgs.OfType<Phd2SetAlpacaServer>().Single();
            Assert.That(alpaca.Parameters!.Host, Is.EqualTo("192.168.1.20"));
            Assert.That(alpaca.Parameters.Port, Is.EqualTo(11111));
            // Choice strings are trimmed — the daemon matches them verbatim.
            Assert.That(msgs.OfType<Phd2SetSelectedCamera>().Single().Parameters!.Camera, Is.EqualTo("Alpaca Camera"));
            Assert.That(msgs.OfType<Phd2SetSelectedCameraId>().Single().Parameters!.CameraId, Is.EqualTo("cam-01"));
            Assert.That(msgs.OfType<Phd2SetSelectedMount>().Single().Parameters!.Mount, Is.EqualTo("On-camera"));
            Assert.That(msgs.OfType<Phd2SetSelectedAuxMount>().Single().Parameters!.AuxMount, Is.EqualTo("Alpaca Telescope"));
            Assert.That(msgs.OfType<Phd2SetSelectedRotator>().Single().Parameters!.Rotator, Is.EqualTo("Alpaca Rotator"));
            // Selections precede everything else (the daemon's flow selects devices before profile setup);
            // the alpaca server binding comes first of all.
            Assert.That(msgs[0], Is.InstanceOf<Phd2SetAlpacaServer>());
        }

        [Test]
        public void Build_skips_unset_selections_including_moq_null_defaults() {
            // The plain Settings() mock returns null for every §63.17 selection — none may be pushed
            // (a null/""/whitespace choice must never reach the daemon as a selection of "nothing").
            var msgs = PHD2Guider.BuildGuiderEngineConfigMessages(Settings());
            Assert.That(msgs.OfType<Phd2SetAlpacaServer>(), Is.Empty);
            Assert.That(msgs.OfType<Phd2SetSelectedCamera>(), Is.Empty);
            Assert.That(msgs.OfType<Phd2SetSelectedCameraId>(), Is.Empty);
            Assert.That(msgs.OfType<Phd2SetSelectedMount>(), Is.Empty);
            Assert.That(msgs.OfType<Phd2SetSelectedAuxMount>(), Is.Empty);
            // No guide camera picked = Ara doesn't own the guider's equipment, so not even the guide-scope
            // rotator reset is sent.
            Assert.That(msgs.OfType<Phd2SetSelectedRotator>(), Is.Empty);
        }

        [Test]
        public void Alpaca_server_message_omits_the_unset_half() {
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuiderAlpacaHost).Returns("sbc.local");
            // Port 0 = unset → must serialize as absent, not 0 (the daemon would reject port 0).
            var msgs = PHD2Guider.BuildGuiderEngineConfigMessages(s.Object);
            var alpaca = msgs.OfType<Phd2SetAlpacaServer>().Single();
            Assert.That(alpaca.Parameters!.Host, Is.EqualTo("sbc.local"));
            Assert.That(alpaca.Parameters.Port, Is.Null);
        }

        [Test]
        public void RequiresDisconnectedEquipment_covers_setup_and_every_selection_setter() {
            // Single source of truth for the push's disconnect window — every §63.17 selection message and
            // set_profile_setup force it; runtime-safe messages (algo params, dec mode) must not.
            var forced = PHD2Guider.BuildGuiderEngineConfigMessages(SettingsWithSelections());
            Assert.That(forced, Is.Not.Empty);
            Assert.That(forced.All(PHD2Guider.RequiresDisconnectedEquipment), Is.True);
            var runtimeSafe = PHD2Guider.BuildGuiderEngineConfigMessages(Settings(focal: 0, pixel: 0));
            Assert.That(runtimeSafe, Is.Not.Empty);
            Assert.That(runtimeSafe.Any(PHD2Guider.RequiresDisconnectedEquipment), Is.False);
        }

        [Test]
        public void Build_maps_profile_setup_algo_params_and_dec_mode() {
            var msgs = PHD2Guider.BuildGuiderEngineConfigMessages(Settings());

            // set_profile_setup with the configured focal + pixel.
            var setup = msgs.OfType<Phd2SetProfileSetup>().Single();
            Assert.That(setup.Parameters!.FocalLength, Is.EqualTo(250));
            Assert.That(setup.Parameters.PixelSize, Is.EqualTo(2.9));

            // Four algo params: ra/dec × aggression (PHD2's wire name for ARA's aggressiveness) +
            // ra/dec × minMove, with the profile values.
            var algo = msgs.OfType<Phd2SetAlgoParam>().ToList();
            Assert.That(algo.Count, Is.EqualTo(4));
            Assert.That(algo.Select(a => a.Parameters!.Name), Has.None.EqualTo("aggressiveness"),
                "the daemon has no such param; it answers 'could not set param'");
            var raAgg = algo.Single(a => a.Parameters!.Axis == "ra" && a.Parameters.Name == "aggression");
            Assert.That(raAgg.Parameters!.Value, Is.EqualTo(0.7));
            var decAgg = algo.Single(a => a.Parameters!.Axis == "dec" && a.Parameters.Name == "aggression");
            Assert.That(decAgg.Parameters!.Value, Is.EqualTo(0.65));
            var minMoves = algo.Where(a => a.Parameters!.Name == "minMove").ToList();
            Assert.That(minMoves.Count, Is.EqualTo(2));
            Assert.That(minMoves.Any(a => a.Parameters!.Axis == "ra"), Is.True);
            Assert.That(minMoves.Any(a => a.Parameters!.Axis == "dec"), Is.True);
            Assert.That(minMoves.All(a => a.Parameters!.Value == 0.15), Is.True);

            // set_dec_guide_mode mapped to PHD2 casing.
            var decMode = msgs.OfType<Phd2SetDecGuideMode>().Single();
            Assert.That(decMode.Parameters!.Mode, Is.EqualTo("North"));
        }

        [Test]
        public void Build_omits_profile_setup_when_focal_and_pixel_are_unset() {
            var msgs = PHD2Guider.BuildGuiderEngineConfigMessages(Settings(focal: 0, pixel: 0));
            Assert.That(msgs.OfType<Phd2SetProfileSetup>(), Is.Empty);
            // Algo + dec-mode still pushed.
            Assert.That(msgs.OfType<Phd2SetAlgoParam>().Count(), Is.EqualTo(4));
            Assert.That(msgs.OfType<Phd2SetDecGuideMode>().Count(), Is.EqualTo(1));
        }

        [Test]
        public void Build_pushes_each_axis_its_own_minimum_move() {
            // #1156 — Dec typically wants a larger dead band than RA (backlash); the two are
            // separate profile values and separate set_algo_param calls. An unset axis is left alone.
            var msgs = PHD2Guider.BuildGuiderEngineConfigMessages(Settings(minMove: 0.1, decMinMove: 0.3));
            var minMoves = msgs.OfType<Phd2SetAlgoParam>().Where(a => a.Parameters!.Name == "minMove").ToList();
            Assert.That(minMoves.Single(a => a.Parameters!.Axis == "ra").Parameters!.Value, Is.EqualTo(0.1));
            Assert.That(minMoves.Single(a => a.Parameters!.Axis == "dec").Parameters!.Value, Is.EqualTo(0.3));

            var raOnly = PHD2Guider.BuildGuiderEngineConfigMessages(Settings(minMove: 0.1, decMinMove: 0))
                .OfType<Phd2SetAlgoParam>().Where(a => a.Parameters!.Name == "minMove").ToList();
            Assert.That(raOnly.Select(a => a.Parameters!.Axis), Is.EqualTo(RaAxisOnly), "Dec at 0 = unset: not pushed");
        }

        private static readonly string[] RaAxisOnly = { "ra" };

        [Test]
        public void Build_with_all_unset_values_pushes_nothing() {
            // Every value at its unset/default sentinel (numerics 0, dec-mode "auto") → push nothing, leaving
            // PHD2's own configuration untouched rather than overwriting it with ARA's blank defaults.
            var msgs = PHD2Guider.BuildGuiderEngineConfigMessages(
                Settings(focal: 0, pixel: 0, ra: 0, dec: 0, minMove: 0, mode: "auto"));
            Assert.That(msgs, Is.Empty);
        }

        [Test]
        public void Build_pushes_dec_mode_only_when_it_is_not_auto() {
            // "auto" is both ARA's and PHD2's default → don't push (don't overwrite the user's PHD2 setting).
            Assert.That(PHD2Guider.BuildGuiderEngineConfigMessages(Settings(mode: "auto"))
                .OfType<Phd2SetDecGuideMode>(), Is.Empty);
            // An explicit mode IS pushed.
            var north = PHD2Guider.BuildGuiderEngineConfigMessages(Settings(mode: "north"))
                .OfType<Phd2SetDecGuideMode>().Single();
            Assert.That(north.Parameters!.Mode, Is.EqualTo("North"));
        }

        [Test]
        public void Build_pushes_only_pixel_size_when_focal_is_unset() {
            var setup = PHD2Guider.BuildGuiderEngineConfigMessages(Settings(focal: 0, pixel: 3.8))
                .OfType<Phd2SetProfileSetup>().Single();
            Assert.That(setup.Parameters!.FocalLength, Is.Null); // unset → not serialized
            Assert.That(setup.Parameters.PixelSize, Is.EqualTo(3.8));
        }

        // ── §63.20: cross-server Alpaca retargeting from the selection strings ──

        [Test]
        public void ParseAlpacaSelectionEndpoint_reads_the_suffix_or_returns_null() {
            Assert.That(PHD2Guider.ParseAlpacaSelectionEndpoint("Alpaca Camera [rc91.lan:6800/1]"),
                Is.EqualTo(("rc91.lan", 6800, 1)));
            Assert.That(PHD2Guider.ParseAlpacaSelectionEndpoint("Alpaca Mount [192.168.1.20:11111/0]  "),
                Is.EqualTo(("192.168.1.20", 11111, 0)));
            Assert.That(PHD2Guider.ParseAlpacaSelectionEndpoint("None"), Is.Null);
            Assert.That(PHD2Guider.ParseAlpacaSelectionEndpoint(null), Is.Null);
            Assert.That(PHD2Guider.ParseAlpacaSelectionEndpoint("Weird [h:0/0]"), Is.Null);
        }

        [Test]
        public void Unserved_alpaca_selection_is_described_when_no_host_or_port_is_stored() {
            // #1306 — the field case: the picker saved the daemon's bare choice strings and nothing else,
            // so the push left the guider on localhost:0.
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuiderCamera).Returns("Alpaca Camera");
            s.SetupGet(x => x.GuiderMount).Returns("Alpaca Mount");
            s.SetupGet(x => x.GuiderAlpacaHost).Returns("");
            s.SetupGet(x => x.GuiderAlpacaPort).Returns(0);
            var reason = PHD2Guider.DescribeUnservedAlpacaSelection(s.Object);
            Assert.That(reason, Does.Contain("guide camera 'Alpaca Camera'"));
            Assert.That(reason, Does.Contain("Alpaca host and port"));
        }

        [Test]
        public void Unserved_alpaca_selection_names_the_one_missing_half() {
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuiderMount).Returns("Alpaca Mount");
            s.SetupGet(x => x.GuiderAlpacaHost).Returns("127.0.0.1");
            s.SetupGet(x => x.GuiderAlpacaPort).Returns(0);
            var reason = PHD2Guider.DescribeUnservedAlpacaSelection(s.Object);
            Assert.That(reason, Does.Contain("guide mount 'Alpaca Mount'"));
            Assert.That(reason, Does.EndWith("Alpaca port."));
        }

        [Test]
        public void Suffixed_mount_beside_a_non_alpaca_camera_with_no_stored_server_is_flagged() {
            // Review r4: the camera alone carries the server into set_alpaca_server, so a suffixed mount
            // with a non-Alpaca camera and no host/port would also leave the guider on localhost:0.
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuiderCamera).Returns("Simulator");
            s.SetupGet(x => x.GuiderMount).Returns("Alpaca Mount [10.0.0.5:6800/1]");
            var reason = PHD2Guider.DescribeUnservedAlpacaSelection(s.Object);
            Assert.That(reason, Does.Contain("guide mount 'Alpaca Mount [10.0.0.5:6800/1]'"));
            Assert.That(reason, Does.Contain("10.0.0.5:6800"));
            Assert.That(reason, Does.Contain("set the Alpaca host and port to it"));
        }

        [Test]
        public void Served_or_non_alpaca_selections_are_not_flagged() {
            // Stored host/port, a suffixed camera (which carries its own server), and non-Alpaca drivers
            // are all pushable; blank selections never are a problem.
            Assert.That(PHD2Guider.DescribeUnservedAlpacaSelection(SettingsWithSelections()), Is.Null, "stored host/port");
            var suffixed = new Mock<IGuiderSettings>();
            suffixed.SetupGet(x => x.GuiderCamera).Returns("Alpaca Camera [127.0.0.1:6800/0]");
            suffixed.SetupGet(x => x.GuiderMount).Returns("Alpaca Mount");
            Assert.That(PHD2Guider.DescribeUnservedAlpacaSelection(suffixed.Object), Is.Null, "camera endpoint serves the bare mount");
            var indi = new Mock<IGuiderSettings>();
            indi.SetupGet(x => x.GuiderCamera).Returns("INDI Camera");
            indi.SetupGet(x => x.GuiderMount).Returns("On-camera");
            Assert.That(PHD2Guider.DescribeUnservedAlpacaSelection(indi.Object), Is.Null, "non-Alpaca drivers");
            Assert.That(PHD2Guider.DescribeUnservedAlpacaSelection(new Mock<IGuiderSettings>().Object), Is.Null, "nothing selected");
        }

        [Test]
        public void Build_retargets_the_alpaca_server_from_the_camera_selection() {
            // The camera lives on a DIFFERENT server (AlpacaBridge) than the stored alpaca host — the
            // camera's endpoint wins, and only same-server slots contribute device numbers.
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuiderCamera).Returns("Alpaca Camera [bridge.local:11111/2]");
            s.SetupGet(x => x.GuiderMount).Returns("Alpaca Mount [rc91.lan:6800/0]");
            s.SetupGet(x => x.GuiderRotator).Returns("Alpaca Rotator [bridge.local:11111/0]");
            s.SetupGet(x => x.GuiderSetupType).Returns(GuiderSettings.OagSetupType);
            s.SetupGet(x => x.GuiderAlpacaHost).Returns("rc91.lan");
            s.SetupGet(x => x.GuiderAlpacaPort).Returns(6800);
            var alpaca = PHD2Guider.BuildGuiderEngineConfigMessages(s.Object)
                .OfType<Phd2SetAlpacaServer>().Single().Parameters!;
            Assert.That(alpaca.Host, Is.EqualTo("bridge.local"));
            Assert.That(alpaca.Port, Is.EqualTo(11111));
            Assert.That(alpaca.CameraDevice, Is.EqualTo(2));
            Assert.That(alpaca.RotatorDevice, Is.EqualTo(0), "same server as the camera → device rides along");
            Assert.That(alpaca.TelescopeDevice, Is.Null, "mount is on a different server — its device must not be sent");
        }

        [Test]
        public void Build_falls_back_to_the_stored_alpaca_host_for_generic_selections() {
            // Pre-§63.20 selections have no [host:port/N] suffix — host/port come from the stored
            // settings and no device numbers are pushed (the daemon keeps its own).
            var alpaca = PHD2Guider.BuildGuiderEngineConfigMessages(SettingsWithSelections())
                .OfType<Phd2SetAlpacaServer>().Single().Parameters!;
            Assert.That(alpaca.Host, Is.EqualTo("192.168.1.20"));
            Assert.That(alpaca.Port, Is.EqualTo(11111));
            Assert.That(alpaca.CameraDevice, Is.Null);
            Assert.That(alpaca.TelescopeDevice, Is.Null);
            Assert.That(alpaca.RotatorDevice, Is.Null);
        }

        [Test]
        public void Build_suffixed_selections_carry_devices_on_the_stored_server() {
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuiderCamera).Returns("Alpaca Camera [rc91.lan:6800/1]");
            s.SetupGet(x => x.GuiderMount).Returns("Alpaca Mount [rc91.lan:6800/0]");
            var alpaca = PHD2Guider.BuildGuiderEngineConfigMessages(s.Object)
                .OfType<Phd2SetAlpacaServer>().Single().Parameters!;
            Assert.That(alpaca.Host, Is.EqualTo("rc91.lan"));
            Assert.That(alpaca.Port, Is.EqualTo(6800));
            Assert.That(alpaca.CameraDevice, Is.EqualTo(1));
            Assert.That(alpaca.TelescopeDevice, Is.EqualTo(0));
        }

        [Test]
        [TestCase("auto", "Auto")]
        [TestCase("north", "North")]
        [TestCase("south", "South")]
        [TestCase("off", "Off")]
        [TestCase("AUTO", "Auto")]   // case-insensitive
        [TestCase("bogus", "Auto")]  // unknown → Auto
        [TestCase(null, "Auto")]
        public void MapDecGuideMode_maps_to_phd2_casing(string? ara, string expected) {
            Assert.That(PHD2Guider.MapDecGuideMode(ara), Is.EqualTo(expected));
        }
    }
}
