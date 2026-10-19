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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System.Collections.Generic;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>#1364 item 3 — the guider settings beyond Tune Guiding: what reaches the guider, what the
    /// write boundary refuses, and that the block follows the profile through select and clone.</summary>
    [TestFixture]
    public class GuiderAdvancedSettingsTest {

        private static IGuiderSettings Plain() {
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.DecGuideMode).Returns("auto");
            return s.Object;
        }

        private static Dictionary<string, object>? Params(IReadOnlyList<Phd2Method> msgs, string method) =>
            msgs.OfType<Phd2NamedSetter>().FirstOrDefault(m => m.Method == method)?.Parameters;

        [Test]
        public void Nothing_set_sends_nothing() {
            Assert.That(PHD2Guider.BuildAdvancedSettingsMessages(null), Is.Empty);
            Assert.That(PHD2Guider.BuildAdvancedSettingsMessages(new GuiderAdvancedSettings()), Is.Empty);
            Assert.That(PHD2Guider.BuildGuiderEngineConfigMessages(Plain(), new GuiderAdvancedSettings()), Is.Empty,
                "an untouched profile never overwrites the guider");
        }

        [Test]
        public void A_backlash_enable_without_a_pulse_width_is_not_sent() {
            // The guider range-checks the pulse it already holds (0) against its floor (20) and refuses an
            // enable-only group, so an older profile in that state must not poison every push.
            var msgs = PHD2Guider.BuildAdvancedSettingsMessages(new GuiderAdvancedSettings { BacklashEnabled = true, SearchRegionPx = 15 });
            Assert.That(msgs.OfType<Phd2NamedSetter>().Select(m => m.Method), Does.Not.Contain("set_backlash_comp"));
            Assert.That(Params(msgs, "set_star_detection"), Is.EquivalentTo(new Dictionary<string, object> { ["SearchRegion"] = 15 }), "the rest of the push is unaffected");
            var off = PHD2Guider.BuildAdvancedSettingsMessages(new GuiderAdvancedSettings { BacklashEnabled = false });
            Assert.That(Params(off, "set_backlash_comp"), Is.EquivalentTo(new Dictionary<string, object> { ["Enabled"] = false }), "turning it off needs no pulse");
        }

        [Test]
        public void Only_the_keys_Ara_set_are_sent_in_their_guider_groups() {
            var msgs = PHD2Guider.BuildAdvancedSettingsMessages(new GuiderAdvancedSettings {
                MinStarSnr = 8, SearchRegionPx = 20,
                MaxDecDurationMs = 1500,
                BacklashEnabled = true, BacklashPulseMs = 450,
                NoiseReduction = "2x2 Mean",
                RaAlgorithm = "Predictive PEC", DecAlgorithm = "Resist Switch",
            });
            Assert.That(Params(msgs, "set_star_detection"), Is.EquivalentTo(new Dictionary<string, object> { ["MinStarSNR"] = 8.0, ["SearchRegion"] = 20 }));
            Assert.That(Params(msgs, "set_guide_limits"), Is.EquivalentTo(new Dictionary<string, object> { ["MaxDecDuration"] = 1500 }));
            Assert.That(Params(msgs, "set_backlash_comp"), Is.EquivalentTo(new Dictionary<string, object> { ["Enabled"] = true, ["PulseWidth"] = 450 }));
            Assert.That(Params(msgs, "set_noise_reduction"), Is.EquivalentTo(new Dictionary<string, object> { ["method"] = "2x2 Mean" }));
            var algos = msgs.OfType<Phd2NamedSetter>().Where(m => m.Method == "set_algo").Select(m => m.Parameters!).ToList();
            Assert.That(algos, Has.Count.EqualTo(2));
            Assert.That(algos[0], Is.EquivalentTo(new Dictionary<string, object> { ["axis"] = "ra", ["name"] = "Predictive PEC" }));
            Assert.That(algos[1], Is.EquivalentTo(new Dictionary<string, object> { ["axis"] = "dec", ["name"] = "Resist Switch" }));
            Assert.That(Params(msgs, "set_auto_exposure"), Is.Null, "a group with nothing set is not sent");
            Assert.That(Params(msgs, "set_mount_options"), Is.Null);
        }

        [Test]
        public void Variable_delay_goes_only_as_all_three() {
            Assert.That(Params(PHD2Guider.BuildAdvancedSettingsMessages(new GuiderAdvancedSettings { VariableDelayEnabled = true, VariableDelayShortS = 1 }),
                "set_variable_delay_settings"), Is.Null);
            Assert.That(Params(PHD2Guider.BuildAdvancedSettingsMessages(new GuiderAdvancedSettings {
                VariableDelayEnabled = true, VariableDelayShortS = 1, VariableDelayLongS = 4,
            }), "set_variable_delay_settings"), Has.Count.EqualTo(3));
        }

        [Test]
        public void Multi_star_rides_the_profile_setup_and_stays_out_of_the_runtime_push() {
            var advanced = new GuiderAdvancedSettings { MultiStarEnabled = false, MinStarSnr = 6 };
            var full = PHD2Guider.BuildGuiderEngineConfigMessages(Plain(), advanced);
            var setup = full.OfType<Phd2SetProfileSetup>().Single();
            Assert.That(setup.Parameters!.MultiStarEnabled, Is.False);
            Assert.That(setup.Parameters.FocalLength, Is.Null, "nothing else in the setup was set");
            Assert.That(PHD2Guider.RequiresDisconnectedEquipment(setup), Is.True);
            var tuning = PHD2Guider.BuildGuiderTuningMessages(Plain(), advanced);
            Assert.That(tuning.OfType<Phd2SetProfileSetup>(), Is.Empty, "Apply while guiding never opens the disconnect window");
            Assert.That(tuning.OfType<Phd2NamedSetter>().Select(m => m.Method), Does.Contain("set_star_detection"));
        }

        [Test]
        public void An_algorithm_change_is_sent_before_the_tune_guiding_params_so_they_land_on_it() {
            // set_algo replaces the axis's algorithm; params sent before it land on the old instance and the
            // new one runs with the guider's stored values instead of Ara's aggression / minimum move.
            var tuned = new Mock<IGuiderSettings>();
            tuned.SetupGet(x => x.DecGuideMode).Returns("auto");
            tuned.SetupGet(x => x.RAAggressiveness).Returns(0.7);
            tuned.SetupGet(x => x.DecAggressiveness).Returns(0.6);
            tuned.SetupGet(x => x.MinimumMove).Returns(0.15);
            tuned.SetupGet(x => x.DecMinimumMove).Returns(0.2);
            var advanced = new GuiderAdvancedSettings { RaAlgorithm = "Lowpass2", DecAlgorithm = "Resist Switch", MinStarSnr = 6 };
            foreach (var msgs in new[] {
                    PHD2Guider.BuildGuiderEngineConfigMessages(tuned.Object, advanced),
                    PHD2Guider.BuildGuiderTuningMessages(tuned.Object, advanced) }) {
                var methods = msgs.Select(m => m.Method).ToList();
                var lastAlgo = methods.LastIndexOf("set_algo");
                var firstParam = methods.IndexOf("set_algo_param");
                Assert.That(lastAlgo, Is.GreaterThanOrEqualTo(0));
                Assert.That(firstParam, Is.GreaterThanOrEqualTo(0));
                Assert.That(lastAlgo, Is.LessThan(firstParam), string.Join(", ", methods));
            }
        }

        [Test]
        public void The_write_boundary_refuses_values_the_guider_would_reject() {
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto()), Is.Null);
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto(MinStarSnr: 0)), Does.Contain("min_star_snr"));
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto(MaxRaDurationMs: -5)), Does.Contain("max_ra_duration_ms"));
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto(MaxDecDurationMs: 0)), Is.Null,
                "0 is a real max pulse to the guider (no Dec correction)");
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto(MaxRaDurationMs: 1_000_001)), Does.Contain("max_ra_duration_ms"));
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto(BacklashPulseMs: 10)), Does.Contain("backlash_pulse_ms"));
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto(BacklashPulseMs: 9000)), Does.Contain("backlash_pulse_ms"));
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto(BacklashPulseMs: 450)), Is.Null);
            // 2026-10-18: an enable with no pulse made the guider refuse the whole group on every push.
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto(BacklashEnabled: true)), Does.Contain("backlash_pulse_ms"));
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto(BacklashEnabled: true, BacklashPulseMs: 450)), Is.Null);
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto(BacklashEnabled: false)), Is.Null, "off needs no pulse");
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto(MinStarHfd: 9, MaxStarHfd: 3)), Does.Contain("min_star_hfd"));
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto(AutoExposureMinMs: 4000, AutoExposureMaxMs: 1000)), Does.Contain("auto_exposure_min_ms"));
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto(NoiseReduction: "median")), Does.Contain("noise_reduction"));
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto(VariableDelayEnabled: true)), Does.Contain("all three"));
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto(RaAlgorithm: " ")), Does.Contain("ra_algorithm"));
            Assert.That(GuiderAdvancedSettingsMapping.Validate(new GuiderAdvancedSettingsDto(
                MinStarSnr: 6, NoiseReduction: "3x3 Median", VariableDelayEnabled: false, VariableDelayShortS: 1, VariableDelayLongS: 5)), Is.Null);
        }

        [Test]
        public void The_dto_and_engine_record_map_both_ways() {
            var dto = new GuiderAdvancedSettingsDto(MultiStarEnabled: true, MinStarSnr: 6, MaxDecDurationMs: 1200, RaAlgorithm: "Hysteresis", DitherRaOnly: true);
            Assert.That(GuiderAdvancedSettingsMapping.ToDto(GuiderAdvancedSettingsMapping.ToEngine(dto)), Is.EqualTo(dto));
        }
    }
}
