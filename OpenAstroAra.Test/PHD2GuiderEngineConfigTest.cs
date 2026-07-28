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
                double dec = 0.65, double minMove = 0.15, string mode = "north") {
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuideFocalLength).Returns(focal);
            s.SetupGet(x => x.GuidePixelSize).Returns(pixel);
            s.SetupGet(x => x.RAAggressiveness).Returns(ra);
            s.SetupGet(x => x.DecAggressiveness).Returns(dec);
            s.SetupGet(x => x.MinimumMove).Returns(minMove);
            s.SetupGet(x => x.DecGuideMode).Returns(mode);
            return s.Object;
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
            s.SetupGet(x => x.GuiderAlpacaHost).Returns("192.168.1.20");
            s.SetupGet(x => x.GuiderAlpacaPort).Returns(11111);
            return s.Object;
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

            // Four algo params: ra/dec × aggressiveness + ra/dec × minMove, with the profile values.
            var algo = msgs.OfType<Phd2SetAlgoParam>().ToList();
            Assert.That(algo.Count, Is.EqualTo(4));
            var raAgg = algo.Single(a => a.Parameters!.Axis == "ra" && a.Parameters.Name == "aggressiveness");
            Assert.That(raAgg.Parameters!.Value, Is.EqualTo(0.7));
            var decAgg = algo.Single(a => a.Parameters!.Axis == "dec" && a.Parameters.Name == "aggressiveness");
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
