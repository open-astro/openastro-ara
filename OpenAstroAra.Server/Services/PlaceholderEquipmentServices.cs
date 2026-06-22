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
/// Phase 13.12 — placeholder implementations for all 12 §52 equipment
/// services. Each follows the same shape:
///
///   - <c>GetAsync</c> returns <c>null</c> (no hardware connected; the
///     §52.5 endpoint convention is 404 when no device is selected, so
///     null surfaces correctly via the endpoint's null-check).
///   - <c>ConnectAsync</c> / <c>DisconnectAsync</c> accept the request
///     and return <see cref="OperationAcceptedDto"/> immediately. Real
///     impl handshakes via the existing <see cref="IEquipmentDiscoveryService"/>
///     (already functional since Phase 6) and starts the polling worker
///     that publishes state-change WS events.
///   - Device-specific ops (slew, focus-move, filter-change, exposure,
///     etc.) return <see cref="OperationAcceptedDto"/>. Real impl drives
///     the ASCOM Alpaca SDK.
///
/// One file for all 12 keeps the symmetry visible: each service is ~5
/// lines, and they share the same "no hardware yet, accept everything"
/// shape. Phase 14 splits per-device when the real impls land.
/// </summary>
static class PlaceholderEquipmentHelpers {
    public static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);
}

public sealed class PlaceholderCameraService : ICameraService {
    public Task<CameraDto?> GetAsync(CancellationToken ct) => Task.FromResult<CameraDto?>(null);
    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("camera.connect", idempotencyKey));
    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("camera.disconnect", idempotencyKey));
    public Task<ExposureResponseDto> StartExposureAsync(ExposureRequestDto request, string? idempotencyKey, CancellationToken ct) {
        // Synthetic frame id + matching preview URL so WILMA can construct
        // the §65 preview-fetch URL from the FrameId alone (the URL just
        // happens to point at the §13.1 placeholder JPEG since no real
        // frame with this GUID lives in the §28 catalog).
        var frameId = Guid.NewGuid();
        return Task.FromResult(new ExposureResponseDto(
            FrameId: frameId.ToString(),
            PreviewUrl: new Uri($"/api/v1/frames/{frameId}/preview", UriKind.Relative),
            ExposureSec: 1.0,
            CapturedAt: DateTimeOffset.UtcNow.ToString("O")));
    }
    public Task AbortExposureAsync(CancellationToken ct) => Task.CompletedTask;
    public Task SetCoolerAsync(bool enabled, double? targetTemperatureC, CancellationToken ct) => Task.CompletedTask;
    public Task StartLiveViewAsync(LiveViewStartRequestDto request, CancellationToken ct) => Task.CompletedTask;
    public Task StopLiveViewAsync(CancellationToken ct) => Task.CompletedTask;
    public LiveViewStatusDto GetLiveViewStatus() => new(false, 0, null, null, null, null, null);
    public (byte[] Jpeg, long Seq)? GetLiveViewFrame() => null;
}

public sealed class PlaceholderTelescopeService : ITelescopeService {
    public Task<TelescopeDto?> GetAsync(CancellationToken ct) => Task.FromResult<TelescopeDto?>(null);
    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("telescope.connect", idempotencyKey));
    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("telescope.disconnect", idempotencyKey));
    public Task<OperationAcceptedDto> SlewAsync(SlewRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("telescope.slew", idempotencyKey));
    public Task<OperationAcceptedDto> ParkAsync(ParkRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("telescope.park", idempotencyKey));
    public Task<OperationAcceptedDto> UnparkAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("telescope.unpark", idempotencyKey));
    public Task SetTrackingAsync(bool enabled, CancellationToken ct) => Task.CompletedTask;
    public Task AbortSlewAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class PlaceholderFocuserService : IFocuserService {
    public Task<FocuserDto?> GetAsync(CancellationToken ct) => Task.FromResult<FocuserDto?>(null);
    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("focuser.connect", idempotencyKey));
    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("focuser.disconnect", idempotencyKey));
    public Task<OperationAcceptedDto> MoveAsync(FocuserMoveRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("focuser.move", idempotencyKey));
}

public sealed class PlaceholderFilterWheelService : IFilterWheelService {
    public Task<FilterWheelDto?> GetAsync(CancellationToken ct) => Task.FromResult<FilterWheelDto?>(null);
    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("filter-wheel.connect", idempotencyKey));
    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("filter-wheel.disconnect", idempotencyKey));
    public Task<OperationAcceptedDto> ChangeFilterAsync(FilterChangeRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("filter-wheel.change", idempotencyKey));
}

public sealed class PlaceholderRotatorService : IRotatorService {
    public Task<RotatorDto?> GetAsync(CancellationToken ct) => Task.FromResult<RotatorDto?>(null);
    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("rotator.connect", idempotencyKey));
    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("rotator.disconnect", idempotencyKey));
    public Task<OperationAcceptedDto> MoveAsync(RotatorMoveRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("rotator.move", idempotencyKey));
    public Task<OperationAcceptedDto> SetReverseAsync(RotatorReverseRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("rotator.reverse", idempotencyKey));
    public Task<OperationAcceptedDto> SyncAsync(RotatorSyncRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("rotator.sync", idempotencyKey));
}

public sealed class PlaceholderDomeService : IDomeService {
    public Task<DomeDto?> GetAsync(CancellationToken ct) => Task.FromResult<DomeDto?>(null);
    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("dome.connect", idempotencyKey));
    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("dome.disconnect", idempotencyKey));
    public Task<OperationAcceptedDto> SlewAsync(DomeSlewRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("dome.slew", idempotencyKey));
    public Task<OperationAcceptedDto> ParkAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("dome.park", idempotencyKey));
    public Task<OperationAcceptedDto> OpenShutterAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("dome.shutter.open", idempotencyKey));
    public Task<OperationAcceptedDto> CloseShutterAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("dome.shutter.close", idempotencyKey));
}

// Superseded by the real SwitchService (registered in Program.cs); kept only so the placeholder block
// stays complete. Tracks the multi-instance ISwitchService surface.
public sealed class PlaceholderSwitchService : ISwitchService {
    public Task<IReadOnlyList<SwitchDto>> GetAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SwitchDto>>(Array.Empty<SwitchDto>());
    public Task<SwitchDto?> GetAsync(int deviceNumber, CancellationToken ct) => Task.FromResult<SwitchDto?>(null);
    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("switch.connect", idempotencyKey));
    public Task<OperationAcceptedDto> DisconnectAsync(int deviceNumber, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("switch.disconnect", idempotencyKey));
    public Task SetValueAsync(int deviceNumber, SwitchValueRequestDto request, CancellationToken ct) => Task.CompletedTask;
}

public sealed class PlaceholderObservingConditionsService : IObservingConditionsService {
    // operation_type prefix matches the route segment ("observing-conditions.*")
    // for consistency with the rest of the 12-service block.
    public Task<ObservingConditionsDto?> GetAsync(CancellationToken ct) => Task.FromResult<ObservingConditionsDto?>(null);
    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("observing-conditions.connect", idempotencyKey));
    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("observing-conditions.disconnect", idempotencyKey));
}

public sealed class PlaceholderSafetyMonitorService : ISafetyMonitorService {
    public Task<SafetyMonitorDto?> GetAsync(CancellationToken ct) => Task.FromResult<SafetyMonitorDto?>(null);
    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("safety-monitor.connect", idempotencyKey));
    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("safety-monitor.disconnect", idempotencyKey));
}

public sealed class PlaceholderFlatDeviceService : IFlatDeviceService {
    public Task<FlatDeviceDto?> GetAsync(CancellationToken ct) => Task.FromResult<FlatDeviceDto?>(null);
    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("flat-device.connect", idempotencyKey));
    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("flat-device.disconnect", idempotencyKey));
    public Task<OperationAcceptedDto> ApplyFlatPanelAsync(FlatPanelRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("flat-device.apply", idempotencyKey));
}

public sealed class PlaceholderGuiderService : IGuiderService {
    public Task<GuiderDto?> GetAsync(CancellationToken ct) => Task.FromResult<GuiderDto?>(null);
    public Task<OperationAcceptedDto> ConnectAsync(GuiderConnectRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("guider.connect", idempotencyKey));
    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("guider.disconnect", idempotencyKey));
    public Task<OperationAcceptedDto> StartGuidingAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("guider.start", idempotencyKey));
    public Task<OperationAcceptedDto> StopGuidingAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("guider.stop", idempotencyKey));
    public Task<OperationAcceptedDto> DitherAsync(double pixels, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("guider.dither", idempotencyKey));
    public Task<OperationAcceptedDto> BuildDarkLibraryAsync(BuildDarkLibraryRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("guider.dark_library.build", idempotencyKey));
    public Task<OperationAcceptedDto> BuildDefectMapDarksAsync(BuildDefectMapDarksRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("guider.defect_map.build", idempotencyKey));
    public Task<CalibrationFilesStatusDto?> GetCalibrationFilesStatusAsync(CancellationToken ct) =>
        Task.FromResult<CalibrationFilesStatusDto?>(null);
    // Faulted Task (not a synchronous throw) so the stub faithfully honors the Task<T>-returning contract — a
    // caller awaiting this sees a faulted task, the same as any other not-connected service path.
    public Task<CalibrationFilesStatusDto> SetDarkLibraryEnabledAsync(bool enabled, CancellationToken ct) =>
        Task.FromException<CalibrationFilesStatusDto>(new InvalidOperationException("guider is not connected"));
    public Task<CalibrationFilesStatusDto> SetDefectMapEnabledAsync(bool enabled, CancellationToken ct) =>
        Task.FromException<CalibrationFilesStatusDto>(new InvalidOperationException("guider is not connected"));
}

public sealed class PlaceholderPolarAlignService : IPolarAlignService {
    // Unlike the device services, polar align status returns a state
    // record (not nullable) — "idle" when no alignment is in progress.
    public Task<PolarAlignStateDto> GetStatusAsync(CancellationToken ct) =>
        Task.FromResult(new PolarAlignStateDto(
            State: "idle",
            CurrentErrorArcmin: null,
            AzimuthAdjustmentArcmin: null,
            AltitudeAdjustmentArcmin: null,
            FramesCaptured: 0,
            LastFrameId: null));
    public Task<OperationAcceptedDto> StartAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("polar-align.start", idempotencyKey));
    public Task<OperationAcceptedDto> StopAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("polar-align.stop", idempotencyKey));
}