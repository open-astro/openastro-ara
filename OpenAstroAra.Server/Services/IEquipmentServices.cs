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

/// <summary>#1127 — a single-instance equipment service that retains its selected device
/// (<c>GetAsync</c> non-null) even while disconnected. The remembered-selection store and this
/// record can diverge (the wizard's store-only <c>DELETE …/remembered</c> keeps the retained
/// device), and the card's Reconnect names the retained one, so the reconnector falls back to it
/// when nothing is remembered. Null when nothing is retained or the service is disposed.</summary>
public interface IRetainedDeviceSource {
    DiscoveredDeviceDto? RetainedDevice { get; }
}

public interface ICameraService {
    Task<CameraDto?> GetAsync(CancellationToken ct);
    // True when no exposure (sequenced, one-off REST, or live-view frame) holds
    // the shared in-flight gate — storage maintenance must not run otherwise.
    bool IsFreeToCapture(object consumer);
    /// <summary>A sequence frame is still on its way to disk (written in the background, or held after
    /// a failed save): storage maintenance must not move the drive under it.</summary>
    bool HasPendingFrameSaves => false;
    /// <summary>Waits for every frame still being saved in the background, retrying a held one once.
    /// Frames that still can't be saved are dropped (logged with their ids); returns how many. A run
    /// calls this before it ends, so its last frames land in its session.</summary>
    Task<int> FinishPendingSavesAsync() => Task.FromResult(0);
    /// <summary>Why a run must not start for want of somewhere to save its frames — the store drive is
    /// not mounted, or the save volume is critically low under an abort/pause policy — in words for the
    /// user; null when frames can be saved (and on a host that cannot tell). The same checks every
    /// frame passes before the shutter opens, asked once before the run instead of failing each
    /// frame of it.</summary>
    string? StoreRefusal() => null;
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>Drop the retained (selected-but-not-connected) device so <c>GetAsync</c> reads null
    /// (404) again — the card's Remove, the single-instance form of the §45 stuck-device escape
    /// hatch. True when a device was dropped, false when none was retained. Throws
    /// InvalidOperationException (→ 409) while Connecting/Connected: disconnect first, so a removal
    /// on live hardware is always an explicit two-step.</summary>
    Task<bool> ForgetAsync(CancellationToken ct);
    Task<ExposureResponseDto> StartExposureAsync(ExposureRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task AbortExposureAsync(CancellationToken ct);
    Task SetCoolerAsync(bool enabled, double? targetTemperatureC, CancellationToken ct);
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

/// <summary>The mount. #2066 — it is the daemon's <see cref="IMountMotionControl"/>: a routine that drives the motors itself
/// (Polar Align) takes that seam, never this interface.</summary>
public interface ITelescopeService : IMountMotionControl {
    /// <summary>#1710 — keep the profile's site out of the mount while a profile switch swaps the devices; dispose when they are done.</summary>
    IDisposable? HoldSiteFollow() => null;

    Task<TelescopeDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>Drop the retained (selected-but-not-connected) device so <c>GetAsync</c> reads null
    /// (404) again — the card's Remove, the single-instance form of the §45 stuck-device escape
    /// hatch. True when a device was dropped, false when none was retained. Throws
    /// InvalidOperationException (→ 409) while Connecting/Connected: disconnect first, so a removal
    /// on live hardware is always an explicit two-step.</summary>
    Task<bool> ForgetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> SlewAsync(SlewRequestDto request, string? idempotencyKey, CancellationToken ct);
    /// <summary>The park that never yields to the mount's motion owner: the safety and fault parks call it (they end Polar
    /// Align through its takeover first). #2066 — the user's park is <see cref="ParkUnlessMotionOwnedAsync"/>.</summary>
    Task<OperationAcceptedDto> ParkAsync(ParkRequestDto request, string? idempotencyKey, CancellationToken ct);

    /// <summary>#2066 (ARA-2) — the user's park (<c>POST /park</c>): as <see cref="ParkAsync"/>, but counted in the motion
    /// dispatch as a REST GoTo is. Refused (<c>polar-align-moving-mount</c>) when a routine took the mount's motion owner
    /// during its checks, and while it is on its way no routine can take the owner.</summary>
    Task<OperationAcceptedDto> ParkUnlessMotionOwnedAsync(ParkRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> UnparkAsync(string? idempotencyKey, CancellationToken ct);
    /// <summary>The user's Find Home (<c>POST /home</c>; nothing else calls it). #2066 (ARA-2) — counted in the motion dispatch
    /// as <see cref="ParkUnlessMotionOwnedAsync"/> is.</summary>
    Task<OperationAcceptedDto> FindHomeAsync(string? idempotencyKey, CancellationToken ct);
    Task SetTrackingAsync(bool enabled, CancellationToken ct);

    /// <summary>#1649 — set the drive rate (an ASCOM DriveRate name). Throws InvalidOperationException
    /// when not connected or when the mount doesn't list the rate.</summary>
    Task SetTrackingRateAsync(string rate, CancellationToken ct);

    /// <summary>Start (rate != 0) or stop (rate 0) constant-rate motion on one mount axis
    /// (0 = primary/RA-Az, 1 = secondary/Dec-Alt). CONTRACT: the underlying device call MUST run
    /// to completion regardless of <paramref name="ct"/> — a cancelled request must never leave the
    /// mount running. Implementations therefore treat the stop as unconditional (the request token
    /// is intentionally not honored); a future implementor wiring <paramref name="ct"/> into the
    /// device call would break that safety invariant. #1649 — a non-zero rate with
    /// <paramref name="leaseMs"/> (clamped to 250–5000) is stopped by the service itself when no
    /// renewal arrives within the lease; null keeps the move running until a stop.</summary>
    Task MoveAxisAsync(int axis, double rate, int? leaseMs, CancellationToken ct);
    Task AbortSlewAsync(CancellationToken ct);

    /// <summary><see cref="AbortSlewAsync(CancellationToken)"/> with the caller named in the abort's log line and its
    /// <c>slew_aborted</c> event (<c>user_request</c> for Stop Mount; <c>polar_align</c> for the routine's own stop).
    /// Like it, the abort reaches the driver even when <paramref name="ct"/> is already cancelled.</summary>
    Task IMountMotionControl.AbortSlewAsync(string reason, CancellationToken ct) => AbortSlewAsync(ct);

    /// <summary>The mount's Slewing read from the driver now, not the 2 s refresh; null when not connected or the read
    /// fails. A routine that stops the mount confirms the stop on it.</summary>
    Task<bool?> ReadSlewingNowAsync(CancellationToken ct) => Task.FromResult<bool?>(null);

    /// <summary>The mount's RA (hours) and Dec (degrees) read from the driver now, not the 2 s refresh, in the
    /// mount's own frame (<see cref="OpenAstroAra.Equipment.Interfaces.Mediator.ITelescopeMediator.GetCurrentPosition"/>'s);
    /// null when not connected or the read fails. Polar Align reads <see cref="IMountMotionControl.ReadNowAsync"/>.</summary>
    Task<(double RaHours, double DecDegrees)?> ReadPointingNowAsync(CancellationToken ct) =>
        Task.FromResult<(double RaHours, double DecDegrees)?>(null);

    /// <summary>#2066 — count a stop in <see cref="IMountMotionControl.AbortEpoch"/> without sending the mount anything: Emergency Stop's first
    /// step, before any of its rungs. Every routine and queued GoTo or Park that read the count before treats it as a stop:
    /// a leased motor renewal is refused (its lease then runs out and stops the axis), and Polar Align fails its run.</summary>
    void FenceMotion(string reason) { }

    /// <summary><see cref="MoveAxisAsync(int, double, int?, CancellationToken)"/>, refused (InvalidOperationException,
    /// nothing reaches the driver) when a non-zero <paramref name="rate"/> would start the motor after an abort that
    /// came since <paramref name="unlessAbortedSince"/> (an <see cref="IMountMotionControl.AbortEpoch"/> reading): a
    /// routine's lease renewal must never restart a motor the user just stopped.</summary>
    Task IMountMotionControl.MoveAxisAsync(int axis, double rate, int? leaseMs, long? unlessAbortedSince, CancellationToken ct) =>
        MoveAxisAsync(axis, rate, leaseMs, ct);

    /// <summary>#2070 — the user says the mount is physically at Home: the position is trusted again.
    /// Throws InvalidOperationException when the mount is not connected or does not report AtHome now.</summary>
    Task ConfirmHomeAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>#2066, #2065 (ARA-10) — a routine that drove the motors marks the mount's position unverified with
    /// <paramref name="reason"/> (<c>stop_unconfirmed</c>): its stop was not confirmed, or the mount reconnected while it
    /// ran. GoTo, Park and polar alignment are then refused, as after a power loss, until a Home search ends at Home
    /// (#2173, for the unconfirmed-stop reason only; <see cref="MountPoseTrust.HomeSearchMayTrust"/>) or <see cref="ConfirmHomeAsync"/>; the flag is kept per device across reconnects and restarts. Never throws.</summary>
    void MarkPositionUnverified(string reason) { }

    /// <summary>#2173 — how many Ara mount commands and axis moves the position trust has been told of: the plate-solve
    /// check reads it before its exposure and trusts nothing when it changed by the trust.</summary>
    long PoseCommandCount => 0;

    /// <summary>#2173 — the connected mount's device id (the one its trust is kept under), or null when not connected.</summary>
    string? PoseDeviceId => null;

    /// <summary>#2173 — a plate solve that moved nothing agreed with the mount's reported position and pier side: the
    /// position is trusted again for every reason, logged, persisted and pushed. Refused (null) when the mount session
    /// is no longer <paramref name="sessionEpoch"/> or an Ara command came since <paramref name="commandCount"/> was read;
    /// otherwise the reason it cleared (empty when it was already trusted). <paramref name="decDeg"/> is the read Dec.</summary>
    string? TrustPositionBySolve(long sessionEpoch, long commandCount, double decDeg) => null;

    /// <summary>#2173 — the user's one-tap Sync after a plate-solve mismatch, to <paramref name="sky"/>: the existing
    /// mediator Sync (the motion owner, the parked mount, the mount clock, the coordinate system and range guards
    /// apply). False when it was refused or failed.</summary>
    Task<bool> SyncForPoseCheckAsync(OpenAstroAra.Astrometry.Coordinates sky, CancellationToken ct) => Task.FromResult(false);
}

public interface IFocuserService {
    Task<FocuserDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>Drop the retained (selected-but-not-connected) device so <c>GetAsync</c> reads null
    /// (404) again — the card's Remove, the single-instance form of the §45 stuck-device escape
    /// hatch. True when a device was dropped, false when none was retained. Throws
    /// InvalidOperationException (→ 409) while Connecting/Connected: disconnect first, so a removal
    /// on live hardware is always an explicit two-step.</summary>
    Task<bool> ForgetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> MoveAsync(FocuserMoveRequestDto request, string? idempotencyKey, CancellationToken ct);
    /// <summary>#1542: stop a focuser move now, the same panic-stop shape as the rotator. Throws
    /// InvalidOperationException when not connected.</summary>
    Task<OperationAcceptedDto> HaltAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>Make where the focuser is now 0; it doesn't move. Throws InvalidOperationException (→ 409)
    /// when not connected, moving or calibrating, or when the driver offers no SetZero.</summary>
    Task<OperationAcceptedDto> SetZeroAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>Start the driver's travel calibration (it runs to both stops); the runtime's
    /// <c>Calibration</c> reads "calibrating" until it ends. Throws as <see cref="SetZeroAsync"/>.</summary>
    Task<OperationAcceptedDto> CalibrateAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>Stop a travel calibration. Throws when not connected or the driver can't calibrate.</summary>
    Task<OperationAcceptedDto> AbortCalibrationAsync(string? idempotencyKey, CancellationToken ct);
}

public interface IFilterWheelService {
    Task<FilterWheelDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>Drop the retained (selected-but-not-connected) device so <c>GetAsync</c> reads null
    /// (404) again — the card's Remove, the single-instance form of the §45 stuck-device escape
    /// hatch. True when a device was dropped, false when none was retained. Throws
    /// InvalidOperationException (→ 409) while Connecting/Connected: disconnect first, so a removal
    /// on live hardware is always an explicit two-step.</summary>
    Task<bool> ForgetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ChangeFilterAsync(FilterChangeRequestDto request, string? idempotencyKey, CancellationToken ct);
}

public interface IRotatorService {
    Task<RotatorDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>Drop the retained (selected-but-not-connected) device so <c>GetAsync</c> reads null
    /// (404) again — the card's Remove, the single-instance form of the §45 stuck-device escape
    /// hatch. True when a device was dropped, false when none was retained. Throws
    /// InvalidOperationException (→ 409) while Connecting/Connected: disconnect first, so a removal
    /// on live hardware is always an explicit two-step.</summary>
    Task<bool> ForgetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> MoveAsync(RotatorMoveRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> SetReverseAsync(RotatorReverseRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> SyncAsync(RotatorSyncRequestDto request, string? idempotencyKey, CancellationToken ct);
    /// <summary>Stop the rotator where it is (ASCOM <c>Halt</c>). Awaited, so the 202 means the
    /// stop reached the driver. Throws InvalidOperationException when not connected, as MoveAsync
    /// does; the endpoint has no typed mapping for it, so it answers 500.</summary>
    Task<OperationAcceptedDto> HaltAsync(string? idempotencyKey, CancellationToken ct);
}

public interface IDomeService {
    Task<DomeDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>#1689 — the profile switch's disconnect: the store already holds the new profile's dome block,
    /// so its "Follow the mount" is left as saved (DisconnectAsync would turn it off).</summary>
    Task<OperationAcceptedDto> DisconnectForProfileSwitchAsync(CancellationToken ct) => DisconnectAsync(null, ct);

    /// <summary>Drop the retained (selected-but-not-connected) device so <c>GetAsync</c> reads null
    /// (404) again — the card's Remove, the single-instance form of the §45 stuck-device escape
    /// hatch. True when a device was dropped, false when none was retained. Throws
    /// InvalidOperationException (→ 409) while Connecting/Connected: disconnect first, so a removal
    /// on live hardware is always an explicit two-step.</summary>
    Task<bool> ForgetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> SlewAsync(DomeSlewRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> ParkAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> OpenShutterAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> CloseShutterAsync(string? idempotencyKey, CancellationToken ct);
    // §25.5.5 — the remaining Alpaca dome motions (caps for all four were already in DomeCapabilitiesDto).
    Task<OperationAcceptedDto> FindHomeAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> AbortSlewAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> SetParkAsync(string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> SyncToAzimuthAsync(DomeSlewRequestDto request, string? idempotencyKey, CancellationToken ct);

    /// <summary>#1689 — turn "the dome follows the mount" on or off; returns the resulting state.
    /// Throws InvalidOperationException (→ 409) with the reason when it cannot be turned on.</summary>
    Task<bool> SetFollowingAsync(bool enabled, CancellationToken ct);
}

public interface ISwitchService {
    // Multi-instance: switches are addressed by their Alpaca UniqueId (the {id} in
    // /api/v1/equipment/switch/{id}) — NOT the AlpacaDeviceNumber, which is only unique within a
    // single Alpaca host. Two hubs on different hosts are both "device 0"; keying by number made
    // the second connect evict the first (#multi-switch collision). GetAllAsync lists every
    // connected/known switch.
    Task<IReadOnlyList<SwitchDto>> GetAllAsync(CancellationToken ct);
    Task<SwitchDto?> GetAsync(string deviceId, CancellationToken ct);

    /// <summary>The discovery record a KNOWN switch was connected from (its Alpaca host, port and
    /// device number), for <c>GET /driver/switch/{id}</c>; null for an unknown id.</summary>
    DiscoveredDeviceDto? GetDevice(string deviceId);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string deviceId, string? idempotencyKey, CancellationToken ct);
    Task SetValueAsync(string deviceId, SwitchValueRequestDto request, CancellationToken ct);

    /// <summary>Drop a NON-connected switch from the known list (the stuck-device escape hatch: a
    /// dead/duplicate switch otherwise stays listed until a daemon restart). True when removed,
    /// false for an unknown id; throws InvalidOperationException (→ 409) for a Connected switch —
    /// disconnect it first so a removal is always an explicit two-step on live hardware.</summary>
    Task<bool> RemoveAsync(string deviceId, CancellationToken ct);

    /// <summary>Reconnect a KNOWN switch by id — the per-card Connect after a Disconnect (or an
    /// Error) — from the endpoint the registry already holds, so the client needs neither a
    /// rediscovery nor the device's host/port (the <c>SwitchDto</c> carries neither). Null for an
    /// unknown id (→ 404: removed, or never connected this daemon session — use <c>/connect</c>).
    /// Idempotent for a switch that is already Connecting/Connected, like <see cref="ConnectAsync"/>.</summary>
    Task<OperationAcceptedDto?> ReconnectAsync(string deviceId, string? idempotencyKey, CancellationToken ct);
}

public interface IObservingConditionsService {
    Task<ObservingConditionsDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>Drop the retained (selected-but-not-connected) device so <c>GetAsync</c> reads null
    /// (404) again — the card's Remove, the single-instance form of the §45 stuck-device escape
    /// hatch. True when a device was dropped, false when none was retained. Throws
    /// InvalidOperationException (→ 409) while Connecting/Connected: disconnect first, so a removal
    /// on live hardware is always an explicit two-step.</summary>
    Task<bool> ForgetAsync(CancellationToken ct);
}

public interface ISafetyMonitorService {
    Task<SafetyMonitorDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>Drop the retained (selected-but-not-connected) device so <c>GetAsync</c> reads null
    /// (404) again — the card's Remove, the single-instance form of the §45 stuck-device escape
    /// hatch. True when a device was dropped, false when none was retained. Throws
    /// InvalidOperationException (→ 409) while Connecting/Connected: disconnect first, so a removal
    /// on live hardware is always an explicit two-step.</summary>
    Task<bool> ForgetAsync(CancellationToken ct);
}

public interface IFlatDeviceService {
    Task<FlatDeviceDto?> GetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>Drop the retained (selected-but-not-connected) device so <c>GetAsync</c> reads null
    /// (404) again — the card's Remove, the single-instance form of the §45 stuck-device escape
    /// hatch. True when a device was dropped, false when none was retained. Throws
    /// InvalidOperationException (→ 409) while Connecting/Connected: disconnect first, so a removal
    /// on live hardware is always an explicit two-step.</summary>
    Task<bool> ForgetAsync(CancellationToken ct);
    Task<OperationAcceptedDto> ApplyFlatPanelAsync(FlatPanelRequestDto request, string? idempotencyKey, CancellationToken ct);
}

public interface IGuiderService {
    Task<GuiderDto?> GetAsync(CancellationToken ct);
    /// <summary>The connected guider's version from its handshake (OpenAstro Guider or PHD2), or null when
    /// it isn't connected or didn't say.</summary>
    string? GuiderVersion => null;
    /// <summary>§36.10 — the guide camera's frame size from the connected guider, or null when the guider or
    /// its camera is not connected (the profile never holds it; polar alignment reads the same RPC).</summary>
    Task<(int Width, int Height)?> TryGetGuideFrameSizeAsync(CancellationToken ct);
    /// <summary>The connected guider's camera pixel size (µm, from its Alpaca driver) and the guider's
    /// pixel scale at 1x binning (″/px), or null when it isn't connected or either is unknown.</summary>
    Task<(double PixelSizeUm, double PixelScaleArcsec)?> TryGetGuideOpticsAsync(CancellationToken ct) =>
        Task.FromResult<(double, double)?>(null);
    /// <summary>The camera the connected guider has open, or null when it isn't connected or has none.</summary>
    Task<string?> TryGetGuideCameraNameAsync(CancellationToken ct) => Task.FromResult<string?>(null);
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
    /// reports the outcome through the guider connection status). Never requires a connected guider — a manual restart is most
    /// useful when the daemon is hung.</summary>
    Task<OperationAcceptedDto> RestartGuiderAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>§63.17 — delete the stored calibration files (dark library and/or defect map); returns the
    /// updated status. Disconnected → InvalidOperationException (409); both flags false → ArgumentException
    /// (400); daemon rejection → GuiderRpcException (422).</summary>
    Task<CalibrationFilesStatusDto> DeleteCalibrationFilesAsync(bool deleteDarkLibrary, bool deleteDefectMap, CancellationToken ct);

    /// <summary>§63.17 — on-demand re-push of the §63.5 engine config + equipment selections to the daemon
    /// (runs to completion before the accept returns, then emits <c>guider.profile_pushed</c>). Throws
    /// InvalidOperationException when disconnected (→ 409, typed). With <paramref name="refuseUnservedSelection"/>
    /// (the explicit <c>POST /guider/profile/push</c>), a bare Alpaca selection with no server throws
    /// <see cref="GuiderSelectionInvalidException"/> before anything is sent (→ 422, #1306); the §63.4
    /// profile-switch follow passes false so the twin ensure and tuning still land on such a profile.</summary>
    Task<OperationAcceptedDto> PushGuiderProfileAsync(string? idempotencyKey, CancellationToken ct, bool refuseUnservedSelection = true);

    /// <summary>Tuning-only push (<c>POST /guider/profile/push?scope=tuning</c>): just the runtime-safe
    /// messages (aggressiveness, minimum move, dec guide mode, guide exposure) — no equipment disconnect, so
    /// guiding continues. Emits <c>guider.profile_pushed</c> with <c>scope: "tuning"</c>. Throws
    /// InvalidOperationException when disconnected (→ 409, typed).</summary>
    Task<OperationAcceptedDto> PushGuiderTuningAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>#1592 — write the profile's multi-star setting to the connected guider (needs its equipment
    /// disconnected for a moment). Throws GuiderBusyException while guiding (409), InvalidOperationException
    /// when disconnected (409, typed), GuiderRpcException when the guider refuses (422).</summary>
    Task ApplyGuiderMultiStarAsync(CancellationToken ct);

    /// <summary>§63.4 — what the daemon holds for a profile's guider twin, for the delete dialog: whether
    /// either twin name exists (null = no guider connected), whether it is the daemon's selected profile,
    /// and whether a delete could run right now (idle guider) or would be queued, with the reason. Never
    /// throws; a daemon fault reads as unknown (<c>Exists</c> null) with the fault's text as the reason.</summary>
    Task<GuiderTwinStatus> DescribeAraGuiderTwinAsync(string? araProfileName, Guid araProfileId, CancellationToken ct);

    /// <summary>§63.4 — delete the guider twin of a just-deleted ARA profile (its display-name twin and the
    /// legacy <c>ara-&lt;slug&gt;-&lt;id8&gt;</c> twin), the dark library with it when
    /// <paramref name="deleteDarkFiles"/>. Runs now when the guider is connected and idle (releasing and
    /// reconnecting the daemon's equipment around the RPC, since the daemon refuses the delete while any
    /// equipment is connected); otherwise — no guider, guider busy, or <paramref name="ct"/> ran out — the
    /// delete is queued and drained on the next connect or the next idle moment. Never throws.</summary>
    Task<GuiderTwinDeleteOutcome> DeleteAraGuiderProfileAsync(string? araProfileName, Guid araProfileId, bool deleteDarkFiles, CancellationToken ct);

    /// <summary>#1156 clone hook — best-effort <c>create_profile</c> + <c>copy_from</c> so a duplicated ARA
    /// profile's twin inherits the source twin's calibration. Never throws; false when no guider is
    /// connected, the names map to the same twin, the source twin is missing or the daemon refused.</summary>
    Task<bool> TryCloneAraGuiderProfileAsync(string? sourceAraProfileName, string? newAraProfileName, CancellationToken ct);

    /// <summary>#1156 rename hook — best-effort <c>rename_profile</c> of the twin (display name, then the
    /// legacy id-suffixed name) so a renamed ARA profile keeps its calibration. Never throws.</summary>
    Task<bool> TryRenameAraGuiderProfileAsync(string? oldAraProfileName, Guid araProfileId, string? newAraProfileName, CancellationToken ct);
}

/// <summary>§63.4 — daemon-side half of the twin preview (see <see cref="Contracts.GuiderTwinPreviewDto"/>).</summary>
public sealed record GuiderTwinStatus(bool? Exists, bool SelectedOnDaemon, bool CanDeleteNow, string? Reason);

/// <summary>§63.4 — how a twin delete ended.</summary>
public enum GuiderTwinDeleteStatus {
    Deleted,
    Queued,
    NotFound,
    Failed,
}

/// <summary>§63.4 — the twin delete's outcome plus a human sentence for Queued / Failed.</summary>
public sealed record GuiderTwinDeleteOutcome(GuiderTwinDeleteStatus Status, string? Reason);

public interface IPolarAlignService {
    /// <summary>A routine is running (any stage, any state short of a terminal one), or a run that just ended is still
    /// stopping the mount (#2048) — the one test Emergency Stop and the start gates need; never a list of state names
    /// that a new stage outgrows.</summary>
    bool IsActive { get; }
    Task<PolarAlignStateDto> GetStatusAsync(CancellationToken ct);
    /// <summary>#2048 — what a start at <paramref name="stage"/> would do, for the confirm step: the RA turn direction
    /// from the mount's current pointing, the step and the most the run turns the RA axis from where it starts. Refuses
    /// like <see cref="StartAsync"/> does before any motion: a mount not connected, a parked mount
    /// (<c>polar-align-mount-parked</c>), no site latitude, a pointing within 3° of the pole (<c>polar-align-on-axis</c>),
    /// turns past the session's sweep limit (<c>polar-align-sweep-limit</c>); #2068: on the first failing check of
    /// <see cref="GetPreflightAsync"/> (the mount's clock, the clearance not confirmed among them). A null stage plans the
    /// fine measurement.</summary>
    Task<PolarAlignStartPlanDto> GetStartPlanAsync(string? stage, CancellationToken ct);
    /// <summary>#2068 (ARA-18) — the pre-flight list for a start at <paramref name="stage"/> (null = fine): every check a
    /// start makes, in its order, with what the start would do. Never refuses: a check that fails is listed as
    /// <c>fail</c>, and <see cref="StartAsync"/> and <see cref="GetStartPlanAsync"/> refuse on the first one. Throws
    /// <see cref="ArgumentException"/> for an unknown stage only.</summary>
    Task<PolarAlignPreflightDto> GetPreflightAsync(string? stage, CancellationToken ct);
    /// <summary>#2166 — "Move to start position": the scope points within 3° of the pole, so move its Dec axis alone (no
    /// GoTo, the counterweights stay down) to 4° from the site's pole, with the guards of every Polar Align turn, then
    /// read the pointing again. Refuses before anything moves (409) with the pre-flight list's cause: a run going
    /// (<c>polar-align-run-active</c>), another motion owner (<c>polar-align-mount-busy</c>), a parked mount, an unverified
    /// position, a clearance not confirmed for the move's reach, an alt-az mount, CanMoveAxis(Secondary) false
    /// (<c>polar-align-cannot-move-dec</c>), or a scope not within 3° of the pole (<c>polar-align-move-to-start-not-needed</c>).
    /// Once it moved, answers with the outcome and the fresh list; Stop (<see cref="StopAsync"/>) ends it. Nothing moves
    /// without the user's <paramref name="confirmation"/> of the offer's numbers as they are now (else
    /// <c>polar-align-move-not-confirmed</c>).</summary>
    Task<PolarAlignMoveToStartResultDto> MoveToStartAsync(PolarAlignMoveToStartConfirmation? confirmation, CancellationToken ct) =>
        throw new InvalidOperationException("this daemon cannot move the mount to the polar-align start position");
    /// <summary>Begin ONE measurement (#2082) at <paramref name="stage"/> (<see cref="PolarAlignStages"/>; null =
    /// fine), from where the mount points now: no stage sends the mount Home or slews it (#2048), and none leads into
    /// another. The mount moves only with a <paramref name="confirmation"/> whose direction is the one the run would
    /// turn now (else the <c>polar-align-unconfirmed</c> Refusal); a parked mount is refused
    /// (<c>polar-align-mount-parked</c>) and never unparked; a pointing within 3° of the pole is refused
    /// (<c>polar-align-on-axis</c>); #2068: it refuses on the first failing check of <see cref="GetPreflightAsync"/>, so no
    /// motion until the mount's clearance is confirmed (<c>polar-align-clearance-not-confirmed</c>). A <c>check</c> while
    /// the routine adjusts ends the loop like Stop once the check's
    /// plan and confirmation hold (the adjust screen's Check). Throws <see cref="ArgumentException"/> for an unknown
    /// stage (the endpoint answers 400 before calling) and <see cref="InvalidOperationException"/> (→ 409) for a
    /// failed preflight.</summary>
    Task<OperationAcceptedDto> StartAsync(string? stage, PolarAlignStartConfirmation? confirmation, string? idempotencyKey, CancellationToken ct);
    /// <summary>Retired (#2082): a Start makes one measurement and never leads into another. Always throws
    /// <see cref="InvalidOperationException"/> (→ 409).</summary>
    Task<OperationAcceptedDto> NextAsync(string? idempotencyKey, CancellationToken ct);
    /// <summary>#1971 — stop the Polar Aligner's motors; the routine carries on by hand. Throws
    /// <see cref="InvalidOperationException"/> (→ 409) when no routine is running with a Polar Aligner.</summary>
    Task<OperationAcceptedDto> StopAlignerAsync(string? idempotencyKey, CancellationToken ct);
    /// <summary>#1971 — after a stop, correct with the motors again (a new probe first). Throws
    /// <see cref="InvalidOperationException"/> (→ 409) when the Polar Aligner is not stopped.</summary>
    Task<OperationAcceptedDto> ResumeAlignerAsync(string? idempotencyKey, CancellationToken ct);
    /// <summary>Abort: same unwind as complete, logged with outcome <c>aborted</c> (§45 step 10 —
    /// the mount stops where it is: AbortSlew and MoveAxis 0 go out before any wait, the stop is
    /// confirmed, and tracking is left off, #2048).</summary>
    Task<OperationAcceptedDto> StopAsync(string? idempotencyKey, CancellationToken ct);
    /// <summary>A sequence run starts over an open routine: the same unwind as <see cref="StopAsync"/>
    /// (state <c>stopped</c>), but no measured result is stored (no last result, no session row).</summary>
    Task<OperationAcceptedDto> StopForSequenceAsync(CancellationToken ct);
    /// <summary>#2066 — Emergency Stop or the safety park takes the mount over (<paramref name="by"/> names it in the log):
    /// the run ends like a Stop (its first stop round goes out before any wait, state <c>stopped</c>), but its exit sends
    /// no stop of its own, does not confirm one and writes no tracking. The caller stops and checks the mount itself, or
    /// parks it. Bounded by the run's unwind, not by the stop's confirmation.</summary>
    Task<OperationAcceptedDto> StopForTakeoverAsync(string by, CancellationToken ct) => StopAsync(null, ct);
    /// <summary>#2066 — a run's motor move is under way, or its stop is not confirmed yet: the telescope endpoints answer
    /// GoTo, pad moves, Home and Park with the <c>polar-align-moving-mount</c> Refusal meanwhile.</summary>
    bool IsMovingMount => false;
    /// <summary>§45 step 9 — the user marks alignment done; the achieved error is logged with
    /// outcome <c>complete</c> (§45.13).</summary>
    Task<OperationAcceptedDto> CompleteAsync(string? idempotencyKey, CancellationToken ct);
    /// <summary>Single-frame mode: take the next adjust frame now. A request made while a frame is
    /// already exposing queues exactly one more frame after it (requests do not stack). Throws
    /// <see cref="InvalidOperationException"/> (→ 409) outside the adjust phase.</summary>
    Task<OperationAcceptedDto> RequestCaptureAsync(string? idempotencyKey, CancellationToken ct);
    /// <summary>The guide camera's latest frame as a JPEG (the polar-align live view), or null
    /// when no guider is connected or the daemon has no frame to serve.</summary>
    Task<byte[]?> GetLiveFrameJpegAsync(CancellationToken ct);
}