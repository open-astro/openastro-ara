#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// The single home for "a profile snapshot never has null sections". A section
/// key missing from an older on-disk JSON deserializes the (non-nullable)
/// record parameter to null rather than throwing — and that null then either
/// ships on the wire (a <c>"camera_electronics": null</c> GET body) or NREs in
/// a consumer (the null-optics → <c>Optics().ApertureMm</c> path that once
/// 500'd /planning/optimal-sub).
///
/// Shared by BOTH places snapshots are born from disk: the active-profile
/// store (<see cref="FileProfileStore"/>, load + every write) and the
/// multi-profile repository (<see cref="FileProfileRepository.ReadFile"/>,
/// which serves GET /api/v1/profiles/{id} and feeds profile-share export) —
/// normalizing only the former was the #689 gap this closes.
/// </summary>
public static class ProfileSnapshotNormalizer {

    /// <summary>
    /// Hard-coded defaults matching <see cref="InMemoryProfileStore"/>
    /// + the client's notifier constructor defaults. Used on first boot
    /// (no profile.json), on parse failure (corrupt file), and as the
    /// per-section back-fill in <see cref="Normalize"/>. Built once: a pure
    /// constant of immutable records (profile-select pushes 16 back-to-back
    /// writes through <see cref="Normalize"/> — rebuilding per call is
    /// deterministic wasted allocation).
    /// </summary>
    public static ProfileSnapshotDto Defaults { get; } = BuildDefaults();

    /// <summary>Back-fills every null section (and the known null inner values —
    /// a <c>"filter_set": {}</c> yields a DTO with a null list) from
    /// <see cref="Defaults"/>. The nullable casts sidestep the "left operand is
    /// never null" NRT warnings — the runtime values genuinely can be null here
    /// (Json.NET-style holes in older files).</summary>
    public static ProfileSnapshotDto Normalize(ProfileSnapshotDto snap) {
        var defaults = Defaults;
        var filterSet = (FilterSetDto?)snap.FilterSet ?? defaults.FilterSet;
        if ((IReadOnlyList<PlanningFilterDto>?)filterSet.Filters is null) {
            filterSet = defaults.FilterSet;
        }
        var wheelLabels = (FilterWheelLabelsDto?)snap.FilterWheelLabels ?? defaults.FilterWheelLabels;
        if ((IReadOnlyList<string>?)wheelLabels.Labels is null) {
            wheelLabels = defaults.FilterWheelLabels;
        }
        var customHorizon = (CustomHorizonDto?)snap.CustomHorizon ?? defaults.CustomHorizon!;
        if ((IReadOnlyList<CustomHorizonPointDto>?)customHorizon.Points is null) {
            customHorizon = defaults.CustomHorizon!;
        }
        var stretch = (StretchDefaultsDto?)snap.StretchDefaults ?? defaults.StretchDefaults;
        if ((StretchManualDefaultsDto?)stretch.ManualDefaultParams is null) {
            stretch = stretch with { ManualDefaultParams = defaults.StretchDefaults.ManualDefaultParams };
        }
        return snap with {
            ImagingDefaults = (ImagingDefaultsDto?)snap.ImagingDefaults ?? defaults.ImagingDefaults,
            Storage = (StorageSettingsDto?)snap.Storage ?? defaults.Storage,
            Notifications = (NotificationsSettingsDto?)snap.Notifications ?? defaults.Notifications,
            Site = (SiteSettingsDto?)snap.Site ?? defaults.Site,
            Filenames = (FilenamesSettingsDto?)snap.Filenames ?? defaults.Filenames,
            SafetyPolicies = (SafetyPoliciesDto?)snap.SafetyPolicies ?? defaults.SafetyPolicies,
            Autofocus = (AutofocusSettingsDto?)snap.Autofocus ?? defaults.Autofocus,
            PlateSolve = (PlateSolveSettingsDto?)snap.PlateSolve ?? defaults.PlateSolve,
            DiagnosticsMode = (DiagnosticsModeDto?)snap.DiagnosticsMode ?? defaults.DiagnosticsMode,
            Phd2 = (Phd2SettingsDto?)snap.Phd2 ?? defaults.Phd2,
            EquipmentConnection = (EquipmentConnectionDto?)snap.EquipmentConnection ?? defaults.EquipmentConnection,
            StretchDefaults = stretch,
            Optics = (OpticsSettingsDto?)snap.Optics ?? defaults.Optics,
            CameraElectronics = (CameraElectronicsDto?)snap.CameraElectronics ?? defaults.CameraElectronics,
            FilterSet = filterSet,
            FilterWheelLabels = wheelLabels,
            CustomHorizon = customHorizon,
        };
    }

    private static ProfileSnapshotDto BuildDefaults() => new(
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
        FilterWheelLabels: FilterWheelLabelsDto.Default,
        // §36 custom terrain horizon — empty until the user enters a skyline.
        CustomHorizon: new(Points: []));
}
