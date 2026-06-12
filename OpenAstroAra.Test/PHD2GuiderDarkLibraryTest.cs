#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using Newtonsoft.Json;
using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §63.6 (guider-e-4b) — the send-site validation that guards <c>build_dark_library</c> before it reaches
    /// the socket, plus deserialization of the daemon's build result + calibration-status payloads.
    /// </summary>
    [TestFixture]
    public class PHD2GuiderDarkLibraryTest {

        private static readonly int[] ExpectedExposuresMs = [1000, 2000, 3000, 5000];

        // ── BuildDarkLibraryRequest: send-site validation ──

        [Test]
        public void Build_request_carries_validated_params() {
            var req = PHD2Guider.BuildDarkLibraryRequest(
                frameCount: 10, minExposureMs: 1000, maxExposureMs: 5000, clearExisting: true, notes: "rig A", loadAfter: false);
            Assert.That(req.Parameters!.FrameCount, Is.EqualTo(10));
            Assert.That(req.Parameters!.MinExposureMs, Is.EqualTo(1000));
            Assert.That(req.Parameters!.MaxExposureMs, Is.EqualTo(5000));
            Assert.That(req.Parameters!.ClearExisting, Is.True);
            Assert.That(req.Parameters!.Notes, Is.EqualTo("rig A"));
            Assert.That(req.Parameters!.LoadAfter, Is.False);
        }

        [Test]
        public void Build_request_blank_notes_become_null_so_no_empty_notes_field_is_sent() {
            var req = PHD2Guider.BuildDarkLibraryRequest(5, null, null, false, "   ", true);
            Assert.That(req.Parameters!.Notes, Is.Null);
        }

        [Test]
        public void Build_request_accepts_frame_count_boundaries() {
            Assert.That(PHD2Guider.BuildDarkLibraryRequest(1, null, null, false, null, true).Parameters!.FrameCount, Is.EqualTo(1));
            Assert.That(PHD2Guider.BuildDarkLibraryRequest(50, null, null, false, null, true).Parameters!.FrameCount, Is.EqualTo(50));
        }

        [Test]
        public void Build_request_rejects_out_of_range_frame_count() {
            Assert.Throws<ArgumentOutOfRangeException>(() => PHD2Guider.BuildDarkLibraryRequest(0, null, null, false, null, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => PHD2Guider.BuildDarkLibraryRequest(51, null, null, false, null, true));
        }

        [Test]
        public void Build_request_rejects_out_of_range_exposure_bounds() {
            Assert.Throws<ArgumentOutOfRangeException>(() => PHD2Guider.BuildDarkLibraryRequest(5, 0, null, false, null, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => PHD2Guider.BuildDarkLibraryRequest(5, null, 600001, false, null, true));
        }

        [Test]
        public void Build_request_rejects_min_greater_than_max() {
            Assert.Throws<ArgumentException>(() => PHD2Guider.BuildDarkLibraryRequest(5, 5000, 1000, false, null, true));
        }

        [Test]
        public void Build_request_accepts_equal_min_and_max() {
            var req = PHD2Guider.BuildDarkLibraryRequest(5, 3000, 3000, false, null, true);
            Assert.That(req.Parameters!.MinExposureMs, Is.EqualTo(3000));
            Assert.That(req.Parameters!.MaxExposureMs, Is.EqualTo(3000));
        }

        // ── Response deserialization ──

        [Test]
        public void Build_result_deserializes_from_daemon_payload() {
            const string json = """
                {"jsonrpc":"2.0","id":"abc","result":{"profile_id":3,"dark_library_path":"/darks/ara-rig.fits",
                "frame_count":5,"exposure_count":4,"exposures_ms":[1000,2000,3000,5000]}}
                """;
            var response = JsonConvert.DeserializeObject<Phd2BuildDarkLibraryResponse>(json);
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.error, Is.Null);
            var result = response.result!;
            Assert.That(result.ProfileId, Is.EqualTo(3));
            Assert.That(result.DarkLibraryPath, Is.EqualTo("/darks/ara-rig.fits"));
            Assert.That(result.FrameCount, Is.EqualTo(5));
            Assert.That(result.ExposureCount, Is.EqualTo(4));
            Assert.That(result.ExposuresMs, Is.EqualTo(ExpectedExposuresMs));
        }

        [Test]
        public void Calibration_status_deserializes_with_loaded_dark_stats() {
            const string json = """
                {"jsonrpc":"2.0","id":"xyz","result":{"profile_id":3,"dark_library_path":"/darks.fits",
                "defect_map_path":"/defect.fit","dark_library_exists":true,"defect_map_exists":false,
                "dark_library_compatible":true,"defect_map_compatible":false,"dark_library_loaded":true,
                "defect_map_loaded":false,"auto_load_darks":true,"auto_load_defect_map":false,
                "dark_count_loaded":8,"dark_min_exposure_seconds_loaded":1.0,"dark_max_exposure_seconds_loaded":5.0}}
                """;
            var status = JsonConvert.DeserializeObject<Phd2CalibrationFilesStatusResponse>(json)!.result!;
            Assert.That(status.ProfileId, Is.EqualTo(3));
            Assert.That(status.DarkLibraryExists, Is.True);
            Assert.That(status.DefectMapExists, Is.False);
            Assert.That(status.DarkLibraryLoaded, Is.True);
            Assert.That(status.AutoLoadDarks, Is.True);
            Assert.That(status.DarkCountLoaded, Is.EqualTo(8));
            Assert.That(status.DarkMinExposureSecondsLoaded, Is.EqualTo(1.0));
            Assert.That(status.DarkMaxExposureSecondsLoaded, Is.EqualTo(5.0));
        }

        [Test]
        public void Calibration_status_loaded_dark_stats_are_null_when_absent() {
            // No camera connected → daemon omits the loaded-dark stats; the nullable fields stay null.
            const string json = """
                {"jsonrpc":"2.0","id":"xyz","result":{"profile_id":1,"dark_library_exists":false,
                "defect_map_exists":false,"dark_library_compatible":false,"defect_map_compatible":false,
                "dark_library_loaded":false,"defect_map_loaded":false,"auto_load_darks":true,"auto_load_defect_map":true}}
                """;
            var status = JsonConvert.DeserializeObject<Phd2CalibrationFilesStatusResponse>(json)!.result!;
            Assert.That(status.DarkCountLoaded, Is.Null);
            Assert.That(status.DarkMinExposureSecondsLoaded, Is.Null);
            Assert.That(status.DarkMaxExposureSecondsLoaded, Is.Null);
        }
    }
}
