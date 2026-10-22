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
/// REST DTOs for Phase 6 equipment endpoints per PORT_PLAYBOOK.md §10.6.
/// Each device type follows the same pattern:
///   1. <c>{Type}Dto</c> — full snapshot of device state (connected, capabilities, last reading)
///   2. <c>{Type}StateDto</c> — lightweight runtime-state subset for WS broadcast
///   3. Operation request DTOs — input shape for POST endpoints
///
/// All DTOs are records (immutable, value-equality, AOT-friendly per §71).
/// Serializer uses <see cref="System.Text.Json"/> via AraJsonContext (Phase 9
/// will wire the source-generated context for AOT compat).
/// </summary>

/// <summary>
/// Device type per §6.2 / OpenAPI DeviceType enum. Token serialization is
/// all-lowercase concatenated (e.g., <c>filterwheel</c>, <c>covercalibrator</c>);
/// the route handler uses <c>Enum.TryParse&lt;DeviceType&gt;(ignoreCase: true)</c>
/// so the JSON token matches the URL segment and the per-device literal route.
///
/// <c>FlatDevice</c> is the NINA UX-facing concept; under the hood it discovers
/// as Alpaca <c>CoverCalibrator</c> (the only Alpaca device type that combines
/// cover + light source). Both tokens are retained so the WILMA UI can keep
/// NINA's separation while the underlying Alpaca query maps to CoverCalibrator.
/// </summary>
public enum DeviceType {
    Camera, Telescope, Focuser, FilterWheel, Rotator, Dome,
    SafetyMonitor, Switch, ObservingConditions, CoverCalibrator, FlatDevice, Guider
}

public static class DeviceTypeExtensions {
    /// <summary>FlatDevice/CoverCalibrator are one physical device under two tokens (ASCOM type
    /// vs NINA concept) — canonicalize so grouping logic (reconnect, forget, fault episodes)
    /// treats them as a single group. The one shared definition (PR #856 review): three services
    /// each carried a private copy, and an edit to one but not the others would silently
    /// reintroduce the mismatch this collapse exists to prevent.</summary>
    public static DeviceType Canonical(this DeviceType t) =>
        t == DeviceType.FlatDevice ? DeviceType.CoverCalibrator : t;
}

/// <summary>Connection state machine state. Drives <c>equipment.{type}.state</c> WS events.</summary>
public enum EquipmentConnectionState { Disconnected, Connecting, Connected, Error }

/// <summary>Per §6.2 — descriptor returned by <c>GET /api/v1/equipment/{type}</c>.</summary>
public sealed record DiscoveredDeviceDto(
    string UniqueId,
    string Name,
    DeviceType Type,
    string HostName,
    string IpAddress,
    int IpPort,
    int AlpacaDeviceNumber,
    bool UseHttps);

/// <summary>Connect-request body. Idempotency-Key header per §60.5.</summary>
public sealed record ConnectRequestDto(DiscoveredDeviceDto Device);

// OperationAcceptedDto lives in SharedDtos.cs (added in Phase 7 — same record
// shape reused for every long-running operation across Phases 6-9). Phase 6's
// original stub OperationAcceptedDto(string) was superseded.

// ─── Camera (§10.6 row 1) ─────────────────────────────────────────────────────

public sealed record CameraDto(
    string DeviceId,
    string Name,
    EquipmentConnectionState State,
    CameraCapabilitiesDto? Capabilities,
    CameraStateDto Runtime);

public sealed record CameraCapabilitiesDto(
    int SensorWidth, int SensorHeight,
    double PixelSizeUm,
    bool CanSetTemperature, bool CanAbortExposure, bool CanGetCoolerPower,
    int MinGain, int MaxGain,
    int MinOffset, int MaxOffset,
    int MinBinX, int MaxBinX, int MinBinY, int MaxBinY,
    double MinExposureSec, double MaxExposureSec,
    // Bayer color-filter pattern at the image origin (RGGB/BGGR/GRBG/GBRG) for an OSC sensor;
    // null for a monochrome sensor. Drives the §65 debayered color preview + the FITS BAYERPAT header.
    string? BayerPattern = null,
    // NEXTGEN §3/§4 exposure-planning electronics, reported by ASCOM for the CURRENT readout
    // mode (an HFW-mode reconnect re-reads the bigger well). 0 / null / -1 = the driver didn't
    // expose it. Feed the profile's camera-electronics auto-capture; optional ctor defaults keep
    // every existing constructor call compiling unchanged.
    double FullWellCapacityE = 0,
    double ElectronsPerAdu = 0,
    string? SensorName = null,
    int CurrentGain = -1,
    // §25.5.5 — whether the camera has a cooler at all (the ASCOM CoolerOn property is
    // implemented), independent of CanSetTemperature: a "dumb" on/off cooler with no TEC
    // set-point reports HasCooler=true + CanSetTemperature=false. Drives the client's
    // cooler-Switch gating (the set-point field stays gated on CanSetTemperature).
    bool HasCooler = false,
    // §25.5.5 — the driver's readout-mode display names, in driver index order (POST
    // /camera/readoutmode selects by index into THIS list). Null/empty = no readout-mode support.
    System.Collections.Generic.IReadOnlyList<string>? ReadoutModes = null,
    // §25.5.5 — Y pixel pitch for asymmetric-pixel sensors; 0 = not reported (assume square,
    // use PixelSizeUm).
    double PixelSizeUmY = 0,
    // #1185 (§25.5.5 DTO gaps) — the ASCOM SensorType name ("Monochrome", "Color", "RGGB", "CMYG",
    // "CMYG2", "LRGB"); null when the driver does not report it. BayerPattern above stays the field
    // the debayer path reads; this one tells a client a "Color" sensor (driver-debayered) from an
    // RGGB mosaic and from a mono chip.
    string? SensorType = null,
    // #1185 — whether the camera has a mechanical shutter (ASCOM HasShutter): a DSLR or a shuttered
    // CCD can take darks with the cap off; a shutterless CMOS cannot.
    bool HasShutter = false,
    // #1185 — the driver's discrete gain and offset NAME lists (ASCOM Gains / Offsets, for cameras
    // whose gain is a named setting such as "ISO 800" rather than a 0..N range; the index selects).
    // Null when the camera uses the Min..Max range instead.
    System.Collections.Generic.IReadOnlyList<string>? Gains = null,
    System.Collections.Generic.IReadOnlyList<string>? Offsets = null,
    // #1185 — the smallest exposure step the driver honours (ASCOM ExposureResolution, seconds);
    // 0 = not reported.
    double ExposureResolutionSec = 0,
    // ASCOM MaxADU — the brightest value a pixel can read (the full scale panel flats aim at a
    // share of); 0 = not reported.
    int MaxAdu = 0);

public sealed record CameraStateDto(
    string State,   // "idle" | "exposing" | "downloading" | "error"
    double? CcdTemperature,
    double? CoolerPowerPct,
    bool CoolerOn,
    double? ExposureProgressPct,
    // §25.5.5 — the target the TEC is cooling TO (ASCOM SetCCDTemperature read-back), so the
    // panel can show "cooling to −10°C" alongside the actual sensor temperature. Null when the
    // driver doesn't report one (or no cooler). Optional defaults keep pre-slice shapes valid.
    double? CoolerSetpointC = null,
    // §25.5.5 — the CURRENT readout mode's display name (from the driver's ReadoutModes list);
    // null when the camera has no readout-mode support.
    string? ReadoutMode = null,
    // #1076 — false when the CoolerOn property read threw this pass: CoolerOn then reads false by
    // fallback, and the cooling-fan interlock must treat the state as UNKNOWN (refuse a fan-off),
    // not as "off".
    bool CoolerStateKnown = true,
    // #1185 (§25.5.5) — the cooler's hot side (ASCOM HeatSinkTemperature); null when the driver
    // does not report it. A heat sink that climbs while CCD temperature holds is a fan or airflow
    // problem the cooler-power figure alone does not show.
    double? HeatSinkTemperatureC = null);

// §64 Live View: a server-driven short-exposure loop that renders the latest
// frame to JPEG (no FITS write, not cataloged) for framing/focus. Start/stop +
// status; the frame bytes come from GET /camera/liveview/frame. ExposureSec is
// short (sub-second to a few seconds); BinX/BinY default to 2 for a faster,
// lower-noise framing loop.
public sealed record LiveViewStartRequestDto(double ExposureSec, int? Gain = null, int BinX = 2, int BinY = 2, bool Annotate = false);

// All nullable fields are null until the session has the relevant value, not optional config:
// StartedAtUtc is null before the first start AND after each stop (meta clears on stop) — use
// SessionId > 0 to tell "stopped" from "never started"; Width/Height/LastFrameAtUtc are null until
// the first frame of the current session has been rendered.
// During an in-flight StopLiveViewAsync there is a brief window where Active=false but FrameSeq>0
// (the frame is dropped only after the loop drains); treat FrameSeq as advisory, gate "live" on
// Active. Once stop returns, FrameSeq is 0 and GET /liveview/frame is 204.
public sealed record LiveViewStatusDto(
    bool Active,
    // Bumped each time Live View starts. FrameSeq restarts at 1 per session, so a client doing
    // X-Frame-Seq change-detection should treat a changed SessionId as "new session" (FrameSeq
    // going backward is a restart, not a regression). 0 before Live View has ever started.
    long SessionId,
    long FrameSeq,
    int? Width,
    int? Height,
    double? ExposureSec,
    string? StartedAtUtc,
    string? LastFrameAtUtc);

// Cooler control: turn the cooler on/off and optionally set the target CCD
// temperature. CoolerOn and the set-point are independent ASCOM properties, so a
// null TargetTemperatureC means "leave the set-point unchanged" — the service
// only writes SetCCDTemperature when a target is supplied (a pure on/off toggle
// sends null). TargetTemperatureC is likewise ignored when Enabled is false.
public sealed record CameraCoolerRequestDto(bool Enabled, double? TargetTemperatureC = null);

/// <summary>POST /camera/readoutmode — select a readout mode by index into
/// <see cref="CameraCapabilitiesDto.ReadoutModes"/> (driver order).</summary>
public sealed record CameraReadoutModeRequestDto(int ModeIndex);

public sealed record ExposureRequestDto(
    double ExposureSec,
    int? Gain,
    int BinX = 1, int BinY = 1,
    int? OffsetX = null, int? OffsetY = null,
    int? Width = null, int? Height = null,
    string? FilterName = null,
    // Electronic (pedestal) offset — distinct from the OffsetX/Y subframe position.
    int? CameraOffset = null,
    // #1177 — the frame type for a manual capture: LIGHT (default), DARK, BIAS, FLAT or DARKFLAT,
    // case-insensitive. Drives the FITS IMAGETYP, the catalog frame type and the shutter: DARK and
    // BIAS are taken with the shutter closed (Alpaca StartExposure light=false). Anything else is 422.
    string? ImageType = null);

public sealed record ExposureResponseDto(
    string FrameId,
    Uri PreviewUrl,
    double ExposureSec,
    string CapturedAt);

// ─── Telescope (§10.6 row 2) ──────────────────────────────────────────────────

public sealed record TelescopeDto(
    string DeviceId,
    string Name,
    EquipmentConnectionState State,
    TelescopeCapabilitiesDto? Capabilities,
    TelescopeStateDto Runtime);

public sealed record TelescopeCapabilitiesDto(
    bool CanSlew, bool CanSync, bool CanPark, bool CanUnpark,
    bool CanSetTracking, bool CanPulseGuide,
    bool CanFindHome,
    IReadOnlyList<string> SupportedSiderealRates,
    // Optics, in mm (ASCOM Telescope.FocalLength / ApertureDiameter report
    // metres; converted at the read). Null when the driver doesn't report it —
    // many mounts NotImplement these. Surfaced so the §37 wizard can auto-fill
    // the optics screen from a connected mount.
    double? FocalLengthMm = null,
    double? ApertureDiameterMm = null,
    // Manual control: whether the mount supports MoveAxis on BOTH pad axes, and the
    // rates the direction pad may ask for (deg/sec). `MoveAxisRateBandsDegPerSec`
    // (#1126) is the authoritative shape: the primary axis's AxisRates [min, max]
    // bands, ascending, each intersected with the secondary axis's bands (#1230) so
    // one picked rate is honoured on both axes; a discrete rate is a band with
    // min == max, a "any speed up to max" band has min 0. `MoveAxisRatesDegPerSec`
    // is the legacy flattening of the same bands to their positive endpoints
    // (ascending, deduped) kept for clients that predate the bands. Both empty
    // when the mount reports no axis rates.
    bool CanMoveAxis = false,
    IReadOnlyList<double>? MoveAxisRatesDegPerSec = null,
    IReadOnlyList<MoveAxisRateBandDto>? MoveAxisRateBandsDegPerSec = null,
    // #1264 stage 1 — the mount's spec sheet from AlpacaBridge #815 (`openastro:specs`
    // action), read once per connect. Null when the driver ships no sheet: the client
    // shows "No spec sheet for this mount" and the tracking probe starts from its ladder.
    MountSpecSheetDto? MountSpecs = null);

/// <summary>#1264 — the bridge's per-driver mount spec sheet (schema <c>openastro.mount-specs/1</c>).
/// <c>WormPeriodS</c> and <c>PeriodicErrorPpArcsec</c> are the two the unguided prior needs; null means
/// unknown, never a guess. <c>Source</c> is <c>manufacturer</c> | <c>measured</c> | <c>community</c>;
/// <c>MountType</c> is <c>worm_eq</c> | <c>strain_wave</c> | <c>direct_drive</c> | <c>alt_az</c> | <c>other</c>.</summary>
public sealed record MountSpecSheetDto(
    string? DriverId,
    string? Model,
    string? Source,
    double? WormPeriodS,
    double? PeriodicErrorPpArcsec,
    bool HasEncoders,
    bool HasPec,
    string? MountType,
    double? MaxPayloadKg,
    string? Notes);

/// <summary>One MoveAxis rate band (deg/sec): the mount honours any magnitude in
/// <c>[Min, Max]</c>; <c>Min == Max</c> is a single discrete rate (#1126).</summary>
public sealed record MoveAxisRateBandDto(double Min, double Max);

public sealed record TelescopeStateDto(
    string State,    // "idle" | "slewing" | "tracking" | "parked" | "unparking" | "error"
    double? RightAscensionHours,
    double? DeclinationDegrees,
    bool Tracking,
    bool Parked,
    bool AtHome,
    // §57.9 — the destination of the last equatorial slew/sync, read back from the mount's
    // own TargetRA/TargetDec bookkeeping (null when the mount can't report it or none set yet).
    double? TargetRightAscensionHours = null,
    double? TargetDeclinationDegrees = null);

/// <param name="Force">A slew while a sequence run is in progress stops guiding and pauses the run,
/// so it is refused (409) unless the caller says it knows: the client asks, then repeats with
/// <c>force: true</c>. A sync never moves the mount and is never refused.</param>
public sealed record SlewRequestDto(
    double RightAscensionHours,
    double DeclinationDegrees,
    bool? Sync = false,
    bool? Force = false);

public sealed record ParkRequestDto(string? Reason = null);

// Manual axis move: Axis 0 = primary (RA/Az), 1 = secondary (Dec/Alt). Rate is deg/sec
// (signed: positive/negative for the two directions); Rate 0 stops that axis.
public sealed record MoveAxisRequestDto(int Axis, double Rate);

// Tracking control: start/stop sidereal tracking (the set-point rate is the
// device's current TrackingRate; this only toggles Tracking on/off).
public sealed record TelescopeTrackingRequestDto(bool Enabled);

// ─── Focuser (§10.6 row 3) ────────────────────────────────────────────────────

public sealed record FocuserDto(
    string DeviceId,
    string Name,
    EquipmentConnectionState State,
    FocuserCapabilitiesDto? Capabilities,
    FocuserStateDto Runtime);

public sealed record FocuserCapabilitiesDto(
    int MinPosition, int MaxPosition,
    double StepSizeUm,
    bool CanTempComp, bool AbsoluteFocuser);

public sealed record FocuserStateDto(
    string State,    // "idle" | "moving" | "settled" | "backlash_compensating" | "error"
    int? Position,
    double? Temperature,
    bool TempCompEnabled);

public sealed record FocuserMoveRequestDto(int TargetPosition, bool? UseTempComp = false);

// ─── FilterWheel (§10.6 row 4) ────────────────────────────────────────────────

/// <summary><c>Manual</c> (#1298): the wheel is the driverless manual filter wheel — filters are
/// swapped by hand, its slots are the profile's filter labels, and a change waits for the user to
/// report the filter installed (<c>POST /equipment/filterwheel/installed</c>).</summary>
public sealed record FilterWheelDto(
    string DeviceId,
    string Name,
    EquipmentConnectionState State,
    FilterWheelStateDto Runtime,
    IReadOnlyList<FilterSlotDto> Slots,
    bool Manual = false);

public sealed record FilterSlotDto(int Position, string Name, int FocusOffset);

/// <summary><c>State</c> is <c>idle</c> | <c>moving</c> (Alpaca, slot -1 while rotating) |
/// <c>awaiting_user</c> (#1298 manual wheel: a change was requested and the user has not yet
/// reported <c>PendingSlot</c> installed). <c>CurrentSlot</c> is null when unknown.</summary>
public sealed record FilterWheelStateDto(string State, int? CurrentSlot, int? PendingSlot = null);

public sealed record FilterChangeRequestDto(int Position);

/// <summary>#1298 — the user reports which filter is now in the manual filter wheel. Completes a
/// pending change to that position; any other position just records what is installed.</summary>
public sealed record FilterInstalledRequestDto(int Position);

// ─── Rotator (§10.6 row 5) ────────────────────────────────────────────────────

public sealed record RotatorDto(
    string DeviceId,
    string Name,
    EquipmentConnectionState State,
    RotatorCapabilitiesDto? Capabilities,
    RotatorStateDto Runtime);

public sealed record RotatorCapabilitiesDto(bool CanReverse, double StepSize);

public sealed record RotatorStateDto(
    string State, double? MechanicalAngleDeg, double? SkyAngleDeg, bool Reverse);

public sealed record RotatorMoveRequestDto(double TargetAngleDeg, bool UseSkyAngle = false);

public sealed record RotatorReverseRequestDto(bool Reverse);

public sealed record RotatorSyncRequestDto(double SkyAngleDeg);

// ─── Dome (§10.6 row 6) ───────────────────────────────────────────────────────

public sealed record DomeDto(
    string DeviceId,
    string Name,
    EquipmentConnectionState State,
    DomeCapabilitiesDto? Capabilities,
    DomeStateDto Runtime);

public sealed record DomeCapabilitiesDto(
    bool CanSetShutter, bool CanSetAzimuth, bool CanSyncAzimuth, bool CanPark, bool CanFindHome,
    // §25.5.5 — optional ctor default so pre-slice serialized shapes still deserialize.
    bool CanSetPark = false);

public sealed record DomeStateDto(string State, double? AzimuthDeg, bool ShutterOpen, bool AtHome, bool Parked);

public sealed record DomeSlewRequestDto(double TargetAzimuthDeg);

// ─── Switch (§10.6 row 7) ─────────────────────────────────────────────────────

public sealed record SwitchDto(
    string DeviceId,
    int AlpacaDeviceNumber,
    string Name,
    EquipmentConnectionState State,
    IReadOnlyList<SwitchPortDto> Ports);

public sealed record SwitchPortDto(int Id, string Name, double Value, double Min, double Max, bool CanWrite);

public sealed record SwitchValueRequestDto(int PortId, double Value);

// ─── ObservingConditions (§10.6 row 8) ────────────────────────────────────────

public sealed record ObservingConditionsDto(
    string DeviceId,
    string Name,
    EquipmentConnectionState State,
    double? TemperatureC,
    double? HumidityPct,
    double? DewPointC,
    double? PressureHpa,
    double? CloudCoverPct,
    double? WindSpeedMs,
    double? WindGustMs,
    double? WindDirectionDeg,
    double? RainRate,
    bool Safe,
    string CapturedAt,
    // Sky quality (SQM) in mag/arcsec² — optional ctor default so pre-slice
    // serialized shapes still deserialize (§25.5.5 pattern).
    double? SkyQualityMagArcsec2 = null,
    // SQM sensor temperature (°C) — Alpaca SkyTemperature.
    double? SkyTemperatureC = null);

// ─── SafetyMonitor (§10.6 row 9) ──────────────────────────────────────────────

public sealed record SafetyMonitorDto(
    string DeviceId,
    string Name,
    EquipmentConnectionState State,
    bool Safe,
    string LastTransitionAt);

// ─── FlatDevice (§10.6 row 10) ────────────────────────────────────────────────

public sealed record FlatDeviceDto(
    string DeviceId,
    string Name,
    EquipmentConnectionState State,
    FlatDeviceStateDto Runtime);

public sealed record FlatDeviceStateDto(
    string State,    // "cover_open" | "cover_moving" | "cover_closed" | "light_on" | "error"
    bool CoverOpen,
    bool LightOn,
    int Brightness,
    /// <summary>Device's <c>MaxBrightness</c>, or 0 until it has been read (or when the
    /// device has no calibrator). The client's brightness control scales to this.</summary>
    int MaxBrightness = 0,
    /// <summary>False when the device reports <c>CoverStatus.NotPresent</c> — a bare light
    /// panel with no motorised cover, so the client hides the open/close controls.</summary>
    bool HasCover = true,
    /// <summary>False when the device reports <c>CalibratorStatus.NotPresent</c> — a plain
    /// dust cover with no light, so the client hides the light/brightness controls.</summary>
    bool HasCalibrator = true,
    /// <summary><c>CalibratorStatus.NotReady</c> — the light is warming up or changing level
    /// (EL panels take a moment to reach the commanded brightness). Not yet on, but on its
    /// way: the client waits rather than reporting the command as failed.</summary>
    bool LightWarming = false);

public sealed record FlatPanelRequestDto(
    bool? OpenCover = null,
    bool? LightOn = null,
    int? Brightness = null);

// ─── Guider (PHD2, §10.6 row 11) ──────────────────────────────────────────────

public sealed record GuiderDto(
    string DeviceId,
    string Name,
    EquipmentConnectionState State,
    GuiderStateDto Runtime);

/// <summary>Guider runtime snapshot. <c>RmsTotal/Ra/Dec</c> are raw PIXELS over the recent
/// guide-step window; the <c>*Arcsec</c> twins are the same figures scaled by the guider's
/// reported pixel scale (the unit guiding accuracy is conventionally quoted in) — null whenever
/// the pixel RMS is null or the guider hasn't reported a positive pixel scale yet (§63 guider-a
/// follow-up: "also expose arcsec via PixelScale").</summary>
public sealed record GuiderStateDto(
    string State,    // "stopped" | "looping" | "star_selected" | "calibrating" | "guiding" | "settling" | "paused" | "star_lost" | "assistant" (#1364; "dithering" is never sent; "assistant" = the Guiding Assistant is measuring)
    double? RmsTotal,
    double? RmsRa,
    double? RmsDec,
    string? CurrentProfile,
    double? RmsTotalArcsec = null,
    double? RmsRaArcsec = null,
    double? RmsDecArcsec = null,
    PaResidualDto? PaResidual = null,
    /// <summary>While <c>State</c> is <c>calibrating</c>: the leg under way (<c>West</c>, <c>East</c>,
    /// <c>North</c>, <c>South</c> or a backlash-clearing leg), its step count and the star's distance from
    /// the leg's start in guide-camera pixels. Null otherwise.</summary>
    string? CalibrationDirection = null,
    int? CalibrationStep = null,
    double? CalibrationDistancePx = null);

/// <summary>
/// #1364 — the live guide-camera view (<c>GET /equipment/guider/live</c>): what the guider is doing
/// (<c>AppState</c>: Stopped | Looping | Selected | Calibrating | Guiding | LostLock | Paused), the frame
/// size the picture at <c>GET …/live/frame</c> covers, the locked star and the search box around it, the
/// star's SNR and mass (the latest guide frame's while <c>AppState</c> is Guiding; the chosen star's detected
/// centroid under the lock position while it is Selected; null otherwise), whether multi-star tracking is on, and the other stars the
/// guider found (<c>Stars</c>, brightest first; empty when it has no image). Coordinates are guide-camera
/// pixels. Fields the guider could not answer are null, never an error — the picture still shows.
/// </summary>
public sealed record GuiderLiveDto(
    bool Connected,
    string? AppState,
    int? FrameWidth,
    int? FrameHeight,
    double? LockX,
    double? LockY,
    int? SearchRegion,
    double? Snr,
    double? StarMass,
    bool? MultiStarEnabled,
    IReadOnlyList<GuiderLiveStarDto> Stars,
    bool HasFrame);

/// <summary>One star the guider sees, in guide-camera pixels.</summary>
public sealed record GuiderLiveStarDto(double X, double Y, double? Snr, double? Mass, double? Hfd);

/// <summary>
/// #1364 — the guider's calibration (<c>GET /equipment/guider/live/calibration</c>): the run this
/// session (<c>RunState</c>: <c>none</c> | <c>running</c> | <c>complete</c> | <c>failed</c>, its steps —
/// the plot PHD2 draws — and the failure reason), the stored result (<c>Data</c>, null when the guider
/// has none), and Ara's verdict on it: <c>good</c>, <c>check</c> (usable, but the axes are far from
/// square or the rates far apart — <c>VerdictReason</c> says which) or <c>redo</c> (failed or absent).
/// </summary>
public sealed record GuiderCalibrationViewDto(
    bool Connected,
    string RunState,
    IReadOnlyList<GuiderCalibrationStepDto> Steps,
    string? Error,
    GuiderCalibrationDataDto? Data,
    string Verdict,
    string? VerdictReason);

/// <summary>#1364 item 5 — the guider's own graph history (<c>GET /equipment/guider/history</c>), so a
/// client that connects mid-session draws the last minutes instead of an empty graph. Steps are oldest
/// first in the <c>guider.step</c> shape, each with its age in seconds measured on the daemon's clock (the
/// guider's own when co-hosted, as on the SBC), so the client can place it on its own time axis without trusting the two clocks
/// agree. Empty when the guider has no history or is not connected.</summary>
public sealed record GuiderHistoryDto(bool Connected, IReadOnlyList<GuiderHistoryStepDto> Steps);

/// <summary>One historical guide step: the <c>guider.step</c> fields plus <c>AgeSec</c>. Pulse durations
/// follow <c>guider.step</c>'s sign: negative = East (RA) / South (Dec).</summary>
public sealed record GuiderHistoryStepDto(
    double AgeSec,
    double? RaRawPx,
    double? DecRawPx,
    double? RaArcsec,
    double? DecArcsec,
    double RaDurationMs,
    double DecDurationMs,
    double? PixelScaleArcsec,
    double? Snr,
    double? StarMass);

/// <summary>#1364 — what the guider has now (<c>GET /equipment/guider/advanced</c>), beside the profile's
/// guider-advanced block: its current values (fields it did not answer are null) and the guide algorithms
/// each axis offers. <c>Connected</c> false with nulls and empty lists when no guider is connected.</summary>
public sealed record GuiderAdvancedViewDto(
    bool Connected,
    GuiderAdvancedSettingsDto Current,
    IReadOnlyList<string> RaAlgorithms,
    IReadOnlyList<string> DecAlgorithms);

/// <summary>One calibration step: the leg (<c>West</c>, <c>East</c>, <c>North</c>, <c>South</c> or a
/// backlash-clearing leg), its number and the star's displacement from the leg's start, guide px.</summary>
public sealed record GuiderCalibrationStepDto(string Direction, int Step, double Dx, double Dy, double Dist);

/// <summary>The stored calibration: axis angles on the camera (degrees), guide rates (px/s), parities,
/// the declination it was taken at, and the orthogonality error (how far the RA and Dec axes are from
/// 90° apart).</summary>
public sealed record GuiderCalibrationDataDto(
    bool Calibrated,
    double? XAngleDeg,
    double? YAngleDeg,
    double? XRatePxPerSec,
    double? YRatePxPerSec,
    string? XParity,
    string? YParity,
    double? DeclinationDeg,
    double? OrthogonalityErrorDeg);

/// <summary>A tap on the live picture: lock on the star nearest (<c>X</c>, <c>Y</c>) guide pixels.</summary>
public sealed record GuiderLiveSelectRequestDto(double X, double Y);

/// <summary>
/// #1364 item 4 — the guider's Guiding Assistant (<c>GET /equipment/guider/live/assistant</c>): the
/// unguided-drift measurement PHD2's dialog runs, headless (openastro-guider #83). <c>State</c> is
/// <c>idle</c> | <c>measuring</c> | <c>measuring_backlash</c> | <c>done</c> | <c>failed</c>; <c>Connected</c>
/// false (state <c>idle</c>) when no guider is connected or it does not answer. The statistics are present
/// once two samples are in; <c>Recommendations</c> once the run is done.
/// </summary>
public sealed record GuidingAssistantStatusDto(
    bool Connected,
    string State,
    double? DurationSec,
    bool? MeasureBacklash,
    double? ElapsedSec,
    int? Samples,
    double? PixelScale,
    double? RaDriftPxMin,
    double? DecDriftPxMin,
    double? RaRmsPx,
    double? DecRmsPx,
    double? HighFreqRmsPx,
    double? RaPeakPx,
    double? DecPeakPx,
    double? RaPeakPeakPx,
    double? RaMaxDriftPxSec,
    double? Snr,
    double? StarMass,
    double? PolarAlignErrorArcmin,
    bool? PolarAlignErrorIsLowerBound,
    string? BacklashStatus,
    string? BacklashResult,
    double? BacklashMs,
    double? BacklashSigmaMs,
    double? BacklashPx,
    GuidingAssistantRecommendationsDto? Recommendations,
    string? Error);

/// <summary>What the assistant suggests: min-move per axis (guide px), the exposure range, the Dec
/// backlash compensation and, when the backlash is too big to compensate, a Dec guide mode or
/// algorithm. <c>Messages</c> are PHD2's own explanations, one id + text each. The names accepted by
/// <c>POST …/assistant/apply</c> are <c>exposure</c>, <c>min_move_ra</c>, <c>min_move_dec</c>,
/// <c>dec_backlash</c>, <c>dec_guide_mode</c>, <c>dec_algorithm</c>.</summary>
public sealed record GuidingAssistantRecommendationsDto(
    double? MinMoveRaPx,
    double? MinMoveDecPx,
    bool? MinMoveSeeingBased,
    double? ExposureSec,
    double? ExposureMaxSec,
    double? DriftLimitingExposureSec,
    double? DecBacklashMs,
    string? DecGuideMode,
    string? DecAlgorithm,
    IReadOnlyList<GuidingAssistantMessageDto> Messages);

public sealed record GuidingAssistantMessageDto(string Id, string Text);

/// <summary><c>POST /equipment/guider/live/assistant/start</c>: how long to sample (10–3600 s; null =
/// the guider's two-minute default) and whether to measure Dec backlash afterwards.</summary>
public sealed record GuidingAssistantStartRequestDto(double? DurationSec, bool MeasureBacklash);

/// <summary><c>POST /equipment/guider/live/assistant/apply</c>: the recommendation names to write.</summary>
public sealed record GuidingAssistantApplyRequestDto(IReadOnlyList<string> Which);

public sealed record GuidingAssistantApplyResultDto(IReadOnlyList<string> Applied);

/// <summary>
/// #1311 — the polar alignment error left after Align, measured from the Dec drift the guider
/// corrects during the first minutes of each guided run (no extra setup step). Status is
/// <c>measuring</c> (progress in <c>SampleSeconds</c> of <c>TargetSeconds</c>), <c>done</c> or
/// <c>unavailable</c> (<c>Reason</c> says why: <c>lock_shift</c>, <c>ao</c>, <c>no_calibration</c>,
/// <c>no_pixel_scale</c>, <c>no_fit</c>). <c>PaErrorMinArcmin</c> is a LOWER BOUND: Dec drift at one
/// hour angle sees one component of the error. <c>Reliable</c> is false when the 1σ
/// <c>UncertaintyArcmin</c> exceeds max(1′, half the value). <c>AlignErrorArcmin</c> is the last
/// Polar Align result in the 12 h before, for comparison. Hour angle / Dec are the mount's at the end.
/// </summary>
public sealed record PaResidualDto(
    Guid Id,
    string Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    double SampleSeconds,
    double TargetSeconds,
    int Frames,
    double? DriftArcsecPerMin = null,
    double? PaErrorMinArcmin = null,
    double? UncertaintyArcmin = null,
    bool? Reliable = null,
    double? HourAngleHours = null,
    double? DecDeg = null,
    double? AlignErrorArcmin = null,
    DateTimeOffset? AlignEndedUtc = null,
    Guid? SessionId = null,
    string? Reason = null);

/// <summary>Null host/port mean "use the ACTIVE PROFILE's phd2 host/port" — an
/// omitted field must not silently repoint a remote-PHD2 profile (e.g. an SBC at
/// :8080) back at localhost:4400 (the old non-null defaults did exactly that).</summary>
public sealed record GuiderConnectRequestDto(string? Host = null, int? Port = null);

// ─── Guider dark library / calibration files (§63.6 guider-e-4b) ──────────────

/// <summary>Request to build a dark-frame library for the active guider profile. All fields optional with
/// daemon-aligned defaults; bounds are validated server-side before the build is dispatched (frame_count 1..50,
/// exposure bounds 1..600000 ms, min ≤ max).</summary>
public sealed record BuildDarkLibraryRequestDto(
    int FrameCount = 5,
    int? MinExposureMs = null,
    int? MaxExposureMs = null,
    bool ClearExisting = false,
    string? Notes = null,
    bool LoadAfter = true);

/// <summary>Request to build a defect (bad-pixel) map for the active guider profile. All fields optional with
/// daemon-aligned defaults; bounds are validated server-side before the build is dispatched (frame_count 1..50,
/// exposure 1..600000 ms).</summary>
public sealed record BuildDefectMapDarksRequestDto(
    int ExposureMs = 3000,
    int FrameCount = 10,
    string? Notes = null,
    bool LoadAfter = true);

/// <summary>Toggle a calibration artifact (dark library or defect map) on/off. Enabling needs a connected
/// camera (the daemon loads the artifact).</summary>
public sealed record SetCalibrationEnabledRequestDto(bool Enabled);

/// <summary>The status read's envelope: <c>Connected</c> distinguishes "guider not connected" (Status null)
/// from "connected, here's the status" — so a client polling to drive the "Build dark library" affordance never
/// has to read a 404 as a missing route. Always returned with 200.</summary>
public sealed record CalibrationFilesStatusResponseDto(
    bool Connected,
    CalibrationFilesStatusDto? Status);

/// <summary>The guider's calibration-files status: which dark-library / defect-map files exist for the active
/// profile, whether they're loaded/compatible, the auto-load flags, and (when a camera is connected and darks
/// are loaded) the loaded-dark count + exposure range.</summary>
public sealed record CalibrationFilesStatusDto(
    int ProfileId,
    string? DarkLibraryPath,
    string? DefectMapPath,
    bool DarkLibraryExists,
    bool DefectMapExists,
    bool DarkLibraryCompatible,
    bool DefectMapCompatible,
    bool DarkLibraryLoaded,
    bool DefectMapLoaded,
    bool AutoLoadDarks,
    bool AutoLoadDefectMap,
    int? DarkCountLoaded,
    double? DarkMinExposureSecondsLoaded,
    double? DarkMaxExposureSecondsLoaded);

// ─── Guider equipment choices / Alpaca discovery (§63.17 PR 1) ────────────────

/// <summary>The choices read's envelope: <c>Connected</c> distinguishes "guider not connected"
/// (Choices null) from "connected, here are the device lists" — same 200-always contract as
/// <see cref="CalibrationFilesStatusResponseDto"/> so a client driving the equipment pickers never has to
/// read a 404 as a missing route.</summary>
public sealed record GuiderEquipmentChoicesResponseDto(
    bool Connected,
    GuiderEquipmentChoicesDto? Choices);

/// <summary>§63.20 — the Alpaca-camera pixel-size read's envelope, same 200-always contract as the choices
/// read: <c>Connected</c> false when no guider is connected; <c>PixelSize</c> (µm) null when the daemon
/// couldn't reach the camera or the driver reports no usable size (a best-effort picker assist, never an
/// error status).</summary>
public sealed record GuiderCameraPixelSizeResponseDto(
    bool Connected,
    double? PixelSize);

/// <summary>§63.20 / #1067 — real device names read off one Alpaca server's management API by the
/// daemon, keyed <c>"&lt;devicetype&gt;/&lt;devicenumber&gt;"</c> (type lowercased, e.g. <c>"camera/1"</c>).
/// Empty when the host could not be reached or answered nothing usable — a best-effort labelling
/// assist, never an error status.</summary>
public sealed record AlpacaDeviceNamesResponseDto(
    IReadOnlyDictionary<string, string> Names,
    // #1308 — "type/number" → the server's UniqueID, so a picker can pin the guide camera by identity;
    // entries the server reports no UniqueID for are left out. Additive; older clients ignore it.
    IReadOnlyDictionary<string, string>? UniqueIds = null);

/// <summary>Per-slot device-name lists as the guider daemon offers them (its own equipment-dialog strings).
/// Values are passed back verbatim to the §63.17 apply path; empty lists mean the daemon offers nothing for
/// that slot (e.g. no rotator support compiled in).</summary>
public sealed record GuiderEquipmentChoicesDto(
    IReadOnlyList<string> Cameras,
    IReadOnlyList<string> Mounts,
    IReadOnlyList<string> AuxMounts,
    IReadOnlyList<string> AdaptiveOptics,
    IReadOnlyList<string> Rotators);

/// <summary>Daemon-side Alpaca discovery sweep. Null fields use the daemon defaults (2 queries × 2 s);
/// bounds are validated server-side (queries 1..20, timeout 1..30 s, combined sweep ≤ 60 s) before the
/// sweep is dispatched. The request blocks for roughly <c>queries × timeout</c> seconds — the combined cap
/// keeps this synchronous endpoint well inside common client HTTP timeouts.</summary>
public sealed record DiscoverAlpacaServersRequestDto(
    int? NumQueries = null,
    int? TimeoutSeconds = null);

/// <summary>Result of a daemon-side Alpaca discovery sweep: <c>"host:port"</c> strings as the guider saw
/// them on its network.</summary>
public sealed record GuiderAlpacaDiscoveryDto(IReadOnlyList<string> Servers);

// ─── Polar Alignment (§10.6 row 12) ───────────────────────────────────────────

public sealed record PolarAlignStateDto(
    // §45 engine states: seeding (2-point axis measurement incl. the RA slew), adjusting (live
    // knob-turning loop, error fields populated), paused (5 consecutive failed solves — still
    // retrying), failed (fatal routine error; see the polar_align.error WS event).
    string State,    // "idle" | "seeding" | "adjusting" | "paused" | "stopped" | "failed"
    double? CurrentErrorArcmin,
    double? AzimuthAdjustmentArcmin,
    double? AltitudeAdjustmentArcmin,
    int FramesCaptured,
    string? LastFrameId,
    // Single-frame mode: the adjust loop is parked until POST /polaralign/capture.
    bool WaitingForCapture = false,
    PolarAlignLastFrameDto? LastFrame = null);

/// <summary>§45 the most recent polar-align frame: its exposure, how long the capture (exposure +
/// download) and the plate solve took, and the solved apparent-of-date pointing when it solved.</summary>
public sealed record PolarAlignLastFrameDto(
    string FrameId,
    bool Solved,
    double ExposureSeconds,
    int CaptureMs,
    int? SolveMs,
    double? RaDeg,
    double? DecDeg,
    DateTimeOffset CompletedAt);

public sealed record PolarAlignFrameDto(
    string FrameId,
    Uri PreviewUrl,
    double? SolvedRaHours,
    double? SolvedDecDeg,
    string CapturedAt);