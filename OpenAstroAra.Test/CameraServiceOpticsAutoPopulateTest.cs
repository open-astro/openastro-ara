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
    /// §36/§25.5 + §30.7 — the camera-connect path caches the camera's sensor geometry into the
    /// profile's optics section so Planning Frame mode works without the user typing it. These pin
    /// the pure decision: populate when unset, re-cache on a swapped camera, preserve the
    /// telescope-owned focal length + reducer, and ignore a camera that reports no geometry.
    /// </summary>
    [TestFixture]
    public class CameraServiceOpticsAutoPopulateTest {

        private static CameraCapabilitiesDto Caps(int w, int h, double px) =>
            new(SensorWidth: w, SensorHeight: h, PixelSizeUm: px,
                CanSetTemperature: false, CanAbortExposure: false, CanGetCoolerPower: false,
                MinGain: 0, MaxGain: 0, MinOffset: 0, MaxOffset: 0,
                MinBinX: 1, MaxBinX: 1, MinBinY: 1, MaxBinY: 1,
                MinExposureSec: 0, MaxExposureSec: 0);

        private static OpticsSettingsDto Optics(
                int w = 0, int h = 0, double px = 0, double fl = 0, double red = 1.0) =>
            new(FocalLengthMm: fl, ReducerFactor: red, SensorWidthPx: w, SensorHeightPx: h, PixelSizeUm: px);

        [Test]
        public void Unset_optics_are_populated_from_the_camera_preserving_focal_and_reducer() {
            var current = Optics(fl: 1000, red: 0.8); // user set the scope; sensor still unset
            var result = CameraService.AutoPopulatedOptics(current, Caps(6248, 4176, 3.76));
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.SensorWidthPx, Is.EqualTo(6248));
            Assert.That(result.SensorHeightPx, Is.EqualTo(4176));
            Assert.That(result.PixelSizeUm, Is.EqualTo(3.76));
            Assert.That(result.FocalLengthMm, Is.EqualTo(1000), "focal length is preserved");
            Assert.That(result.ReducerFactor, Is.EqualTo(0.8), "reducer is preserved");
        }

        [Test]
        public void Matching_geometry_yields_no_change() {
            var current = Optics(w: 6248, h: 4176, px: 3.76, fl: 1000);
            Assert.That(CameraService.AutoPopulatedOptics(current, Caps(6248, 4176, 3.76)), Is.Null);
        }

        [Test]
        public void A_swapped_camera_re_caches_the_new_geometry() {
            var current = Optics(w: 6248, h: 4176, px: 3.76, fl: 1000);
            var result = CameraService.AutoPopulatedOptics(current, Caps(9576, 6388, 3.76));
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.SensorWidthPx, Is.EqualTo(9576));
            Assert.That(result.SensorHeightPx, Is.EqualTo(6388));
            Assert.That(result.FocalLengthMm, Is.EqualTo(1000), "focal length still preserved on a re-cache");
        }

        [Test]
        public void A_sub_epsilon_pixel_size_difference_is_treated_as_no_change() {
            var current = Optics(w: 6248, h: 4176, px: 3.76);
            Assert.That(CameraService.AutoPopulatedOptics(current, Caps(6248, 4176, 3.76 + 1e-12)), Is.Null,
                "a float round-trip epsilon must not spuriously re-cache on every reconnect");
        }

        [Test]
        public void A_pixel_size_change_alone_re_caches() {
            var current = Optics(w: 6248, h: 4176, px: 3.76);
            var result = CameraService.AutoPopulatedOptics(current, Caps(6248, 4176, 2.4));
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.PixelSizeUm, Is.EqualTo(2.4));
        }

        [Test]
        public void A_camera_that_reports_no_geometry_is_ignored() {
            var current = Optics(fl: 1000);
            Assert.That(CameraService.AutoPopulatedOptics(current, Caps(0, 0, 0)), Is.Null);
            Assert.That(CameraService.AutoPopulatedOptics(current, Caps(6248, 4176, 0)), Is.Null,
                "a missing pixel size can't drive the FOV, so don't half-populate");
        }

        // The atomic read-modify-write the auto-populate uses (closes the TOCTOU vs a concurrent PUT).

        [Test]
        public void UpdateOpticsSettings_applies_a_change_under_the_lock_and_returns_it() {
            var store = new InMemoryProfileStore();
            store.PutOpticsSettings(Optics(fl: 1000));
            var result = store.UpdateOpticsSettings(cur => cur with { SensorWidthPx = 6248, SensorHeightPx = 4176, PixelSizeUm = 3.76 });
            Assert.That(result.SensorWidthPx, Is.EqualTo(6248));
            Assert.That(store.GetOpticsSettings().SensorWidthPx, Is.EqualTo(6248), "persisted");
            Assert.That(store.GetOpticsSettings().FocalLengthMm, Is.EqualTo(1000), "untouched fields preserved");
        }

        [Test]
        public void UpdateOpticsSettings_returning_null_leaves_the_value_unchanged() {
            var store = new InMemoryProfileStore();
            store.PutOpticsSettings(Optics(w: 6248, h: 4176, px: 3.76, fl: 1000));
            var changed = false;
            store.Changed += (_, _) => changed = true;
            var result = store.UpdateOpticsSettings(_ => null);
            Assert.That(result.SensorWidthPx, Is.EqualTo(6248));
            Assert.That(changed, Is.False, "no write means no change event");
        }

        [Test]
        public void FileProfileStore_UpdateOpticsSettings_persists_to_disk() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-optics-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                var store = new FileProfileStore(dir);
                store.PutOpticsSettings(Optics(fl: 1000));
                var result = store.UpdateOpticsSettings(cur =>
                    cur with { SensorWidthPx = 6248, SensorHeightPx = 4176, PixelSizeUm = 3.76 });
                Assert.That(result.SensorWidthPx, Is.EqualTo(6248));

                // Reopen from disk: the update was persisted under the lock, focal length preserved.
                var reopened = new FileProfileStore(dir);
                Assert.That(reopened.GetOpticsSettings().SensorWidthPx, Is.EqualTo(6248), "persisted to disk");
                Assert.That(reopened.GetOpticsSettings().FocalLengthMm, Is.EqualTo(1000));

                Assert.That(store.UpdateOpticsSettings(_ => null).SensorWidthPx, Is.EqualTo(6248),
                    "a null updater leaves the persisted value unchanged");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
