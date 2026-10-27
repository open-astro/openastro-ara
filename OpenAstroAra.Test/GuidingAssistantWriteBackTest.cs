#region "copyright"
/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>#1448 — the guider's post-Apply values → Ara's profile blocks. Pure.</summary>
    [TestFixture]
    public class GuidingAssistantWriteBackTest {

        private static Phd2SettingsDto Phd2() => new("localhost", 4400, "Ara", true, 1, 3, 1.5, 10, 60, false,
            GuideExposureMs: 1500, MinimumMove: 0.15, DecMinimumMove: 0.2, DecGuideMode: "auto");

        [TestCase("North", "north")]
        [TestCase("south", "south")]
        [TestCase(" AUTO ", "auto")]
        [TestCase("Off", "off")]
        [TestCase("Sideways", null)]
        [TestCase(null, null)]
        public void Dec_mode_names_map_from_the_guiders(string? guider, string? ara) =>
            Assert.That(GuiderService.DecGuideModeFromGuider(guider), Is.EqualTo(ara));

        [Test]
        public void Phd2_block_takes_each_reported_value() {
            var next = GuiderService.WithGuidingAssistantApplied(Phd2(), new GuidingAssistantAppliedValues {
                ExposureMs = 2000, MinMoveRaPx = 0.18, MinMoveDecPx = 0.32, DecGuideMode = "North",
            });
            Assert.That((next.GuideExposureMs, next.MinimumMove, next.DecMinimumMove, next.DecGuideMode),
                Is.EqualTo((2000, 0.18, (double?)0.32, "north")));
            Assert.That(next.Host, Is.EqualTo("localhost"), "the rest of the block is untouched");
        }

        [Test]
        public void Phd2_block_keeps_values_the_guider_did_not_report_or_reported_as_unset() {
            var before = Phd2();
            var next = GuiderService.WithGuidingAssistantApplied(before, new GuidingAssistantAppliedValues {
                ExposureMs = 0, MinMoveRaPx = double.NaN, MinMoveDecPx = -1, DecGuideMode = "garbage",
            });
            Assert.That(next, Is.EqualTo(before));
        }

        [Test]
        public void Advanced_block_takes_the_dec_algorithm_and_backlash_group() {
            var next = GuiderService.WithGuidingAssistantApplied(new GuiderAdvancedSettingsDto(MaxRaDurationMs: 2500),
                new GuidingAssistantAppliedValues {
                    DecAlgorithm = " Resist Switch ", BacklashEnabled = true, BacklashPulseMs = 1200, BacklashFloorMs = 20, BacklashCeilingMs = 2400,
                });
            Assert.That((next.DecAlgorithm, next.BacklashEnabled, next.BacklashPulseMs, next.BacklashFloorMs, next.BacklashCeilingMs, next.MaxRaDurationMs),
                Is.EqualTo(("Resist Switch", (bool?)true, (int?)1200, (int?)20, (int?)2400, (int?)2500)));
            Assert.That(GuiderAdvancedSettingsMapping.Validate(next), Is.Null, "what Ara stores passes its own write boundary");
        }

        [TestCase(null)]
        [TestCase(10)]
        [TestCase(11417)]
        public void Advanced_block_skips_backlash_without_a_pulse_in_the_guiders_range(int? pulse) {
            var before = new GuiderAdvancedSettingsDto(BacklashEnabled: false, BacklashPulseMs: 500);
            var next = GuiderService.WithGuidingAssistantApplied(before,
                new GuidingAssistantAppliedValues { BacklashEnabled = true, BacklashPulseMs = pulse });
            Assert.That(next, Is.EqualTo(before));
        }

        [Test]
        public void Advanced_block_clears_an_old_floor_and_ceiling_the_guider_did_not_confirm() {
            // An old 2000 ms floor beside a new 1200 ms pulse would fail the guider's [floor, pulse] check
            // on every later push; null leaves the guider's own pair.
            var next = GuiderService.WithGuidingAssistantApplied(
                new GuiderAdvancedSettingsDto(BacklashPulseMs: 3000, BacklashFloorMs: 2000, BacklashCeilingMs: 4000),
                new GuidingAssistantAppliedValues { BacklashEnabled = true, BacklashPulseMs = 1200 });
            Assert.That((next.BacklashPulseMs, next.BacklashFloorMs, next.BacklashCeilingMs), Is.EqualTo(((int?)1200, (int?)null, (int?)null)));
        }
    }
}
