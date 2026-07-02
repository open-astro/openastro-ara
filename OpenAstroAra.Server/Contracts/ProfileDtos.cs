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
    bool SkipTargetIfRecoveryFails,
    // §29 — action when the §29 disk-space monitor hits the critical threshold: "warn" (default — the diagnostic
    // + notification only) or "abort" (also halt any running sequence so it doesn't capture into a full disk).
    // Optional ctor default so a profile.json predating this field still deserializes.
    string OnDiskSpaceCritical = "warn",
    // §58.9 unattended flip safety — the pre-flip flight check (endpoint-altitude / mount-health /
    // required-equipment gates) + the in-slew watchdog (stall / timeout / pier-side-unchanged hard
    // fail). Default ON: the layers protect a sleeping user and cost nothing when everything is
    // healthy; a rig whose driver misreports pier side can turn it off. Optional ctor defaults keep
    // an older profile.json deserializing.
    bool FlipSafetyEnabled = true,
    // §58.9 — the expected flip-slew duration (seconds). Alpaca has no slew-duration estimate API,
    // so this profile figure stands in for the spec's "mount estimate": the Layer-2 watchdog's hard
    // timeout is min(3 × this, 5 minutes).
    int ExpectedFlipSlewSeconds = 90);

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
/// §36 imaging-train optics + sensor geometry — the inputs the Planning
/// tab's Frame mode needs to draw the field-of-view box. The framing math
/// is: pixel scale = 206.265 × <c>PixelSizeUm</c> ÷ (<c>FocalLengthMm</c> ×
/// <c>ReducerFactor</c>); FOV = sensor_px × pixel scale.
/// <c>ReducerFactor</c> is 1.0 for none, 0.8 for a 0.8× reducer, 2.0 for a
/// 2× barlow. Sensor dimensions are cached here on first camera connect
/// (PORT_DECISIONS §36/§25.5) so framing works with the camera
/// disconnected/offline; the user can override in Settings. NOTE: the
/// on-first-connect auto-population is a FUTURE slice — this section is the
/// storage + API foundation only.
/// <para>NEXTGEN §4 — <c>ApertureMm</c> (objective diameter) feeds the Optimal-Sub
/// sky-flux term (aperture area); it is telescope-owned like focal length, so camera
/// auto-population never touches it. 0 = unset (optional ctor default keeps a
/// profile.json predating this field deserializing; exposure advice is simply
/// unavailable until it's set).</para>
/// </summary>
public sealed record OpticsSettingsDto(
    double FocalLengthMm,
    double ReducerFactor,
    int SensorWidthPx,
    int SensorHeightPx,
    double PixelSizeUm,
    double ApertureMm = 0);

/// <summary>
/// NEXTGEN §3/§4 — camera electronics for exposure planning. Per-camera, per-mode
/// values: full well differs across cameras sharing a sensor (ASI2600 ≈ 50 ke⁻ vs
/// ToupTek 2600 up to 100 ke⁻ in High Full Well mode — same IMX571), so these are
/// auto-captured from the connected camera where ASCOM exposes them
/// (<c>FullWellCapacity</c>, <c>ElectronsPerADU</c>, <c>Gain</c>, <c>SensorName</c> —
/// all reported for the CURRENT readout mode; reconnecting in HFW mode re-captures
/// the bigger well automatically). Read noise is NOT in standard ASCOM and is always
/// user-entered (manufacturer gain chart / SharpCap sensor analysis), as is the QE
/// peak. 0 / −1 / "" mean unset → planning falls back to the Tier-0 generic-CMOS
/// defaults in <c>OptimalSubCalculator</c> and says so via <c>assumed_defaults</c>.
/// <c>AutoCaptured</c> records provenance of the ASCOM-sourced fields (display-only).
/// </summary>
/// <summary>§37.4/§46.2 — the filter-wheel SLOT LABELS used for offline sequence
/// authoring (the §38 editor's SwitchFilter picker sources its dropdown from these)
/// and as the seed for the planning filter set. Slot-indexed (index 0 = slot 1);
/// an empty string marks an unused slot; labels are trimmed on write. Distinct from
/// BOTH the connected wheel's driver-reported names (authoritative while a wheel is
/// connected — the Settings panel hides this list then) and the NEXTGEN planning
/// <see cref="FilterSetDto"/> (kind + bandwidth, matched by name).</summary>
public sealed record FilterWheelLabelsDto(System.Collections.Generic.IReadOnlyList<string> Labels) {
    /// <summary>The §46.2 reference-wheel default set (8 slots) — mirrors the
    /// client's pre-round-trip in-memory default so first hydration is a no-op
    /// visual change.</summary>
    public static FilterWheelLabelsDto Default { get; } =
        new(["L", "R", "G", "B", "Hα", "OIII", "SII", ""]);
}

public sealed record CameraElectronicsDto(
    string SensorName = "",
    double ReadNoiseE = 0,
    double FullWellE = 0,
    double ElectronsPerAdu = 0,
    int Gain = -1,
    double QuantumEfficiencyPeak = 0,
    bool AutoCaptured = false);

/// <summary>
/// NEXTGEN §1/§4 — one filter in the user's planning filter set. Deliberately separate
/// from the equipment <c>FilterInfo</c> (NINA-shaped, no bandwidth, must round-trip
/// imports untouched); matched to sequences by <c>Name</c> (case-insensitive).
/// <c>BandwidthNm</c> 0 → the kind's default effective bandwidth
/// (<c>OptimalSubCalculator.DefaultBandwidthNm</c>). Dual/tri-band kinds use the
/// per-pixel single-line width (each OSC Bayer channel sees only the line that lands
/// in it) — the conservative choice for the read-noise floor.
/// </summary>
public sealed record PlanningFilterDto(
    string Name,
    FilterKind Kind,
    double BandwidthNm = 0);

/// <summary>NEXTGEN §1/§4 — the user's declared planning filter set (captured at setup,
/// NOT read from the connected wheel at plan time — planning runs offline).</summary>
public sealed record FilterSetDto(IReadOnlyList<PlanningFilterDto> Filters);

/// <summary>Planning filter kind. Broadband: <see cref="L"/>/<see cref="R"/>/<see cref="G"/>/
/// <see cref="B"/> mono, <see cref="Osc"/> (one-shot color / DSLR, no filter). Narrowband:
/// <see cref="Ha"/>/<see cref="Oiii"/>/<see cref="Sii"/> mono line filters. <see cref="Duo"/>/
/// <see cref="Tri"/> = OSC dual/tri-band (L-eXtreme, L-eNhance …). Serialized all-lowercase
/// per §60.6 (<c>l</c>/<c>r</c>/<c>g</c>/<c>b</c>/<c>osc</c>/<c>ha</c>/<c>oiii</c>/<c>sii</c>/
/// <c>duo</c>/<c>tri</c>).</summary>
public enum FilterKind {
    L,
    R,
    G,
    B,
    Osc,
    Ha,
    Oiii,
    Sii,
    Duo,
    Tri,
}

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
    bool SafetyMonitor,
    bool Switch = true);

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