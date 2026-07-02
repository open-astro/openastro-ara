#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OpenAstroAra.Server.Services;

// Phase 14a: source-gen context lives in the parent namespace; bring it
// in here so Persist/LoadOrDefaults can use the typed overloads (no
// reflection, no AOT warnings).

/// <summary>
/// Phase 12h.7 — file-backed profile store. Wraps an in-memory cache
/// for hot reads; serializes the full <see cref="ProfileSnapshotDto"/>
/// to <c>profile.json</c> after each <c>Put*</c> so settings survive
/// daemon restart.
///
/// Storage path: <c>{profileDir}/profile.json</c>, where <c>profileDir</c>
/// is resolved in <c>Program.cs</c> (env <c>OPENASTROARA_PROFILE_DIR</c>
/// → <c>/var/lib/openastroara</c> for systemd-managed installs → a
/// per-user fallback for dev runs).
///
/// Atomic writes: serialize to <c>profile.json.tmp</c> first, then
/// <see cref="File.Move(string,string,bool)"/> overwriting the canonical
/// file. A crash mid-write leaves the previous version intact rather
/// than a truncated file.
///
/// Multi-profile + §42 import/export lands in v0.1.0 per the §55.1
/// roadmap; this class is the foundation that both reuse.
/// </summary>
public sealed partial class FileProfileStore : IProfileStore {
    private readonly object _lock = new();
    private readonly string _profileDir;
    private readonly string _profilePath;
    private readonly string _tempPath;
    private readonly ILogger<FileProfileStore> _logger;
    // Pretty-printed variant of the AOT-safe context for on-disk
    // readability. Reading uses the default (non-indented) instance.
    private static readonly AraJsonSerializerContext _indentedContext =
        new(new JsonSerializerOptions(AraJsonSerializerContext.Default.Options) {
            WriteIndented = true,
        });

    private ProfileSnapshotDto _snapshot;

    public FileProfileStore(string profileDir, ILogger<FileProfileStore>? logger = null) {
        _profileDir = profileDir;
        _profilePath = Path.Combine(profileDir, "profile.json");
        _tempPath = _profilePath + ".tmp";
        _logger = logger ?? NullLogger<FileProfileStore>.Instance;

        _snapshot = LoadOrDefaults();
    }

    public ImagingDefaultsDto GetImagingDefaults() { lock (_lock) { return _snapshot.ImagingDefaults; } }
    public void PutImagingDefaults(ImagingDefaultsDto value) => UpdateAndPersist(s => s with { ImagingDefaults = value });

    public StretchDefaultsDto GetStretchDefaults() { lock (_lock) { return _snapshot.StretchDefaults; } }
    public void PutStretchDefaults(StretchDefaultsDto value) => UpdateAndPersist(s => s with { StretchDefaults = value });

    public StorageSettingsDto GetStorageSettings() { lock (_lock) { return _snapshot.Storage; } }
    public void PutStorageSettings(StorageSettingsDto value) => UpdateAndPersist(s => s with { Storage = value });

    public NotificationsSettingsDto GetNotificationsSettings() { lock (_lock) { return _snapshot.Notifications; } }
    public void PutNotificationsSettings(NotificationsSettingsDto value) => UpdateAndPersist(s => s with { Notifications = value });

    public SiteSettingsDto GetSiteSettings() { lock (_lock) { return _snapshot.Site; } }
    public void PutSiteSettings(SiteSettingsDto value) => UpdateAndPersist(s => s with { Site = value });

    public FilenamesSettingsDto GetFilenamesSettings() { lock (_lock) { return _snapshot.Filenames; } }
    public void PutFilenamesSettings(FilenamesSettingsDto value) => UpdateAndPersist(s => s with { Filenames = value });

    public SafetyPoliciesDto GetSafetyPolicies() { lock (_lock) { return _snapshot.SafetyPolicies; } }
    public void PutSafetyPolicies(SafetyPoliciesDto value) => UpdateAndPersist(s => s with { SafetyPolicies = value });

    public AutofocusSettingsDto GetAutofocusSettings() { lock (_lock) { return _snapshot.Autofocus; } }
    public void PutAutofocusSettings(AutofocusSettingsDto value) => UpdateAndPersist(s => s with { Autofocus = value });

    public PlateSolveSettingsDto GetPlateSolveSettings() { lock (_lock) { return _snapshot.PlateSolve; } }
    public void PutPlateSolveSettings(PlateSolveSettingsDto value) => UpdateAndPersist(s => s with { PlateSolve = value });

    public DiagnosticsModeDto GetDiagnosticsMode() { lock (_lock) { return _snapshot.DiagnosticsMode; } }
    public void PutDiagnosticsMode(DiagnosticsModeDto value) => UpdateAndPersist(s => s with { DiagnosticsMode = value });

    public Phd2SettingsDto GetPhd2Settings() { lock (_lock) { return _snapshot.Phd2; } }
    public void PutPhd2Settings(Phd2SettingsDto value) => UpdateAndPersist(s => s with { Phd2 = value });

    public EquipmentConnectionDto GetEquipmentConnection() { lock (_lock) { return _snapshot.EquipmentConnection; } }
    public void PutEquipmentConnection(EquipmentConnectionDto value) => UpdateAndPersist(s => s with { EquipmentConnection = value });

    public OpticsSettingsDto GetOpticsSettings() { lock (_lock) { return _snapshot.Optics; } }
    public void PutOpticsSettings(OpticsSettingsDto value) => UpdateAndPersist(s => s with { Optics = value });

    public OpticsSettingsDto UpdateOpticsSettings(Func<OpticsSettingsDto, OpticsSettingsDto?> update) {
        OpticsSettingsDto next;
        lock (_lock) {
            var current = _snapshot.Optics;
            var candidate = update(current);
            if (candidate is null || candidate == current) {
                return current; // no change — no persist, no event
            }
            next = candidate;
            _snapshot = _snapshot with { Optics = next };
            Persist(_snapshot);
        }
        // Outside _lock, mirroring UpdateAndPersist, so a Changed subscriber that reads back can't deadlock.
        Changed?.Invoke(this, EventArgs.Empty);
        return next;
    }

    public CameraElectronicsDto GetCameraElectronics() { lock (_lock) { return _snapshot.CameraElectronics; } }
    public void PutCameraElectronics(CameraElectronicsDto value) => UpdateAndPersist(s => s with { CameraElectronics = value });

    public CameraElectronicsDto UpdateCameraElectronics(Func<CameraElectronicsDto, CameraElectronicsDto?> update) {
        CameraElectronicsDto next;
        lock (_lock) {
            var current = _snapshot.CameraElectronics;
            var candidate = update(current);
            if (candidate is null || candidate == current) {
                return current; // no change — no persist, no event
            }
            next = candidate;
            _snapshot = _snapshot with { CameraElectronics = next };
            Persist(_snapshot);
        }
        // Outside _lock, mirroring UpdateAndPersist, so a Changed subscriber that reads back can't deadlock.
        Changed?.Invoke(this, EventArgs.Empty);
        return next;
    }

    public FilterSetDto GetFilterSet() { lock (_lock) { return _snapshot.FilterSet; } }
    public void PutFilterSet(FilterSetDto value) => UpdateAndPersist(s => s with { FilterSet = value });

    public FilterWheelLabelsDto GetFilterWheelLabels() { lock (_lock) { return _snapshot.FilterWheelLabels; } }
    public void PutFilterWheelLabels(FilterWheelLabelsDto value) => UpdateAndPersist(s => s with { FilterWheelLabels = value });

    public event EventHandler? Changed;

    private void UpdateAndPersist(Func<ProfileSnapshotDto, ProfileSnapshotDto> mutate) {
        lock (_lock) {
            _snapshot = mutate(_snapshot);
            Persist(_snapshot);
        }
        // Raised OUTSIDE _lock so a subscriber that reads back through the Get* methods can't
        // deadlock against the store.
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Persist(ProfileSnapshotDto snapshot) {
        try {
            Directory.CreateDirectory(_profileDir);
            var json = JsonSerializer.Serialize(snapshot, _indentedContext.ProfileSnapshotDto);
            File.WriteAllText(_tempPath, json);
            File.Move(_tempPath, _profilePath, overwrite: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            // Best-effort persistence. In-memory state still updated;
            // the user's next save attempt will retry.
            LogPersistFailed(ex, _profilePath);
        }
    }

    private ProfileSnapshotDto LoadOrDefaults() {
        var defaults = DefaultSnapshot();
        if (!File.Exists(_profilePath)) {
            // First boot — write the defaults out so the user can see
            // the schema even before they edit anything.
            try {
                Directory.CreateDirectory(_profileDir);
                Persist(defaults);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                LogInitialWriteFailed(ex, _profilePath);
            }
            return defaults;
        }
        try {
            var json = File.ReadAllText(_profilePath);
            var loaded = JsonSerializer.Deserialize(json, AraJsonSerializerContext.Default.ProfileSnapshotDto);
            if (loaded is null) {
                LogDeserializedNull(_profilePath);
                return defaults;
            }
            // Back-fill sections added after a profile.json was first written: a key
            // missing from an older file deserializes the (non-nullable) record
            // parameter to null rather than throwing, which would surface as a null
            // GET body on upgrade. §36 optics is the first such section. The nullable
            // cast sidesteps the "left operand is never null" NRT warning — the
            // runtime value genuinely can be null here.
            var optics = (OpticsSettingsDto?)loaded.Optics ?? defaults.Optics;
            // NEXTGEN §4 sections (camera electronics + filter set) — same back-fill.
            var electronics = (CameraElectronicsDto?)loaded.CameraElectronics ?? defaults.CameraElectronics;
            var filterSet = (FilterSetDto?)loaded.FilterSet ?? defaults.FilterSet;
            // FilterSetDto's list itself can be null from a hand-edited "filter_set": {} — normalize.
            if ((IReadOnlyList<PlanningFilterDto>?)filterSet.Filters is null) {
                filterSet = defaults.FilterSet;
            }
            // §37.4 slot labels — same back-fill + inner-list normalization.
            var wheelLabels = (FilterWheelLabelsDto?)loaded.FilterWheelLabels ?? defaults.FilterWheelLabels;
            if ((IReadOnlyList<string>?)wheelLabels.Labels is null) {
                wheelLabels = defaults.FilterWheelLabels;
            }
            return loaded with {
                Optics = optics, CameraElectronics = electronics, FilterSet = filterSet,
                FilterWheelLabels = wheelLabels,
            };
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            LogParseFailed(ex, _profilePath);
            return defaults;
        }
    }

    /// <summary>
    /// Hard-coded defaults matching <see cref="InMemoryProfileStore"/>
    /// + the client's notifier constructor defaults. Used on first boot
    /// (no profile.json) and on parse failure (corrupt file).
    /// </summary>
    private static ProfileSnapshotDto DefaultSnapshot() => new(
        ImagingDefaults: new(
            ExposureSeconds: 5, Gain: 100, Offset: 50, Bin: 1, FrameKind: "light",
            CoolerTargetC: -10.0, CoolerRampCPerMin: 1.0, WarmupAtSessionEnd: false),
        Storage: new(
            SaveDirectory: "/media/openastroara",
            FileFormat: "fits",
            Compression: "rice",
            FilenameTemplate: @"$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s"),
        Notifications: new(
            InAppBanner: true, OsDesktop: true, SoundAlert: true,
            PushoverToken: "", TelegramBotToken: "",
            OnSequenceComplete: true, OnSequencePaused: true, OnCriticalDiagnostic: true,
            OnSafetyEvent: true, OnAutofocusFailed: true, OnPlateSolveFailed: true,
            OnDiskSpaceLow: true),
        Site: new(
            SiteName: "Backyard", LatitudeDeg: 0.0, LongitudeDeg: 0.0, ElevationM: 0.0,
            TimeZone: "UTC", UseCustomHorizon: false, DefaultHorizonAltitudeDeg: 20.0,
            BortleClass: 6, TypicalSeeingArcsec: 2.5, TwilightDefinition: "astronomical"),
        Filenames: new(DateSeparator: "forward_slash", CompressDarksAndBias: true),
        SafetyPolicies: new(
            OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
            MeridianFlipAuto: true, MeridianPauseMin: 5, MeridianRecenter: true,
            MeridianRecalGuider: true, OnAltitudeLimit: "skip_target",
            ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
            GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: true),
        Autofocus: new(
            Method: "hfr_v_curve", Steps: 7, StepSize: 50, ExposureSeconds: 5,
            Binning: 1, AfFilter: "L", RunAfterFilterChange: true,
            TriggerTempDeltaC: 2.0, TriggerHfrDriftPct: 15.0, EveryNHours: 2,
            AbortSequenceOnAfFailure: true, RestorePositionOnFailure: true),
        PlateSolve: new(
            Engine: "astap", PathOrEndpoint: "/usr/bin/astap",
            IndexDownloadPath: "/var/lib/astap", SearchRadiusDeg: 30.0,
            DownsampleFactor: 2, TimeoutSeconds: 60, UseBlindFallback: true,
            CenterAfterSlew: true, SyncToCoordinates: true, MaxIterations: 5,
            ConvergenceToleranceArcsec: 60.0),
        DiagnosticsMode: new(Mode: "notify_only"),
        Phd2: new(
            Host: "localhost", Port: 4400, Phd2Profile: "Default",
            DitherEnabled: true, DitherEveryNFrames: 1, DitherPixels: 5.0,
            SettlePixels: 1.5, SettleTimeSec: 10, SettleTimeoutSec: 60,
            ForceCalibrationEachSession: false,
            GuideFocalLength: 0, GuidePixelSize: 0, RaAggressiveness: 0.7,
            DecAggressiveness: 0.7, MinimumMove: 0.15, DecGuideMode: "auto"),
        EquipmentConnection: new(
            Camera: true, Mount: true, Focuser: true, FilterWheel: true,
            Rotator: true, Guider: false, FlatPanel: true, Dome: false,
            Weather: false, SafetyMonitor: true),
        // §65.2 stretch defaults. Lights default to auto_stf (matches
        // PixInsight + NINA UX). Manual sliders seed at the §65.2 example
        // values (bp=0.02, mp=0.5, wp=0.98). Asinh β = 3.0 (§65.1 default).
        // Linear clip percentiles per §65.1 (0.5% / 99.5%).
        StretchDefaults: new(
            LightDefault: "auto_stf",
            ManualDefaultParams: new(Blackpoint: 0.02, Midpoint: 0.5, Whitepoint: 0.98),
            AsinhDefaultBeta: 3.0,
            LinearClipPercentilesLow: 0.005,
            LinearClipPercentilesHigh: 0.995),
        // §36 optics — 0s mean "not yet configured"; ReducerFactor 1.0 so
        // it's never a zero multiplier in the framing math.
        Optics: new(
            FocalLengthMm: 0, ReducerFactor: 1.0,
            SensorWidthPx: 0, SensorHeightPx: 0, PixelSizeUm: 0),
        // NEXTGEN §4 — unset until the user enters values / a camera connect
        // auto-captures; planning falls back to Tier-0 defaults and says so.
        CameraElectronics: new(),
        FilterSet: new(Filters: []),
        FilterWheelLabels: FilterWheelLabelsDto.Default);

    #region LoggerMessage delegates (CA1848)

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to persist profile to {Path}")]
    private partial void LogPersistFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write initial profile to {Path}")]
    private partial void LogInitialWriteFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Profile file {Path} deserialized to null — using defaults")]
    private partial void LogDeserializedNull(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Profile file {Path} could not be parsed — using defaults")]
    private partial void LogParseFailed(Exception ex, string path);

    #endregion
}