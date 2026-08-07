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
using OpenAstroAra.Server.Services;
using System;
using System.IO;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §29.2 — the filename template names what lands on disk. These pin the
    /// contract that a night reads like a night ("2026-08-03/Light/…"), that
    /// missing hardware leaves no separator scars, and that no template input
    /// can ever produce an unusable or escaping path.
    /// </summary>
    [TestFixture]
    public class FrameNamingTest {

        private static readonly char S = Path.DirectorySeparatorChar;

        /// <summary>2026-08-04 01:30 local — after midnight, same night as Aug 3.</summary>
        private static FrameNamingContext Ctx(
            string imageType = "light",
            string? filter = "L",
            double exposure = 180,
            double? temp = -10.2,
            string? target = null,
            int frameNr = 42) => new(
                ImageType: imageType,
                CapturedLocal: new DateTimeOffset(2026, 8, 4, 1, 30, 5, TimeSpan.FromHours(-6)),
                ExposureSec: exposure,
                Filter: filter,
                Gain: 100,
                Offset: 30,
                BinX: 1, BinY: 1,
                SensorTemp: temp,
                TargetName: target,
                FrameNumber: frameNr);

        [Test]
        public void Aras_own_language_is_words_in_braces() {
            var path = FrameNaming.ExpandRelativePath(
                "{night}/{type}/{datetime}_{filter}_{exposure}s", Ctx());
            Assert.That(path, Is.EqualTo($"2026-08-03{S}Light{S}2026-08-04_01-30-05_L_180s"));
        }

        [Test]
        public void Both_dialects_name_the_same_frame_identically() {
            var legacy = FrameNaming.ExpandRelativePath(
                @"$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s", Ctx());
            var ara = FrameNaming.ExpandRelativePath(
                "{night}/{type}/{datetime}_{filter}_{exposure}s", Ctx());
            Assert.That(ara, Is.EqualTo(legacy),
                "an imported NINA profile and its Ara translation must file frames identically");
        }

        [Test]
        public void The_inherited_default_template_reads_as_a_night() {
            var path = FrameNaming.ExpandRelativePath(
                @"$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s", Ctx());
            // 01:30 minus 12h lands on Aug 3 — the evening the night began.
            Assert.That(path, Is.EqualTo($"2026-08-03{S}Light{S}2026-08-04_01-30-05_L_180s"));
        }

        [Test]
        public void A_missing_filter_leaves_no_separator_scar() {
            var path = FrameNaming.ExpandRelativePath(
                "$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s", Ctx(filter: null));
            Assert.That(path, Is.EqualTo("2026-08-04_01-30-05_180s"),
                "the vanished token's separators collapse instead of leaving __");
        }

        [Test]
        public void An_empty_folder_level_is_dropped_not_kept_blank() {
            var path = FrameNaming.ExpandRelativePath(
                @"$$TARGETNAME$$\\$$IMAGETYPE$$\\$$FRAMENR$$", Ctx(target: null));
            Assert.That(path, Is.EqualTo($"Light{S}0042"),
                "no target must not create an empty directory level");
        }

        [Test]
        public void Target_folders_appear_when_a_target_exists() {
            var path = FrameNaming.ExpandRelativePath(
                @"$$TARGETNAME$$\\$$FRAMENR$$", Ctx(target: "M 31"));
            Assert.That(path, Is.EqualTo($"M 31{S}0042"));
        }

        [Test]
        public void Forward_slash_templates_mean_the_same_folders() {
            var back = FrameNaming.ExpandRelativePath(@"$$DATE$$\\$$FRAMENR$$", Ctx());
            var fwd = FrameNaming.ExpandRelativePath("$$DATE$$/$$FRAMENR$$", Ctx());
            Assert.That(fwd, Is.EqualTo(back));
        }

        [Test]
        public void Path_hostile_target_names_cannot_escape_or_break() {
            var path = FrameNaming.ExpandRelativePath(
                "$$TARGETNAME$$_$$FRAMENR$$", Ctx(target: @"NGC 7000 / ""Wall"": <north>?"));
            Assert.That(path, Does.Not.Contain('/').And.Not.Contain('"')
                .And.Not.Contain('<').And.Not.Contain('?').And.Not.Contain(':'));
        }

        [Test]
        public void Dotted_relative_escapes_are_neutralized() {
            var path = FrameNaming.ExpandRelativePath(@"..\\..\\$$FRAMENR$$", Ctx());
            Assert.That(path, Is.EqualTo("0042"),
                "'..' segments trim to nothing — a template cannot climb out of the store");
        }

        [Test]
        public void Exposure_is_a_bare_number_because_the_template_writes_the_s() {
            // The inherited default ends "$$EXPOSURETIME$$s" — a token that
            // appended its own unit produced "180ss".
            Assert.That(FrameNaming.ExpandRelativePath("$$EXPOSURETIME$$s", Ctx(exposure: 180)), Is.EqualTo("180s"));
            Assert.That(FrameNaming.ExpandRelativePath("$$EXPOSURETIME$$s", Ctx(exposure: 12.5)), Is.EqualTo("12.5s"));
            Assert.That(FrameNaming.ExpandRelativePath("$$EXPOSURETIME$$s", Ctx(exposure: 0.001)), Is.EqualTo("0.001s"));
        }

        [Test]
        public void Sensor_temperature_rounds_to_whole_degrees() {
            Assert.That(FrameNaming.ExpandRelativePath("$$SENSORTEMP$$", Ctx(temp: -10.2)), Is.EqualTo("-10C"));
        }

        [Test]
        public void A_negative_temperature_keeps_its_sign_mid_name() {
            // "_-" is usually a separator scar — but not when the '-' starts a
            // number. Collapsing it here silently reported the wrong temperature.
            var path = FrameNaming.ExpandRelativePath(
                "$$EXPOSURETIME$$s_$$SENSORTEMP$$_$$FRAMENR$$", Ctx(temp: -10.2));
            Assert.That(path, Is.EqualTo("180s_-10C_0042"));
        }

        [Test]
        public void A_vanished_token_before_a_dash_still_collapses() {
            var path = FrameNaming.ExpandRelativePath(
                "$$HOCUSPOCUS$$_-extra_$$FRAMENR$$", Ctx());
            Assert.That(path, Is.EqualTo("extra_0042"));
        }

        [Test]
        public void Unknown_tokens_vanish_rather_than_leak() {
            var path = FrameNaming.ExpandRelativePath("$$HOCUSPOCUS$$_$$FRAMENR$$", Ctx());
            Assert.That(path, Is.EqualTo("0042"));
        }

        [Test]
        public void Nothing_usable_returns_null_for_the_id_fallback() {
            Assert.That(FrameNaming.ExpandRelativePath(null, Ctx()), Is.Null);
            Assert.That(FrameNaming.ExpandRelativePath("", Ctx()), Is.Null);
            Assert.That(FrameNaming.ExpandRelativePath("$$TARGETNAME$$", Ctx(target: null)), Is.Null);
        }

        [Test]
        public void Image_type_is_capitalized_for_people() {
            Assert.That(FrameNaming.ExpandRelativePath("$$IMAGETYPE$$", Ctx(imageType: "LIGHT")), Is.EqualTo("Light"));
            Assert.That(FrameNaming.ExpandRelativePath("$$IMAGETYPE$$", Ctx(imageType: "dark")), Is.EqualTo("Dark"));
        }
    }
}
