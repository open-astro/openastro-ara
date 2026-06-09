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
using OpenAstroAra.Equipment.Equipment.MyFocuser;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OxyPlot;
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

    // Bounded settle wait after a Move: ~60s (600 × 100ms). A move that outlasts this returns early
    // (the device keeps moving; the cache catches up on the next timer tick) rather than blocking a
    // sequence thread indefinitely.
    private const int MoveSettleMaxPolls = 600;
    private static readonly TimeSpan MoveSettlePollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Synchronous live snapshot for the Sequencer, served from the §32.4 cache the background loop
    /// maintains (no blocking HTTP on the sequence thread). Never throws after Dispose — a running
    /// sequence may poll during shutdown, in which case it simply reports "not connected".
    /// </summary>
    public FocuserInfo GetInfo() {
        lock (_gate) {
            var connected = !_disposed && _state == EquipmentConnectionState.Connected && _client is not null;
            var caps = _capabilities;
            var runtime = _runtime;
            return new FocuserInfo {
                Connected = connected,
                Name = _device?.Name ?? string.Empty,
                DeviceId = _device?.UniqueId ?? string.Empty,
                Position = connected ? runtime.Position ?? 0 : 0,
                Temperature = connected && runtime.Temperature is double t ? t : double.NaN,
                IsMoving = connected && runtime.State == "moving",
                TempComp = connected && runtime.TempCompEnabled,
                TempCompAvailable = connected && (caps?.CanTempComp ?? false),
                StepSize = connected ? caps?.StepSizeUm ?? 0 : 0,
            };
        }
    }

    // Absolute move: drive the device to the target and return the settled position.
    public Task<int> MoveFocuser(int position, CancellationToken ct) => MoveFocuserBlockingAsync(position, ct);

    // Relative move: current position + signed offset.
    public Task<int> MoveFocuserRelative(int position, CancellationToken ct) {
        int current;
        lock (_gate) {
            current = _runtime.Position ?? 0;
        }
        return MoveFocuserBlockingAsync(current + position, ct);
    }

    // Temperature-relative move (NINA semantics): shift the current position by slope × temperature.
    public Task<int> MoveFocuserByTemperatureRelative(double temperature, double Slope, CancellationToken ct) {
        int current;
        lock (_gate) {
            current = _runtime.Position ?? 0;
        }
        var target = current + (int)Math.Round(Slope * temperature);
        return MoveFocuserBlockingAsync(target, ct);
    }

    public void ToggleTempComp(bool tempComp) {
        AlpacaFocuser? client;
        lock (_gate) {
            client = _state == EquipmentConnectionState.Connected ? _client : null;
        }
        if (client is null) {
            return; // not connected: no-op (the REST/sequencer caller has already validated)
        }
        TrySetTempComp(client, tempComp);
        RefreshCacheOnce();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort property write: setting TempComp can throw on a focuser without temp-comp support; the failure is logged and ignored rather than faulting the sequence. CA1031's log-and-recover boundary applies.")]
    private void TrySetTempComp(AlpacaFocuser client, bool tempComp) {
        try {
            client.TempComp = tempComp;
        } catch (Exception ex) {
            LogTempCompIgnored(ex);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Sequencer move boundary: the blocking ASCOM Move can throw arbitrary driver/HTTP exceptions, and a concurrent Disconnect/Dispose can dispose the captured client mid-move; cancellation is rethrown (the sequence is aborting) but any other escape is logged and the last known position is returned rather than faulting the autonomous run. CA1031's log-and-recover boundary applies.")]
    private async Task<int> MoveFocuserBlockingAsync(int target, CancellationToken ct) {
        AlpacaFocuser? client;
        lock (_gate) {
            if (_disposed) {
                return 0;
            }
            client = _state == EquipmentConnectionState.Connected ? _client : null;
        }
        if (client is null) {
            // Not connected: the instruction's Validate has already blocked this; defensively report
            // the cached position without moving rather than throwing into the sequencer.
            lock (_gate) {
                return _runtime.Position ?? 0;
            }
        }
        try {
            await Task.Run(() => client.Move(target), CancellationToken.None).ConfigureAwait(false);
            await WaitForMoveCompleteAsync(ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw; // honour sequencer cancellation
        } catch (Exception ex) {
            LogMoveFailed(ex, target);
        }
        RefreshCacheOnce();
        lock (_gate) {
            return _runtime.Position ?? target;
        }
    }

    // Polls the §32.4 cache (refreshing each tick) until the focuser leaves the "moving" state or the
    // bound elapses. An initial delay gives the device time to assert IsMoving before the first read,
    // so a just-issued Move is not mistaken for already-settled.
    private async Task WaitForMoveCompleteAsync(CancellationToken ct) {
        for (var i = 0; i < MoveSettleMaxPolls; i++) {
            await Task.Delay(MoveSettlePollInterval, ct).ConfigureAwait(false);
            RefreshCacheOnce();
            lock (_gate) {
                if (_runtime.State != "moving") {
                    return;
                }
            }
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
