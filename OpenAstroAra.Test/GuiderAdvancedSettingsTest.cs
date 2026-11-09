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

        private static readonly string[] DecModeOnly = { "set_dec_guide_mode" };

        [Test]
        public void Nothing_set_sends_nothing() {
            Assert.That(PHD2Guider.BuildAdvancedSettingsMessages(null), Is.Empty);
            Assert.That(PHD2Guider.BuildAdvancedSettingsMessages(new GuiderAdvancedSettings()), Is.Empty);
            // Only the Dec mode, which Ara always pushes (#1448); nothing else of an untouched profile.
            Assert.That(PHD2Guider.BuildGuiderEngineConfigMessages(Plain(), new GuiderAdvancedSettings()).Select(m => m.Method),
                Is.EqualTo(DecModeOnly), "an untouched profile never overwrites the guider's other settings");
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
        public void Multi_star_is_on_for_a_new_profile_and_an_unset_one_and_an_explicit_off_is_kept() {
            Assert.That(GuiderAdvancedSettingsMapping.ToEngine(new GuiderAdvancedSettingsDto()).MultiStarEnabled, Is.True, "new profile");
            Assert.That(GuiderAdvancedSettingsMapping.ToEngine(new GuiderAdvancedSettingsDto(MultiStarEnabled: null, MinStarSnr: 6)).MultiStarEnabled, Is.True, "null migrates to on");
            Assert.That(GuiderAdvancedSettingsMapping.ToEngine(new GuiderAdvancedSettingsDto(MultiStarEnabled: false)).MultiStarEnabled, Is.False, "explicit off");
            Assert.That(GuiderAdvancedSettingsMapping.ToEngine(new GuiderAdvancedSettingsDto(MultiStarEnabled: true)).MultiStarEnabled, Is.True);
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


        // #1748 — the Guider's limits, both sides of each boundary.
        private static string? Check(GuiderAdvancedSettingsDto d) => GuiderAdvancedSettingsMapping.Validate(d);

        [TestCase(6, false)] [TestCase(7, true)] [TestCase(50, true)] [TestCase(51, false)]
        public void Search_region_is_7_to_50(int px, bool ok) =>
            Assert.That(Check(new GuiderAdvancedSettingsDto(SearchRegionPx: px)), ok ? Is.Null : Does.Contain("search_region_px"));

        [TestCase(0.0, false)] [TestCase(0.01, true)] [TestCase(1.0, true)] [TestCase(1.01, false)]
        public void Mass_change_is_a_fraction_up_to_1(double v, bool ok) =>
            Assert.That(Check(new GuiderAdvancedSettingsDto(MassChangeThreshold: v)), ok ? Is.Null : Does.Contain("mass_change_threshold"));

        [Test]
        public void Star_hfd_needs_min_strictly_below_max() {
            Assert.That(Check(new GuiderAdvancedSettingsDto(MinStarHfd: 3, MaxStarHfd: 3)), Does.Contain("min_star_hfd"));
            Assert.That(Check(new GuiderAdvancedSettingsDto(MinStarHfd: 2.9, MaxStarHfd: 3)), Is.Null);
            Assert.That(Check(new GuiderAdvancedSettingsDto(MaxStarHfd: 0)), Does.Contain("max_star_hfd"));
        }

        [TestCase(100.0, true)] [TestCase(100.5, false)] [TestCase(0.0, false)]
        public void Tolerate_jumps_and_dither_scale_stop_at_100(double v, bool ok) {
            Assert.That(Check(new GuiderAdvancedSettingsDto(TolerateJumpsThresholdPx: v)), ok ? Is.Null : Does.Contain("tolerate_jumps_threshold_px"));
            Assert.That(Check(new GuiderAdvancedSettingsDto(DitherScaleFactor: v)), ok ? Is.Null : Does.Contain("dither_scale_factor"));
        }

        [Test]
        public void Backlash_floor_pulse_and_ceiling_are_checked_together() {
            Assert.That(Check(new GuiderAdvancedSettingsDto(BacklashFloorMs: 19)), Does.Contain("backlash_floor_ms"));
            Assert.That(Check(new GuiderAdvancedSettingsDto(BacklashFloorMs: 20)), Is.Null);
            Assert.That(Check(new GuiderAdvancedSettingsDto(BacklashCeilingMs: 8001)), Does.Contain("backlash_ceiling_ms"));
            Assert.That(Check(new GuiderAdvancedSettingsDto(BacklashCeilingMs: 8000)), Is.Null);
            Assert.That(Check(new GuiderAdvancedSettingsDto(BacklashPulseMs: 100, BacklashFloorMs: 101)), Does.Contain("backlash_floor_ms"));
            Assert.That(Check(new GuiderAdvancedSettingsDto(BacklashPulseMs: 100, BacklashFloorMs: 100, BacklashCeilingMs: 100)), Is.Null);
            Assert.That(Check(new GuiderAdvancedSettingsDto(BacklashPulseMs: 100, BacklashCeilingMs: 99)), Does.Contain("backlash_ceiling_ms"));
        }

        [TestCase(0.0, 0.0, true)] [TestCase(10.0, 120.0, true)] [TestCase(10.5, 120.0, false)]
        [TestCase(11.0, 120.0, false)] [TestCase(1.0, 121.0, false)] [TestCase(5.0, 4.0, false)]
        public void Variable_delay_is_whole_seconds_with_0_allowed(double shortS, double longS, bool ok) =>
            Assert.That(Check(new GuiderAdvancedSettingsDto(VariableDelayEnabled: true, VariableDelayShortS: shortS, VariableDelayLongS: longS)),
                ok ? Is.Null : Does.Contain("variable_delay"));

        [TestCase("Hysteresis", true)] [TestCase(" Lowpass2 ", true)] [TestCase("ResistSwitch", false)] [TestCase("Predictive PEC", true)]
        public void Ra_algorithm_must_be_one_the_guider_knows(string name, bool ok) =>
            Assert.That(Check(new GuiderAdvancedSettingsDto(RaAlgorithm: name)), ok ? Is.Null : Does.Contain("ra_algorithm"));

        [Test]
        public void Predictive_pec_is_refused_on_dec() {
            Assert.That(Check(new GuiderAdvancedSettingsDto(DecAlgorithm: "Predictive PEC")), Does.Contain("dec_algorithm"));
            Assert.That(Check(new GuiderAdvancedSettingsDto(DecAlgorithm: "ZFilter")), Is.Null);
        }

        [Test]
        public void One_bad_saved_value_leaves_the_rest_of_its_group_in_the_push() {
            var msgs = PHD2Guider.BuildAdvancedSettingsMessages(new GuiderAdvancedSettings { SearchRegionPx = 60, MinStarSnr = 8 });
            var detection = Params(msgs, "set_star_detection");
            Assert.That(detection, Does.ContainKey("MinStarSNR"));
            Assert.That(detection, Does.Not.ContainKey("SearchRegion"));
        }

        [Test]
        public void An_inconsistent_backlash_group_is_left_out_whole() {
            var msgs = PHD2Guider.BuildAdvancedSettingsMessages(new GuiderAdvancedSettings { BacklashEnabled = true, BacklashPulseMs = 100, BacklashFloorMs = 200 });
            Assert.That(Params(msgs, "set_backlash_comp"), Is.Null);
        }

        private static readonly string[] Lowpass2Names = ["algorithmName", "minMove", "aggressiveness"];
        private static readonly string[] OnlyName = ["algorithmName"];
        private static readonly string[] LowpassNames = ["algorithmName", "minMove", "slopeWeight"];

        [Test]
        public void Aggressiveness_resolves_from_the_names_the_guider_reports() {
            Assert.That(PHD2Guider.ResolveAggressiveness(null, 0.6), Is.EqualTo(("aggression", 0.6)), "question failed");
            Assert.That(PHD2Guider.ResolveAggressiveness(OnlyName, 0.6), Is.EqualTo(("aggression", 0.6)));
            Assert.That(PHD2Guider.ResolveAggressiveness(Lowpass2Names, 0.6)!.Value.Value, Is.EqualTo(60).Within(1e-9), "Lowpass2 is a percentage");
            Assert.That(PHD2Guider.ResolveAggressiveness(LowpassNames, 0.6), Is.Null);
        }


        [Test]
        public void A_left_out_value_names_the_setting_it_is_about_in_one_sentence() {
            var floor = GuiderAdvancedLimits.Apply(new GuiderAdvancedSettings { BacklashFloorMs = 200, BacklashCeilingMs = 100 }).Violations.Single();
            Assert.That((floor.Setting, floor.Reason), Is.EqualTo(("Backlash floor", "Must not exceed the ceiling. Not sent.")));
            var pulse = GuiderAdvancedLimits.Apply(new GuiderAdvancedSettings { BacklashEnabled = true }).Violations.Single();
            Assert.That(pulse.Reason, Does.StartWith("Is required (20–8000 ms)"));
        }

        [Test]
        public void The_dto_and_engine_record_map_both_ways() {
            var dto = new GuiderAdvancedSettingsDto(MultiStarEnabled: true, MinStarSnr: 6, MaxDecDurationMs: 1200, RaAlgorithm: "Hysteresis", DitherRaOnly: true);
            Assert.That(GuiderAdvancedSettingsMapping.ToDto(GuiderAdvancedSettingsMapping.ToEngine(dto)), Is.EqualTo(dto));
        }
    }
}
