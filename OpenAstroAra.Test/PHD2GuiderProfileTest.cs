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
    }
}
