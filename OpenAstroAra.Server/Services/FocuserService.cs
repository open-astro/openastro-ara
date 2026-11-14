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
/// §14e — fourth real Alpaca-backed device service: connects to a discovered Alpaca Focuser, serves its
/// capabilities + live runtime (position/moving/temperature/temp-comp) over the §6 REST surface, and
/// moves it via <see cref="MoveAsync"/> (a §60.5 202-Accepted background operation).
///
/// Its lifecycle (generation supersede, §32.4 background-refresh cache, Halt-before-disconnect, the §42.3
/// probe and read health) is the shared <see cref="DeviceLifecycle{TDevice}"/> (ADR 0011). Capabilities are
/// static, so they are read once (lazily) and cached separately from the runtime.
/// REST-only for now — the <c>IFocuserMediator</c> unification (MoveFocuser/autofocus
/// instructions) is the follow-up.
/// </summary>
public sealed partial class FocuserService : IFocuserService, IRetainedDeviceSource, IRecoverableDevice, IDisposable {

    private static readonly FocuserStateDto IdleRuntime = new("idle", null, null, false);

    // Driver actions (AlpacaBridge lists them in SupportedActions) for what ASCOM's focuser has no member for:
    // make the position now 0, and the Celestron focus motor's run to both stops to find its travel.
    internal const string SetZeroAction = "SetZero";
    internal const string CalibrateAction = "Calibrate";
    internal const string AbortCalibrationAction = "AbortCalibration";
    internal const string CalibrationStatusAction = "CalibrationStatus";

    private readonly ILogger<FocuserService> _logger;
    private readonly IEquipmentFaultSink? _faults;
    private readonly IProfileStore? _profiles;
    // ADR 0011 — connect, disconnect, forget, refresh, probe, read health and the desired state.
    private readonly DeviceLifecycle<IFocuserDevice> _lifecycle;
    private Func<DiscoveredDeviceDto, IFocuserDevice> _createDevice = AlpacaFocuserDevice.Create;
    // Guarded by the lifecycle's gate, like the connection state.
    private FocuserCapabilitiesDto? _capabilities;
    private FocuserStateDto _runtime = IdleRuntime;
    // #1541 — direction (+1 out / -1 in) of the last move, 0 until one is made on this connection; and whether a
    // compensated move is in flight (reported as `backlash_compensating` until a refresh reads IsMoving false). Both under _gate.
    private int _lastDirection;
    // Sum of the backlash steps added so far (absolute focusers): hardware position = reported position + this.
    private int _backlashOffset;
    private bool _compensating;
    // Odd while a compensated Move call is in flight: only a read that starts and commits on one even value may clear the flag.
    private int _moveSequence;

    public FocuserService(ILogger<FocuserService>? logger = null, EquipmentEventPublisher? events = null,
            IEquipmentFaultSink? faults = null, IProfileStore? profiles = null) {
        _logger = logger ?? NullLogger<FocuserService>.Instance;
        _faults = faults;
        _profiles = profiles;
        _lifecycle = new DeviceLifecycle<IFocuserDevice>(this, DeviceType.Focuser, "focuser",
            d => _createDevice(d), ReadRuntime, ResetSessionLocked,
            DeviceHealthTracker.DefaultThreshold, _logger, events, faults);
    }

    private object _gate => _lifecycle.Gate;

    // #1265 — bench-test knob: re-arms the refresh timer at a shorter cadence. Init-only, never used by the daemon.
    internal TimeSpan RefreshPeriod { init => _lifecycle.RefreshPeriod = value; }

    // Test seam (ADR 0011 rule 4): the adapter the lifecycle builds for a selected device.
    internal Func<DiscoveredDeviceDto, IFocuserDevice> DeviceFactory { init => _createDevice = value; }

    // #1127 — the record the card names. The remembered-selection store and this can diverge
    // (a store-only DELETE /remembered keeps the device here), so the reconnector falls back to it.
    public DiscoveredDeviceDto? RetainedDevice => _lifecycle.RetainedDevice;

    public DesiredConnection? DesiredConnection => _lifecycle.Desired;

    // #1246 — true once a refresh tick since the current connect read the essential read cleanly.
    internal bool HasCleanReadSinceConnect => _lifecycle.HasCleanReadSinceConnect;

    public bool TryRecover(DiscoveredDeviceDto device) => _lifecycle.TryRecover(device);

    public Task<FocuserDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_lifecycle.IsDisposed, this);
            var device = _lifecycle.Device;
            if (device is null) {
                return Task.FromResult<FocuserDto?>(null);
            }
            var state = _lifecycle.State;
            var connected = state == EquipmentConnectionState.Connected;
            var runtime = connected ? _runtime : IdleRuntime;
            var caps = connected ? _capabilities : null;
            return Task.FromResult<FocuserDto?>(new FocuserDto(device.UniqueId, device.Name, state, caps, runtime));
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
        _capabilities = null;   // re-read for the new device
        _lastDirection = 0;
        _backlashOffset = 0;
        _compensating = false;
        _runtime = IdleRuntime; // don't serve a prior device's runtime
    }

    // The rotator's panic-stop shape: await Halt so the 202 means the stop reached the driver.
    // CancellationToken.None: an already-cancelled token would skip the stop.
    public async Task<OperationAcceptedDto> HaltAsync(string? idempotencyKey, CancellationToken ct) {
        var client = _lifecycle.ConnectedDevice();
        if (client is null) {
            throw new InvalidOperationException("focuser is not connected");
        }
        await Task.Run(() => client.Halt(), CancellationToken.None).ConfigureAwait(false);
        return Accepted("focuser.halt", idempotencyKey);
    }

    public Task<OperationAcceptedDto> SetZeroAsync(string? idempotencyKey, CancellationToken ct) =>
        RunActionAsync(c => c.CanSetZero, SetZeroAction, "focuser.set_zero", idempotencyKey);

    public Task<OperationAcceptedDto> CalibrateAsync(string? idempotencyKey, CancellationToken ct) =>
        RunActionAsync(c => c.CanCalibrate, CalibrateAction, "focuser.calibrate", idempotencyKey);

    public Task<OperationAcceptedDto> AbortCalibrationAsync(string? idempotencyKey, CancellationToken ct) =>
        RunActionAsync(c => c.CanCalibrate, AbortCalibrationAction, "focuser.calibrate_abort", idempotencyKey);

    // One driver action the capabilities say it offers; a zero or a calibration waits for the focuser to be still.
    private async Task<OperationAcceptedDto> RunActionAsync(Func<FocuserCapabilitiesDto, bool> offered, string action,
            string operationType, string? idempotencyKey) {
        IFocuserDevice? client;
        FocuserCapabilitiesDto? caps;
        FocuserStateDto runtime;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_lifecycle.IsDisposed, this);
            client = _lifecycle.ConnectedDevice();
            caps = _capabilities;
            runtime = _runtime;
        }
        if (client is null) {
            throw new InvalidOperationException("focuser is not connected");
        }
        if (caps is null || !offered(caps)) {
            throw new InvalidOperationException($"the focuser's driver does not offer {action}");
        }
        if (action != AbortCalibrationAction
                && (runtime.State is "moving" or "backlash_compensating" || runtime.Calibration == "calibrating")) {
            throw new InvalidOperationException("focuser is moving");
        }
        await Task.Run(() => client.Action(action, string.Empty), CancellationToken.None).ConfigureAwait(false);
        if (action == SetZeroAction) {
            // The hardware's count starts again at 0: no backlash steps are in it any more.
            lock (_gate) { _backlashOffset = 0; }
        }
        _lifecycle.Refresh();
        return Accepted(operationType, idempotencyKey);
    }

    public Task<OperationAcceptedDto> MoveAsync(FocuserMoveRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        IFocuserDevice? client;
        FocuserCapabilitiesDto? caps;
        lock (_gate) {
            client = _lifecycle.ConnectedDevice();
            caps = _capabilities;
        }
        if (client is null) {
            throw new InvalidOperationException("focuser is not connected");
        }
        // Validate the target against known capabilities when available; the device validates the
        // bound itself otherwise (ASCOM Move throws InvalidValue out of range).
        if (IsTargetOutOfRange(caps, request.TargetPosition)) {
            throw new ArgumentOutOfRangeException(nameof(request), request.TargetPosition,
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"TargetPosition is out of range ({caps!.MinPosition}..{caps.MaxPosition})."));
        }
        // 202 contract: the move runs in the background; GetAsync reports State="moving" until the
        // device settles (the refresh cache picks up IsMoving / Position).
        var useTempComp = request.UseTempComp;
        _ = Task.Run(() => MoveInBackground(client, request.TargetPosition, useTempComp), CancellationToken.None);
        return Task.FromResult(Accepted("focuser.move", idempotencyKey));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background move boundary: the blocking ASCOM Move / TempComp set can throw arbitrary driver/HTTP exceptions; any escape must be contained and logged, never fault the fire-and-forget task or the daemon. This also covers the dispose-ordering edge — a concurrent DisconnectAsync/Dispose can dispose the captured client before (or during) Move(), so the call throws ObjectDisposed/HTTP here; caught and logged. CA1031's log-and-recover boundary applies.")]
    private void MoveInBackground(IFocuserDevice client, int target, bool? useTempComp) {
        try {
            if (useTempComp is bool tc) {
                // Best-effort: temperature-compensation behaviour during a manual move is
                // driver-dependent; honour an explicit request where the driver allows it.
                try { client.TempComp = tc; } catch (Exception ex) { LogTempCompIgnored(ex); }
            }
            MoveCompensated(client, target);
            _lifecycle.Refresh(); // surface IsMoving/Position promptly
        } catch (Exception ex) {
            LogMoveFailed(ex, target);
        }
    }

    // The runtime read the lifecycle runs each tick; capabilities are read here too until they are known.
    private DeviceRead ReadRuntime(IFocuserDevice client) {
        FocuserCapabilitiesDto? knownCaps;
        int sequence;
        lock (_gate) {
            knownCaps = _capabilities;
            sequence = _moveSequence;
        }
        var runtime = ReadRuntime(client, out var readError);
        var caps = knownCaps is null ? ReadCapabilities(client) : null;
        if ((knownCaps ?? caps)?.CanCalibrate == true) {
            runtime = runtime with { Calibration = ReadCalibration(client) };
        }
        // A relative focuser may have no Position to read (NotImplemented is already excluded; some
        // drivers answer with a plain error instead): only an absolute focuser's Position is essential.
        var essential = (knownCaps?.AbsoluteFocuser ?? true) ? readError : null;
        return new DeviceRead(essential, CommitLocked: () => {
            if (runtime.State != "moving" && sequence == _moveSequence && sequence % 2 == 0) {
                _compensating = false; // the compensated move has settled (this read started after its dispatch)
            }
            _runtime = FocuserBacklash.Report(runtime with { Position = runtime.Position - _backlashOffset }, _compensating);
            if (caps is not null) {
                _capabilities = caps;
            }
        });
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported/transiently-failing focuser property (e.g. Temperature on a focuser with no probe) throws; that field is reported absent/default rather than failing the whole runtime read. CA1031's log-and-recover boundary applies.")]
    private static FocuserStateDto ReadRuntime(IFocuserDevice c, out string? readError) {
        bool moving;
        readError = null;
        try { moving = c.IsMoving; } catch (Exception) { moving = false; }
        int? position;
        // Position is the essential read: a streak of failures is a degraded fault (§42.3 second channel).
        try { position = c.Position; }
        catch (ASCOM.NotImplementedException) { position = null; } // not a fault: the driver has no Position
        catch (Exception ex) { position = null; readError = ex.Message; }
        double? temperature;
        try { temperature = c.Temperature; } catch (Exception) { temperature = null; }
        bool tempComp;
        try { tempComp = c.TempComp; } catch (Exception) { tempComp = false; }
        return new FocuserStateDto(moving ? "moving" : "idle", position, temperature, tempComp);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported capability property throws; a non-essential field falls back to a default, while a failed MaxStep returns null so capabilities are not cached with a bogus zero range. CA1031's log-and-recover boundary applies.")]
    private static FocuserCapabilitiesDto? ReadCapabilities(IFocuserDevice c) {
        int maxStep;
        // MaxStep is essential — if it fails, return null so we DON'T cache a [0,0] range (which
        // would permanently reject every Move). _lifecycle.Refresh leaves _capabilities null and the
        // next tick retries; MoveAsync device-validates the bound until then.
        try { maxStep = c.MaxStep; } catch (Exception) { return null; }
        double stepSize;
        try { stepSize = c.StepSize; } catch (Exception) { stepSize = 0; }
        bool canTempComp;
        try { canTempComp = c.TempCompAvailable; } catch (Exception) { canTempComp = false; }
        bool absolute;
        try { absolute = c.Absolute; } catch (Exception) { absolute = false; }
        // Absolute focusers take an absolute target in [0, MaxStep]; relative focusers take a
        // signed offset in [-MaxStep, MaxStep] (negative = backward). MinPosition reflects that so
        // MoveAsync's range check accepts backward moves on a relative focuser.
        var actions = ReadActions(c);
        bool Lists(string action) => actions.Any(a => string.Equals(a, action, StringComparison.OrdinalIgnoreCase));
        return new FocuserCapabilitiesDto(
            MinPosition: absolute ? 0 : -maxStep,
            MaxPosition: maxStep, StepSizeUm: stepSize,
            CanTempComp: canTempComp, AbsoluteFocuser: absolute,
            // A relative focuser has no position to zero.
            CanSetZero: absolute && Lists(SetZeroAction),
            CanCalibrate: Lists(CalibrateAction) && Lists(CalibrationStatusAction));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a failed CalibrationStatus is an unknown calibration, not a fault.")]
    private static string? ReadCalibration(IFocuserDevice c) {
        try {
            return c.Action(CalibrationStatusAction, string.Empty) switch {
                var s when s.Equals("Calibrating", StringComparison.OrdinalIgnoreCase) => "calibrating",
                var s when s.Equals("Calibrated", StringComparison.OrdinalIgnoreCase) => "calibrated",
                var s when s.Equals("NotCalibrated", StringComparison.OrdinalIgnoreCase) => "not_calibrated",
                _ => null,
            };
        } catch (Exception) {
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A driver may NotImplement SupportedActions or fail it; it then offers no actions.")]
    private static IList<string> ReadActions(IFocuserDevice c) {
        try { return c.SupportedActions ?? []; } catch (Exception) { return []; }
    }

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    // Extracted (internal) for direct unit testing. A target is out of range only when
    // capabilities are loaded with a real range (MaxPosition > 0); otherwise the device validates
    // the bound — covering both the caps-loading window and a relative focuser that reports
    // MaxStep=0 ("unlimited"). MinPosition is 0 for absolute focusers, -MaxStep for relative.
    internal static bool IsTargetOutOfRange(FocuserCapabilitiesDto? caps, int target) =>
        caps is not null && caps.MaxPosition > 0
            && (target < caps.MinPosition || target > caps.MaxPosition);

    public void Dispose() {
        _lifecycle.Dispose();
        GC.SuppressFinalize(this);
    }

    // Warning, not Error: a move that the device rejects (e.g. ASCOM InvalidValue for an
    // out-of-range target accepted in the caps-unknown window) is an operation that didn't
    // complete, not a daemon fault. GetAsync reflects the unchanged position.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Focuser move to {Target} failed")]
    private partial void LogMoveFailed(Exception ex, int target);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Focuser TempComp set ignored")]
    private partial void LogTempCompIgnored(Exception ex);
}
