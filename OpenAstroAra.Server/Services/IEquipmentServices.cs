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
    // True when no exposure (sequenced, one-off REST, or live-view frame) holds
    // the shared in-flight gate — storage maintenance must not run otherwise.
    bool IsFreeToCapture(object consumer);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);
    Task<ExposureResponseDto> StartExposureAsync(ExposureRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task AbortExposureAsync(CancellationToken ct);
    Task SetCoolerAsync(bool enabled, double? targetTemperatureC, CancellationToken ct);
    /// <summary>Vendor cooling-fan read (bridge <c>/fan</c> extension): current
    /// speed + max, or null when the camera/bridge has no fan support.</summary>
    Task<CameraFanDto?> GetFanAsync(CancellationToken ct);
    /// <summary>Vendor cooling-fan control: 0 = off, [1, max] = speed.</summary>
    Task SetFanAsync(int fanSpeed, CancellationToken ct);
    // §25.5.5 — select a readout mode by index into CameraCapabilitiesDto.ReadoutModes.
    Task SetReadoutModeAsync(int modeIndex, CancellationToken ct);
    // §64 Live View: a short-exposure render loop for framing/focus (no catalog write).
    Task StartLiveViewAsync(LiveViewStartRequestDto request, CancellationToken ct);
    // No CancellationToken by design: a stop is unconditional — it always runs to completion (it
    // awaits the loop draining, up to the exposure cap). Omitting the param makes that explicit at
    // every call site rather than handing callers a token that's silently ignored.
    Task StopLiveViewAsync();
    LiveViewStatusDto GetLiveViewStatus();
    // ReadOnlyMemory (not byte[]): the published buffer is shared across readers and must not be
    // mutated; the read-only view makes that explicit without a per-fetch defensive copy.
    (ReadOnlyMemory<byte> Jpeg, long Seq, long SessionId)? GetLiveViewFrame();
}

public interface ITelescopeService {
    Task<TelescopeDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> SlewAsync(SlewRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> ParkAsync(ParkRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> UnparkAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> FindHomeAsync(string? idempotencyKey, CancellationToken ct);
    Task SetTrackingAsync(bool enabled, CancellationToken ct);

    /// <summary>Start (rate != 0) or stop (rate 0) constant-rate motion on one mount axis
    /// (0 = primary/RA-Az, 1 = secondary/Dec-Alt). CONTRACT: the underlying device call MUST run
    /// to completion regardless of <paramref name="ct"/> — a cancelled request must never leave the
    /// mount running. Implementations therefore treat the stop as unconditional (the request token
    /// is intentionally not honored); a future implementor wiring <paramref name="ct"/> into the
    /// device call would break that safety invariant.</summary>
    Task MoveAxisAsync(int axis, double rate, CancellationToken ct);
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
    // §25.5.5 — the remaining Alpaca dome motions (caps for all four were already in DomeCapabilitiesDto).
    Task<OperationAcceptedDto> FindHomeAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> AbortSlewAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> SetParkAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> SyncToAzimuthAsync(DomeSlewRequestDto request, string? idempotencyKey, CancellationToken ct);
}

public interface ISwitchService {
    // Multi-instance: switches are addressed by their Alpaca UniqueId (the {id} in
    // /api/v1/equipment/switch/{id}) — NOT the AlpacaDeviceNumber, which is only unique within a
    // single Alpaca host. Two hubs on different hosts are both "device 0"; keying by number made
    // the second connect evict the first (#multi-switch collision). GetAllAsync lists every
    // connected/known switch.
    Task<IReadOnlyList<SwitchDto>> GetAllAsync(CancellationToken ct);
    Task<SwitchDto?> GetAsync(string deviceId, CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string deviceId, string? idempotencyKey, CancellationToken ct);
    Task SetValueAsync(string deviceId, SwitchValueRequestDto request, CancellationToken ct);

    /// <summary>Drop a NON-connected switch from the known list (the stuck-device escape hatch: a
    /// dead/duplicate switch otherwise stays listed until a daemon restart). True when removed,
    /// false for an unknown id; throws InvalidOperationException (→ 409) for a Connected switch —
    /// disconnect it first so a removal is always an explicit two-step on live hardware.</summary>
    Task<bool> RemoveAsync(string deviceId, CancellationToken ct);
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

    /// <summary>§63.17 — read the daemon's per-slot equipment choices (camera / mount / aux-mount / AO /
    /// rotator device names). Returns null when no guider is connected.</summary>
    Task<GuiderEquipmentChoicesDto?> GetEquipmentChoicesAsync(CancellationToken ct);

    /// <summary>§63.20 — read a camera's sensor pixel size (µm) from its Alpaca driver via the daemon.
    /// Null when no guider is connected OR the camera is unreachable / reports no usable size (best-effort
    /// picker assist). Omitted params fall back to the daemon profile's stored Alpaca camera.</summary>
    Task<GuiderCameraPixelSizeResponseDto> GetAlpacaCameraPixelSizeAsync(string? host, int? port, int? deviceNumber, CancellationToken ct);

    /// <summary>§63.17 — daemon-side Alpaca network discovery (blocking for roughly queries × timeout).
    /// Throws ArgumentOutOfRangeException on out-of-range parameters (→ 400), InvalidOperationException when
    /// disconnected (→ 409), and GuiderRpcException when the daemon rejects the sweep (→ 422, e.g. a build
    /// without Alpaca support).</summary>
    Task<GuiderAlpacaDiscoveryDto> DiscoverAlpacaServersAsync(DiscoverAlpacaServersRequestDto request, CancellationToken ct);

    /// <summary>§63.17 — request a systemd restart of the guider unit (fire-and-forget 202; §63.3 recovery
    /// reports the outcome over guider.state). Never requires a connected guider — a manual restart is most
    /// useful when the daemon is hung.</summary>
    Task<OperationAcceptedDto> RestartGuiderAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>§63.17 — delete the stored calibration files (dark library and/or defect map); returns the
    /// updated status. Disconnected → InvalidOperationException (409); both flags false → ArgumentException
    /// (400); daemon rejection → GuiderRpcException (422).</summary>
    Task<CalibrationFilesStatusDto> DeleteCalibrationFilesAsync(bool deleteDarkLibrary, bool deleteDefectMap, CancellationToken ct);

    /// <summary>§63.17 — on-demand re-push of the §63.5 engine config + equipment selections to the daemon
    /// (runs to completion before the accept returns, then emits <c>guider.profile_pushed</c>). Throws
    /// InvalidOperationException when disconnected (→ 409, typed).</summary>
    Task<OperationAcceptedDto> PushGuiderProfileAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>§63.4 delete hook — best-effort removal of the PHD2 profile mapped to a just-deleted ARA
    /// profile (its <c>ara-&lt;slug&gt;-&lt;id8&gt;</c> twin, dark files included). Never throws: returns
    /// true when the daemon accepted the delete, false when no guider is connected, the profile wasn't
    /// there, or the RPC failed (all logged) — a failed cleanup must never fail the ARA profile delete.</summary>
    Task<bool> TryDeleteAraGuiderProfileAsync(string? araProfileName, Guid araProfileId, CancellationToken ct);
}

public interface IPolarAlignService {
    Task<PolarAlignStateDto> GetStatusAsync(CancellationToken ct);
    Task<OperationAcceptedDto> StartAsync(string? idempotencyKey, CancellationToken ct);
    /// <summary>Abort: same unwind as complete, logged with outcome <c>aborted</c> (§45 step 10 —
    /// the mount stays put).</summary>
    Task<OperationAcceptedDto> StopAsync(string? idempotencyKey, CancellationToken ct);
    /// <summary>§45 step 9 — the user marks alignment done; the achieved error is logged with
    /// outcome <c>complete</c> (§45.13).</summary>
    Task<OperationAcceptedDto> CompleteAsync(string? idempotencyKey, CancellationToken ct);
}