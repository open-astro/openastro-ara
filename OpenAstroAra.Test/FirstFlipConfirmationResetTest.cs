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

namespace OpenAstroAra.Test {

    /// <summary>
    /// §58.8 — an optics-train change resets <c>first_flip_confirmed</c> (the safety net assumes
    /// the rig hasn't changed since the user watched a flip succeed; optics is the ARA profile's
    /// rig identity). An identical re-save keeps the confirmation — panel Save-without-changes
    /// must not re-arm the announce. Covered on both store implementations.
    /// </summary>
    [TestFixture]
    public class FirstFlipConfirmationResetTest {

        private static void AssertResetSemantics(IProfileStore store) {
            store.PutSafetyPolicies(store.GetSafetyPolicies() with { FirstFlipConfirmed = true });
            var optics = store.GetOpticsSettings();

            // Identical re-save (the panel's Save with nothing edited) keeps the confirmation.
            store.PutOpticsSettings(optics);
            Assert.That(store.GetSafetyPolicies().FirstFlipConfirmed, Is.True,
                "an identical optics re-save is not an equipment change");

            // A genuine train change re-arms the announce.
            store.PutOpticsSettings(optics with { FocalLengthMm = optics.FocalLengthMm + 100 });
            Assert.That(store.GetSafetyPolicies().FirstFlipConfirmed, Is.False,
                "a changed optics train invalidates the first-flip confirmation");
        }

        [Test]
        public void InMemory_store_resets_on_optics_change_only() =>
            AssertResetSemantics(new InMemoryProfileStore());

        [Test]
        public void File_store_resets_on_optics_change_only_and_persists_it() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-firstflip-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                var store = new FileProfileStore(dir);
                AssertResetSemantics(store);
                // The reset survives a reopen — it went through the same persist as the optics.
                var reopened = new FileProfileStore(dir);
                Assert.That(reopened.GetSafetyPolicies().FirstFlipConfirmed, Is.False);
            } finally {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
