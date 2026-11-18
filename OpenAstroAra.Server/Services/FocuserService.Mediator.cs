// The device-event members (Connected/Disconnected) satisfy the equipment mediator interface but
// are never raised server-side (the Flutter client drives state over REST/WS), so CS0067 "event is
// never used" is expected here and intentionally suppressed for the whole file — same as
// SafetyMonitorService.Mediator.cs / HeadlessFocuserMediator.
#pragma warning disable CS0067

#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Alpaca.Clients;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Equipment.MyFocuser;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e — the real <see cref="FocuserService"/> also serves the Sequencer's
/// <see cref="IFocuserMediator"/> (playbook §8.1: one singleton backs both the REST service and the
/// mediator), so the focuser sequence instructions (<c>MoveFocuserAbsolute</c>/<c>Relative</c>/
/// <c>ByTemperature</c>) drive the live Alpaca device instead of the <c>HeadlessFocuserMediator</c>
/// no-op stub.
///
/// The instructions only call <see cref="GetInfo"/> (Validate → <c>Connected</c>) and the
/// <c>MoveFocuser*</c> methods (Execute), so those carry real behaviour. Connection lifecycle is
/// driven through the REST surface (<see cref="ConnectAsync"/>/<see cref="DisconnectAsync"/>), so the
/// parameterless mediator <c>Connect()</c>/<c>Disconnect()</c>, the command members, and the
/// broadcast/event surface mirror the headless stub.
/// </summary>
public sealed partial class FocuserService : IFocuserMediator {

    // ADR 0011 rule 5: the focuser commands' timeout values and terminal condition. Hard timeout = wall-clock
    // ceiling for the blocking Move call only (the settle wait adds at most 600 x 100 ms = 60s, so ~6min worst
    // case). A move that outlasts the settle bound fails as a stall. The motion flag is optional: five
    // unreadable IsMoving reads in a row mean the driver has no IsMoving (its Move blocks until done), so the
    // wait ends rather than burning the bound.
    internal static readonly DeviceCommandPolicy MovePolicy = new(Contracts.DeviceType.Focuser,
        HardTimeout: TimeSpan.FromMinutes(5), PollInterval: TimeSpan.FromMilliseconds(100), MaxPolls: 600,
        MotionFlagOptional: true, UnreadableReadsBeforeSettled: 5);

    // Test seam: millisecond timeouts. Production keeps MovePolicy.
    internal DeviceCommandPolicy CommandPolicy { get; init; } = MovePolicy;
    private DeviceCommand? _command;
    private DeviceCommand Command => _command ??= new DeviceCommand(CommandPolicy, _faults, _logger);

    /// <summary>
    /// Synchronous live snapshot for the Sequencer, served from the §32.4 cache the background loop
    /// maintains (no blocking HTTP on the sequence thread). Never throws after Dispose — a running
    /// sequence may poll during shutdown, in which case it simply reports "not connected".
    /// </summary>
    public FocuserInfo GetInfo() {
        lock (_gate) {
            var connected = _lifecycle.Live is not null;
            var caps = _capabilities;
            var runtime = _runtime;
            return new FocuserInfo {
                Connected = connected,
                Name = _lifecycle.Device?.Name ?? string.Empty,
                DeviceId = _lifecycle.Device?.UniqueId ?? string.Empty,
                Position = connected ? runtime.Position ?? 0 : 0,
                Temperature = connected && runtime.Temperature is double t ? t : double.NaN,
                IsMoving = connected && runtime.State is "moving" or "backlash_compensating",
                TempComp = connected && runtime.TempCompEnabled,
                TempCompAvailable = connected && (caps?.CanTempComp ?? false),
                StepSize = connected ? caps?.StepSizeUm ?? 0 : 0,
            };
        }
    }

    // Absolute move: drive the device to the target and return the settled position.
    public Task<int> MoveFocuser(int position, CancellationToken ct) => MoveFocuserBlockingAsync(position, ct);

    // Relative move: current position + signed offset.
    public Task<int> MoveFocuserRelative(int position, CancellationToken ct) =>
        MoveFocuserBlockingAsync(CurrentPosition() + position, ct);

    // Temperature-relative move (NINA semantics): shift the current position by slope × temperature.
    public Task<int> MoveFocuserByTemperatureRelative(double temperature, double Slope, CancellationToken ct) =>
        MoveFocuserBlockingAsync(CurrentPosition() + (int)Math.Round(Slope * temperature), ct);

    // #1541 — drive the focuser to the logical `target`; a reversal of the last move adds the new direction's
    // backlash to the same move (no return leg). The extra steps accumulate in _backlashOffset, so the hardware
    // sits at logical + offset and every reported position subtracts it. Blocking (runs on the pool); the sweep
    // suppresses compensation.
    private void MoveCompensated(IFocuserDevice client, int target) {
        int lastDirection, offset;
        FocuserCapabilitiesDto? caps;
        lock (_gate) {
            lastDirection = _lastDirection;
            offset = _backlashOffset;
            caps = _capabilities;
        }
        var absolute = caps?.AbsoluteFocuser ?? true;
        int? from = absolute && ReadPosition(client) is int hw ? hw - offset : null;
        var inputs = FocuserBacklash.IsSuppressed || lastDirection == 0 ? null : ReadBacklashInputs();
        var move = FocuserBacklash.Plan(absolute, from, target, lastDirection,
            d => inputs is var (settings, calibration) ? FocuserBacklash.StepsFor(d, settings, calibration) : 0,
            offset, caps?.MinPosition ?? 0, caps?.MaxPosition ?? 0);
        if (move.Extra != 0) {
            lock (_gate) {
                _compensating = true;
                _moveSequence++;
                _runtime = FocuserBacklash.Report(_runtime, true);
            }
        }
        try {
            client.Move(move.HardwareTarget);
        } catch {
            if (move.Extra != 0) {
                lock (_gate) {
                    _compensating = false;
                    _moveSequence++;
                }
            }
            throw;
        }
        // Move returns once the move is dispatched, so the flag stays up; RefreshCacheOnce drops it when IsMoving reads false.
        lock (_gate) {
            if (move.Extra != 0) {
                _moveSequence++; // dispatched: a read that starts from here on sees the move
            }
            if (absolute) {
                _backlashOffset = move.Offset;
            }
            if (move.Direction != 0) {
                _lastDirection = move.Direction;
            }
        }
    }

    // The hardware position as the user's scale sees it: minus the backlash steps added so far.
    private int? Logical(int? hardware) {
        lock (_gate) {
            return hardware - _backlashOffset;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort profile read: a store hiccup degrades to an uncompensated move rather than failing it. Log-and-recover boundary.")]
    private (AutofocusSettingsDto Settings, FocusCalibrationDto? Calibration)? ReadBacklashInputs() {
        if (_profiles is null) {
            return null;
        }
        try {
            return (_profiles.GetAutofocusSettings(), _profiles.GetFocusCalibration());
        } catch (Exception ex) {
            LogMoveFailed(ex, 0);
            return null;
        }
    }

    // Base position for a relative move. Prefer a fresh DIRECT device read over the §32.4 cache so
    // the computed absolute target doesn't drift by up to one polling interval (the cache can lag, and
    // a relative move is only meaningful against the focuser's true current position). Falls back to
    // the cache when disconnected / the read fails.
    private int CurrentPosition() {
        IFocuserDevice? client;
        lock (_gate) {
            client = _lifecycle.Live;
        }
        var live = client is not null ? Logical(ReadPosition(client)) : null;
        if (live is int p) {
            return p;
        }
        lock (_gate) {
            return _runtime.Position ?? 0;
        }
    }

    public void ToggleTempComp(bool tempComp) {
        IFocuserDevice? client;
        lock (_gate) {
            client = _lifecycle.Live;
        }
        if (client is null) {
            return; // not connected: no-op (the REST/sequencer caller has already validated)
        }
        TrySetTempComp(client, tempComp);
        _lifecycle.Refresh();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort property write: setting TempComp can throw on a focuser without temp-comp support; the failure is logged and ignored rather than faulting the sequence. CA1031's log-and-recover boundary applies.")]
    private void TrySetTempComp(IFocuserDevice client, bool tempComp) {
        try {
            client.TempComp = tempComp;
        } catch (Exception ex) {
            LogTempCompIgnored(ex);
        }
    }

    private async Task<int> MoveFocuserBlockingAsync(int target, CancellationToken ct) {
        IFocuserDevice? client;
        lock (_gate) {
            if (_lifecycle.IsDisposed) {
                return 0;
            }
            client = _lifecycle.Live;
        }
        if (client is null) {
            // Not connected: the instruction's Validate has already blocked this; defensively report
            // the cached position without moving rather than throwing into the sequencer.
            lock (_gate) {
                return _runtime.Position ?? 0;
            }
        }
        // Backlash compensation stays in MoveCompensated, inside the call; the module owns the race,
        // the settle poll (direct IsMoving reads, refreshing the §32.4 cache each tick) and the faults.
        await Command.RunAsync(new DeviceCommandRequest($"focuser Move to {target}",
            Call: () => MoveCompensated(client, target),
            IsLive: () => _lifecycle.IsLive(client),
            CurrentDevice: () => _lifecycle.DeviceIfCurrent(client),
            Terminal: () => ReadIsMoving(client) is bool moving ? !moving : null,
            Polled: _lifecycle.Refresh), ct).ConfigureAwait(false);
        _lifecycle.Refresh();
        // Prefer a direct device read for the returned position so a single-flight RefreshCacheOnce
        // that no-op'd against a concurrent timer tick can't make us report a stale cache value
        // instead of where the focuser actually settled. If both the live read and the cache are
        // unavailable, report 0 ("unknown") rather than the requested target — a failed move must not
        // hand back the target and let the caller infer it reached it.
        var settled = Logical(ReadPosition(client));
        lock (_gate) {
            return settled ?? _runtime.Position ?? 0;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a transient/unsupported IsMoving read throws; report null ('unknown') so the settle-wait keeps polling rather than concluding 'settled', while a genuine disconnect is caught by the connection check in the loop. CA1031's log-and-recover boundary applies.")]
    private static bool? ReadIsMoving(IFocuserDevice client) {
        try {
            return client.IsMoving;
        } catch (Exception) {
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a transient/unsupported Position read throws; report null so the caller falls back to the cached position rather than faulting. CA1031's log-and-recover boundary applies.")]
    private static int? ReadPosition(IFocuserDevice client) {
        try {
            return client.Position;
        } catch (Exception) {
            return null;
        }
    }

    // Connection lifecycle is driven by the REST surface (ConnectAsync/DisconnectAsync), not the
    // parameterless mediator Connect/Disconnect; these mirror the headless stub. The instructions
    // never call them.
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());

    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(IFocuserConsumer consumer) { }
    public void RemoveConsumer(IFocuserConsumer consumer) { }
    public void Broadcast(FocuserInfo deviceInfo) { }

    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }

    public IDevice GetDevice() =>
        throw new NotSupportedException(
            "FocuserService does not expose a raw ASCOM IDevice; the Sequencer uses GetInfo()/MoveFocuser and connection is driven through the REST surface.");

    public void BroadcastSuccessfulAutoFocusRun(AutoFocusInfo info) { }
    public void BroadcastNewAutoFocusPoint(DataPoint dataPoint) { }
    public void BroadcastUserFocused(FocuserInfo info) { }
    public void BroadcastAutoFocusRunStarting() { }

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;
}
