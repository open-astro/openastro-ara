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
        public void Build_omits_aggressiveness_when_zero_but_still_pushes_minmove() {
            // An all-defaults-zero profile (aggressiveness 0) must NOT push aggressiveness=0 (that would
            // disable PHD2 corrections); minMove=0 is a valid value and is still pushed.
            var msgs = PHD2Guider.BuildGuiderEngineConfigMessages(
                Settings(focal: 0, pixel: 0, ra: 0, dec: 0, minMove: 0, mode: "auto"));

            var algo = msgs.OfType<Phd2SetAlgoParam>().ToList();
            Assert.That(algo.Any(a => a.Parameters!.Name == "aggressiveness"), Is.False);
            var minMoves = algo.Where(a => a.Parameters!.Name == "minMove").ToList();
            Assert.That(minMoves.Count, Is.EqualTo(2));
            Assert.That(minMoves.All(a => a.Parameters!.Value == 0.0), Is.True);
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
