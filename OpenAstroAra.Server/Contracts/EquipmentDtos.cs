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

/// <summary>GET /api/v1/equipment/driver/{type}: the selected device's Alpaca driver version and, when its
/// server reports them (AlpacaBridge does), the vendor SDK version, firmware and serial number; each null
/// when not known. <c>/driver/guidecamera</c> also names the camera (the guider's pick is a choice string,
/// not a model).</summary>
public sealed record DeviceDriverDto(string? DriverVersion, string? SdkVersion, string? Firmware, string? SerialNumber = null,
    string? Name = null);

// OperationAcceptedDto lives in SharedDtos.cs (added in Phase 7 — same record
// shape reused for every long-running operation across Phases 6-9). Phase 6's
// original stub OperationAcceptedDto(string) was superseded.

// ─── Camera (§10.6 row 1) ─────────────────────────────────────────────────────

public sealed record CameraDto(
    string DeviceId,
    string Name,
    EquipmentConnectionState State,
    CameraCapabilitiesDto? Capabilities,
    CameraStateDto Runtime,
    // #1663 — the switch port that is THIS camera's own anti-dew heater (paired by serial, see
    // DewHeaterPairing), or null: none, not connected, or not known. Additive; older clients ignore it.
    DewHeaterDto? DewHeater = null);

/// <summary>#1663 — a camera's heater: the switch's Alpaca UniqueId and the port's id. The level, range and
/// write permission come from the switch list; writes use the switch set-value path.</summary>
public sealed record DewHeaterDto(string DeviceId, int PortId);

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
    double? HeatSinkTemperatureC = null,
    // #2076 — true while a cooler Ara switched on does not respond: CoolerOn has read back off
    // for a minute, or on at 0 % power with the sensor well above its set-point and not moving
    // (a camera with no 12 V). Cleared when the cooler responds, is switched off, or on reconnect.
    bool CoolerNotResponding = false);

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
    double? TargetDeclinationDegrees = null,
    // #1649 — the mount's drive rate ("Sidereal" | "Lunar" | "Solar" | "King", ASCOM DriveRate
    // names as in SupportedSiderealRates); null when the mount can't report it.
    string? TrackingRate = null,
    // #2070 — the mount's reported position is not trusted (it reported Home right after it
    // connected, or its position jumped with nothing commanded): GoTo, Park, Home and polar
    // alignment are refused until the user confirms Home. The reason is one of
    // at_home_on_connect | pose_jump_on_reconnect | at_home_without_command | pose_jump_in_session.
    bool PositionUnverified = false,
    string? PositionUnverifiedReason = null);

/// <summary>#2070 — one mount whose position is unverified, as the daemon keeps it across restarts.</summary>
public sealed record MountPoseEntry(string DeviceId, string Reason);

/// <summary>#2070 — on-disk shape of the unverified-mount file (<c>mount-pose.json</c>).</summary>
public sealed record MountPoseFile(IReadOnlyList<MountPoseEntry> Unverified);

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
// (signed: positive/negative for the two directions); Rate 0 stops that axis. #1649 — LeaseMs
// (optional, clamped to 250–5000): the daemon stops the axis itself unless the move is renewed
// within it; absent, the move runs until a stop (the Full UI pad).
public sealed record MoveAxisRequestDto(int Axis, double Rate, int? LeaseMs = null);

// Tracking control: start/stop sidereal tracking (the set-point rate is the
// device's current TrackingRate; this only toggles Tracking on/off).
public sealed record TelescopeTrackingRequestDto(bool Enabled);

// #1649 — set the mount's drive rate, one of the capability's SupportedSiderealRates (any case).
// Tracking on/off is unchanged.
public sealed record TelescopeTrackingRateRequestDto(string Rate);

// ─── Focuser (§10.6 row 3) ────────────────────────────────────────────────────

public sealed record FocuserDto(
    string DeviceId,
    string Name,
    EquipmentConnectionState State,
    FocuserCapabilitiesDto? Capabilities,
    FocuserStateDto Runtime);

/// <summary><c>CanSetZero</c> / <c>CanCalibrate</c>: the driver lists the SetZero action (an absolute focuser
/// only), or Calibrate with CalibrationStatus (it runs to both stops to find its travel).</summary>
public sealed record FocuserCapabilitiesDto(
    int MinPosition, int MaxPosition,
    double StepSizeUm,
    bool CanTempComp, bool AbsoluteFocuser,
    bool CanSetZero = false, bool CanCalibrate = false);

public sealed record FocuserStateDto(
    string State,    // "idle" | "moving" | "settled" | "backlash_compensating" | "error"
    int? Position,
    double? Temperature,
    bool TempCompEnabled,
    string? Calibration = null); // "calibrating" | "calibrated" | "not_calibrated"; null when the focuser can't calibrate

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

// #1689 — Following: the dome is turning to keep the slit in front of the mount.
public sealed record DomeStateDto(string State, double? AzimuthDeg, bool ShutterOpen, bool AtHome, bool Parked, bool Following = false);

public sealed record DomeFollowRequestDto(bool Enabled);

public sealed record DomeSlewRequestDto(double TargetAzimuthDeg);

// ─── Switch (§10.6 row 7) ─────────────────────────────────────────────────────

public sealed record SwitchDto(
    string DeviceId,
    int AlpacaDeviceNumber,
    string Name,
    EquipmentConnectionState State,
    IReadOnlyList<SwitchPortDto> Ports);

/// <param name="Step">The device's <c>SwitchStep</c>; 0 = unknown (#1680).</param>
public sealed record SwitchPortDto(int Id, string Name, double Value, double Min, double Max, bool CanWrite, double Step = 0);

public sealed record SwitchValueRequestDto(int PortId, double Value);

// ─── Polar Aligner (#1972) ──────────────────────────────────────────────────

/// <summary>The rig's Polar Aligner: a connected Switch, else the mount, whose driver lists
/// <c>PolarAlign:Info</c>. <c>Carrier</c> is "switch" | "mount" | null; <c>DeviceId</c> is the
/// switch's UniqueId (null for the mount). <c>NotDrivableReason</c>: "device_runs_its_own_loop"
/// (Info lists no tier 1, or an axis is missing) | "info_unreadable" | null. Positions and faults
/// are null when the device does not report them or Status cannot be read, never 0.</summary>
public sealed record PolarAlignerDto(
    bool Present,
    string? Carrier,
    string? DeviceId,
    string? Name,
    bool Drivable,
    string? NotDrivableReason,
    PolarAlignerCapabilitiesDto? Capabilities,
    bool Moving,
    double? AzimuthPositionArcmin,
    double? AltitudePositionArcmin,
    string? AzimuthFault,
    string? AltitudeFault);

/// <summary>The device's Info reply. An axis the device does not describe has null figures.</summary>
public sealed record PolarAlignerCapabilitiesDto(
    string DeviceName,
    string DeviceVersion,
    double? AzimuthMaxMoveArcmin,
    double? AzimuthResolutionArcmin,
    double? AltitudeMaxMoveArcmin,
    double? AltitudeResolutionArcmin,
    bool SimultaneousMoves,
    bool MovesWhileTracking,
    bool MovesWhileSlewing,
    bool CanStop,
    double SettleSeconds);

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
    double? CalibrationDistancePx = null,
    /// <summary>The guider's profiles (its <c>get_profiles</c>), for the Settings profile picker. Empty until
    /// the guider has listed its profiles; null only while there is no guider at all.</summary>
    System.Collections.Generic.IReadOnlyList<GuiderProfileDto>? Profiles = null,
    /// <summary>#1665 — why the guider does not hold the active profile's guider settings, and how to fix
    /// it. Null when it does, or when that is not known yet.</summary>
    string? OutOfStepReason = null);

/// <summary>One guider profile: the daemon's id and its name.</summary>
public sealed record GuiderProfileDto(int Id, string Name);

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
/// guider-advanced block: its current values (fields it did not answer are null), the guide algorithms
/// each axis offers, and what the latest settings push did not land (#1748). <c>Connected</c> false with
/// nulls and empty lists when no guider is connected.</summary>
public sealed record GuiderAdvancedViewDto(
    bool Connected,
    GuiderAdvancedSettingsDto Current,
    IReadOnlyList<string> RaAlgorithms,
    IReadOnlyList<string> DecAlgorithms,
    IReadOnlyList<GuiderNotAppliedDto> NotApplied);

/// <summary>#1748 — one setting the latest push did not land: the name the imager sees and why, in plain text.</summary>
public sealed record GuiderNotAppliedDto(string Setting, string Reason);

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

/// <summary>The star the guider locked on, guide pixels (<c>POST …/live/find_star</c>).</summary>
public sealed record GuiderLiveLockDto(double X, double Y);

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
/// <c>no_pixel_scale</c>, <c>no_fit</c>, <c>guiding_not_holding</c>: an axis ran away, 10 steps with the pulse pinned
/// and the distance growing; <c>settle_failed</c>: a settle ended in an error; <c>mount_not_tracking</c>: the mount
/// was not tracking when the run started; the first reason set is kept). <c>PaErrorMinArcmin</c> is a LOWER BOUND: Dec drift at one
/// hour angle sees one component of the error. <c>Reliable</c> is false when the 1σ
/// <c>UncertaintyArcmin</c> exceeds max(1′, half the value), or when the value less twice that
/// uncertainty is more than 2′ above <c>AlignErrorArcmin</c> (#1731). <c>AlignErrorArcmin</c> is the last
/// Polar Align result in the 12 h before, for comparison. <c>AboveAlign</c> (#2072) is that second rule on its own:
/// the two numbers disagree (Dec lost motion inflated the guiding figure, or the mount shifted or the alignment was
/// wrong); null with no Polar Align result to compare. Hour angle / Dec are the mount's at the end.
/// <c>AlignSigmaArcmin</c> (#2072) is that Polar Align result's own 1σ; with it, the two disagree when the value is
/// above the result by more than twice their combined 1σ, √(<c>UncertaintyArcmin</c>² + <c>AlignSigmaArcmin</c>²)
/// (without it, an older row, the fixed 2′ margin stands in for the result's error). Stored with the row
/// (<c>pa_residuals.align_sigma_arcmin</c>), so a listed row is judged as the live one was; null on a row stored before it.
/// <c>DecPulsesUnanswered</c> (#2072) is true when the star did not answer the Dec pulses (Dec backlash: the pulses went one
/// way and the star kept moving away from them), so the figure is the pulses, not drift: <c>Reliable</c> is false and
/// <c>AboveAlign</c> null. Always sent; false while measuring, when unavailable, and on a row stored before it.
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
    string? Reason = null,
    bool? AboveAlign = null,
    double? AlignSigmaArcmin = null,
    bool DecPulsesUnanswered = false);

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
    IReadOnlyDictionary<string, string>? UniqueIds = null,
    // #1580 — "type/number" → the device's serial, so the setup flow can tell two cameras of one model apart;
    // entries the server reports no serial for are left out. Additive; older clients ignore it.
    IReadOnlyDictionary<string, string>? Serials = null);

/// <summary>#1580 — AlpacaBridge's device catalog as the wizard shows it: the drivers that are built in
/// (<c>available</c> true), grouped by device type. Each driver (and so each field schema) is AlpacaBridge's own
/// JSON, passed through unchanged (its keys are AlpacaBridge's camelCase, not Ara's snake_case). Empty when the
/// server is not an AlpacaBridge with a catalog, or does not answer: the client then offers no Add.</summary>
public sealed record AlpacaBridgeCatalogResponseDto(IReadOnlyList<AlpacaBridgeDeviceTypeDto> DeviceTypes);

/// <summary>#1580 — the drivers of one device type (lowercase token, e.g. <c>camera</c>).</summary>
public sealed record AlpacaBridgeDeviceTypeDto(string DeviceType, IReadOnlyList<System.Text.Json.JsonElement> Drivers);

/// <summary>#1580 — the cameras plugged in for one vendor; empty for a vendor with no lister.</summary>
public sealed record AlpacaBridgeCamerasResponseDto(IReadOnlyList<AlpacaBridgeCameraDto> Cameras);

/// <summary>#1580 — one attached camera: AlpacaBridge's enumeration index, model name and serial.</summary>
public sealed record AlpacaBridgeCameraDto(int Index, string Name, string Serial);

/// <summary>#1580 — a device was added. <c>Message</c> is AlpacaBridge's own text; <c>Warning</c> is its warning
/// when it registered the device with one, else null.</summary>
public sealed record AlpacaBridgeAddDeviceResponseDto(string Message, string? Warning);

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

/// <summary>The routine's measurements (<see cref="PolarAlignStateDto.Stage"/>), one per Start (#2082): <c>coarse</c> —
/// from where the user pointed the mount, the three-point seed (two RA-motor turns, three solves) + live adjust loop on the
/// main camera (slower frames, for the big moves); <c>fine</c> — the same seed + live adjust loop on the guide camera;
/// <c>check</c> — three guide-camera solves with two RA turns, an exact three-point axis fit, no adjusting. No stage sends
/// the mount Home, slews it or moves its Dec axis (#2048, NINA's start), and none leads into another.</summary>
public static class PolarAlignStages {
    public const string Coarse = "coarse";
    public const string Fine = "fine";
    public const string Check = "check";

    public static bool IsValid(string? stage) => stage is Coarse or Fine or Check;
}

/// <summary><c>POST /polaralign/start</c> body: the measurement to make (null/absent = fine, #2082), and the user's
/// confirmation of the start (#2048). The
/// routine starts from where the mount points now; it moves only with <c>Confirmed</c> true and <c>Direction</c> equal
/// to the <see cref="PolarAlignStartPlanDto.Direction"/> the user was shown. Without them the start is refused
/// (<c>polar-align-unconfirmed</c>, with the direction the run would turn now). <c>NewOrigin</c> echoes the plan's
/// <see cref="PolarAlignStartPlanDto.NewOrigin"/>: a start whose plan changed between a new start pose and a continued
/// one since the user saw it is refused the same way.
/// <para>#2081 — <c>DirectionTest</c> echoes the plan's <see cref="PolarAlignStartPlanDto.DirectionTestDeg"/> (true when it
/// is over 0): the user saw that the run first turns the mount 0.5° to learn its motor's direction. A motor start whose
/// plan has a test the confirmation did not see, or the other way round, is refused like a changed direction.</para>
/// <para>#2170 — there is no hand-turn start: Polar Align turns RA only with the mount's motor. A body that still carries
/// the removed <c>hand_turn</c> field is read like any unknown field (ignored), so it makes the same motor start.</para></summary>
public sealed record PolarAlignStartRequestDto(string? Stage = null, bool Confirmed = false, string? Direction = null, bool NewOrigin = false,
    bool DirectionTest = false);

/// <summary>#2048 — what a confirmed start will do, for the confirm step the user answers before the mount moves
/// (<c>GET /polaralign/start-plan</c>). <c>Direction</c> (<c>east</c> | <c>west</c>) is the way the RA axis turns
/// first (#2081: from the profile at a new sweep origin, else back toward it); <c>StepDeg</c> is each of the
/// <c>Turns</c> turns; <c>MaxSweepDeg</c> is how far the run turns the RA axis from where it starts (its two turns:
/// one measurement per Start, #2082). <c>Stage</c> echoes the request.
/// <para>#2081 — at a new sweep origin <c>Direction</c> is the profile's <c>polar_align.east_direction</c> (east in the
/// north and west in the south by default), never the mount's hour angle; a later start of the session turns back toward
/// the origin. <c>DirectionTestDeg</c> (0.5, or 0) is the test move a run makes before its first turn when no saved RA
/// motor direction stands for the mount's driver (<c>polar_align_motor_direction</c>): the mount turns that far toward
/// <c>Direction</c> (or the other way, on a driver whose positive rate lowers RA) and the run saves which way it went. The
/// test counts toward the sweep limit.</para>
/// <para>#2089 — the session's sweep origin, from RA differences the mount reports (never its clock, LST or absolute
/// RA): <c>NewOrigin</c> is true when this start's pose becomes Polar Align's origin (the first start since the mount
/// connected, or the mount was moved by other means since Polar Align last turned it); otherwise
/// <c>OriginOffsetDeg</c> is how far the RA axis has turned from the origin (signed: + east, − west), and
/// <c>Direction</c> turns back toward it. No turn takes the axis more than <c>SweepLimitDeg</c> from the
/// origin (else the <c>polar-align-sweep-limit</c> Refusal).</para>
/// <para>#2082 — <c>Camera</c> (<see cref="PolarAlignCameras"/>) is the camera the run measures with: <c>main</c> for the
/// <c>coarse</c> stage ("Rough first"), else the profile's <c>polar_align.camera</c>.</para></summary>
public sealed record PolarAlignStartPlanDto(string? Stage, string Direction, double StepDeg, int Turns, double MaxSweepDeg,
    bool NewOrigin = true, double OriginOffsetDeg = 0.0, double SweepLimitDeg = 30.0, string Camera = PolarAlignCameras.Guide,
    double DirectionTestDeg = 0.0);

/// <summary>#2068 (ARA-18) — Polar Align's pre-flight list (<c>GET /polaralign/preflight</c>): every safety check a start
/// makes, in the order a start makes them, with what the next Start would do. <c>Ready</c> is true when no check
/// fails; a start (<c>POST /polaralign/start</c>, <c>GET /polaralign/start-plan</c>) refuses on the first check that
/// fails, with that check's cause. A <c>warn</c> or <c>unknown</c> check never refuses: <c>unknown</c> is a check this
/// daemon does not make yet (its detail names the issue that adds it). <c>MountKey</c> is the connected mount's key in
/// the polar-align section's <c>mount_clearance</c> (null with no mount connected): the clearance confirmation writes
/// there. <c>MoveToStart</c> (#2166) is the offer of <c>POST /polaralign/move-to-start</c>: present only when the
/// <c>start_pointing</c> check fails because the scope points within 3° of the pole, null otherwise.</summary>
public sealed record PolarAlignPreflightDto(
    bool Ready,
    IReadOnlyList<PolarAlignPreflightCheckDto> Checks,
    PolarAlignPreflightPlannedDto Planned,
    string? MountKey,
    PolarAlignMoveToStartOfferDto? MoveToStart);

/// <summary>#2166 — the "Move to start position" offer on the pre-flight list: Polar Align can move the mount's Dec
/// axis alone to <see cref="PolarAlignMoveToStartOfferDto.TargetOffPoleDeg"/> from the pole (counterweights stay where
/// they are). <c>BlockedBy</c> is null when the move may be asked for, else why not
/// (<see cref="PolarAlignMoveToStartBlocks"/>): the app shows the button disabled with that reason. <c>ReachDeg</c> is
/// the most the move takes the Dec axis from where it starts (the target plus the probe), the reach the mount's
/// clearance must cover.</summary>
public sealed record PolarAlignMoveToStartOfferDto(string? BlockedBy, double TargetOffPoleDeg, double ReachDeg);

/// <summary>#2166 — why the move to the start position cannot be asked for now (<see cref="PolarAlignMoveToStartOfferDto.BlockedBy"/>).</summary>
public static class PolarAlignMoveToStartBlocks {
    /// <summary>The mount is parked: Polar Align never unparks it.</summary>
    public const string MountParked = "mount_parked";
    /// <summary>The site's latitude is not set: the pole's side is not known.</summary>
    public const string SiteLatitude = "site_latitude";
    /// <summary>The mount's position is not trusted (#2070): confirm Home first.</summary>
    public const string PoseUnverified = "pose_unverified";
    /// <summary>The mount's clearance is not confirmed for the move's reach.</summary>
    public const string ClearanceNotConfirmed = "clearance_not_confirmed";
    /// <summary>The mount reports alt-az: its second axis is altitude, not Dec.</summary>
    public const string AltAzMount = "alt_az_mount";
    /// <summary>The mount reports CanMoveAxis(Secondary) false: it cannot turn its Dec axis by rate.</summary>
    public const string CannotMoveDec = "cannot_move_dec";
}

/// <summary>#2166 — what <c>POST /polaralign/move-to-start</c> did once it moved: <c>Outcome</c> is <c>arrived</c> (the
/// scope now points 2–5° from the pole), <c>stopped</c> (Stop, Stop Mount or Emergency Stop ended it; the mount stands
/// where it stopped) or <c>failed</c> (a guard stopped it: <c>Reason</c> is the <c>polar_align.error</c> reason, such as
/// <c>slew_stalled</c>, <c>ra_moved</c> or <c>pier_changed</c>, and <c>Message</c> says what to do). <c>Preflight</c> is the
/// list read again after the move, from a fresh reading of the mount.</summary>
public sealed record PolarAlignMoveToStartResultDto(string Outcome, string? Reason, string? Message, PolarAlignPreflightDto Preflight);

/// <summary>The outcomes of <see cref="PolarAlignMoveToStartResultDto"/>.</summary>
public static class PolarAlignMoveToStartOutcomes {
    public const string Arrived = "arrived";
    public const string Stopped = "stopped";
    public const string Failed = "failed";
}

/// <summary>One check of <see cref="PolarAlignPreflightDto"/>: its id (<see cref="PolarAlignPreflightChecks"/>), its
/// status (<see cref="PolarAlignPreflightStatuses"/>) and a sentence for people (English).</summary>
public sealed record PolarAlignPreflightCheckDto(string Id, string Status, string Detail);

/// <summary>What the next Start at <c>Stage</c> would do (the confirm step's plan where the mount could be read):
/// <c>Direction</c> (<c>east</c> | <c>west</c>, null when the plan could not be made), <c>TurnStepDeg</c> each of the
/// measurement's two turns (<c>CheckStepDeg</c> a Check's), <c>MaxSweepDeg</c> the most it turns from where it starts,
/// the session's sweep origin (<c>NewOrigin</c>, <c>OriginOffsetDeg</c>, <c>SweepLimitDeg</c>, #2089) and (#2165)
/// <c>ReachDeg</c>, the most the run's motor turns RA each way: <c>MaxSweepDeg</c> plus the 0.5° direction test when it
/// is owed (the plan's <see cref="PolarAlignStartPlanDto.DirectionTestDeg"/>), the reach the clearance is confirmed for.
/// <c>StartCwAngleDeg</c> and
/// <c>MinAltitudeDeg</c> are always null: Ara has no counterweight model or pose envelope (NINA parity, Joey
/// 2026-11-21); they stay so an app that reads them still parses the list.</summary>
public sealed record PolarAlignPreflightPlannedDto(
    string Stage,
    string? Direction,
    double TurnStepDeg,
    double CheckStepDeg,
    int Turns,
    double MaxSweepDeg,
    bool? NewOrigin,
    double? OriginOffsetDeg,
    double? SweepLimitDeg,
    double ReachDeg,
    double? StartCwAngleDeg,
    double? MinAltitudeDeg);

/// <summary>The statuses of a <see cref="PolarAlignPreflightCheckDto"/>.</summary>
public static class PolarAlignPreflightStatuses {
    public const string Ok = "ok";
    /// <summary>Worth reading; never refuses a start.</summary>
    public const string Warn = "warn";
    /// <summary>A start refuses on it.</summary>
    public const string Fail = "fail";
    /// <summary>Not checked by this daemon yet, or its input could not be read for an informational check; never
    /// refuses a start.</summary>
    public const string Unknown = "unknown";
}

/// <summary>The ids of the <see cref="PolarAlignPreflightCheckDto"/> rows (ARA-18), in the order a start checks
/// them.</summary>
public static class PolarAlignPreflightChecks {
    public const string MountConnected = "mount_connected";
    public const string MountParked = "mount_parked";
    public const string SiteLatitude = "site_latitude";
    public const string SiteLongitudeLst = "site_longitude_lst";
    public const string MountClockOrSite = "mount_clock_or_site";
    public const string PoseVerified = "pose_verified";
    public const string ClearanceConfirmed = "clearance_confirmed";
    public const string StartPointing = "start_pointing";
    public const string MountType = "mount_type";
    public const string CanMoveAxis = "can_move_axis";
    public const string Aligner = "aligner";
    public const string MountLimits = "mount_limits";

    /// <summary>Every id, in order.</summary>
    public static readonly IReadOnlyList<string> All = [
        MountConnected, MountParked, SiteLatitude, SiteLongitudeLst, MountClockOrSite, PoseVerified, ClearanceConfirmed,
        StartPointing, MountType, CanMoveAxis, Aligner, MountLimits,
    ];
}

/// <summary>#2048 — the user's answer to the confirm step: they were shown the plan and the RA turn direction
/// (<see cref="PolarAlignStartPlanDto.Direction"/>), and agreed the mount may move. <c>NewOrigin</c> (#2089): the plan
/// they saw made this pose Polar Align's new origin. <c>DirectionTest</c> (#2081): the plan they saw had the 0.5°
/// motor-direction test.</summary>
public sealed record PolarAlignStartConfirmation(string Direction, bool NewOrigin = false, bool DirectionTest = false);

/// <summary>#2166 — <c>POST /polaralign/move-to-start</c> body: the user's confirmation of the move (the app's motion
/// confirm). The mount moves only with <c>Confirmed</c> true and <c>TargetOffPoleDeg</c> and <c>ReachDeg</c> equal to the
/// pre-flight list's <see cref="PolarAlignMoveToStartOfferDto"/> the confirm showed; no body, <c>confirmed: false</c> or
/// other numbers is the <c>polar-align-move-not-confirmed</c> Refusal, with nothing moved.</summary>
public sealed record PolarAlignMoveToStartRequestDto(bool Confirmed = false, double? TargetOffPoleDeg = null, double? ReachDeg = null);

/// <summary>#2166 — the move's confirmation as the service reads it: the offer's numbers the user confirmed.</summary>
public sealed record PolarAlignMoveToStartConfirmation(double TargetOffPoleDeg, double ReachDeg);

/// <summary>The RA turn directions of <see cref="PolarAlignStartPlanDto.Direction"/>.</summary>
public static class PolarAlignDirections {
    /// <summary>The mount's RA reading increases: the tube turns toward the east.</summary>
    public const string East = "east";
    /// <summary>The mount's RA reading decreases: the tube turns toward the west.</summary>
    public const string West = "west";
}

/// <summary>The check stage's three-point result: the axis error against the apparent pole, the
/// cone half-angle the three pointings sat at (conditioning), how far the solved turns disagreed
/// with the mount's reported RA turns, the last run's readout for comparison (#2082: <c>FineErrorArcmin</c> keeps its
/// wire name from when the fine stage led into the check; it is the readout of the last run that ended with one, null
/// when none did since the daemon started. <c>FineDeltaArcmin</c> is the distance between the two measured axes — alt
/// and az both — so a readout of the same size on the other side of the pole still reads as a disagreement), the verdict band
/// (<c>excellent</c> ≤1′, <c>very_good</c> ≤3′, <c>good</c> ≤10′, <c>rough</c> ≤30′, else <c>far_off</c>), and whether a
/// point stayed noisy after its retake (#2082, <c>Noisy</c>). The <c>*SigmaArcmin</c> fields (#2072) are the fit's 1σ on
/// each error, from the scatter of each point's frames (a single-frame point counts a floor of 0.15 px at the solving
/// camera's pixel scale, 1″ when that is unknown); the total's is the rms length of the error vector, √(alt² + az²) of
/// the two. Null from an older daemon. With them, the verdict band rates the total plus twice its 1σ (#2072), so a
/// noisy check does not read <c>excellent</c>.
/// <c>DecSpreadArcsec</c> (#2081): how far the mount's Dec reading spread across the check's three points, when that is
/// over 2″ (NINA's warning: the mount moved in Dec, so the result may be off); null otherwise.</summary>
public sealed record PolarAlignCheckResultDto(
    double AltitudeErrorArcmin,
    double AzimuthErrorArcmin,
    double TotalErrorArcmin,
    double ConeHalfAngleDeg,
    double TurnMismatchDeg,
    double? FineErrorArcmin,
    double? FineDeltaArcmin,
    string Verdict,
    DateTimeOffset MeasuredAt,
    // #2082 — a point of the check stayed scattered over 3″ after its retake (polar_align.frames_per_point).
    bool Noisy = false,
    // #2072 — the fit's 1σ on each error (arcmin).
    double? AltitudeSigmaArcmin = null,
    double? AzimuthSigmaArcmin = null,
    double? TotalSigmaArcmin = null,
    // #2081 — the mount's Dec spread across the check's three points (arcseconds) when over 2″; null otherwise.
    double? DecSpreadArcsec = null);

public sealed record PolarAlignStateDto(
    // §45 engine states: homing (retired by #2048, never published), seeding (3-point axis measurement: two RA-motor turns),
    // adjusting (live loop, error fields populated — every stage reaches it through seeding), paused
    // (5 consecutive failed solves — still retrying), checking (the three check solves), checked
    // (terminal: CheckResult populated), failed (fatal routine error; see the polar_align.error WS event).
    string State,    // "idle" | "homing" | "seeding" | "adjusting" | "paused" | "checking" | "checked" | "stopped" | "failed"
    double? CurrentErrorArcmin,
    double? AzimuthAdjustmentArcmin,
    double? AltitudeAdjustmentArcmin,
    int FramesCaptured,
    string? LastFrameId,
    // Single-frame mode: the adjust loop is parked until POST /polaralign/capture.
    bool WaitingForCapture = false,
    PolarAlignLastFrameDto? LastFrame = null,
    // Which stage is running (PolarAlignStages); null when idle/stopped before any run.
    string? Stage = null,
    // Always false since #2048: no stage sends the mount Home or waits for it. Kept for older clients.
    bool WaitingForHome = false,
    // Check stage: solves done so far (0–3).
    int CheckSolvesDone = 0,
    PolarAlignCheckResultDto? CheckResult = null,
    // #1565 — the last routine to stop or finish with an error reading outside the rough stage, kept in memory per
    // daemon boot (like the autofocus run tracker); null until one has. A new run does not clear it: it describes
    // the last finished one.
    PolarAlignLastResultDto? LastResult = null,
    // #1971 — what the Polar Aligner is doing in this run; null when the rig has none, when Ara cannot
    // drive the one it has, or before the run's first measurement.
    PolarAlignCorrectionDto? Correction = null,
    // #2072 — a caution on the live readout from the last seed (PolarAlignReadoutCautions), also on each
    // polar_align.progress event; null when the readout is sound, before the first seed, and outside adjusting/paused.
    string? ReadoutCaution = null,
    // #2082 — the camera this run measures with (PolarAlignCameras); null before the first run.
    string? Camera = null,
    // #2082 — a point of this run's measurement stayed scattered over 3″ after its retake: the readout rests on noisy
    // solves. Also on each polar_align.progress event. False before the first measurement.
    bool MeasurementNoisy = false,
    // #2072 — the 1σ of CurrentErrorArcmin, AzimuthAdjustmentArcmin and AltitudeAdjustmentArcmin (arcmin): the fit the
    // readout rests on (the seed's in the live loop, the verdict's once checked). Null whenever the error is null.
    // The clients show "aligned" only when error + 2σ is inside the tolerance (Done is not held by it).
    double? CurrentSigmaArcmin = null,
    double? AzimuthSigmaArcmin = null,
    double? AltitudeSigmaArcmin = null,
    // #2081 — the mount's Dec spread across the seed's three points (arcseconds) when over 2″ (NINA's warning: the
    // readout may be off), also on each polar_align.progress event; null otherwise and outside adjusting/paused.
    double? DecSpreadArcsec = null);

/// <summary>#2072 — the tokens of <see cref="PolarAlignStateDto.ReadoutCaution"/>.</summary>
public static class PolarAlignReadoutCautions {
    /// <summary>The camera sits nearly due east or west of the pole's azimuth (|cos ψ| under 0.2): the live readout
    /// amplifies solve noise in altitude five times or more, and near ψ = ±90° it under-reads the altitude knob. The
    /// Check (a fresh three-point measurement) is the number to trust.</summary>
    public const string AltitudeWeak = "altitude_weak";
}

/// <summary>#1971 the Polar Aligner in a running routine, also the payload of <c>polar_align.correction</c>.
/// <c>State</c>: <c>manual</c> (the person turns the bolts), <c>probing</c> / <c>moving</c> (with <c>Axis</c>
/// <c>azimuth</c> | <c>altitude</c> and the signed <c>Arcmin</c> sent), <c>settling</c> (<c>Seconds</c>),
/// <c>measuring</c>, <c>converged</c> (inside the tolerance), <c>stopped</c> (<c>Cause</c>, the device's own
/// <c>DeviceText</c> when it gave one, the <c>Axis</c> when one is to blame). <c>Moves</c> counts the moves sent.</summary>
public sealed record PolarAlignCorrectionDto(
    string State,
    string? Axis = null,
    double? Arcmin = null,
    double? Seconds = null,
    string? Cause = null,
    string? DeviceText = null,
    int Moves = 0);

/// <summary>#1565 the last polar-align routine's final error (total, arcmin) and when it ended; a Stop in the rough
/// stage records none. Simple Mode's Night Setup counts polar alignment as done tonight when this is under the green
/// zone and ended in tonight's observing night.</summary>
public sealed record PolarAlignLastResultDto(double ErrorArcmin, DateTimeOffset CompletedUtc);

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