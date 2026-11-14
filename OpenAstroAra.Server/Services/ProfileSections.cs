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
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace OpenAstroAra.Server.Services;

/// <summary>One profile section, as the table sees it, whatever its DTO type.</summary>
internal abstract class ProfileSection {
    public abstract string Name { get; }
    public abstract Type DtoType { get; }
    /// <summary>The DTO fields a share file carries as they are.</summary>
    public abstract IReadOnlyList<string> Kept { get; }
    /// <summary>The DTO fields a share file replaces with a neutral value.</summary>
    public abstract IReadOnlyList<string> Stripped { get; }
    public abstract object? DefaultValue { get; }
    /// <summary>The stored section, whatever its DTO type.</summary>
    public abstract object? Read(IProfileStore store);
    public abstract object? ValueIn(ProfileSnapshotDto snap);
    public abstract ProfileSnapshotDto Capture(IProfileStore store, ProfileSnapshotDto into);
    public abstract void Apply(IProfileStore store, ProfileSnapshotDto snap);
    public abstract ProfileSnapshotDto WithDefault(ProfileSnapshotDto snap);
    public abstract ProfileSnapshotDto Normalize(ProfileSnapshotDto snap, Func<string, bool> fileExists);
    public abstract ProfileSnapshotDto Strip(ProfileSnapshotDto snap);
}

/// <summary>
/// One entry of the section table (#1697, record 0012): how a section is read from and written to the
/// snapshot and the store, what a fresh profile holds, how a loaded value is healed, and what a share
/// file carries. A null <c>Default</c> means null is meaningful ("never calibrated") and passes through.
/// </summary>
/// <param name="kept">Every DTO field a share carries, by name.</param>
/// <param name="stripped">Every DTO field a share replaces; together with <paramref name="kept"/> it names
/// each constructor parameter of the DTO once, which a test checks.</param>
/// <param name="strip">The share's replacement value; null keeps the section whole.</param>
/// <param name="heal">Repairs a non-null loaded value (retired tokens, null inner lists, stock paths).</param>
internal sealed class ProfileSection<T>(
    string name,
    Func<ProfileSnapshotDto, T?> read,
    Func<ProfileSnapshotDto, T?, ProfileSnapshotDto> write,
    Func<IProfileStore, T?> get,
    Action<IProfileStore, T?> put,
    T? @default,
    IReadOnlyList<string> kept,
    IReadOnlyList<string> stripped,
    Func<T, T?>? strip = null,
    Func<T, Func<string, bool>, T>? heal = null) : ProfileSection where T : class {

    public T? Default { get; } = @default;
    public override string Name => name;
    public override Type DtoType => typeof(T);
    public override IReadOnlyList<string> Kept => kept;
    public override IReadOnlyList<string> Stripped => stripped;
    public override object? DefaultValue => Default;
    public override object? Read(IProfileStore store) => get(store);
    public override object? ValueIn(ProfileSnapshotDto snap) => read(snap);

    public override ProfileSnapshotDto Capture(IProfileStore store, ProfileSnapshotDto into) => write(into, get(store));

    public override void Apply(IProfileStore store, ProfileSnapshotDto snap) => put(store, read(snap) ?? Default);

    public override ProfileSnapshotDto WithDefault(ProfileSnapshotDto snap) => write(snap, Default);

    public override ProfileSnapshotDto Normalize(ProfileSnapshotDto snap, Func<string, bool> fileExists) {
        var value = read(snap) ?? Default;
        return value is null ? write(snap, null) : write(snap, heal is null ? value : heal(value, fileExists));
    }

    // A section a hand-built snapshot left null still gets its strip (the default stands in), so nothing
    // unclassified slips through.
    public override ProfileSnapshotDto Strip(ProfileSnapshotDto snap) =>
        strip is not null && (read(snap) ?? Default) is { } value ? write(snap, strip(value)) : snap;
}

/// <summary>
/// The section table (#1697, record 0012): every profile section is declared once, here, with its
/// store accessors, its default and its share policy. <see cref="ProfileStoreSnapshot"/> (Capture and
/// Apply), <see cref="ProfileSnapshotNormalizer"/> and <see cref="ProfileShareService.StripForShare"/> walk
/// <see cref="All"/>; the stores read the defaults. To add a section, add its DTO field to
/// <see cref="ProfileSnapshotDto"/>, its Get/Put pair to <see cref="IProfileStore"/> and one entry below.
/// A test fails on a section left out of the table, on a DTO field that is neither kept nor stripped, and
/// on a store whose default differs from the table's.
/// </summary>
internal static class ProfileSections {
    /// <summary>PHD2's default port (§63); the neutral value once the donor's port is stripped.</summary>
    private const int DefaultPhd2Port = 4400;

    /// <summary>Exposure, gain and cooler tuning, no PII.</summary>
    public static readonly ProfileSection<ImagingDefaultsDto> ImagingDefaults = new(
        "ImagingDefaults",
        s => s.ImagingDefaults,
        (s, v) => s with { ImagingDefaults = v! },
        st => st.GetImagingDefaults(),
        (st, v) => st.PutImagingDefaults(v!),
        new(
            ExposureSeconds: 5, Gain: 100, Offset: 50, Bin: 1, FrameKind: "light",
            CoolerTargetC: -10.0, CoolerRampCPerMin: 1.0, WarmupAtSessionEnd: false),
        kept: ["ExposureSeconds", "Gain", "Offset", "Bin", "FrameKind", "CoolerTargetC", "CoolerRampCPerMin", "WarmupAtSessionEnd"],
        stripped: []);

    /// <summary>Paths are host-specific and un-remappable across OSes, so the recipient sets them in the wizard.</summary>
    public static readonly ProfileSection<StorageSettingsDto> Storage = new(
        "Storage",
        s => s.Storage,
        (s, v) => s with { Storage = v! },
        st => st.GetStorageSettings(),
        (st, v) => st.PutStorageSettings(v!),
        new(
            SaveDirectory: "/media/openastroara", // the §13 systemd unit's WorkingDirectory
            FileFormat: StorageFormatTokens.Fits,
            Compression: StorageFormatTokens.CompressionZstd,
            FilenameTemplate: @"$$TARGETNAME$$\\$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s"),
        kept: ["FileFormat", "Compression", "MinFreeDiskWarnGb", "MinFreeDiskCriticalGb", "BackupRetentionCount", "FaultLogRetentionDays"],
        stripped: ["SaveDirectory", "FilenameTemplate"],
        strip: v => v with { SaveDirectory = "", FilenameTemplate = "" },
        heal: (v, _) => StorageFormatTokens.Normalize(v));

    /// <summary>Both halves of each channel are secrets (token + user key, bot token + chat id) and never travel in a share file.</summary>
    public static readonly ProfileSection<NotificationsSettingsDto> Notifications = new(
        "Notifications",
        s => s.Notifications,
        (s, v) => s with { Notifications = v! },
        st => st.GetNotificationsSettings(),
        (st, v) => st.PutNotificationsSettings(v!),
        new(
            InAppBanner: true, OsDesktop: true, SoundAlert: true,
            PushoverToken: "", TelegramBotToken: "",
            OnSequenceComplete: true, OnSequencePaused: true, OnCriticalDiagnostic: true,
            OnSafetyEvent: true, OnAutofocusFailed: true, OnPlateSolveFailed: true,
            OnDiskSpaceLow: true),
        kept: ["InAppBanner", "OsDesktop", "SoundAlert", "OnSequenceComplete", "OnSequencePaused", "OnCriticalDiagnostic", "OnSafetyEvent", "OnAutofocusFailed", "OnPlateSolveFailed", "OnDiskSpaceLow"],
        stripped: ["PushoverToken", "TelegramBotToken", "PushoverUserKey", "TelegramChatId"],
        strip: v => v with { PushoverToken = "", TelegramBotToken = "", PushoverUserKey = "", TelegramChatId = "" });

    /// <summary>Lat/lon default to 0,0 (the astrometry math works there; the wizard prompts for the real site). The donor's location and name are stripped; sky-quality judgement (bortle, seeing, SQM, twilight, horizon) stays as context.</summary>
    public static readonly ProfileSection<SiteSettingsDto> Site = new(
        "Site",
        s => s.Site,
        (s, v) => s with { Site = v! },
        st => st.GetSiteSettings(),
        (st, v) => st.PutSiteSettings(v!),
        new(
            SiteName: "Backyard", LatitudeDeg: 0.0, LongitudeDeg: 0.0, ElevationM: 0.0,
            TimeZone: "UTC", UseCustomHorizon: false, DefaultHorizonAltitudeDeg: 20.0,
            BortleClass: 6, TypicalSeeingArcsec: 2.5, TwilightDefinition: "astronomical"),
        kept: ["UseCustomHorizon", "DefaultHorizonAltitudeDeg", "BortleClass", "TypicalSeeingArcsec", "TwilightDefinition", "MaxSequenceRuntimeMin", "SoftWarningAltitudeDeg", "SqmMagPerArcsec2", "PermanentPier"],
        stripped: ["SiteName", "LatitudeDeg", "LongitudeDeg", "ElevationM", "TimeZone", "ObserverName"],
        strip: v => v with { SiteName = "", LatitudeDeg = 0, LongitudeDeg = 0, ElevationM = 0, TimeZone = "", ObserverName = "" });

    /// <summary>A date-separator enum and toggles, not paths.</summary>
    public static readonly ProfileSection<FilenamesSettingsDto> Filenames = new(
        "Filenames",
        s => s.Filenames,
        (s, v) => s with { Filenames = v! },
        st => st.GetFilenamesSettings(),
        (st, v) => st.PutFilenamesSettings(v!),
        new(DateSeparator: "forward_slash", CompressDarksAndBias: true),
        kept: ["DateSeparator", "CompressDarksAndBias", "HeaderSite", "HeaderOptics", "HeaderTemperature", "HeaderWeather", "HeaderIdentity", "HeaderEphemeris"],
        stripped: []);

    /// <summary>Tuning judgement; the donor's first-flip confirmation describes the donor's mount, so the recipient confirms their own.</summary>
    public static readonly ProfileSection<SafetyPoliciesDto> SafetyPolicies = new(
        "SafetyPolicies",
        s => s.SafetyPolicies,
        (s, v) => s with { SafetyPolicies = v! },
        st => st.GetSafetyPolicies(),
        (st, v) => st.PutSafetyPolicies(v!),
        new(
            OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
            MeridianFlipAuto: true, MeridianPauseMin: 5, MeridianRecenter: true,
            MeridianRecalGuider: true, OnAltitudeLimit: "skip_target",
            ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
            GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: true),
        kept: ["OnUnsafe", "AutoResumeWhenSafe", "ResumeDelayMin", "MeridianFlipAuto", "MeridianPauseMin", "MeridianRecenter", "MeridianRecalGuider", "OnAltitudeLimit", "ParkIfNoMoreTargets", "OnGuiderLost", "GuiderRetryTimeoutSec", "SkipTargetIfRecoveryFails", "OnDiskSpaceCritical", "CalibrationCaptureDefault", "FlipSafetyEnabled", "ExpectedFlipSlewSeconds", "UnattendedEscalation", "UnattendedShutdownEnabled", "UnattendedShutdownWaitMinutes", "WeatherTriggersEnabled", "MaxWindKmh", "MaxHumidityPct", "MinDewDeltaC", "FlatTargetAdu", "FlatTargetAduTolerancePct", "FlatFramesPerFilter", "PostFlatParkMount", "SkyFlatTargetAdu", "SkyFlatFramesPerFilter", "SkyFlatTargetAzimuth", "SkyFlatTargetAltitude", "SkyFlatStopAtMaxAdu", "SkyFlatStopAtMinAdu", "SkyFlatSunAltitude", "HotReconnectEnabled", "OnCameraLost", "OnMountLost", "OnTrackingLost", "SwitchValueTolerancePct", "MeridianRefocus", "MeridianRefocusTempThresholdC", "MeridianGuiderRecal", "UnattendedWindowStart", "UnattendedWindowEnd", "FlatTargetPercent"],
        stripped: ["FirstFlipConfirmed"],
        strip: v => v with { FirstFlipConfirmed = false });

    /// <summary>Method, sweep and trigger judgement; the focuser backlash is measured on the donor's focuser.</summary>
    public static readonly ProfileSection<AutofocusSettingsDto> Autofocus = new(
        "Autofocus",
        s => s.Autofocus,
        (s, v) => s with { Autofocus = v! },
        st => st.GetAutofocusSettings(),
        (st, v) => st.PutAutofocusSettings(v!),
        new(
            Method: "hfr_v_curve", Steps: 7, StepSize: 50, ExposureSeconds: 5,
            Binning: 1, AfFilter: "L", RunAfterFilterChange: true,
            TriggerTempDeltaC: 2.0, TriggerHfrDriftPct: 15.0, EveryNHours: 2,
            AbortSequenceOnAfFailure: true, RestorePositionOnFailure: true,
            TelescopeType: "other", StepSizeAuto: true),
        kept: ["Method", "Steps", "StepSize", "ExposureSeconds", "Binning", "AfFilter", "RunAfterFilterChange", "TriggerTempDeltaC", "TriggerHfrDriftPct", "EveryNHours", "AbortSequenceOnAfFailure", "RestorePositionOnFailure", "TelescopeType", "StepSizeAuto"],
        stripped: ["BacklashInSteps", "BacklashOutSteps"],
        strip: v => v with { BacklashInSteps = 0, BacklashOutSteps = 0 });

    /// <summary>The solver and index paths are host-specific; the engine and knobs stay.</summary>
    public static readonly ProfileSection<PlateSolveSettingsDto> PlateSolve = new(
        "PlateSolve",
        s => s.PlateSolve,
        (s, v) => s with { PlateSolve = v! },
        st => st.GetPlateSolveSettings(),
        (st, v) => st.PutPlateSolveSettings(v!),
        new(
            Engine: "astap", PathOrEndpoint: SolverPathMigration.AstapCliPath, // the multi-core solver bundled with the daemon
            IndexDownloadPath: SolverPathMigration.DefaultDatabasePath, SearchRadiusDeg: 30.0,
            DownsampleFactor: 0, // 0 = auto from the pixel scale (#1136)
            TimeoutSeconds: 60, UseBlindFallback: true,
            CenterAfterSlew: true, SyncToCoordinates: true, MaxIterations: 5,
            ConvergenceToleranceArcsec: 60.0),
        kept: ["Engine", "SearchRadiusDeg", "DownsampleFactor", "TimeoutSeconds", "UseBlindFallback", "CenterAfterSlew", "SyncToCoordinates", "MaxIterations", "ConvergenceToleranceArcsec"],
        stripped: ["PathOrEndpoint", "IndexDownloadPath"],
        strip: v => v with { PathOrEndpoint = "", IndexDownloadPath = "" },
        heal: // #1121/#1136 - a stock solver path becomes the bundled /opt/openastroara/astap_cli once it is on disk.
            (v, fileExists) => SolverPathMigration.Migrate(v, fileExists));

    /// <summary>A single enum; notify_only never auto-pauses a sequence (§51).</summary>
    public static readonly ProfileSection<DiagnosticsModeDto> DiagnosticsMode = new(
        "DiagnosticsMode",
        s => s.DiagnosticsMode,
        (s, v) => s with { DiagnosticsMode = v! },
        st => st.GetDiagnosticsMode(),
        (st, v) => st.PutDiagnosticsMode(v!),
        new(Mode: "notify_only"),
        kept: ["Mode"],
        stripped: []);

    /// <summary>The donor's network, guider gear selection and guide-scope geometry are stripped (the geometry is lifted into the rig description); dither, settle and aggressiveness stay.</summary>
    public static readonly ProfileSection<Phd2SettingsDto> Phd2 = new(
        "Phd2",
        s => s.Phd2,
        (s, v) => s with { Phd2 = v! },
        st => st.GetPhd2Settings(),
        (st, v) => st.PutPhd2Settings(v!),
        new(
            Host: "localhost", Port: DefaultPhd2Port, Phd2Profile: "Default",
            DitherEnabled: true, DitherEveryNFrames: 1, DitherPixels: 5.0,
            SettlePixels: 1.5, SettleTimeSec: 10, SettleTimeoutSec: 60,
            ForceCalibrationEachSession: false,
            GuideFocalLength: 0, GuidePixelSize: 0, RaAggressiveness: 0.7,
            DecAggressiveness: 0.7, MinimumMove: 0.15, DecGuideMode: "auto", DecMinimumMove: 0.15),
        kept: ["DitherEnabled", "DitherEveryNFrames", "DitherPixels", "SettlePixels", "SettleTimeSec", "SettleTimeoutSec", "ForceCalibrationEachSession", "RaAggressiveness", "DecAggressiveness", "MinimumMove", "DecGuideMode", "GuideExposureMinMs", "GuideExposureMaxMs", "GuideExposureMs", "DecMinimumMove", "GuideCameraGain"],
        stripped: ["Host", "Port", "Phd2Profile", "GuideFocalLength", "GuidePixelSize", "GuiderCamera", "GuiderCameraId", "GuiderCameraAlpacaUniqueId", "GuiderMount", "GuiderAuxMount", "GuiderRotator", "GuiderAlpacaHost", "GuiderAlpacaPort", "GuiderSetupType", "Phd2ProfileId"],
        strip: v => v with {
            Host = "",
            Port = DefaultPhd2Port,
            Phd2Profile = "",
            // Guider profile ids are small per-rig integers: the donor's id can name a different
            // profile on the recipient's guider, so the share falls back to Automatic.
            Phd2ProfileId = null,
            GuideFocalLength = 0,
            GuidePixelSize = 0,
            GuiderCamera = "",
            GuiderCameraId = "",
            GuiderCameraAlpacaUniqueId = "",
            GuiderMount = "",
            GuiderAuxMount = "",
            GuiderRotator = "",
            GuiderAlpacaHost = "",
            GuiderAlpacaPort = 0,
            GuiderSetupType = "guide_scope",
        });

    /// <summary>Every type auto-connects by default: auto-connect only tries devices the user connected before, so all-on is safe for unconfigured types and a working rig comes back after a reboot. A share keeps the booleans.</summary>
    public static readonly ProfileSection<EquipmentConnectionDto> EquipmentConnection = new(
        "EquipmentConnection",
        s => s.EquipmentConnection,
        (s, v) => s with { EquipmentConnection = v! },
        st => st.GetEquipmentConnection(),
        (st, v) => st.PutEquipmentConnection(v!),
        new(
            Camera: true, Mount: true, Focuser: true, FilterWheel: true,
            Rotator: true, Guider: true, FlatPanel: true, Dome: true,
            Weather: true, SafetyMonitor: true, Switch: true),
        kept: ["Camera", "Mount", "Focuser", "FilterWheel", "Rotator", "Guider", "FlatPanel", "Dome", "Weather", "SafetyMonitor", "Switch"],
        stripped: []);

    /// <summary>Lights default to auto_stf; calibration frames always render linear via the frame-type override. Display judgement (§65.2).</summary>
    public static readonly ProfileSection<StretchDefaultsDto> StretchDefaults = new(
        "StretchDefaults",
        s => s.StretchDefaults,
        (s, v) => s with { StretchDefaults = v! },
        st => st.GetStretchDefaults(),
        (st, v) => st.PutStretchDefaults(v!),
        new(
            LightDefault: "auto_stf",
            ManualDefaultParams: new(Blackpoint: 0.02, Midpoint: 0.5, Whitepoint: 0.98),
            AsinhDefaultBeta: 3.0,
            LinearClipPercentilesLow: 0.005,
            LinearClipPercentilesHigh: 0.995),
        kept: ["LightDefault", "ManualDefaultParams", "AsinhDefaultBeta", "LinearClipPercentilesLow", "LinearClipPercentilesHigh"],
        stripped: [],
        heal: (v, _) => (StretchManualDefaultsDto?)v.ManualDefaultParams is null
                ? v with { ManualDefaultParams = StretchDefaults!.Default!.ManualDefaultParams }
                : v);

    /// <summary>0s mean not yet configured (the wizard, Settings or the first camera connect fill them); ReducerFactor starts at 1.0 so framing never multiplies by zero. Rig geometry is lifted into the rig description, and the telescope name names the donor's gear.</summary>
    public static readonly ProfileSection<OpticsSettingsDto> Optics = new(
        "Optics",
        s => s.Optics,
        (s, v) => s with { Optics = v! },
        st => st.GetOpticsSettings(),
        (st, v) => st.PutOpticsSettings(v!),
        new(
            FocalLengthMm: 0, ReducerFactor: 1.0,
            SensorWidthPx: 0, SensorHeightPx: 0, PixelSizeUm: 0),
        kept: [],
        stripped: ["FocalLengthMm", "ReducerFactor", "SensorWidthPx", "SensorHeightPx", "PixelSizeUm", "ApertureMm", "TelescopeName"],
        strip: v => v with { FocalLengthMm = 0, ReducerFactor = 1.0, SensorWidthPx = 0, SensorHeightPx = 0, PixelSizeUm = 0, ApertureMm = 0, TelescopeName = "" });

    /// <summary>Unset until the user enters values or a camera connect auto-captures them. A share strips the donor's camera hardware; the recipient's camera auto-captures on connect.</summary>
    public static readonly ProfileSection<CameraElectronicsDto> CameraElectronics = new(
        "CameraElectronics",
        s => s.CameraElectronics,
        (s, v) => s with { CameraElectronics = v! },
        st => st.GetCameraElectronics(),
        (st, v) => st.PutCameraElectronics(v!),
        new(),
        kept: [],
        stripped: ["SensorName", "ReadNoiseE", "FullWellE", "ElectronsPerAdu", "Gain", "QuantumEfficiencyPeak", "AutoCaptured"],
        strip: _ => new CameraElectronicsDto());

    /// <summary>Empty until the user declares filters (NEXTGEN §4). A share strips the donor's physical filters; the recipient declares their own.</summary>
    public static readonly ProfileSection<FilterSetDto> FilterSet = new(
        "FilterSet",
        s => s.FilterSet,
        (s, v) => s with { FilterSet = v! },
        st => st.GetFilterSet(),
        (st, v) => st.PutFilterSet(v!),
        new(Filters: []),
        kept: [],
        stripped: ["Filters"],
        strip: _ => new FilterSetDto(Filters: []),
        heal: (v, _) => (IReadOnlyList<PlanningFilterDto>?)v.Filters is null ? FilterSet!.Default! : v);

    /// <summary>The reference-8 labels until the user edits (§37.4). Slot labels are names, not gear identity.</summary>
    public static readonly ProfileSection<FilterWheelLabelsDto> FilterWheelLabels = new(
        "FilterWheelLabels",
        s => s.FilterWheelLabels,
        (s, v) => s with { FilterWheelLabels = v! },
        st => st.GetFilterWheelLabels(),
        (st, v) => st.PutFilterWheelLabels(v!),
        FilterWheelLabelsDto.Default,
        kept: ["Labels"],
        stripped: [],
        heal: (v, _) => (IReadOnlyList<string>?)v.Labels is null ? FilterWheelLabels!.Default! : v);

    /// <summary>Empty until a skyline is entered (§36). A skyline fingerprints the site, same stance as the coordinates.</summary>
    public static readonly ProfileSection<CustomHorizonDto> CustomHorizon = new(
        "CustomHorizon",
        s => s.CustomHorizon,
        (s, v) => s with { CustomHorizon = v! },
        st => st.GetCustomHorizon(),
        (st, v) => st.PutCustomHorizon(v!),
        new(Points: []),
        kept: [],
        stripped: ["Points"],
        strip: _ => new CustomHorizonDto(Points: []),
        heal: (v, _) => (IReadOnlyList<CustomHorizonPointDto>?)v.Points is null ? CustomHorizon!.Default! : v);

    /// <summary>Null until a Classic AF sweep records one. Fitted to the donor's focuser and optical train; a share must not plant bad state (§59.2).</summary>
    public static readonly ProfileSection<FocusCalibrationDto> FocusCalibration = new(
        "FocusCalibration",
        s => s.FocusCalibration,
        (s, v) => s with { FocusCalibration = v },
        st => st.GetFocusCalibration(),
        (st, v) => st.PutFocusCalibration(v),
        null,
        kept: [],
        stripped: ["Samples", "CalibratedUtc", "FocuserTemperatureC", "Filter", "CurveHalfWidthSteps", "InFocusHfr", "BacklashUpSteps", "BacklashVerified"],
        strip: _ => null);

    /// <summary>Everything invalid until a build stamps it. The donor's dark library and defect map live on the donor's guider host (§30.7.4).</summary>
    public static readonly ProfileSection<CalibrationStateDto> CalibrationState = new(
        "CalibrationState",
        s => s.CalibrationState,
        (s, v) => s with { CalibrationState = v! },
        st => st.GetCalibrationState(),
        (st, v) => st.PutCalibrationState(v!),
        CalibrationStateDto.Empty,
        kept: [],
        stripped: ["DarkLibrary", "DefectMap"],
        strip: _ => CalibrationStateDto.Empty,
        heal: // e-4b-2 - back-fill missing entries too; never-calibrated is the safe default for any hole.
            (v, _) => new CalibrationStateDto(
                DarkLibrary: (GuiderCalibrationEntryDto?)v.DarkLibrary ?? new GuiderCalibrationEntryDto(),
                DefectMap: (GuiderCalibrationEntryDto?)v.DefectMap ?? new GuiderCalibrationEntryDto()));

    /// <summary>Polar-alignment tuning (exposure, tolerance, loop cadence); no path, network or identity.</summary>
    public static readonly ProfileSection<PolarAlignSettingsDto> PolarAlign = new(
        "PolarAlign",
        s => s.PolarAlign,
        (s, v) => s with { PolarAlign = v! },
        st => st.GetPolarAlignSettings(),
        (st, v) => st.PutPolarAlignSettings(v!),
        new(),
        kept: ["ExposureSeconds", "Binning", "TargetToleranceArcmin", "SeedRotationDeg", "LoopCadenceMs", "SettleSeconds", "LoopMode", "CheckStepDeg", "CoarseExposureSeconds", "UseHome"],
        stripped: []);

    /// <summary>Guard thresholds and probe ladder: tuning judgement.</summary>
    public static readonly ProfileSection<UnguidedSettingsDto> Unguided = new(
        "Unguided",
        s => s.Unguided,
        (s, v) => s with { Unguided = v! },
        st => st.GetUnguidedSettings(),
        (st, v) => st.PutUnguidedSettings(v!),
        new(),
        kept: ["GuardEnabled", "EccentricityTolerance", "GoodEccentricity", "StepDownAfter", "StepUpAfter", "ProbeRungsSeconds", "RecentreDriftArcmin", "RecentreSolveEveryN", "PriorToleranceBasis"],
        stripped: []);

    /// <summary>All-null by default: the guider keeps its own values. Guider tuning: star detection, algorithms, limits.</summary>
    public static readonly ProfileSection<GuiderAdvancedSettingsDto> GuiderAdvanced = new(
        "GuiderAdvanced",
        s => s.GuiderAdvanced,
        (s, v) => s with { GuiderAdvanced = v! },
        st => st.GetGuiderAdvancedSettings(),
        (st, v) => st.PutGuiderAdvancedSettings(v!),
        new(),
        kept: ["MultiStarEnabled", "FastRecenter", "TolerateJumps", "TolerateJumpsThresholdPx", "MinStarHfd", "MaxStarHfd", "MinStarSnr", "MassChangeThresholdEnabled", "MassChangeThreshold", "SearchRegionPx", "AutoExposureMinMs", "AutoExposureMaxMs", "AutoExposureTargetSnr", "NoiseReduction", "UseSubframes", "MaxRaDurationMs", "MaxDecDurationMs", "BacklashEnabled", "BacklashPulseMs", "BacklashFloorMs", "BacklashCeilingMs", "AssumeOrthogonal", "StopGuidingWhenSlewing", "CalFlipRequiresDecFlip", "VariableDelayEnabled", "VariableDelayShortS", "VariableDelayLongS", "DitherScaleFactor", "DitherRaOnly", "RaAlgorithm", "DecAlgorithm"],
        stripped: []);

    /// <summary>Home on first connect is on by default. Both fields are the donor's tuning judgement, so a share keeps them.</summary>
    public static readonly ProfileSection<FilterWheelPolicyDto> FilterWheelPolicy = new(
        "FilterWheelPolicy",
        s => s.FilterWheelPolicy,
        (s, v) => s with { FilterWheelPolicy = v! },
        st => st.GetFilterWheelPolicy(),
        (st, v) => st.PutFilterWheelPolicy(v!),
        FilterWheelPolicyDto.Default,
        kept: ["HomeOnFirstConnect", "UseFilterWheelOffsets"],
        stripped: []);

    /// <summary>Null until a TrackingProbe records one. Measured on the donor's mount.</summary>
    public static readonly ProfileSection<TrackingCalibrationDto> TrackingCalibration = new(
        "TrackingCalibration",
        s => s.TrackingCalibration,
        (s, v) => s with { TrackingCalibration = v },
        st => st.GetTrackingCalibration(),
        (st, v) => st.PutTrackingCalibration(v),
        null,
        kept: [],
        stripped: ["CeilingSeconds", "MeasuredUtc", "PolarAlignErrorArcmin", "PixelScaleArcsec", "SeeingFwhmArcsec", "PriorSeconds", "Source", "Rungs"],
        strip: _ => null);

    /// <summary>Null until a recalibrate fits one. Fitted to the donor's optics, camera and sky.</summary>
    public static readonly ProfileSection<SkyModelCalibrationDto> SkyModelCalibration = new(
        "SkyModelCalibration",
        s => s.SkyModelCalibration,
        (s, v) => s with { SkyModelCalibration = v },
        st => st.GetSkyModelCalibration(),
        (st, v) => st.PutSkyModelCalibration(v),
        null,
        kept: [],
        stripped: ["FittedUtc", "FramesConsidered", "Filters"],
        strip: _ => null);

    /// <summary>Nothing observed until a device connects. The donor's serial numbers; on the recipient's rig every slot would read as a gear change.</summary>
    public static readonly ProfileSection<EquipmentIdentityDto> EquipmentIdentity = new(
        "EquipmentIdentity",
        s => s.EquipmentIdentity,
        (s, v) => s with { EquipmentIdentity = v! },
        st => st.GetEquipmentIdentity(),
        (st, v) => st.PutEquipmentIdentity(v!),
        EquipmentIdentityDto.Empty,
        kept: [],
        stripped: ["Devices"],
        strip: _ => EquipmentIdentityDto.Empty,
        heal: // #1362 - a missing block, a null map or a null entry all read as "not observed".
            (v, _) => CleanIdentity(v));

    /// <summary>Nothing saved until the client writes the list. The donor's recent and saved targets are the donor's own list (CTO decision).</summary>
    public static readonly ProfileSection<SavedTargetsDto> SavedTargets = new(
        "SavedTargets",
        s => s.SavedTargets,
        (s, v) => s with { SavedTargets = v! },
        st => st.GetSavedTargets(),
        (st, v) => st.PutSavedTargets(v!),
        SavedTargetsDto.Empty,
        kept: [],
        stripped: ["Targets"],
        strip: _ => SavedTargetsDto.Empty,
        heal: (v, _) => SavedTargetsValidator.NormalizeLenient(v));

    /// <summary>No fan designated until the user chooses one. It names the donor's own switch; the recipient designates theirs.</summary>
    public static readonly ProfileSection<CoolingFanDto> CoolingFan = new(
        "CoolingFan",
        s => s.CoolingFan,
        (s, v) => s with { CoolingFan = v! },
        st => st.GetCoolingFan(),
        (st, v) => st.PutCoolingFan(v!),
        CoolingFanDto.None,
        kept: [],
        stripped: ["DeviceId", "PortId"],
        strip: _ => null);

    /// <summary>None saved until a calibration saves them. Measured on the donor's filters and focuser.</summary>
    public static readonly ProfileSection<FilterFocusOffsetsDto> FilterFocusOffsets = new(
        "FilterFocusOffsets",
        s => s.FilterFocusOffsets,
        (s, v) => s with { FilterFocusOffsets = v! },
        st => st.GetFilterFocusOffsets(),
        (st, v) => st.PutFilterFocusOffsets(v!),
        FilterFocusOffsetsDto.Empty,
        kept: [],
        stripped: ["Offsets"],
        strip: _ => FilterFocusOffsetsDto.Empty,
        heal: (v, _) => (IReadOnlyList<FilterFocusOffsetDto>?)v.Offsets is null ? FilterFocusOffsetsDto.Empty : v);

    /// <summary>Not following, no geometry, until the user sets them. The geometry measures the donor's dome; the recipient measures theirs.</summary>
    public static readonly ProfileSection<DomeSettingsDto> Dome = new(
        "Dome",
        s => s.Dome,
        (s, v) => s with { Dome = v! },
        st => st.GetDomeSettings(),
        (st, v) => st.PutDomeSettings(v!),
        DomeSettingsDto.Default,
        kept: [],
        stripped: ["FollowMount", "DomeRadiusMm", "ScopePositionNorthSouthMm", "ScopePositionEastWestMm", "ScopePositionUpDownMm", "MountType", "GemAxisMm", "LateralAxisMm", "DecOffsetHorizontalMm", "AzimuthToleranceDegrees", "SynchronizeDuringMountSlew", "DomeSyncTimeoutSeconds"],
        strip: _ => null);

    /// <summary>
    /// Every section, in the order Apply writes them. The order matters in one place: Safety policies come
    /// before Optics, because a store resets the first-flip confirmation when Optics changes (§58.8).
    /// </summary>
    public static readonly IReadOnlyList<ProfileSection> All = [
        ImagingDefaults,
        Storage,
        Notifications,
        Site,
        Filenames,
        SafetyPolicies,
        Autofocus,
        PlateSolve,
        DiagnosticsMode,
        Phd2,
        EquipmentConnection,
        StretchDefaults,
        Optics,
        CameraElectronics,
        FilterSet,
        FilterWheelLabels,
        CustomHorizon,
        FocusCalibration,
        CalibrationState,
        PolarAlign,
        Unguided,
        GuiderAdvanced,
        FilterWheelPolicy,
        TrackingCalibration,
        SkyModelCalibration,
        EquipmentIdentity,
        SavedTargets,
        CoolingFan,
        FilterFocusOffsets,
        Dome,
    ];

    /// <summary>
    /// What a fresh or unreadable profile holds: every section's default. Built once (a pure constant of
    /// immutable records). A key missing from the empty JSON object deserializes to null, the same hole an
    /// older profile.json has, so the walk fills every section.
    /// </summary>
    public static ProfileSnapshotDto Defaults { get; } = All.Aggregate(
        JsonSerializer.Deserialize("{}", AraJsonSerializerContext.Default.ProfileSnapshotDto)!,
        (snap, section) => section.WithDefault(snap));

    // #1362 - a missing block, a null map or a null entry all read as "not observed".
    private static EquipmentIdentityDto CleanIdentity(EquipmentIdentityDto identity) {
        if ((IReadOnlyDictionary<string, DeviceIdentityDto>?)identity.Devices is null) {
            return EquipmentIdentityDto.Empty;
        }
        if (identity.Devices.All(kv => kv.Value is not null && kv.Value.UniqueId is not null && kv.Value.Name is not null)) {
            return identity;
        }
        var clean = new Dictionary<string, DeviceIdentityDto>();
        foreach (var (slot, entry) in identity.Devices) {
            if (entry is not null) {
                clean[slot] = entry with { UniqueId = entry.UniqueId ?? string.Empty, Name = entry.Name ?? string.Empty };
            }
        }
        return new EquipmentIdentityDto(clean);
    }
}