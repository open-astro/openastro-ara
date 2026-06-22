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
/// Service interfaces per PORT_PLAYBOOK.md §8.1 mapping table. Each interface
/// replaces NINA's WPF-thread-affinity mediator with a thread-safe singleton
/// service the daemon can call from any endpoint or hosted worker.
///
/// Phase 6 lands the interfaces + endpoint shells (501 NotImplemented bodies).
/// Implementations come incrementally as each device type's Alpaca wiring lands.
/// </summary>

public interface IEquipmentDiscoveryService {
    Task<IReadOnlyList<DiscoveredDeviceDto>> DiscoverAsync(DeviceType type, bool forceRefresh, CancellationToken ct);
}

public interface ICameraService {
    Task<CameraDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);
    Task<ExposureResponseDto> StartExposureAsync(ExposureRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task AbortExposureAsync(CancellationToken ct);
    Task SetCoolerAsync(bool enabled, double? targetTemperatureC, CancellationToken ct);
    // §64 Live View: a short-exposure render loop for framing/focus (no catalog write).
    Task StartLiveViewAsync(LiveViewStartRequestDto request, CancellationToken ct);
    // The ct is intentionally NOT honored: a stop always runs to completion (it awaits the loop
    // draining, up to the exposure cap), so callers must not treat a cancelled ct as "stop failed".
    Task StopLiveViewAsync(CancellationToken ct);
    LiveViewStatusDto GetLiveViewStatus();
    // ReadOnlyMemory (not byte[]): the published buffer is shared across readers and must not be
    // mutated; the read-only view makes that explicit without a per-fetch defensive copy.
    (ReadOnlyMemory<byte> Jpeg, long Seq)? GetLiveViewFrame();
}

public interface ITelescopeService {
    Task<TelescopeDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> SlewAsync(SlewRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> ParkAsync(ParkRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> UnparkAsync(string? idempotencyKey, CancellationToken ct);
    Task SetTrackingAsync(bool enabled, CancellationToken ct);
    Task AbortSlewAsync(CancellationToken ct);
}

public interface IFocuserService {
    Task<FocuserDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> MoveAsync(FocuserMoveRequestDto request, string? idempotencyKey, CancellationToken ct);
}

public interface IFilterWheelService {
    Task<FilterWheelDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> ChangeFilterAsync(FilterChangeRequestDto request, string? idempotencyKey, CancellationToken ct);
}

public interface IRotatorService {
    Task<RotatorDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> MoveAsync(RotatorMoveRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> SetReverseAsync(RotatorReverseRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> SyncAsync(RotatorSyncRequestDto request, string? idempotencyKey, CancellationToken ct);
}

public interface IDomeService {
    Task<DomeDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> SlewAsync(DomeSlewRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> ParkAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> OpenShutterAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> CloseShutterAsync(string? idempotencyKey, CancellationToken ct);
}

public interface ISwitchService {
    // Multi-instance: switches are addressed by their AlpacaDeviceNumber (the {n} in
    // /api/v1/equipment/switch/{n}). GetAllAsync lists every connected/known switch.
    Task<IReadOnlyList<SwitchDto>> GetAllAsync(CancellationToken ct);
    Task<SwitchDto?> GetAsync(int deviceNumber, CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(int deviceNumber, string? idempotencyKey, CancellationToken ct);
    Task SetValueAsync(int deviceNumber, SwitchValueRequestDto request, CancellationToken ct);
}

public interface IObservingConditionsService {
    Task<ObservingConditionsDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);
}

public interface ISafetyMonitorService {
    Task<SafetyMonitorDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);
}

public interface IFlatDeviceService {
    Task<FlatDeviceDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> ApplyFlatPanelAsync(FlatPanelRequestDto request, string? idempotencyKey, CancellationToken ct);
}

public interface IGuiderService {
    Task<GuiderDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(GuiderConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> StartGuidingAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> StopGuidingAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DitherAsync(double pixels, string? idempotencyKey, CancellationToken ct);

    /// <summary>§63.6 — dispatch a dark-library build (202-Accepted; runs on a background task and reports
    /// start/finish over the WS stream). Validates the request synchronously; throws on a bad request or a
    /// disconnected guider before accepting.</summary>
    Task<OperationAcceptedDto> BuildDarkLibraryAsync(BuildDarkLibraryRequestDto request, string? idempotencyKey, CancellationToken ct);

    /// <summary>§63.6 — dispatch a defect-map (bad-pixel) build (202-Accepted; shares the single calibration-build
    /// gate with the dark-library build). Validates synchronously; throws on a bad request or a disconnected
    /// guider before accepting.</summary>
    Task<OperationAcceptedDto> BuildDefectMapDarksAsync(BuildDefectMapDarksRequestDto request, string? idempotencyKey, CancellationToken ct);

    /// <summary>§63.6 — read the guider's calibration-files status. Returns null when no guider is connected.</summary>
    Task<CalibrationFilesStatusDto?> GetCalibrationFilesStatusAsync(CancellationToken ct);

    /// <summary>§63.6 — enable/disable dark subtraction; returns the updated calibration status. Throws when
    /// disconnected (→ 409) or when the daemon rejects the toggle (→ 422).</summary>
    Task<CalibrationFilesStatusDto> SetDarkLibraryEnabledAsync(bool enabled, CancellationToken ct);

    /// <summary>§63.6 — enable/disable bad-pixel (defect-map) correction; returns the updated calibration status.</summary>
    Task<CalibrationFilesStatusDto> SetDefectMapEnabledAsync(bool enabled, CancellationToken ct);
}

public interface IPolarAlignService {
    Task<PolarAlignStateDto> GetStatusAsync(CancellationToken ct);
    Task<OperationAcceptedDto> StartAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> StopAsync(string? idempotencyKey, CancellationToken ct);
}