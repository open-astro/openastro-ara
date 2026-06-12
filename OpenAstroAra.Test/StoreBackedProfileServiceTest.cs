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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System.Text.Json;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Sim-free coverage for the §14e profile source-of-truth bridge: the
    /// <see cref="StoreBackedProfileService"/> lifecycle (hydrate at construction, re-hydrate on
    /// store change, unsubscribe on dispose) plus every <see cref="ProfileStoreMapper"/> section
    /// mapping against fabricated store DTOs.
    /// </summary>
    [TestFixture]
    public class StoreBackedProfileServiceTest {

        private static SiteSettingsDto Site(double lat = 47.5, double lon = -122.3, double elev = 120.0) => new(
            SiteName: "Backyard", LatitudeDeg: lat, LongitudeDeg: lon, ElevationM: elev,
            TimeZone: "America/Los_Angeles", UseCustomHorizon: false, DefaultHorizonAltitudeDeg: 20,
            BortleClass: 6, TypicalSeeingArcsec: 2.5, TwilightDefinition: "astronomical");

        [Test]
        public void Hydrates_ActiveProfile_from_the_store_at_construction() {
            var store = new InMemoryProfileStore();
            store.PutSiteSettings(Site(lat: -33.9, lon: 18.4, elev: 42.0));

            using var svc = new StoreBackedProfileService(store);

            Assert.That(svc.ActiveProfile.AstrometrySettings.Latitude, Is.EqualTo(-33.9));
            Assert.That(svc.ActiveProfile.AstrometrySettings.Longitude, Is.EqualTo(18.4));
            Assert.That(svc.ActiveProfile.AstrometrySettings.Elevation, Is.EqualTo(42.0));
        }

        [Test]
        public void Rehydrates_when_the_store_changes() {
            var store = new InMemoryProfileStore();
            using var svc = new StoreBackedProfileService(store);

            store.PutSiteSettings(Site(lat: 51.48));

            Assert.That(svc.ActiveProfile.AstrometrySettings.Latitude, Is.EqualTo(51.48),
                "a settings PUT must propagate to the live ActiveProfile");
        }

        [Test]
        public void Dispose_unsubscribes_from_store_changes() {
            var store = new InMemoryProfileStore();
            var svc = new StoreBackedProfileService(store);
            svc.Dispose();

            store.PutSiteSettings(Site(lat: 0.123));

            Assert.That(svc.ActiveProfile.AstrometrySettings.Latitude, Is.Not.EqualTo(0.123),
                "after Dispose the bridge must stop tracking the store");
        }

        [Test]
        public void Phd2_section_maps_onto_GuiderSettings() {
            var store = new InMemoryProfileStore();
            using var svc = new StoreBackedProfileService(store);

            store.PutPhd2Settings(new Phd2SettingsDto(
                Host: "astro-pi.local", Port: 4400, Phd2Profile: "main",
                DitherEnabled: true, DitherEveryNFrames: 3, DitherPixels: 4.5,
                SettlePixels: 1.25, SettleTimeSec: 9, SettleTimeoutSec: 77,
                ForceCalibrationEachSession: false,
                GuideFocalLength: 250, GuidePixelSize: 2.9, RaAggressiveness: 0.8,
                DecAggressiveness: 0.65, MinimumMove: 0.2, DecGuideMode: "north"));

            var guider = svc.ActiveProfile.GuiderSettings;
            Assert.That(guider.PHD2ServerHost, Is.EqualTo("astro-pi.local"));
            Assert.That(guider.PHD2ServerPort, Is.EqualTo(4400));
            Assert.That(guider.DitherPixels, Is.EqualTo(4.5));
            Assert.That(guider.SettlePixels, Is.EqualTo(1.25));
            Assert.That(guider.SettleTime, Is.EqualTo(9));
            Assert.That(guider.SettleTimeout, Is.EqualTo(77));
            // §63.5 guider-engine config.
            Assert.That(guider.GuideFocalLength, Is.EqualTo(250));
            Assert.That(guider.GuidePixelSize, Is.EqualTo(2.9));
            Assert.That(guider.RAAggressiveness, Is.EqualTo(0.8));
            Assert.That(guider.DecAggressiveness, Is.EqualTo(0.65));
            Assert.That(guider.MinimumMove, Is.EqualTo(0.2));
            Assert.That(guider.DecGuideMode, Is.EqualTo("north"));
        }

        [Test]
        public void Phd2Settings_deserializes_pre_63_5_json_to_guider_engine_defaults() {
            // An existing profile.json Phd2 section written before §63.5 — none of the guider-engine keys.
            // The DTO's optional ctor defaults must fill them (not null / 0.0), so an upgrade doesn't leave
            // aggressiveness at 0 ("never correct") or DecGuideMode null.
            const string oldJson = """
                {"host":"astro-pi.local","port":4400,"phd2_profile":"Default","dither_enabled":true,
                 "dither_every_n_frames":1,"dither_pixels":5,"settle_pixels":1.5,"settle_time_sec":10,
                 "settle_timeout_sec":60,"force_calibration_each_session":false}
                """;

            var dto = JsonSerializer.Deserialize(oldJson, AraJsonSerializerContext.Default.Phd2SettingsDto);

            Assert.That(dto, Is.Not.Null);
            Assert.That(dto!.Host, Is.EqualTo("astro-pi.local")); // existing keys still bind
            Assert.That(dto.DecGuideMode, Is.EqualTo("auto"));
            Assert.That(dto.RaAggressiveness, Is.EqualTo(0.7));
            Assert.That(dto.DecAggressiveness, Is.EqualTo(0.7));
            Assert.That(dto.MinimumMove, Is.EqualTo(0.15));
            Assert.That(dto.GuideFocalLength, Is.EqualTo(0));
            Assert.That(dto.GuidePixelSize, Is.EqualTo(0));
        }

        [Test]
        public void Autofocus_section_maps_onto_FocuserSettings() {
            var store = new InMemoryProfileStore();
            using var svc = new StoreBackedProfileService(store);

            store.PutAutofocusSettings(new AutofocusSettingsDto(
                Method: "hfr_v_curve", Steps: 7, StepSize: 55, ExposureSeconds: 4, Binning: 2,
                AfFilter: "L", RunAfterFilterChange: true, TriggerTempDeltaC: 1.5,
                TriggerHfrDriftPct: 10, EveryNHours: 2, AbortSequenceOnAfFailure: false,
                RestorePositionOnFailure: true));

            var focuser = svc.ActiveProfile.FocuserSettings;
            Assert.That(focuser.AutoFocusStepSize, Is.EqualTo(55));
            Assert.That(focuser.AutoFocusInitialOffsetSteps, Is.EqualTo(7));
            Assert.That(focuser.AutoFocusExposureTime, Is.EqualTo(4));
            Assert.That(focuser.AutoFocusBinning, Is.EqualTo((short)2));
        }

        [Test]
        public void Storage_section_maps_onto_ImageFileSettings() {
            var store = new InMemoryProfileStore();
            using var svc = new StoreBackedProfileService(store);

            store.PutStorageSettings(new StorageSettingsDto(
                SaveDirectory: "/data/frames", FileFormat: "xisf", Compression: "off",
                FilenameTemplate: "$$DATETIME$$_$$FILTER$$"));

            var imageFile = svc.ActiveProfile.ImageFileSettings;
            Assert.That(imageFile.FilePath, Is.EqualTo("/data/frames"));
            Assert.That(imageFile.FilePattern, Is.EqualTo("$$DATETIME$$_$$FILTER$$"));
            Assert.That(imageFile.FileType, Is.EqualTo(FileType.XISF));
        }

        [Test]
        public void PlateSolve_section_maps_with_arcsec_to_arcmin_threshold() {
            var store = new InMemoryProfileStore();
            using var svc = new StoreBackedProfileService(store);

            store.PutPlateSolveSettings(new PlateSolveSettingsDto(
                Engine: "astap", PathOrEndpoint: "/usr/bin/astap", IndexDownloadPath: "/data/astap",
                SearchRadiusDeg: 15, DownsampleFactor: 2, TimeoutSeconds: 90,
                UseBlindFallback: true, CenterAfterSlew: true, SyncToCoordinates: true,
                MaxIterations: 4, ConvergenceToleranceArcsec: 90.0));

            var plateSolve = svc.ActiveProfile.PlateSolveSettings;
            Assert.That(plateSolve.SearchRadius, Is.EqualTo(15));
            Assert.That(plateSolve.DownSampleFactor, Is.EqualTo(2));
            Assert.That(plateSolve.NumberOfAttempts, Is.EqualTo(4));
            Assert.That(plateSolve.Sync, Is.True);
            Assert.That(plateSolve.SlewToTarget, Is.True);
            Assert.That(plateSolve.Threshold, Is.EqualTo(1.5), "90 arcsec = 1.5 arcmin");
        }

        [Test]
        public void SafetyPolicies_meridian_fields_map_onto_MeridianFlipSettings() {
            var store = new InMemoryProfileStore();
            using var svc = new StoreBackedProfileService(store);

            store.PutSafetyPolicies(new SafetyPoliciesDto(
                OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: true, MeridianPauseMin: 6, MeridianRecenter: true,
                MeridianRecalGuider: false, OnAltitudeLimit: "skip_target",
                ParkIfNoMoreTargets: true, OnGuiderLost: "pause", GuiderRetryTimeoutSec: 120,
                SkipTargetIfRecoveryFails: true));

            var meridian = svc.ActiveProfile.MeridianFlipSettings;
            Assert.That(meridian.PauseTimeBeforeMeridian, Is.EqualTo(6));
            Assert.That(meridian.Recenter, Is.True);
        }

        [Test]
        public void MapFileType_falls_back_to_FITS_for_fits_variants() {
            Assert.That(ProfileStoreMapper.MapFileType("xisf"), Is.EqualTo(FileType.XISF));
            Assert.That(ProfileStoreMapper.MapFileType("XISF"), Is.EqualTo(FileType.XISF));
            Assert.That(ProfileStoreMapper.MapFileType("fits"), Is.EqualTo(FileType.FITS));
            Assert.That(ProfileStoreMapper.MapFileType("fits_rice"), Is.EqualTo(FileType.FITS));
            Assert.That(ProfileStoreMapper.MapFileType("fits_gzip"), Is.EqualTo(FileType.FITS));
        }

        [Test]
        public void ClampToShort_bounds_out_of_range_values() {
            Assert.That(ProfileStoreMapper.ClampToShort(2), Is.EqualTo((short)2));
            Assert.That(ProfileStoreMapper.ClampToShort(int.MaxValue), Is.EqualTo(short.MaxValue));
            Assert.That(ProfileStoreMapper.ClampToShort(int.MinValue), Is.EqualTo(short.MinValue));
        }
    }
}
