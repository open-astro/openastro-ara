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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test;

/// <summary>
/// §70 profile-share export (§14.1 test cases). Verifies the §70.1 stripping
/// policy on ARA's real section DTOs: paths, secrets, donor location + network,
/// and rig geometry are removed; tuning judgement + a rig description survive.
/// </summary>
[TestFixture]
public class ProfileShareServiceTest {
    private const string PushSecret = "PUSHOVER-TOKEN-DO-NOT-LEAK";
    private const string TelegramSecret = "TELEGRAM-TOKEN-DO-NOT-LEAK";
    private static readonly Guid ProfileId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// A donor snapshot whose strip-target fields hold recognizable sentinel
    /// values, and whose kept fields hold non-default values to prove they survive.
    private static ProfileSnapshotDto DonorSnapshot() => new(
        ImagingDefaults: new(
            ExposureSeconds: 5, Gain: 123, Offset: 50, Bin: 1, FrameKind: "light",
            CoolerTargetC: -10.0, CoolerRampCPerMin: 1.0, WarmupAtSessionEnd: false),
        Storage: new(
            SaveDirectory: @"C:\Astro\Captures",
            FileFormat: "fits",
            Compression: "rice",
            FilenameTemplate: @"$$TARGET$$\$$FILTER$$"),
        Notifications: new(
            InAppBanner: true, OsDesktop: true, SoundAlert: true,
            PushoverToken: PushSecret, TelegramBotToken: TelegramSecret,
            OnSequenceComplete: true, OnSequencePaused: true, OnCriticalDiagnostic: true,
            OnSafetyEvent: true, OnAutofocusFailed: true, OnPlateSolveFailed: true,
            OnDiskSpaceLow: true),
        Site: new(
            SiteName: "Joey's backyard", LatitudeDeg: 45.52, LongitudeDeg: -122.68,
            ElevationM: 120.0, TimeZone: "America/Los_Angeles", UseCustomHorizon: false,
            DefaultHorizonAltitudeDeg: 20.0, BortleClass: 4, TypicalSeeingArcsec: 2.5,
            TwilightDefinition: "astronomical"),
        Filenames: new(DateSeparator: "forward_slash", CompressDarksAndBias: true),
        SafetyPolicies: new(
            OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
            MeridianFlipAuto: true, MeridianPauseMin: 5, MeridianRecenter: true,
            MeridianRecalGuider: true, OnAltitudeLimit: "skip_target",
            ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
            GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: true),
        Autofocus: new(
            Method: "fwhm", Steps: 9, StepSize: 50, ExposureSeconds: 5,
            Binning: 1, AfFilter: "L", RunAfterFilterChange: true,
            TriggerTempDeltaC: 2.0, TriggerHfrDriftPct: 15.0, EveryNHours: 2,
            AbortSequenceOnAfFailure: true, RestorePositionOnFailure: true),
        PlateSolve: new(
            Engine: "astap", PathOrEndpoint: @"C:\Program Files\astap\astap.exe",
            IndexDownloadPath: @"C:\astap\db", SearchRadiusDeg: 30.0,
            DownsampleFactor: 2, TimeoutSeconds: 60, UseBlindFallback: true,
            CenterAfterSlew: true, SyncToCoordinates: true, MaxIterations: 5,
            ConvergenceToleranceArcsec: 60.0),
        DiagnosticsMode: new(Mode: "notify_only"),
        Phd2: new(
            Host: "guidepi.local", Port: 4401, Phd2Profile: "Donor PHD2 profile",
            DitherEnabled: true, DitherEveryNFrames: 1, DitherPixels: 5.0,
            SettlePixels: 1.5, SettleTimeSec: 10, SettleTimeoutSec: 60,
            ForceCalibrationEachSession: false,
            GuideFocalLength: 240, GuidePixelSize: 3.75, RaAggressiveness: 0.9,
            DecAggressiveness: 0.7, MinimumMove: 0.15, DecGuideMode: "auto"),
        EquipmentConnection: new(
            Camera: true, Mount: true, Focuser: true, FilterWheel: true,
            Rotator: true, Guider: false, FlatPanel: true, Dome: false,
            Weather: false, SafetyMonitor: true),
        StretchDefaults: new(
            LightDefault: "auto_stf",
            ManualDefaultParams: new(Blackpoint: 0.02, Midpoint: 0.5, Whitepoint: 0.98),
            AsinhDefaultBeta: 3.0,
            LinearClipPercentilesLow: 0.005,
            LinearClipPercentilesHigh: 0.995),
        Optics: new(
            FocalLengthMm: 2032, ReducerFactor: 0.8,
            SensorWidthPx: 6248, SensorHeightPx: 4176, PixelSizeUm: 3.76));

    [Test]
    public async Task Export_strips_paths_secrets_location_and_network() {
        using var repo = new FakeRepo(DonorSnapshot());
        var share = await new ProfileShareService(repo).ExportAsync(ProfileId, CancellationToken.None);
        share.Should().NotBeNull();
        var settings = share!.Manifest.GetProperty("settings");

        var storage = settings.GetProperty("storage");
        storage.GetProperty("save_directory").GetString().Should().BeEmpty();
        storage.GetProperty("filename_template").GetString().Should().BeEmpty();

        var solve = settings.GetProperty("plate_solve");
        solve.GetProperty("path_or_endpoint").GetString().Should().BeEmpty();
        solve.GetProperty("index_download_path").GetString().Should().BeEmpty();

        var notif = settings.GetProperty("notifications");
        notif.GetProperty("pushover_token").GetString().Should().BeEmpty();
        notif.GetProperty("telegram_bot_token").GetString().Should().BeEmpty();

        var site = settings.GetProperty("site");
        site.GetProperty("site_name").GetString().Should().BeEmpty();
        site.GetProperty("latitude_deg").GetDouble().Should().Be(0);
        site.GetProperty("longitude_deg").GetDouble().Should().Be(0);
        site.GetProperty("elevation_m").GetDouble().Should().Be(0);
        site.GetProperty("time_zone").GetString().Should().BeEmpty();

        var phd2 = settings.GetProperty("phd2");
        phd2.GetProperty("host").GetString().Should().BeEmpty();
        phd2.GetProperty("phd2_profile").GetString().Should().BeEmpty();
        phd2.GetProperty("port").GetInt32().Should().Be(4400);
        phd2.GetProperty("guide_focal_length").GetInt32().Should().Be(0);
        phd2.GetProperty("guide_pixel_size").GetDouble().Should().Be(0);

        var optics = settings.GetProperty("optics");
        optics.GetProperty("focal_length_mm").GetDouble().Should().Be(0);
        optics.GetProperty("pixel_size_um").GetDouble().Should().Be(0);
    }

    [Test]
    public async Task Export_keeps_general_settings_and_builds_rig_description() {
        using var repo = new FakeRepo(DonorSnapshot());
        var share = await new ProfileShareService(repo).ExportAsync(ProfileId, CancellationToken.None);
        var manifest = share!.Manifest;
        var settings = manifest.GetProperty("settings");

        // Tuning judgement survives.
        settings.GetProperty("autofocus").GetProperty("method").GetString().Should().Be("fwhm");
        settings.GetProperty("imaging_defaults").GetProperty("gain").GetInt32().Should().Be(123);
        settings.GetProperty("safety_policies").GetProperty("on_unsafe").GetString().Should().Be("pause_and_park");
        settings.GetProperty("phd2").GetProperty("ra_aggressiveness").GetDouble().Should().Be(0.9);

        // Rig geometry is lifted into rig_description so the recipient can judge fit.
        var rig = manifest.GetProperty("rig_description");
        rig.GetProperty("focal_length_mm").GetDouble().Should().Be(2032);
        rig.GetProperty("reducer_factor").GetDouble().Should().Be(0.8);
        rig.GetProperty("effective_focal_length_mm").GetDouble().Should().BeApproximately(1625.6, 1e-6);
        rig.GetProperty("pixel_size_um").GetDouble().Should().Be(3.76);
        rig.GetProperty("guide_scope_focal_length_mm").GetInt32().Should().Be(240);

        manifest.GetProperty("schema_version").GetString().Should().Be("profile-share-v1");
    }

    [Test]
    public async Task Export_never_leaks_secrets_anywhere_in_the_payload() {
        using var repo = new FakeRepo(DonorSnapshot());
        var share = await new ProfileShareService(repo).ExportAsync(ProfileId, CancellationToken.None);
        // Defense in depth: the raw serialized manifest must not contain either
        // secret token in ANY field (settings, rig description, or metadata).
        var raw = share!.Manifest.GetRawText();
        raw.Should().NotContain(PushSecret);
        raw.Should().NotContain(TelegramSecret);
    }

    [Test]
    public async Task Export_unknown_profile_returns_null() {
        using var repo = new FakeRepo(null);
        var share = await new ProfileShareService(repo).ExportAsync(ProfileId, CancellationToken.None);
        share.Should().BeNull();
    }

    [Test]
    public async Task Export_reports_profile_name_and_nonzero_payload_size() {
        using var repo = new FakeRepo(DonorSnapshot(), name: "Joey's C8 Setup");
        var share = await new ProfileShareService(repo).ExportAsync(ProfileId, CancellationToken.None);
        share!.ProfileName.Should().Be("Joey's C8 Setup");
        share.PayloadBytes.Should().BeGreaterThan(0);
    }

    /// Minimal IProfileRepository double — only GetProfile is exercised by export.
    private sealed class FakeRepo : IProfileRepository {
        private readonly StoredProfileDto? _stored;

        public FakeRepo(ProfileSnapshotDto? settings, string name = "Test profile") =>
            _stored = settings is null
                ? null
                : new StoredProfileDto(
                    new ProfileMetaDto(ProfileId, name, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                    settings);

        public StoredProfileDto? GetProfile(Guid id) => id == ProfileId ? _stored : null;

        public ProfileListDto List() => new(null, Array.Empty<ProfileMetaDto>());
        public Guid? ActiveId => null;
        public ProfileMetaDto Create(string name, ProfileSnapshotDto? settings, bool makeActive) =>
            throw new NotSupportedException();
        public bool Rename(Guid id, string name) => throw new NotSupportedException();
        public ProfileDeleteResult Delete(Guid id) => throw new NotSupportedException();
        public bool SelectProfile(Guid id) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
