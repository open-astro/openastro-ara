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
    Task SetCoolerAsync(bool on, double? targetTemperatureC, CancellationToken ct);
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
    Task<SwitchDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);
    Task SetValueAsync(SwitchValueRequestDto request, CancellationToken ct);
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
}

public interface IPolarAlignService {
    Task<PolarAlignStateDto> GetStatusAsync(CancellationToken ct);
    Task<OperationAcceptedDto> StartAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> StopAsync(string? idempotencyKey, CancellationToken ct);
}