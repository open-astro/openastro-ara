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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>#1267 item 5 — the saved-targets profile section: the validator's canonical form and its
    /// refusals, the lenient read-back, the profile.json round trip, and profile select.</summary>
    [TestFixture]
    public class SavedTargetsValidatorTest {

        private static readonly DateTimeOffset T0 = new(2026, 10, 18, 22, 0, 0, TimeSpan.Zero);

        private static readonly string[] AbCasThenWolf = { "ab cas", "Wolf 359" };
        private static readonly string[] AB = { "a", "b" };
        private static readonly string[] OtherThenGood = { "other", "good" };

        private static SavedTargetDto T(string name, double ra = 39.38, double dec = 71.30, string source = "simbad",
                int minutes = 0, params string[] aliases) =>
            new(name, aliases, ra, dec, source, T0.AddMinutes(minutes));

        [Test]
        public void Empty_and_null_normalise_to_empty() {
            Assert.That(SavedTargetsValidator.Normalize(null).Normalized!.Targets, Is.Empty);
            Assert.That(SavedTargetsValidator.Normalize(new SavedTargetsDto(null!)).Normalized!.Targets, Is.Empty);
            Assert.That(SavedTargetsValidator.Normalize(SavedTargetsDto.Empty).Error, Is.Null);
        }

        [Test]
        public void Newest_first_one_per_name_trimmed_and_wrapped() {
            var (n, err) = SavedTargetsValidator.Normalize(new SavedTargetsDto([
                T("  AB Cas ", minutes: 0, aliases: ["HIP 12235", " HIP 12235", "", "V* AB Cas"]),
                T("Wolf 359", ra: -195.88, dec: 7.01, minutes: 5),
                T("ab cas", minutes: 10, source: "index"),
            ]));
            Assert.That(err, Is.Null);
            Assert.That(n!.Targets.Select(t => t.Name), Is.EqualTo(AbCasThenWolf), "the newest AB Cas wins, newest first");
            Assert.That(n.Targets[1].RaDeg, Is.EqualTo(164.12).Within(1e-9), "RA wrapped into 0..360");
            Assert.That(n.Targets[0].Source, Is.EqualTo("index"));
        }

        [Test]
        public void Aliases_are_trimmed_deduplicated_and_capped() {
            var many = Enumerable.Range(0, 50).Select(i => $"ID {i}").ToArray();
            var n = SavedTargetsValidator.Normalize(new SavedTargetsDto([T("x", aliases: many)])).Normalized!;
            Assert.That(n.Targets[0].Aliases, Has.Count.EqualTo(SavedTargetsValidator.MaxAliases));
            var dup = SavedTargetsValidator.Normalize(new SavedTargetsDto([T("x", aliases: ["a", " a ", "b"])])).Normalized!;
            Assert.That(dup.Targets[0].Aliases, Is.EqualTo(AB));
        }

        [Test]
        public void Refusals_name_the_problem() {
            Assert.That(SavedTargetsValidator.Normalize(new SavedTargetsDto([T("  ")])).Error, Does.Contain("no name"));
            Assert.That(SavedTargetsValidator.Normalize(new SavedTargetsDto([T("x", dec: 91)])).Error, Does.Contain("position"));
            Assert.That(SavedTargetsValidator.Normalize(new SavedTargetsDto([T("x", ra: double.NaN)])).Error, Does.Contain("position"));
            Assert.That(SavedTargetsValidator.Normalize(new SavedTargetsDto([T("x", source: "gaia")])).Error, Does.Contain("unknown source"));
            Assert.That(SavedTargetsValidator.Normalize(new SavedTargetsDto([T("a\nb")])).Error, Does.Contain("control"));
            Assert.That(SavedTargetsValidator.Normalize(new SavedTargetsDto([T(new string('n', 129))])).Error, Does.Contain("128"));
            var tooMany = Enumerable.Range(0, SavedTargetsValidator.MaxTargets + 1).Select(i => T($"t{i}")).ToArray();
            Assert.That(SavedTargetsValidator.Normalize(new SavedTargetsDto(tooMany)).Error, Does.Contain("at most 200"));
            Assert.That(SavedTargetsValidator.Normalize(new SavedTargetsDto([null!])).Error, Does.Contain("null"));
        }

        [Test]
        public void Lenient_read_back_drops_the_bad_entries_and_keeps_the_rest() {
            var n = SavedTargetsValidator.NormalizeLenient(new SavedTargetsDto([T("good"), T(" "), T("bad", dec: 95), T("other", minutes: 1)]));
            Assert.That(n.Targets.Select(t => t.Name), Is.EqualTo(OtherThenGood));
            Assert.That(SavedTargetsValidator.NormalizeLenient(null).Targets, Is.Empty);
        }
    }

    [TestFixture]
    [Category("IO")] // #1265 — real disk
    public class SavedTargetsStoreTest {

        private static readonly DateTimeOffset T0 = new(2026, 10, 18, 22, 0, 0, TimeSpan.Zero);

        private static readonly string[] AbCasThenField = { "AB Cas", "Field 1" };
        private static readonly string[] AbCasAliases = { "HIP 12235", "V* AB Cas" };
        private static readonly string[] OkOnly = { "ok" };

        private static SavedTargetsDto Two() => new([
            new SavedTargetDto("AB Cas", ["HIP 12235", "V* AB Cas"], 39.38, 71.30, "simbad", T0.AddMinutes(1), false),
            new SavedTargetDto("Field 1", [], 10.5, -5.25, "typed", T0, true),
        ]);

        private static string TempDir() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-saved-targets-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Test]
        public void A_fresh_store_has_none() {
            Assert.That(new InMemoryProfileStore().GetSavedTargets().Targets, Is.Empty);
        }

        [Test]
        public void Targets_round_trip_through_profile_json_in_snake_case() {
            var dir = TempDir();
            try {
                new FileProfileStore(dir).PutSavedTargets(Two());
                var json = File.ReadAllText(Path.Combine(dir, "profile.json"));
                Assert.That(json, Does.Contain("\"saved_targets\"").And.Contain("\"typed_as_j_now\": true").And.Contain("\"source\": \"simbad\""));
                var back = new FileProfileStore(dir).GetSavedTargets();
                Assert.That(back.Targets.Select(t => t.Name), Is.EqualTo(AbCasThenField));
                Assert.That(back.Targets[0].Aliases, Is.EqualTo(AbCasAliases));
                Assert.That(back.Targets[1].TypedAsJNow, Is.True);
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void A_pre_1267_file_and_a_damaged_entry_read_leniently() {
            var dir = TempDir();
            try {
                File.WriteAllText(Path.Combine(dir, "profile.json"), "{}");
                Assert.That(new FileProfileStore(dir).GetSavedTargets().Targets, Is.Empty);
                File.WriteAllText(Path.Combine(dir, "profile.json"),
                    "{\"saved_targets\": {\"targets\": [null, {\"name\": \"\", \"ra_deg\": 1, \"dec_deg\": 1, \"source\": \"typed\", \"saved_utc\": \"2026-10-18T22:00:00Z\"}, "
                    + "{\"name\": \"ok\", \"aliases\": null, \"ra_deg\": 1, \"dec_deg\": 1, \"source\": \"typed\", \"saved_utc\": \"2026-10-18T22:00:00Z\"}]}}");
                var back = new FileProfileStore(dir).GetSavedTargets();
                Assert.That(back.Targets.Select(t => t.Name), Is.EqualTo(OkOnly));
                Assert.That(back.Targets[0].Aliases, Is.Empty);
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Profile_select_carries_the_list_and_a_blank_profile_clears_it() {
            var a = new InMemoryProfileStore();
            a.PutSavedTargets(Two());
            var snap = ProfileStoreSnapshot.Capture(a);
            var b = new InMemoryProfileStore();
            ProfileStoreSnapshot.Apply(b, snap);
            Assert.That(b.GetSavedTargets().Targets, Has.Count.EqualTo(2));
            ProfileStoreSnapshot.Apply(b, snap with { SavedTargets = null });
            Assert.That(b.GetSavedTargets().Targets, Is.Empty);
        }
    }
}
