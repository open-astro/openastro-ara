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
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §59.2 — the Smart Focus calibration persistence primitive: the daemon-owned
    /// <c>focus_calibration</c> profile section (null = "not calibrated"), its round-trip through
    /// <see cref="FileProfileStore"/>'s profile.json, the profile-select Capture/Apply path, and the
    /// bridge from stored sample DTOs back to a usable <see cref="FocusInverseMap"/>.
    /// </summary>
    [TestFixture]
    public class FocusCalibrationStoreTest {

        private static string TempDir() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-focus-cal-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            return dir;
        }

        // A synthetic V-curve calibration: HFR is parabolic about bestPosition, the other features vary
        // smoothly. Enough samples to satisfy FocusInverseMap.MinSamples with margin.
        private static FocusCalibrationDto Calibration(int bestPosition = 10_000, int steps = 4, int stepSize = 100) {
            var samples = new List<FocusCalibrationSampleDto>();
            for (int i = -steps; i <= steps; i++) {
                int position = bestPosition + i * stepSize;
                double delta = (position - bestPosition) / 100.0;
                double hfr = 1.5 + 0.2 * delta * delta;
                samples.Add(new FocusCalibrationSampleDto(
                    FocuserPosition: position,
                    StarCount: 42,
                    MedianHfr: hfr,
                    MedianFwhm: hfr * 2.0,
                    MedianRoundness: 0.9,
                    MedianPeakToBackground: 8.0,
                    MedianDonutOuterDiameter: Math.Abs(delta) * 6.0,
                    MedianDonutInnerDiameter: Math.Abs(delta) * 2.0,
                    MedianRingThickness: Math.Abs(delta) * 4.0,
                    MedianDonutShadowDepth: Math.Min(1.0, Math.Abs(delta) * 0.2)));
            }
            return new FocusCalibrationDto(
                Samples: samples,
                CalibratedUtc: new DateTimeOffset(2026, 7, 9, 3, 0, 0, TimeSpan.Zero),
                FocuserTemperatureC: 12.5,
                Filter: "L");
        }

        [Test]
        public void A_fresh_store_reads_null_meaning_not_calibrated() {
            var dir = TempDir();
            try {
                var store = new FileProfileStore(dir);
                Assert.That(store.GetFocusCalibration(), Is.Null);
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Calibration_round_trips_through_profile_json() {
            var dir = TempDir();
            try {
                var store = new FileProfileStore(dir);
                var cal = Calibration();
                store.PutFocusCalibration(cal);
                Assert.That(store.GetFocusCalibration(), Is.EqualTo(cal));

                // A reopened store re-reads profile.json from disk through the source-gen serializer —
                // the real restart path. Records compare by value, so sample-by-sample equality suffices.
                var reopened = new FileProfileStore(dir);
                var loaded = reopened.GetFocusCalibration();
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded!.Samples, Is.EqualTo(cal.Samples).AsCollection);
                Assert.That(loaded.CalibratedUtc, Is.EqualTo(cal.CalibratedUtc));
                Assert.That(loaded.FocuserTemperatureC, Is.EqualTo(12.5));
                Assert.That(loaded.Filter, Is.EqualTo("L"));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Put_null_clears_the_calibration_persistently() {
            var dir = TempDir();
            try {
                var store = new FileProfileStore(dir);
                store.PutFocusCalibration(Calibration());
                store.PutFocusCalibration(null); // the §59.15 recalibrate command's clear
                Assert.That(store.GetFocusCalibration(), Is.Null);

                var reopened = new FileProfileStore(dir);
                Assert.That(reopened.GetFocusCalibration(), Is.Null, "the clear must survive a restart");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void An_older_profile_json_without_the_section_loads_as_null() {
            var dir = TempDir();
            try {
                // A pre-§59.2 profile file: valid JSON, no focus_calibration key anywhere.
                File.WriteAllText(Path.Combine(dir, "profile.json"), "{}");
                var store = new FileProfileStore(dir);
                Assert.That(store.GetFocusCalibration(), Is.Null,
                    "a missing key must read as not-calibrated, never throw or back-fill");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Snapshot_capture_and_apply_carry_the_calibration_across_profile_select() {
            var calibrated = new InMemoryProfileStore();
            calibrated.PutFocusCalibration(Calibration());
            var snap = ProfileStoreSnapshot.Capture(calibrated);
            Assert.That(snap.FocusCalibration, Is.Not.Null);

            var target = new InMemoryProfileStore();
            ProfileStoreSnapshot.Apply(target, snap);
            Assert.That(target.GetFocusCalibration(), Is.EqualTo(snap.FocusCalibration));
        }

        [Test]
        public void Applying_a_never_calibrated_snapshot_clears_the_live_calibration() {
            // Switching to a never-calibrated profile must CLEAR the live store — keeping the previous
            // rig's calibration would feed Smart Focus a table fitted for a different optical train.
            var live = new InMemoryProfileStore();
            live.PutFocusCalibration(Calibration());

            var fresh = ProfileStoreSnapshot.Capture(new InMemoryProfileStore());
            Assert.That(fresh.FocusCalibration, Is.Null);
            ProfileStoreSnapshot.Apply(live, fresh);

            Assert.That(live.GetFocusCalibration(), Is.Null);
        }

        [Test]
        public void Profile_share_export_strips_the_calibration() {
            // A shared profile must not plant the donor's calibration on the recipient's rig — the map is
            // fitted to the donor's focuser + optical train and would mislead the one-frame runner.
            var live = new InMemoryProfileStore();
            live.PutFocusCalibration(Calibration());
            var stripped = ProfileShareService.StripForShare(ProfileStoreSnapshot.Capture(live));
            Assert.That(stripped.FocusCalibration, Is.Null);
        }

        [Test]
        public void Radial_skew_round_trips_and_a_pre_skew_dto_defaults_to_zero() {
            var dir = TempDir();
            try {
                // §59.3 — a signed skew survives the profile.json round-trip through the source-gen serializer…
                var sample = FocusCalibrationSampleDto.From(10_000, new FocusFeatureVector(
                    42, 2.0, 4.0, 0.9, 8.0, 12.0, 4.0, 8.0, 0.5, MedianRadialSkew: -0.37));
                var store = new FileProfileStore(dir);
                store.PutFocusCalibration(new FocusCalibrationDto(
                    new[] { sample }, new DateTimeOffset(2026, 7, 9, 3, 0, 0, TimeSpan.Zero), 12.5, "L"));
                var reopened = new FileProfileStore(dir);
                Assert.That(reopened.GetFocusCalibration()!.Samples[0].MedianRadialSkew, Is.EqualTo(-0.37));

                // …and a pre-skew DTO (the trailing param omitted, as an old profile.json deserializes)
                // reads 0 — the flat-0 value the side-classifier's separation gate deliberately ignores.
                var preSkew = new FocusCalibrationSampleDto(10_000, 42, 2.0, 4.0, 0.9, 8.0, 12.0, 4.0, 8.0, 0.5);
                Assert.That(preSkew.MedianRadialSkew, Is.EqualTo(0.0));
                Assert.That(preSkew.ToSample().Features.MedianRadialSkew, Is.EqualTo(0.0));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Telescope_type_defaults_to_other_and_round_trips_through_profile_json() {
            var dir = TempDir();
            try {
                // §59.4 — a DTO built without the trailing param (as a pre-§59.4 profile.json deserializes)
                // reads `other`, the assume-nothing type…
                var store = new FileProfileStore(dir);
                Assert.That(store.GetAutofocusSettings().TelescopeType, Is.EqualTo("other"));

                // …and a declared type survives the restart round-trip through the source-gen serializer.
                store.PutAutofocusSettings(store.GetAutofocusSettings() with { TelescopeType = "sct" });
                var reopened = new FileProfileStore(dir);
                Assert.That(reopened.GetAutofocusSettings().TelescopeType, Is.EqualTo("sct"));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Stored_samples_rebuild_a_usable_inverse_map() {
            // The persistence shape exists to feed FocusInverseMap.Build on a later session — prove the
            // DTO→sample bridge produces a map that predicts a sane move magnitude from a defocused frame.
            var cal = Calibration(bestPosition: 10_000);
            var map = FocusInverseMap.Build(cal.Samples.Select(s => s.ToSample()).ToList());
            Assert.That(map, Is.Not.Null, "a full sweep's stored samples must rebuild a usable map");
            Assert.That(map!.BestFocusOffset, Is.EqualTo(10_000).Within(30));

            // A frame measured at the +300 probe (HFR 1.5 + 0.2·3² = 3.3) should predict ≈ 300 steps.
            var defocused = new FocusFeatureVector(42, 3.3, 6.6, 0.9, 8.0, 18.0, 6.0, 12.0, 0.6, 0.0);
            var magnitude = map.PredictOffsetMagnitude(defocused);
            Assert.That(magnitude, Is.Not.Null);
            Assert.That(magnitude!.Value, Is.EqualTo(300).Within(40));
        }
    }
}
