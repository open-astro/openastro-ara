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

namespace OpenAstroAra.Test {

    /// <summary>
    /// §63.4 (guider-e-3a) — the pure ARA-profile → <c>ara-&lt;slug&gt;</c> PHD2-profile name mapping.
    /// </summary>
    [TestFixture]
    public class PHD2GuiderProfileTest {

        [Test]
        public void Maps_playbook_examples() {
            // §63.4 worked examples (this slug keeps filler words like "on" — deterministic, not hand-shortened).
            Assert.That(PHD2Guider.AraGuiderProfileName("C14 on CEM120"), Is.EqualTo("ara-c14-on-cem120"));
            Assert.That(PHD2Guider.AraGuiderProfileName("RedCat on HEQ5"), Is.EqualTo("ara-redcat-on-heq5"));
            Assert.That(PHD2Guider.AraGuiderProfileName("Field rig AM5"), Is.EqualTo("ara-field-rig-am5"));
        }

        [Test]
        public void Lowercases_and_collapses_separator_runs() {
            Assert.That(PHD2Guider.AraGuiderProfileName("My   Rig__#1"), Is.EqualTo("ara-my-rig-1"));
            Assert.That(PHD2Guider.AraGuiderProfileName("UPPER"), Is.EqualTo("ara-upper"));
        }

        [Test]
        public void Trims_leading_and_trailing_separators() {
            Assert.That(PHD2Guider.AraGuiderProfileName("  -RedCat-  "), Is.EqualTo("ara-redcat"));
            Assert.That(PHD2Guider.AraGuiderProfileName("...rig..."), Is.EqualTo("ara-rig"));
        }

        [Test]
        public void Empty_or_separator_only_names_fall_back_to_default() {
            Assert.That(PHD2Guider.AraGuiderProfileName(null), Is.EqualTo("ara-default"));
            Assert.That(PHD2Guider.AraGuiderProfileName(""), Is.EqualTo("ara-default"));
            Assert.That(PHD2Guider.AraGuiderProfileName("   "), Is.EqualTo("ara-default"));
            Assert.That(PHD2Guider.AraGuiderProfileName("!!!"), Is.EqualTo("ara-default"));
        }

        [Test]
        public void Non_ascii_letters_collapse_to_separators() {
            // Not transliterated — the slug is an internal PHD2 identifier, so non-ASCII is treated like any
            // other separator. "Rødt" → r [ø=sep] dt.
            Assert.That(PHD2Guider.AraGuiderProfileName("Rødt teleskop"), Is.EqualTo("ara-r-dt-teleskop"));
        }

        [Test]
        public void Is_deterministic_and_idempotent_on_an_already_slugged_name() {
            var once = PHD2Guider.AraGuiderProfileName("C14 on CEM120");
            // Re-slugging the slug (minus the prefix) is stable — important since the wiring compares against
            // the daemon's stored profile names.
            Assert.That(PHD2Guider.AraGuiderProfileName("c14-on-cem120"), Is.EqualTo(once));
        }

        // ── §63.4 guider-e-3c: the id-suffixed, collision-free name + the connect-time selection decision ──

        private static readonly System.Guid RigId = System.Guid.Parse("a3f8e1c2-1111-2222-3333-444455556666");
        // The resolver now targets the ARA profile's DISPLAY name verbatim; the legacy
        // id-suffixed form is kept only for migration matching.
        private static string RigName => PHD2Guider.AraGuiderDisplayProfileName("Rig");
        private static string RigLegacyName => PHD2Guider.AraGuiderProfileName("Rig", RigId);

        private static System.Collections.Generic.List<Phd2Profile> Profiles(params (int id, string name)[] ps) {
            var list = new System.Collections.Generic.List<Phd2Profile>();
            foreach (var (id, name) in ps) {
                list.Add(new Phd2Profile { Id = id, Name = name });
            }
            return list;
        }

        [Test]
        public void Id_suffixed_name_disambiguates_same_slug_profiles() {
            var a = System.Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
            var b = System.Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");
            // "C-14" and "C 14" slug to the same bare name...
            Assert.That(PHD2Guider.AraGuiderProfileName("C-14"), Is.EqualTo(PHD2Guider.AraGuiderProfileName("C 14")));
            // ...but the id suffix makes the per-profile names distinct, and deterministic for a given Id.
            Assert.That(PHD2Guider.AraGuiderProfileName("C-14", a), Is.EqualTo("ara-c-14-aaaaaaaa"));
            Assert.That(PHD2Guider.AraGuiderProfileName("C 14", b), Is.EqualTo("ara-c-14-bbbbbbbb"));
            Assert.That(PHD2Guider.AraGuiderProfileName("C-14", a),
                Is.Not.EqualTo(PHD2Guider.AraGuiderProfileName("C 14", b)));
        }

        [Test]
        public void Resolve_honors_explicit_PHD2ProfileId_override_over_the_name_mapping() {
            // Override set and not currently selected → switch by id (ignores ara-slug entirely).
            var r = PHD2Guider.ResolveAraProfileSelection(
                overrideProfileId: 7, selectedProfileId: 3, activeAraProfileName: "Rig", activeAraProfileId: RigId,
                availableProfiles: Profiles((3, "Default"), (7, "Custom")));
            Assert.That(r.Kind, Is.EqualTo(AraProfileActionKind.SelectById));
            Assert.That(r.Id, Is.EqualTo(7));
        }

        [Test]
        public void Resolve_override_already_selected_is_a_no_op() {
            var r = PHD2Guider.ResolveAraProfileSelection(
                overrideProfileId: 7, selectedProfileId: 7, activeAraProfileName: "Rig", activeAraProfileId: RigId,
                availableProfiles: Profiles((7, "Custom")));
            Assert.That(r.Kind, Is.EqualTo(AraProfileActionKind.None));
        }

        [Test]
        public void Resolve_selects_existing_ara_profile_by_name_when_not_current() {
            // No override; the id-suffixed ara name exists but a different profile is selected → select by name.
            var r = PHD2Guider.ResolveAraProfileSelection(
                overrideProfileId: null, selectedProfileId: 1, activeAraProfileName: "Rig", activeAraProfileId: RigId,
                availableProfiles: Profiles((1, "Default"), (2, RigName)));
            Assert.That(r.Kind, Is.EqualTo(AraProfileActionKind.SelectByName));
            Assert.That(r.Id, Is.EqualTo(2));
            Assert.That(r.Name, Is.EqualTo(RigName));
        }

        [Test]
        public void Resolve_no_op_when_ara_profile_already_selected() {
            var r = PHD2Guider.ResolveAraProfileSelection(
                overrideProfileId: null, selectedProfileId: 2, activeAraProfileName: "Rig", activeAraProfileId: RigId,
                availableProfiles: Profiles((1, "Default"), (2, RigName)));
            Assert.That(r.Kind, Is.EqualTo(AraProfileActionKind.None));
        }

        [Test]
        public void Resolve_creates_ara_profile_when_absent() {
            var r = PHD2Guider.ResolveAraProfileSelection(
                overrideProfileId: null, selectedProfileId: 1, activeAraProfileName: "RedCat on HEQ5",
                activeAraProfileId: RigId, availableProfiles: Profiles((1, "Some other rig")));
            Assert.That(r.Kind, Is.EqualTo(AraProfileActionKind.Create));
            // The twin carries the ARA profile name verbatim — what the user reads in the guider UI.
            Assert.That(r.Name, Is.EqualTo("RedCat on HEQ5"));
        }

        // ── display-name mapping + legacy migration ──

        [Test]
        public void Display_name_is_verbatim_with_a_fallback_for_empty() {
            Assert.That(PHD2Guider.AraGuiderDisplayProfileName("Backyard RC8"), Is.EqualTo("Backyard RC8"));
            Assert.That(PHD2Guider.AraGuiderDisplayProfileName("  padded  "), Is.EqualTo("padded"));
            Assert.That(PHD2Guider.AraGuiderDisplayProfileName(null), Is.EqualTo("Ara Default"));
            Assert.That(PHD2Guider.AraGuiderDisplayProfileName("   "), Is.EqualTo("Ara Default"));
        }

        [Test]
        public void Resolve_matches_the_display_twin_case_insensitively() {
            // The daemon's own name checks are case-insensitive (rename/create CmpNoCase), so the
            // resolver must treat "rig" as an existing "Rig" twin instead of trying to create it.
            var r = PHD2Guider.ResolveAraProfileSelection(
                overrideProfileId: null, selectedProfileId: 1, activeAraProfileName: "RIG",
                activeAraProfileId: RigId, availableProfiles: Profiles((1, "Default"), (2, "rig")));
            Assert.That(r.Kind, Is.EqualTo(AraProfileActionKind.SelectByName));
            Assert.That(r.Id, Is.EqualTo(2));
        }

        [Test]
        public void Resolve_migrates_a_legacy_twin_by_rename() {
            // No display-name twin, but the id-suffixed legacy twin exists → rename it in place
            // (dark library rides along) instead of creating an empty duplicate.
            var r = PHD2Guider.ResolveAraProfileSelection(
                overrideProfileId: null, selectedProfileId: 1, activeAraProfileName: "Rig",
                activeAraProfileId: RigId, availableProfiles: Profiles((1, "Default"), (5, RigLegacyName)));
            Assert.That(r.Kind, Is.EqualTo(AraProfileActionKind.RenameLegacy));
            Assert.That(r.Id, Is.EqualTo(5), "the legacy twin's daemon id, for rename_profile {id}");
            Assert.That(r.Name, Is.EqualTo("Rig"), "the new display name");
        }

        [Test]
        public void Legacy_matching_follows_the_id_suffix_not_the_slug() {
            // The twin was created when the ARA profile was named differently — the slug part no
            // longer matches, but the id8 suffix does, so the migration still finds it.
            var oldName = PHD2Guider.AraGuiderProfileName("Old Rig Name", RigId);
            Assert.That(PHD2Guider.IsLegacyAraGuiderProfileName(oldName, RigId), Is.True);
            var r = PHD2Guider.ResolveAraProfileSelection(
                overrideProfileId: null, selectedProfileId: 1, activeAraProfileName: "Renamed Rig",
                activeAraProfileId: RigId, availableProfiles: Profiles((1, "Default"), (9, oldName)));
            Assert.That(r.Kind, Is.EqualTo(AraProfileActionKind.RenameLegacy));
            Assert.That(r.Id, Is.EqualTo(9));
            Assert.That(r.Name, Is.EqualTo("Renamed Rig"));
        }

        [Test]
        public void Legacy_matching_rejects_other_profiles_twins_and_non_ara_names() {
            var other = System.Guid.Parse("cccccccc-0000-0000-0000-000000000000");
            Assert.That(PHD2Guider.IsLegacyAraGuiderProfileName(
                PHD2Guider.AraGuiderProfileName("Rig", other), RigId), Is.False,
                "a different ARA profile's twin must not be renamed away from it");
            Assert.That(PHD2Guider.IsLegacyAraGuiderProfileName("My Handmade Profile", RigId), Is.False);
            Assert.That(PHD2Guider.IsLegacyAraGuiderProfileName(null, RigId), Is.False);
        }
    }
}
