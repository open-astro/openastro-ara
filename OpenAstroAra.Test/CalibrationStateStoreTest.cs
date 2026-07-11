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
    /// §30.7.4 (e-4b-2) — the calibration-state profile section: everything-invalid defaults, the
    /// round-trip through <see cref="FileProfileStore"/>'s profile.json, normalizer back-fill for
    /// older/partial files, the profile-select Capture/Apply path, and the share-export strip.
    /// The stamp itself (a completed guider build writes the entry) is covered by the
    /// <see cref="GuiderServiceDarkLibraryProgressTest"/> bench.
    /// </summary>
    [TestFixture]
    public class CalibrationStateStoreTest {

        private static string TempDir() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-cal-state-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static CalibrationStateDto BothBuilt() => new(
            DarkLibrary: new GuiderCalibrationEntryDto(Valid: true, LastBuiltAt: new DateTimeOffset(2026, 7, 9, 3, 0, 0, TimeSpan.Zero)),
            DefectMap: new GuiderCalibrationEntryDto(Valid: true, LastBuiltAt: new DateTimeOffset(2026, 7, 9, 4, 0, 0, TimeSpan.Zero)));

        [Test]
        public void A_fresh_store_reads_everything_invalid() {
            var store = new InMemoryProfileStore();
            var state = store.GetCalibrationState();
            Assert.That(state.DarkLibrary.Valid, Is.False);
            Assert.That(state.DarkLibrary.LastBuiltAt, Is.Null);
            Assert.That(state.DefectMap.Valid, Is.False);
            Assert.That(state.DefectMap.LastBuiltAt, Is.Null);
        }

        [Test]
        public void State_round_trips_through_profile_json() {
            var dir = TempDir();
            try {
                var store = new FileProfileStore(dir);
                var state = BothBuilt();
                store.PutCalibrationState(state);
                Assert.That(store.GetCalibrationState(), Is.EqualTo(state));

                // A reopened store re-reads profile.json from disk through the source-gen serializer —
                // the real restart path (§63.6 step 6: the record must survive restarts).
                var reopened = new FileProfileStore(dir);
                Assert.That(reopened.GetCalibrationState(), Is.EqualTo(state));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void An_older_profile_json_without_the_section_reads_everything_invalid() {
            var dir = TempDir();
            try {
                // A pre-e-4b-2 profile file: valid JSON, no calibration_state key anywhere.
                File.WriteAllText(Path.Combine(dir, "profile.json"), "{}");
                var store = new FileProfileStore(dir);
                Assert.That(store.GetCalibrationState(), Is.EqualTo(CalibrationStateDto.Empty),
                    "a missing section must normalize to the everything-invalid default, never throw");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void A_partial_block_back_fills_the_missing_entry() {
            var dir = TempDir();
            try {
                // A hand-edited/older file carrying only the dark_library entry — the normalizer must
                // back-fill defect_map to never-built rather than surface a null inner record.
                File.WriteAllText(Path.Combine(dir, "profile.json"),
                    """{"calibration_state":{"dark_library":{"valid":true,"last_built_at":"2026-07-09T03:00:00+00:00"}}}""");
                var store = new FileProfileStore(dir);
                var state = store.GetCalibrationState();
                Assert.That(state.DarkLibrary.Valid, Is.True);
                Assert.That(state.DarkLibrary.LastBuiltAt, Is.EqualTo(new DateTimeOffset(2026, 7, 9, 3, 0, 0, TimeSpan.Zero)));
                Assert.That(state.DefectMap, Is.Not.Null, "the missing entry must back-fill, not stay null");
                Assert.That(state.DefectMap.Valid, Is.False);
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Snapshot_capture_and_apply_carry_the_state_across_profile_select() {
            var built = new InMemoryProfileStore();
            built.PutCalibrationState(BothBuilt());
            var snap = ProfileStoreSnapshot.Capture(built);
            Assert.That(snap.CalibrationState, Is.EqualTo(BothBuilt()));

            var target = new InMemoryProfileStore();
            ProfileStoreSnapshot.Apply(target, snap);
            Assert.That(target.GetCalibrationState(), Is.EqualTo(BothBuilt()));
        }

        [Test]
        public void Applying_a_never_built_snapshot_clears_the_live_state() {
            // Switching to a profile that never built darks must clear the live stamps — inheriting the
            // previous profile's "valid" would claim a dark library that profile never built.
            var live = new InMemoryProfileStore();
            live.PutCalibrationState(BothBuilt());

            ProfileStoreSnapshot.Apply(live, ProfileStoreSnapshot.Capture(new InMemoryProfileStore()));

            Assert.That(live.GetCalibrationState(), Is.EqualTo(CalibrationStateDto.Empty));
        }

        [Test]
        public void Profile_share_export_strips_the_state() {
            // The donor's dark library / defect map live on the donor's guider host — "valid" on the
            // recipient's rig would be a lie, same reasoning as the focus-calibration strip.
            var live = new InMemoryProfileStore();
            live.PutCalibrationState(BothBuilt());
            var stripped = ProfileShareService.StripForShare(ProfileStoreSnapshot.Capture(live));
            Assert.That(stripped.CalibrationState, Is.EqualTo(CalibrationStateDto.Empty));
        }
    }
}
