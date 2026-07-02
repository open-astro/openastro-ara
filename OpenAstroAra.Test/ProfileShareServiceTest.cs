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
using System.Collections.Generic;
using System.Linq;
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
            SensorWidthPx: 6248, SensorHeightPx: 4176, PixelSizeUm: 3.76,
            ApertureMm: 203),
        CameraElectronics: new(
            SensorName: "IMX571-DONOR-DO-NOT-LEAK", ReadNoiseE: 3.3, FullWellE: 50_000,
            ElectronsPerAdu: 0.78, Gain: 100, QuantumEfficiencyPeak: 0.85, AutoCaptured: true),
        FilterSet: new(Filters: [
            new PlanningFilterDto(Name: "Donor L-eXtreme", Kind: FilterKind.Duo, BandwidthNm: 7),
        ]),
        // Kept on export: filter names are gear facts, not host/secret/location data.
        FilterWheelLabels: new(["L", "Ha", "", "OIII"]));

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
        optics.GetProperty("sensor_width_px").GetInt32().Should().Be(0);
        optics.GetProperty("sensor_height_px").GetInt32().Should().Be(0);
        optics.GetProperty("reducer_factor").GetDouble().Should().Be(1.0, "stripped optics resets the reducer to the neutral 1.0 multiplier");
        optics.GetProperty("aperture_mm").GetDouble().Should().Be(0, "aperture is rig geometry — stripped with the rest of the optics");

        // NEXTGEN §4 sections: donor camera hardware + filter list are stripped whole.
        var electronics = settings.GetProperty("camera_electronics");
        electronics.GetProperty("sensor_name").GetString().Should().BeEmpty();
        electronics.GetProperty("read_noise_e").GetDouble().Should().Be(0);
        electronics.GetProperty("full_well_e").GetDouble().Should().Be(0);
        electronics.GetProperty("electrons_per_adu").GetDouble().Should().Be(0);
        electronics.GetProperty("gain").GetInt32().Should().Be(-1);
        electronics.GetProperty("quantum_efficiency_peak").GetDouble().Should().Be(0);
        settings.GetProperty("filter_set").GetProperty("filters").GetArrayLength().Should().Be(0);
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

        // Site sky-quality judgement is KEPT (only the precise location is stripped) —
        // assert it survives so an accidental widening of the Site `with {}` strip
        // block is caught.
        var site = settings.GetProperty("site");
        site.GetProperty("bortle_class").GetInt32().Should().Be(4);
        site.GetProperty("typical_seeing_arcsec").GetDouble().Should().Be(2.5);
        site.GetProperty("twilight_definition").GetString().Should().Be("astronomical");
        site.GetProperty("default_horizon_altitude_deg").GetDouble().Should().Be(20.0);

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
    public async Task Export_payload_contains_no_stripped_value_at_all() {
        // Defense in depth against the allowlist-by-omission design: every
        // path/identity/secret sentinel set in DonorSnapshot must be absent from the
        // whole rendered payload. If a future field carries one of these through, this
        // fails — forcing a keep/strip decision (see StripForShare's ledger).
        using var repo = new FakeRepo(DonorSnapshot());
        var share = await new ProfileShareService(repo).ExportAsync(ProfileId, CancellationToken.None);
        var raw = share!.Manifest.GetRawText();
        // Only distinctive STRING sentinels here — numeric donor values (lat/long,
        // PHD2 port) are asserted field-by-field in Export_strips_paths_... instead,
        // since a bare substring scan for a number like "4401" would false-positive
        // on any future field that happens to contain those digits.
        string[] mustNotAppear = {
            PushSecret, TelegramSecret,
            @"C:\Astro\Captures", @"C:\Program Files\astap\astap.exe", @"C:\astap\db",
            "Joey's backyard", "America/Los_Angeles",
            "guidepi.local", "Donor PHD2 profile",
        };
        foreach (var sentinel in mustNotAppear) {
            raw.Should().NotContain(sentinel, "'{0}' is host/identity-specific and must be stripped", sentinel);
        }
    }

    [Test]
    public async Task Import_preview_then_commit_creates_a_profile_from_the_template() {
        using var repo = new FakeRepo(DonorSnapshot());
        var svc = new ProfileShareService(repo);
        // Round-trip: export produces a valid profile-share-v1 manifest; feed it back.
        var share = await svc.ExportAsync(ProfileId, CancellationToken.None);

        var preview = await svc.ImportPreviewAsync(share!.Manifest, CancellationToken.None);
        preview.ImportToken.Should().NotBe(Guid.Empty);
        preview.DroppedFields.Should().NotBeEmpty();
        // Donor block was omitted on export, so the name is derived from the rig's
        // effective focal length (2032 mm × 0.8 reducer = 1625.6 → 1626 mm).
        preview.ProfileName.Should().Be("Imported — 1626 mm rig");

        var newId = await svc.ImportCommitAsync(preview.ImportToken, CancellationToken.None);
        newId.Should().Be(repo.CreatedId);
        repo.CreatedSettings.Should().NotBeNull(); // built from the template's (stripped) settings
        repo.CreatedMakeActive.Should().BeFalse(); // imported as non-active; user wizards + selects it
    }

    [Test]
    public async Task Import_name_falls_back_to_neutral_label_when_no_rig_geometry() {
        // A donor whose optics are unset (focal length 0) yields an effective focal
        // length of 0, so there's nothing to derive a rig label from — the import
        // name falls back to the neutral "Imported profile".
        var noOptics = DonorSnapshot() with {
            Optics = DonorSnapshot().Optics with { FocalLengthMm = 0 },
        };
        using var repo = new FakeRepo(noOptics);
        var svc = new ProfileShareService(repo);
        var share = await svc.ExportAsync(ProfileId, CancellationToken.None);

        var preview = await svc.ImportPreviewAsync(share!.Manifest, CancellationToken.None);
        preview.ProfileName.Should().Be("Imported profile");
    }

    [Test]
    public async Task Import_name_falls_back_when_rig_focal_length_is_absurd() {
        // A crafted/garbage manifest with an absurd focal length (here 2,000,000 mm ×
        // 0.8 = 1.6e6 mm effective, beyond the ~1 km sanity bound) must not produce a
        // garbage label (or, with a naive int cast, a negative overflow) — it falls
        // back to the neutral "Imported profile".
        var absurd = DonorSnapshot() with {
            Optics = DonorSnapshot().Optics with { FocalLengthMm = 2_000_000 },
        };
        using var repo = new FakeRepo(absurd);
        var svc = new ProfileShareService(repo);
        var share = await svc.ExportAsync(ProfileId, CancellationToken.None);

        var preview = await svc.ImportPreviewAsync(share!.Manifest, CancellationToken.None);
        preview.ProfileName.Should().Be("Imported profile");
    }

    [Test]
    public async Task Import_name_is_de_duplicated_against_existing_profiles() {
        // The repo already holds a profile with the rig-derived name (and its first
        // numbered sibling), so the import must suffix to the next free slot rather
        // than colliding. Match is case-insensitive.
        using var repo = new FakeRepo(DonorSnapshot(), "Test profile",
            "imported — 1626 mm rig", "Imported — 1626 mm rig (2)");
        var svc = new ProfileShareService(repo);
        var share = await svc.ExportAsync(ProfileId, CancellationToken.None);

        var preview = await svc.ImportPreviewAsync(share!.Manifest, CancellationToken.None);
        preview.ProfileName.Should().Be("Imported — 1626 mm rig (3)");

        // The commit creates the profile under that same de-duplicated name.
        await svc.ImportCommitAsync(preview.ImportToken, CancellationToken.None);
        repo.CreatedName.Should().Be("Imported — 1626 mm rig (3)");
    }

    [Test]
    public async Task Import_preview_rejects_a_non_share_manifest() {
        using var repo = new FakeRepo(DonorSnapshot());
        var svc = new ProfileShareService(repo);
        using var wrongSchema = JsonDocument.Parse("{\"schema_version\":\"something-else\"}");
        using var empty = JsonDocument.Parse("{}");
        // Right schema but no settings/rig_description — non-nullable on the record,
        // but source-gen JSON leaves them null, so the guard (not an NPE) must reject.
        using var noBody = JsonDocument.Parse("{\"schema_version\":\"profile-share-v1\"}");
        Assert.ThrowsAsync<InvalidProfileShareException>(
            () => svc.ImportPreviewAsync(wrongSchema.RootElement, CancellationToken.None));
        Assert.ThrowsAsync<InvalidProfileShareException>(
            () => svc.ImportPreviewAsync(empty.RootElement, CancellationToken.None));
        Assert.ThrowsAsync<InvalidProfileShareException>(
            () => svc.ImportPreviewAsync(noBody.RootElement, CancellationToken.None));
    }

    [Test]
    public void Import_commit_with_unknown_token_throws() {
        using var repo = new FakeRepo(DonorSnapshot());
        var svc = new ProfileShareService(repo);
        Assert.ThrowsAsync<ProfileShareImportTokenException>(
            () => svc.ImportCommitAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Test]
    public async Task Import_commit_is_single_use() {
        using var repo = new FakeRepo(DonorSnapshot());
        var svc = new ProfileShareService(repo);
        var share = await svc.ExportAsync(ProfileId, CancellationToken.None);
        var preview = await svc.ImportPreviewAsync(share!.Manifest, CancellationToken.None);

        await svc.ImportCommitAsync(preview.ImportToken, CancellationToken.None);
        // The token is consumed on first commit — a second use can't duplicate.
        Assert.ThrowsAsync<ProfileShareImportTokenException>(
            () => svc.ImportCommitAsync(preview.ImportToken, CancellationToken.None));
    }

    [Test]
    public async Task Import_commit_rejects_a_token_past_its_ttl() {
        // Drive the clock so the 15-min TTL deterministically lapses between preview
        // and commit — exercises the expiry guard in ImportCommitAsync (not the
        // prune sweep), so a flipped `<`/`>` in that comparison would fail here.
        var clock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        using var repo = new FakeRepo(DonorSnapshot());
        var svc = new ProfileShareService(repo, clock);
        var share = await svc.ExportAsync(ProfileId, CancellationToken.None);
        var preview = await svc.ImportPreviewAsync(share!.Manifest, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(16)); // past the 15-min window
        Assert.ThrowsAsync<ProfileShareImportTokenException>(
            () => svc.ImportCommitAsync(preview.ImportToken, CancellationToken.None));
    }

    [Test]
    public async Task Import_preview_caps_the_pending_set() {
        // A caller that floods preview without committing is capped so the in-memory
        // store can't grow unboundedly — the (cap+1)th preview is refused.
        using var repo = new FakeRepo(DonorSnapshot());
        var svc = new ProfileShareService(repo);
        var share = await svc.ExportAsync(ProfileId, CancellationToken.None);
        // Reference the constant (not a literal 32) so the test tracks the cap.
        for (var i = 0; i < ProfileShareService.MaxPendingImports; i++) {
            await svc.ImportPreviewAsync(share!.Manifest, CancellationToken.None);
        }
        Assert.ThrowsAsync<ProfileShareImportThrottledException>(
            () => svc.ImportPreviewAsync(share!.Manifest, CancellationToken.None));
    }

    [Test]
    public async Task Dropped_fields_advisory_matches_what_the_export_actually_strips() {
        // Self-enforcing guard against drift: every category the import advertises as
        // "you must re-enter this" must correspond to a field the export really emptied.
        // If the export ever stops stripping one of these, this fails rather than
        // silently showing the recipient a misleading list. (The reverse direction —
        // a newly-stripped field nobody added to the advisory — is tracked in PORT_TODO.)
        using var repo = new FakeRepo(DonorSnapshot());
        var svc = new ProfileShareService(repo);
        var share = await svc.ExportAsync(ProfileId, CancellationToken.None);
        var preview = await svc.ImportPreviewAsync(share!.Manifest, CancellationToken.None);
        var settings = share.Manifest.GetProperty("settings");

        var categories = new (string advisoryKeyword, Func<bool> stripped)[] {
            ("Save directory", () =>
                settings.GetProperty("storage").GetProperty("save_directory").GetString()!.Length == 0),
            ("ASTAP", () =>
                settings.GetProperty("plate_solve").GetProperty("path_or_endpoint").GetString()!.Length == 0),
            ("Site location", () =>
                settings.GetProperty("site").GetProperty("latitude_deg").GetDouble() == 0),
            ("PHD2", () =>
                settings.GetProperty("phd2").GetProperty("host").GetString()!.Length == 0),
            ("Notification", () =>
                settings.GetProperty("notifications").GetProperty("pushover_token").GetString()!.Length == 0),
        };
        foreach (var (keyword, stripped) in categories) {
            preview.DroppedFields.Should().Contain(
                s => s.Contains(keyword, StringComparison.OrdinalIgnoreCase),
                $"the dropped-fields advisory should name the stripped '{keyword}' category");
            stripped().Should().BeTrue($"the export must actually strip '{keyword}'");
        }
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

    /// A TimeProvider whose clock only moves when the test advances it — lets the
    /// import-expiry test step past the TTL deterministically.
    private sealed class MutableTimeProvider : TimeProvider {
        private DateTimeOffset _now;
        public MutableTimeProvider(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// Minimal IProfileRepository double — GetProfile feeds export; Create records
    /// its arguments so the import tests can assert what the commit handed back.
    private sealed class FakeRepo : IProfileRepository {
        private readonly StoredProfileDto? _stored;
        private readonly IReadOnlyList<ProfileMetaDto> _existing;

        public FakeRepo(ProfileSnapshotDto? settings, string name = "Test profile",
            params string[] existingNames) {
            _stored = settings is null
                ? null
                : new StoredProfileDto(
                    new ProfileMetaDto(ProfileId, name, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                    settings);
            _existing = existingNames
                .Select(n => new ProfileMetaDto(Guid.NewGuid(), n, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))
                .ToList();
        }

        public StoredProfileDto? GetProfile(Guid id) => id == ProfileId ? _stored : null;

        /// The Guid a successful Create hands back — import asserts the committed id matches.
        public Guid CreatedId { get; } = Guid.Parse("99999999-9999-9999-9999-999999999999");
        public string? CreatedName { get; private set; }
        public ProfileSnapshotDto? CreatedSettings { get; private set; }
        public bool CreatedMakeActive { get; private set; }

        // Mirror the real repo: List() returns ALL profiles, including the stored one
        // (not just the `existingNames` extras) — so a dedup test that collides with
        // the stored profile's name can't false-pass.
        public ProfileListDto List() => new(null,
            _stored is null ? _existing : _existing.Prepend(_stored.Meta).ToList());
        public Guid? ActiveId => null;
        public ProfileMetaDto Create(string name, ProfileSnapshotDto? settings, bool makeActive) {
            CreatedName = name;
            CreatedSettings = settings;
            CreatedMakeActive = makeActive;
            return new ProfileMetaDto(CreatedId, name, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        }
        public bool Rename(Guid id, string name) => throw new NotSupportedException();
        public ProfileDeleteResult Delete(Guid id) => throw new NotSupportedException();
        public bool SelectProfile(Guid id) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
