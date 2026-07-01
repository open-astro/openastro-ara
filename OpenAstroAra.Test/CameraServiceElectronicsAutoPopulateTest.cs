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
    /// NEXTGEN §3/§4 — the camera-connect path caches the camera's ASCOM-reported electronics
    /// (sensor name, full well, e⁻/ADU, gain — all for the CURRENT readout mode) into the
    /// profile's camera-electronics section for exposure planning. These pin the pure decision:
    /// populate when unset, re-cache when the mode/camera changes (the HFW-mode case), preserve
    /// the user-owned read noise + QE peak (never in ASCOM), and ignore a camera that reports
    /// nothing usable.
    /// </summary>
    [TestFixture]
    public class CameraServiceElectronicsAutoPopulateTest {

        private static CameraCapabilitiesDto Caps(
                double fullWell = 0, double ePerAdu = 0, string? sensorName = null, int gain = -1) =>
            new(SensorWidth: 6248, SensorHeight: 4176, PixelSizeUm: 3.76,
                CanSetTemperature: false, CanAbortExposure: false, CanGetCoolerPower: false,
                MinGain: 0, MaxGain: 0, MinOffset: 0, MaxOffset: 0,
                MinBinX: 1, MaxBinX: 1, MinBinY: 1, MaxBinY: 1,
                MinExposureSec: 0, MaxExposureSec: 0,
                FullWellCapacityE: fullWell, ElectronsPerAdu: ePerAdu,
                SensorName: sensorName, CurrentGain: gain);

        [Test]
        public void Unset_electronics_are_captured_preserving_read_noise_and_qe() {
            // The user already entered read noise + QE (never in ASCOM); first connect captures the rest.
            var current = new CameraElectronicsDto(ReadNoiseE: 3.3, QuantumEfficiencyPeak: 0.85);
            var result = CameraService.AutoPopulatedElectronics(current, Caps(50_000, 0.78, "IMX571", 100));
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.SensorName, Is.EqualTo("IMX571"));
            Assert.That(result.FullWellE, Is.EqualTo(50_000));
            Assert.That(result.ElectronsPerAdu, Is.EqualTo(0.78));
            Assert.That(result.Gain, Is.EqualTo(100));
            Assert.That(result.AutoCaptured, Is.True);
            Assert.That(result.ReadNoiseE, Is.EqualTo(3.3), "user-entered read noise is preserved");
            Assert.That(result.QuantumEfficiencyPeak, Is.EqualTo(0.85), "user-entered QE peak is preserved");
        }

        [Test]
        public void Matching_electronics_yield_no_change() {
            var current = new CameraElectronicsDto(
                SensorName: "IMX571", FullWellE: 50_000, ElectronsPerAdu: 0.78, Gain: 100,
                ReadNoiseE: 3.3, AutoCaptured: true);
            Assert.That(CameraService.AutoPopulatedElectronics(current, Caps(50_000, 0.78, "IMX571", 100)), Is.Null);
        }

        [Test]
        public void A_readout_mode_change_re_caches_the_bigger_well() {
            // Reconnect in High Full Well mode: ASCOM reports the caps for the CURRENT mode,
            // so the 50 → 100 ke⁻ difference re-caches automatically (the ToupTek HFW case).
            var current = new CameraElectronicsDto(
                SensorName: "IMX571", FullWellE: 50_000, ElectronsPerAdu: 0.78, Gain: 100,
                ReadNoiseE: 3.3, AutoCaptured: true);
            var result = CameraService.AutoPopulatedElectronics(current, Caps(100_000, 1.56, "IMX571", 100));
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.FullWellE, Is.EqualTo(100_000));
            Assert.That(result.ElectronsPerAdu, Is.EqualTo(1.56));
            Assert.That(result.ReadNoiseE, Is.EqualTo(3.3), "read noise still user-owned across re-caches");
        }

        [Test]
        public void A_sub_epsilon_difference_is_treated_as_no_change() {
            var current = new CameraElectronicsDto(
                SensorName: "IMX571", FullWellE: 50_000, ElectronsPerAdu: 0.78, Gain: 100, AutoCaptured: true);
            Assert.That(CameraService.AutoPopulatedElectronics(current, Caps(50_000 + 1e-12, 0.78 + 1e-12, "IMX571", 100)),
                Is.Null, "a float round-trip epsilon must not spuriously re-cache on every reconnect");
        }

        [Test]
        public void A_camera_that_reports_nothing_usable_is_ignored() {
            var current = new CameraElectronicsDto(ReadNoiseE: 3.3);
            Assert.That(CameraService.AutoPopulatedElectronics(current, Caps()), Is.Null,
                "no full well, no e⁻/ADU, no sensor name → nothing to capture");
        }

        [Test]
        public void A_partially_supporting_driver_never_clobbers_stored_values_with_unset() {
            // The driver implements FullWellCapacity + SensorName but throws on ElectronsPerADU
            // (caps report 0) and Gain (caps report -1). The stored e⁻/ADU — user-entered or
            // captured from a previous driver — must survive the merge; only reported fields update.
            var current = new CameraElectronicsDto(
                SensorName: "IMX571", FullWellE: 50_000, ElectronsPerAdu: 0.78, Gain: 100,
                ReadNoiseE: 3.3, AutoCaptured: true);
            var result = CameraService.AutoPopulatedElectronics(
                current, Caps(fullWell: 100_000, sensorName: "IMX571"));
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.FullWellE, Is.EqualTo(100_000), "the reported field updates");
            Assert.That(result.ElectronsPerAdu, Is.EqualTo(0.78), "the unreported field keeps its stored value");
            Assert.That(result.Gain, Is.EqualTo(100), "unreported gain keeps its stored value");
            Assert.That(result.ReadNoiseE, Is.EqualTo(3.3), "user-owned field untouched");

            // When the reported fields already match, the unreported ones can't force a write.
            Assert.That(CameraService.AutoPopulatedElectronics(
                    result, Caps(fullWell: 100_000, sensorName: "IMX571")),
                Is.Null, "partial reports matching the stored values are a no-op");
        }

        [Test]
        public void A_sensor_name_alone_is_still_captured() {
            // Partial data is still provenance worth recording (the sensor library keys off it).
            var result = CameraService.AutoPopulatedElectronics(new CameraElectronicsDto(), Caps(sensorName: "IMX571"));
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.SensorName, Is.EqualTo("IMX571"));
            Assert.That(result.AutoCaptured, Is.True);
        }

        // The atomic read-modify-write the auto-capture uses (closes the TOCTOU vs a concurrent PUT).

        [Test]
        public void UpdateCameraElectronics_applies_a_change_under_the_lock_and_returns_it() {
            var store = new InMemoryProfileStore();
            store.PutCameraElectronics(new CameraElectronicsDto(ReadNoiseE: 3.3));
            var result = store.UpdateCameraElectronics(cur => cur with { FullWellE = 50_000, AutoCaptured = true });
            Assert.That(result.FullWellE, Is.EqualTo(50_000));
            Assert.That(store.GetCameraElectronics().FullWellE, Is.EqualTo(50_000), "persisted");
            Assert.That(store.GetCameraElectronics().ReadNoiseE, Is.EqualTo(3.3), "untouched fields preserved");
        }

        [Test]
        public void UpdateCameraElectronics_returning_null_leaves_the_value_unchanged() {
            var store = new InMemoryProfileStore();
            store.PutCameraElectronics(new CameraElectronicsDto(FullWellE: 50_000));
            var changed = false;
            store.Changed += (_, _) => changed = true;
            var result = store.UpdateCameraElectronics(_ => null);
            Assert.That(result.FullWellE, Is.EqualTo(50_000));
            Assert.That(changed, Is.False, "no write means no change event");
        }

        [Test]
        public void FileProfileStore_UpdateCameraElectronics_persists_to_disk() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-electronics-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                var store = new FileProfileStore(dir);
                store.PutCameraElectronics(new CameraElectronicsDto(ReadNoiseE: 3.3));
                var result = store.UpdateCameraElectronics(cur =>
                    cur with { SensorName = "IMX571", FullWellE = 50_000, ElectronsPerAdu = 0.78, Gain = 100, AutoCaptured = true });
                Assert.That(result.FullWellE, Is.EqualTo(50_000));

                // Reopen from disk: the update was persisted under the lock, read noise preserved.
                var reopened = new FileProfileStore(dir);
                Assert.That(reopened.GetCameraElectronics().FullWellE, Is.EqualTo(50_000), "persisted to disk");
                Assert.That(reopened.GetCameraElectronics().ReadNoiseE, Is.EqualTo(3.3));

                Assert.That(store.UpdateCameraElectronics(_ => null).FullWellE, Is.EqualTo(50_000),
                    "a null updater leaves the persisted value unchanged");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void An_old_profile_json_without_the_new_sections_back_fills_defaults() {
            // A profile.json written before the NEXTGEN §4 sections existed must deserialize with
            // the new sections back-filled to defaults (and aperture_mm to 0), not null/garbage.
            var dir = Path.Combine(Path.GetTempPath(), "ara-backfill-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                // Write a pre-NEXTGEN profile.json: build one with the current store, then strip the
                // new keys the way an old file simply wouldn't have them.
                var seeded = new FileProfileStore(dir);
                seeded.PutOpticsSettings(new OpticsSettingsDto(
                    FocalLengthMm: 400, ReducerFactor: 1.0,
                    SensorWidthPx: 6248, SensorHeightPx: 4176, PixelSizeUm: 3.76));
                var path = Path.Combine(dir, "profile.json");
                var json = File.ReadAllText(path);
                var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
                node.Remove("camera_electronics");
                node.Remove("filter_set");
                node["optics"]!.AsObject().Remove("aperture_mm");
                File.WriteAllText(path, node.ToJsonString());

                var reopened = new FileProfileStore(dir);
                Assert.That(reopened.GetCameraElectronics(), Is.EqualTo(new CameraElectronicsDto()),
                    "missing camera_electronics back-fills the unset defaults");
                Assert.That(reopened.GetFilterSet().Filters, Is.Empty,
                    "missing filter_set back-fills an empty list");
                Assert.That(reopened.GetOpticsSettings().ApertureMm, Is.EqualTo(0),
                    "missing aperture_mm deserializes to the 0 = unset default");
                Assert.That(reopened.GetOpticsSettings().FocalLengthMm, Is.EqualTo(400),
                    "the rest of the old optics survive untouched");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
