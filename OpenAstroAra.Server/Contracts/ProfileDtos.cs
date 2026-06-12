#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts;

/// <summary>
/// §37 profile-section DTOs. Phase 12h.6a wires the first section
/// (imaging-defaults) end-to-end (server-side store + endpoints); other
/// sections follow in 12h.6b-N, one per WILMA settings panel.
///
/// Mirrors the WILMA client's <c>ImagingDefaults</c> model
/// (<c>lib/state/settings/imaging_defaults_state.dart</c>) field-for-field.
/// The §69 default-is-no-tooltip principle on the client side determined
/// which fields exist; the server stores whatever the client sends.
/// </summary>
public sealed record ImagingDefaultsDto(
    int ExposureSeconds,
    int Gain,
    int Offset,
    int Bin,
    string FrameKind,
    double CoolerTargetC,
    double CoolerRampCPerMin,
    bool WarmupAtSessionEnd);

/// <summary>
/// §29 storage settings (save directory + file format + compression +
/// filename template). `FileFormat` is one of `fits`/`xisf`/`fits_rice`/
/// `fits_gzip`; `Compression` is `off`/`rice`/`gzip`. Both are strings on
/// the wire (matches the §60.6 enum-as-string convention used everywhere
/// else); the client maps them back to its `StorageFileFormat` +
/// `StorageCompression` enums.
/// </summary>
public sealed record StorageSettingsDto(
    string SaveDirectory,
    string FileFormat,
    string Compression,
    string FilenameTemplate,
    // §29 disk-space monitor thresholds (whole GiB of free space on the save volume). Optional ctor defaults
    // so a profile.json written before these fields existed still deserializes (System.Text.Json source-gen
    // uses the default for a missing key). The monitor reads these live; DiskSpaceMonitor falls back to its own
    // 10/2 defaults if a stored pair is non-positive or inverted.
    int MinFreeDiskWarnGb = 10,
    int MinFreeDiskCriticalGb = 2);

/// <summary>
/// §54 notifications settings — channel toggles + per-channel tokens +
/// trigger toggles. Token fields are stored as plain strings here for
/// v0.0.1 simplicity; Phase 14 hardening will swap to either a
/// secret-ref by name (read from systemd-creds or similar) or
/// at-rest encryption per §40.
/// </summary>
public sealed record NotificationsSettingsDto(
    bool InAppBanner,
    bool OsDesktop,
    bool SoundAlert,
    string PushoverToken,
    string TelegramBotToken,
    bool OnSequenceComplete,
    bool OnSequencePaused,
    bool OnCriticalDiagnostic,
    bool OnSafetyEvent,
    bool OnAutofocusFailed,
    bool OnPlateSolveFailed,
    bool OnDiskSpaceLow);

/// <summary>
/// §37.12 site preferences — location + horizon + observing conditions.
/// `TwilightDefinition` is one of `civil`/`nautical`/`astronomical` on the
/// wire (snake_case lower for enums per §60.6).
/// </summary>
public sealed record SiteSettingsDto(
    string SiteName,
    double LatitudeDeg,
    double LongitudeDeg,
    double ElevationM,
    string TimeZone,
    bool UseCustomHorizon,
    double DefaultHorizonAltitudeDeg,
    int BortleClass,
    double TypicalSeeingArcsec,
    string TwilightDefinition);

/// <summary>
/// §29.2 filenames settings — date-token separator + dark/bias
/// compression toggle. (The main filename template + format live in
/// <see cref="StorageSettingsDto"/>; this section covers what the storage
/// panel doesn't.) `DateSeparator` is `forward_slash`/`underscore`/`dash`
/// on the wire (snake_case for compound names per §60.6).
/// </summary>
public sealed record FilenamesSettingsDto(
    string DateSeparator,
    bool CompressDarksAndBias);

/// <summary>
/// §35 safety policies — unsafe-weather + meridian-flip + altitude-limit
/// + guider-lost reaction config. All enum values are snake_case strings
/// on the wire (e.g. <c>pause_and_park</c>, <c>skip_target</c>).
/// </summary>
public sealed record SafetyPoliciesDto(
    string OnUnsafe,
    bool AutoResumeWhenSafe,
    int ResumeDelayMin,
    bool MeridianFlipAuto,
    int MeridianPauseMin,
    bool MeridianRecenter,
    bool MeridianRecalGuider,
    string OnAltitudeLimit,
    bool ParkIfNoMoreTargets,
    string OnGuiderLost,
    int GuiderRetryTimeoutSec,
    bool SkipTargetIfRecoveryFails);

/// <summary>
/// §37.11 autofocus settings — method + sweep params + filter/runtime
/// policies + abort behavior. `Method` is `hfr_v_curve`/
/// `brightest_star_hfr`/`fwhm` on the wire.
/// </summary>
public sealed record AutofocusSettingsDto(
    string Method,
    int Steps,
    int StepSize,
    int ExposureSeconds,
    int Binning,
    string AfFilter,
    bool RunAfterFilterChange,
    double TriggerTempDeltaC,
    double TriggerHfrDriftPct,
    int EveryNHours,
    bool AbortSequenceOnAfFailure,
    bool RestorePositionOnFailure);

/// <summary>
/// §37.10 plate solving settings — engine + search/timeout knobs +
/// centering policy + convergence loop bounds. `Engine` is `astap`/
/// `astrometry_net`/`platesolve2` on the wire.
/// </summary>
public sealed record PlateSolveSettingsDto(
    string Engine,
    string PathOrEndpoint,
    string IndexDownloadPath,
    double SearchRadiusDeg,
    int DownsampleFactor,
    int TimeoutSeconds,
    bool UseBlindFallback,
    bool CenterAfterSlew,
    bool SyncToCoordinates,
    int MaxIterations,
    double ConvergenceToleranceArcsec);

/// <summary>
/// §51 diagnostics-mode picker. Single-enum section; wrapped in a DTO
/// for symmetry with other profile sections + to leave room for future
/// per-severity threshold knobs without breaking the wire shape.
/// `Mode` is `notify_only`/`pause_on_critical`/`abort_on_critical`.
/// </summary>
public sealed record DiagnosticsModeDto(string Mode);

/// <summary>
/// §63 PHD2 / guider settings — connection (host/port/profile) +
/// dithering knobs + per-session calibration policy. The §35 meridian-
/// flip re-cal-guider toggle lives in <see cref="SafetyPoliciesDto"/>
/// (crosses §35/§63 boundary, belongs with the rest of meridian
/// behavior).
/// </summary>
public sealed record Phd2SettingsDto(
    string Host,
    int Port,
    string Phd2Profile,
    bool DitherEnabled,
    int DitherEveryNFrames,
    double DitherPixels,
    double SettlePixels,
    int SettleTimeSec,
    int SettleTimeoutSec,
    bool ForceCalibrationEachSession,
    // §63.5 guider-engine config — pushed to the guider daemon on connect (guider-e-2). These carry default
    // values so an existing profile.json written before this field set deserializes to the correct PHD2
    // defaults (System.Text.Json uses an optional ctor parameter's default for a missing key) rather than
    // null / 0.0 — otherwise an upgrade would leave aggressiveness at 0 ("never correct") and DecGuideMode null.
    int GuideFocalLength = 0,
    double GuidePixelSize = 0,
    double RaAggressiveness = 0.7,
    double DecAggressiveness = 0.7,
    double MinimumMove = 0.15,
    string DecGuideMode = "auto");

/// <summary>
/// §52.1 connection-lifecycle defaults — which equipment device types
/// auto-connect when the daemon boots. One bool per device type. Wire
/// shape uses snake_case for compound names: `filter_wheel`,
/// `flat_panel`, `safety_monitor`.
/// </summary>
public sealed record EquipmentConnectionDto(
    bool Camera,
    bool Mount,
    bool Focuser,
    bool FilterWheel,
    bool Rotator,
    bool Guider,
    bool FlatPanel,
    bool Dome,
    bool Weather,
    bool SafetyMonitor);

/// <summary>
/// §65.2 stretch defaults per profile. <c>LightDefault</c> is the
/// stretch palette ID applied to Light frames when the request doesn't
/// override; calibration frames (Dark/Bias/Flat) always render `linear`
/// regardless. <c>ManualDefaultParams</c> seeds the §40.5 frame-viewer
/// sliders. <c>AsinhDefaultBeta</c> is the Lupton β when `asinh` is
/// selected without a request-time override. <c>LinearClipPercentilesLow</c>
/// + <c>High</c> are the §65.1 black/white-point clipping percentiles
/// applied by the `linear` algorithm (defaults 0.5% / 99.5%).
/// </summary>
public sealed record StretchDefaultsDto(
    string LightDefault,
    StretchManualDefaultsDto ManualDefaultParams,
    double AsinhDefaultBeta,
    double LinearClipPercentilesLow,
    double LinearClipPercentilesHigh);

public sealed record StretchManualDefaultsDto(
    double Blackpoint,
    double Midpoint,
    double Whitepoint);