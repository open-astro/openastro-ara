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
    string? BayerPattern = null);

public sealed record CameraStateDto(
    string State,   // "idle" | "exposing" | "downloading" | "error"
    double? CcdTemperature,
    double? CoolerPowerPct,
    bool CoolerOn,
    double? ExposureProgressPct);

// Cooler control: turn the cooler on/off and (when on) set the target CCD
// temperature. TargetTemperatureC is ignored when Enabled is false.
public sealed record CameraCoolerRequestDto(bool Enabled, double? TargetTemperatureC = null);

public sealed record ExposureRequestDto(
    double ExposureSec,
    int? Gain,
    int BinX = 1, int BinY = 1,
    int? OffsetX = null, int? OffsetY = null,
    int? Width = null, int? Height = null,
    string? FilterName = null,
    // Electronic (pedestal) offset — distinct from the OffsetX/Y subframe position.
    int? CameraOffset = null);

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
    IReadOnlyList<string> SupportedSiderealRates);

public sealed record TelescopeStateDto(
    string State,    // "idle" | "slewing" | "tracking" | "parked" | "unparking" | "error"
    double? RightAscensionHours,
    double? DeclinationDegrees,
    bool Tracking,
    bool Parked,
    bool AtHome);

public sealed record SlewRequestDto(
    double RightAscensionHours,
    double DeclinationDegrees,
    bool? Sync = false);

public sealed record ParkRequestDto(string? Reason = null);

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

public sealed record FilterWheelDto(
    string DeviceId,
    string Name,
    EquipmentConnectionState State,
    FilterWheelStateDto Runtime,
    IReadOnlyList<FilterSlotDto> Slots);

public sealed record FilterSlotDto(int Position, string Name, int FocusOffset);

public sealed record FilterWheelStateDto(string State, int? CurrentSlot);

public sealed record FilterChangeRequestDto(int Position);

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
    bool CanSetShutter, bool CanSetAzimuth, bool CanSyncAzimuth, bool CanPark, bool CanFindHome);

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
    string CapturedAt);

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
    int Brightness);

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

public sealed record GuiderStateDto(
    string State,    // "stopped" | "calibrating" | "guiding" | "paused" | "star_lost" | "dithering"
    double? RmsTotal,
    double? RmsRa,
    double? RmsDec,
    string? CurrentProfile);

public sealed record GuiderConnectRequestDto(string Host = "localhost", int Port = 4400);

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

// ─── Polar Alignment (§10.6 row 12) ───────────────────────────────────────────

public sealed record PolarAlignStateDto(
    string State,    // "idle" | "capturing" | "solving" | "solved" | "unsolved" | "stopped"
    double? CurrentErrorArcmin,
    double? AzimuthAdjustmentArcmin,
    double? AltitudeAdjustmentArcmin,
    int FramesCaptured,
    string? LastFrameId);

public sealed record PolarAlignFrameDto(
    string FrameId,
    Uri PreviewUrl,
    double? SolvedRaHours,
    double? SolvedDecDeg,
    string CapturedAt);