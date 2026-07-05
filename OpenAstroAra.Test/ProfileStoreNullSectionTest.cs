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
using System.IO;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Regression: a saved multi-profile snapshot whose <c>camera_electronics</c> section is
    /// null (older profiles, or a hand-edited <c>"camera_electronics": null</c>) is pushed
    /// straight into the live store by <see cref="ProfileStoreSnapshot.Apply"/> on profile
    /// select — overwriting the load-time back-fill. The store must still honor the
    /// <see cref="IProfileStore"/> non-null contract, or the optimal-sub calculator
    /// (<c>OptimalSubOverrides.Build</c> derefs <c>GetCameraElectronics()</c>) 500s with an NRE.
    /// </summary>
    [TestFixture]
    public class ProfileStoreNullSectionTest {

        private static string TempDir() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-null-section-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Test]
        public void Applying_a_snapshot_with_a_null_electronics_section_still_reads_non_null() {
            var dir = TempDir();
            try {
                var store = new FileProfileStore(dir);
                // Exactly what ProfileStoreSnapshot.Apply does on profile-select for a saved
                // profile whose section is null: push a whole snapshot with the section nulled.
                var snap = ProfileStoreSnapshot.Capture(store) with { CameraElectronics = null! };
                ProfileStoreSnapshot.Apply(store, snap);

                Assert.That(store.GetCameraElectronics(), Is.Not.Null,
                    "the store must never hand a null section to a consumer (optimal-sub NRE'd on it)");
                Assert.That(store.GetCameraElectronics().ReadNoiseE, Is.EqualTo(0),
                    "a defaulted section reads as unset (0) — the Tier-0 fallback's 'assumed' path, not a crash");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Put_of_a_null_section_is_coalesced_not_stored() {
            var dir = TempDir();
            try {
                var store = new FileProfileStore(dir);
                store.PutCameraElectronics(null!);
                Assert.That(store.GetCameraElectronics(), Is.Not.Null);

                // A reopened store (fresh load of what Put persisted) is also non-null: the
                // persisted section is a real default object, never a JSON null.
                var reopened = new FileProfileStore(dir);
                Assert.That(reopened.GetCameraElectronics(), Is.Not.Null);
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Applying_a_snapshot_with_null_optics_filterset_and_stretch_reads_non_null() {
            // The #689 round-2 finding: the same Apply → Put bypass exists for EVERY
            // section, not just camera electronics — a null optics reaches
            // OptimalSubOverrides.Optics().ApertureMm, a null filter set reaches
            // set.Filters, both NREs. The write-path normalizer must cover them all.
            var dir = TempDir();
            try {
                var store = new FileProfileStore(dir);
                ProfileStoreSnapshot.Apply(store, ProfileStoreSnapshot.Capture(store) with {
                    Optics = null!,
                    FilterSet = null!,
                    StretchDefaults = null!,
                });

                Assert.Multiple(() => {
                    Assert.That(store.GetOpticsSettings(), Is.Not.Null);
                    Assert.That(store.GetOpticsSettings().ReducerFactor, Is.EqualTo(1.0),
                        "the coalesced optics default keeps reducer 1.0 (never a zero multiplier)");
                    Assert.That(store.GetFilterSet(), Is.Not.Null);
                    Assert.That(store.GetFilterSet().Filters, Is.Not.Null,
                        "the inner list normalizes too (a null list NREs the filter lookup)");
                    Assert.That(store.GetStretchDefaults(), Is.Not.Null);
                    Assert.That(store.GetStretchDefaults().ManualDefaultParams, Is.Not.Null);
                });
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Update_over_a_null_section_does_not_throw() {
            var dir = TempDir();
            try {
                var store = new FileProfileStore(dir);
                ProfileStoreSnapshot.Apply(store,
                    ProfileStoreSnapshot.Capture(store) with { CameraElectronics = null! });

                // The auto-populate updater does `current with { … }` — an NRE here would break
                // caching electronics on camera-connect for anyone with a null-section profile.
                var updated = store.UpdateCameraElectronics(c => c with { ReadNoiseE = 3.3 });
                Assert.That(updated.ReadNoiseE, Is.EqualTo(3.3));
                Assert.That(store.GetCameraElectronics().ReadNoiseE, Is.EqualTo(3.3));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
