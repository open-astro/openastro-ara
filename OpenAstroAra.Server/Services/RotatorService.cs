#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e — fifth real Alpaca-backed device service. Replaces <c>PlaceholderRotatorService</c>:
/// connects to a discovered Alpaca Rotator, serves its live runtime (state + mechanical/sky angle)
/// over the §6 REST surface, and rotates it via <see cref="MoveAsync"/> (a §60.5 202-Accepted
/// background operation). Its lifecycle (generation supersede, §32.4 background-refresh cache,
/// Halt-before-disconnect) is the shared <see cref="DeviceLifecycle{TDevice}"/> (ADR 0011). REST-only — the <c>IRotatorMediator</c>
/// unification is the follow-up.
/// </summary>
public sealed partial class RotatorService : IRotatorService, IRetainedDeviceSource, IRecoverableDevice, IDisposable {

    private static readonly RotatorStateDto IdleRuntime = new("idle", null, null, false);

    private readonly ILogger<RotatorService> _logger;
    private readonly IEquipmentFaultSink? _faults;
    // ADR 0011 — connect, disconnect, forget, refresh, probe, read health and the desired state.
    private readonly DeviceLifecycle<IRotatorDevice> _lifecycle;
    private readonly RotatorDriftWatch _drift = new();
    private Func<DiscoveredDeviceDto, IRotatorDevice> _createDevice = AlpacaRotatorDevice.Create;
    // Guarded by the lifecycle's gate, like the connection state.
    private RotatorStateDto _runtime = IdleRuntime;
    private RotatorCapabilitiesDto? _capabilities;

    public RotatorService(ILogger<RotatorService>? logger = null, EquipmentEventPublisher? events = null,
            IEquipmentFaultSink? faults = null) {
        _logger = logger ?? NullLogger<RotatorService>.Instance;
        _faults = faults;
        _lifecycle = new DeviceLifecycle<IRotatorDevice>(this, DeviceType.Rotator, "rotator",
            d => _createDevice(d), ReadRuntime, ResetSessionLocked,
            DeviceHealthTracker.DefaultThreshold, _logger, events, faults);
    }

    private object _gate => _lifecycle.Gate;

    // #1265 — bench-test knob: re-arms the refresh timer at a shorter cadence. Init-only, never used by the daemon.
    internal TimeSpan RefreshPeriod { init => _lifecycle.RefreshPeriod = value; }

    // Test seam (ADR 0011 rule 4): the adapter the lifecycle builds for a selected device.
    internal Func<DiscoveredDeviceDto, IRotatorDevice> DeviceFactory { init => _createDevice = value; }

    // #1127 — the record the card names. The remembered-selection store and this can diverge
    // (a store-only DELETE /remembered keeps the device here), so the reconnector falls back to it.
    public DiscoveredDeviceDto? RetainedDevice => _lifecycle.RetainedDevice;

    public DesiredConnection? DesiredConnection => _lifecycle.Desired;

    public bool TryRecover(DiscoveredDeviceDto device) => _lifecycle.TryRecover(device);

    public Task<RotatorDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_lifecycle.IsDisposed, this);
            // null ONLY when no device has ever been selected (-> 404). After a disconnect the
            // device is retained and the DTO is returned with Disconnected state + idle runtime
            // ("device selected but not connected"), consistent across all the device services.
            var device = _lifecycle.Device;
            if (device is null) {
                return Task.FromResult<RotatorDto?>(null);
            }
            var state = _lifecycle.State;
            var runtime = state == EquipmentConnectionState.Connected ? _runtime : IdleRuntime;
            var caps = state == EquipmentConnectionState.Connected ? _capabilities : null;
            return Task.FromResult<RotatorDto?>(
                new RotatorDto(device.UniqueId, device.Name, state, caps, runtime));
        }
    }

    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Device);
        return _lifecycle.ConnectAsync(request.Device, idempotencyKey);
    }

    public Task<bool> ForgetAsync(CancellationToken ct) => _lifecycle.ForgetAsync();

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) =>
        _lifecycle.DisconnectAsync(idempotencyKey);

    // Caller holds the gate: a fresh, ended or tripped session forgets the last one's reads.
    private void ResetSessionLocked() {
        _runtime = IdleRuntime; // don't serve a prior device's runtime
        _capabilities = null;
        _drift.Reset();         // a prior session's commanded angle means nothing here
    }

    public Task<OperationAcceptedDto> MoveAsync(RotatorMoveRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        // Dispose check first (a disposed service can't be operated regardless of the argument),
        // then argument range, then connection state — aligned with the other device services.
        var client = _lifecycle.ConnectedDevice();
        if (IsAngleOutOfRange(request.TargetAngleDeg)) {
            throw new ArgumentOutOfRangeException(nameof(request), request.TargetAngleDeg,
                "TargetAngleDeg must be in [0, 360).");
        }
        if (client is null) {
            throw new InvalidOperationException("rotator is not connected");
        }
        var useSkyAngle = request.UseSkyAngle;
        // ASCOM rotator Move APIs take a float; the cast from the DTO's double loses ~1e-5° at most,
        // negligible for a rotator angle.
        var target = (float)request.TargetAngleDeg;
        _ = Task.Run(() => MoveInBackground(client, target, useSkyAngle), CancellationToken.None);
        return Task.FromResult(Accepted("rotator.move", idempotencyKey));
    }

    public Task<OperationAcceptedDto> SetReverseAsync(RotatorReverseRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        var client = _lifecycle.ConnectedDevice();
        if (client is null) {
            throw new InvalidOperationException("rotator is not connected");
        }
        var reverse = request.Reverse;
        _ = Task.Run(() => SetReverseInBackground(client, reverse), CancellationToken.None);
        return Task.FromResult(Accepted("rotator.reverse", idempotencyKey));
    }

    public Task<OperationAcceptedDto> SyncAsync(RotatorSyncRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        var client = _lifecycle.ConnectedDevice();
        // Range first, then connection — matches MoveAsync's established order (and
        // the test that exercises range validation via the disconnected path).
        if (IsAngleOutOfRange(request.SkyAngleDeg)) {
            throw new ArgumentOutOfRangeException(nameof(request), request.SkyAngleDeg,
                "SkyAngleDeg must be in [0, 360).");
        }
        if (client is null) {
            throw new InvalidOperationException("rotator is not connected");
        }
        var angle = (float)request.SkyAngleDeg;
        _ = Task.Run(() => SyncInBackground(client, angle), CancellationToken.None);
        return Task.FromResult(Accepted("rotator.sync", idempotencyKey));
    }

    // §57 panic-stop shape (as DomeService.AbortSlewAsync): send Halt NOW and await it, so the 202
    // means the stop reached the driver. CancellationToken.None: Task.Run with an already-cancelled
    // token never runs the lambda, so an HTTP timeout would silently skip the stop.
    public async Task<OperationAcceptedDto> HaltAsync(string? idempotencyKey, CancellationToken ct) {
        var client = _lifecycle.ConnectedDevice();
        if (client is null) {
            throw new InvalidOperationException("rotator is not connected");
        }
        await Task.Run(() => client.Halt(), CancellationToken.None).ConfigureAwait(false);
        // Stopped short of the move's target: the drift watch must not re-issue that move.
        ResetDriftIfLive(client);
        _lifecycle.Refresh();
        return Accepted("rotator.halt", idempotencyKey);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background reverse boundary: the blocking ASCOM Reverse setter can throw arbitrary driver/HTTP exceptions, and a concurrent Disconnect/Dispose can dispose the captured client; any escape must be contained and logged, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    private void SetReverseInBackground(IRotatorDevice client, bool reverse) {
        try {
            client.Reverse = reverse;
            ResetDriftIfLive(client); // reverse remaps the angle domains — the old expectation is stale
            _lifecycle.Refresh();
        } catch (Exception ex) {
            LogReverseFailed(ex, reverse);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background sync boundary: the blocking ASCOM Sync can throw arbitrary driver/HTTP exceptions, and a concurrent Disconnect/Dispose can dispose the captured client; any escape must be contained and logged, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    private void SyncInBackground(IRotatorDevice client, float angle) {
        try {
            client.Sync(angle);
            ResetDriftIfLive(client); // sync redefines the sky-angle frame — the old expectation is stale
            _lifecycle.Refresh();
        } catch (Exception ex) {
            LogSyncFailed(ex, angle);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background move boundary: the blocking ASCOM MoveAbsolute/MoveMechanical can throw arbitrary driver/HTTP exceptions, and a concurrent Disconnect/Dispose can dispose the captured client before/during the move; any escape must be contained and logged, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    private void MoveInBackground(IRotatorDevice client, float target, bool useSkyAngle) {
        try {
            // UseSkyAngle moves to the offset-corrected sky angle (Position); otherwise to the raw
            // mechanical angle (MechanicalPosition).
            if (useSkyAngle) {
                client.MoveAbsolute(target);
            } else {
                client.MoveMechanical(target);
            }
            RecordMoveIfLive(client, target, mechanical: !useSkyAngle);
            _lifecycle.Refresh();
        } catch (Exception ex) {
            ResetDriftIfLive(client); // the move failed — where the rotator sits now is unknown
            LogMoveFailed(ex, target);
        }
    }

    // The runtime read the lifecycle runs each tick. Position is the essential read: a streak of
    // failures is a degraded fault (§42.3 second channel). The drift check commits under the same
    // gate as the liveness check (the #789 one-critical-section lesson) and acts off-lock.
    private DeviceRead ReadRuntime(IRotatorDevice client) {
        var runtime = ReadRuntime(client, out var readError);
        bool needCaps;
        lock (_gate) {
            needCaps = _capabilities is null;
        }
        var caps = needCaps ? ReadCapabilities(client) : null; // read once per session; they don't change
        var verdict = ReadbackVerdict.Idle;
        (float TargetDeg, bool Mechanical)? commanded = null;
        string? deviceName = null;
        return new DeviceRead(readError,
            CommitLocked: () => {
                _runtime = runtime;
                _capabilities ??= caps;
                // §42.2 item 4 — angle-drift check against the last commanded move.
                verdict = _drift.Observe(runtime.MechanicalAngleDeg, runtime.SkyAngleDeg,
                    isMoving: runtime.State == "moving",
                    RotatorDriftWatch.DefaultToleranceDeg, DateTimeOffset.UtcNow);
                if (verdict is ReadbackVerdict.Recommand or ReadbackVerdict.Mismatch) {
                    commanded = _drift.Commanded;
                    deviceName = _lifecycle.Device?.Name;
                }
            },
            AfterCommit: () => {
                if (commanded is not { } move) {
                    return;
                }
                var domain = move.Mechanical ? "mechanical" : "sky";
                if (verdict == ReadbackVerdict.Recommand) {
                    RecommandMoveQuietly(client, move.TargetDeg, move.Mechanical);
                } else {
                    var reported = move.Mechanical ? runtime.MechanicalAngleDeg : runtime.SkyAngleDeg;
                    LogAngleDrift(deviceName ?? "?", domain, reported ?? double.NaN, move.TargetDeg);
                    PublishOpFault(client, EquipmentFaultKind.ValueMismatch,
                        $"rotator {domain} angle reads {reported:0.##}°, commanded {move.TargetDeg:0.##}° " +
                        $"(tolerance ±{RotatorDriftWatch.DefaultToleranceDeg:0.##}°) — persists after one re-command");
                }
            });
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported/transiently-failing rotator property throws; that field is reported null rather than failing the whole runtime read. CA1031's log-and-recover boundary applies.")]
    private static RotatorStateDto ReadRuntime(IRotatorDevice c, out string? readError) {
        bool moving;
        readError = null;
        try { moving = c.IsMoving; } catch (Exception) { moving = false; }
        double? mechanical;
        try { mechanical = c.MechanicalPosition; } catch (Exception) { mechanical = null; }
        double? sky;
        // Position is the essential read: a streak of failures is a degraded fault (§42.3 second channel).
        try { sky = c.Position; }
        catch (ASCOM.NotImplementedException) { sky = null; } // not a fault: the driver has no Position
        catch (Exception ex) { sky = null; readError = ex.Message; }
        bool reverse;
        try { reverse = c.Reverse; } catch (Exception) { reverse = false; }
        return new RotatorStateDto(moving ? "moving" : "idle", mechanical, sky, reverse);
    }

    // Capabilities are read once per session (they don't change while connected): whether the
    // rotator supports the Reverse property and its minimum step size in degrees. Per-field
    // tolerant — an unsupported property yields a safe default.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field capability read: an unsupported rotator property throws; that field falls back to a safe default rather than failing the connect. CA1031's log-and-recover boundary applies.")]
    private static RotatorCapabilitiesDto ReadCapabilities(IRotatorDevice c) {
        bool canReverse;
        try { canReverse = c.CanReverse; } catch (Exception) { canReverse = false; }
        double stepSize;
        try { stepSize = c.StepSize; } catch (Exception) { stepSize = 0; }
        return new RotatorCapabilitiesDto(canReverse, stepSize);
    }

    // §42.2 item 4 — the drift watch may only track (or forget) an expectation for the LIVE
    // client: the move calls run off the gate, so a concurrent disconnect/reconnect can have
    // swapped the client while a stale move's bookkeeping was still in flight.
    private void RecordMoveIfLive(IRotatorDevice client, float targetDeg, bool mechanical) {
        lock (_gate) {
            if (_lifecycle.IsCurrent(client)) {
                _drift.Command(targetDeg, mechanical, DateTimeOffset.UtcNow);
            }
        }
    }

    private void ResetDriftIfLive(IRotatorDevice client) {
        lock (_gate) {
            if (_lifecycle.IsCurrent(client)) {
                _drift.Reset();
            }
        }
    }

    // §42.2 — one best-effort re-issue of the same move before the drift fault fires.
    // Deliberately a raw device call (not the REST/mediator move paths, whose Command()
    // recording would restart the episode and erase the one-recommand budget); the watch
    // already re-armed its settle window.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort recovery step in a fire-and-forget task: the re-issued ASCOM move can throw arbitrary driver/HTTP exceptions; a failure is logged and the drift episode simply proceeds to its fault. CA1031's log-and-recover boundary applies.")]
    private void RecommandMoveQuietly(IRotatorDevice client, float targetDeg, bool mechanical) {
        LogRecommandMove(targetDeg, mechanical ? "mechanical" : "sky");
        _ = Task.Run(() => {
            try {
                if (mechanical) {
                    client.MoveMechanical(targetDeg);
                } else {
                    client.MoveAbsolute(targetDeg);
                }
            } catch (Exception ex) {
                LogRecommandMoveFailed(ex, targetDeg);
            }
        }, CancellationToken.None);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Rotator drifted off its commanded {Domain} angle — re-issuing move to {Target}° once before faulting (§42.2)")]
    private partial void LogRecommandMove(float target, string domain);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotator re-command to {Target}° failed")]
    private partial void LogRecommandMoveFailed(Exception ex, float target);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotator '{Device}' {Domain} angle reads {Reported}°, commanded {Target}° — §42.2 drift fault")]
    private partial void LogAngleDrift(string device, string domain, double reported, float target);

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    // Extracted (internal) for direct unit testing. ASCOM rotator angles are degrees in [0, 360).
    internal static bool IsAngleOutOfRange(double angleDeg) => angleDeg < 0 || angleDeg >= 360;

    public void Dispose() {
        _lifecycle.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotator move to {Target} failed")]
    private partial void LogMoveFailed(Exception ex, float target);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotator set-reverse to {Reverse} failed")]
    private partial void LogReverseFailed(Exception ex, bool reverse);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotator sync to {Angle} failed")]
    private partial void LogSyncFailed(Exception ex, float angle);
}
