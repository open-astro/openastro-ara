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
    // Consecutive unreadable IsMoving polls (~each MoveSettlePollInterval = ~500ms here) after which
    // we treat the property as unsupported and stop waiting, rather than burning the full bound. Large
    // enough to ride out a transient read-blip streak on a non-blocking driver, small enough not to
    // waste ~60s on a driver that genuinely never implements IsMoving.
    private const int UnknownReadsBeforeSettled = 5;
    private static readonly TimeSpan MoveSettlePollInterval = TimeSpan.FromMilliseconds(100);
    // Wall-clock ceiling for the blocking client.Move() CALL only (not the whole operation): a silent
    // device must not park a sequence thread until the OS TCP timeout (minutes). The subsequent
    // settle-wait adds at most MoveSettleMaxPolls × MoveSettlePollInterval (~60s), so the worst-case
    // total is ~MoveHardTimeout + 60s. Generous — real focuser travel is seconds, not minutes.
    private static readonly TimeSpan MoveHardTimeout = TimeSpan.FromMinutes(5);

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
    public Task<int> MoveFocuserRelative(int position, CancellationToken ct) =>
        MoveFocuserBlockingAsync(CurrentPosition() + position, ct);

    // Temperature-relative move (NINA semantics): shift the current position by slope × temperature.
    public Task<int> MoveFocuserByTemperatureRelative(double temperature, double Slope, CancellationToken ct) =>
        MoveFocuserBlockingAsync(CurrentPosition() + (int)Math.Round(Slope * temperature), ct);

    // Base position for a relative move. Prefer a fresh DIRECT device read over the §32.4 cache so
    // the computed absolute target doesn't drift by up to one polling interval (the cache can lag, and
    // a relative move is only meaningful against the focuser's true current position). Falls back to
    // the cache when disconnected / the read fails.
    private int CurrentPosition() {
        AlpacaFocuser? client;
        lock (_gate) {
            client = _state == EquipmentConnectionState.Connected ? _client : null;
        }
        var live = client is not null ? ReadPosition(client) : null;
        if (live is int p) {
            return p;
        }
        lock (_gate) {
            return _runtime.Position ?? 0;
        }
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
            // The blocking ASCOM Move is launched on the pool; race it against BOTH the sequencer
            // token and an explicit wall-clock bound (CancelAfter on the linked source) so a hung HTTP
            // call can't pin the sequence thread "regardless of cancellation" even when ct never fires
            // and the Alpaca client's own HTTP timeout is generous. The abandoned move is observed so
            // it can't surface as an unobserved task exception.
            var moveTask = Task.Run(() => client.Move(target), CancellationToken.None);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                linked.CancelAfter(MoveHardTimeout);
                var completed = await Task.WhenAny(moveTask, Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);
                if (completed != moveTask) {
                    ObserveQuietly(moveTask);
                    ct.ThrowIfCancellationRequested();          // sequencer cancel → propagate
                    throw new TimeoutException(                 // wall-clock bound → device fault
                        $"focuser Move to {target} did not complete within {MoveHardTimeout.TotalSeconds:0}s");
                }
                await linked.CancelAsync().ConfigureAwait(false); // move won the race: cancel the timer so it can't leak
            }
            await moveTask.ConfigureAwait(false); // observe Move()'s result / surface its exception
            var reachedRest = await WaitForMoveCompleteAsync(client, ct).ConfigureAwait(false);
            if (!reachedRest) {
                // §42.4 — the §42.2 "commanded position not reached" row: the Move dispatched
                // fine but the focuser still reports moving after the full settle bound.
                PublishOpFault(client, EquipmentFaultKind.StallTimeout,
                    $"focuser still reports moving {MoveSettleMaxPolls * MoveSettlePollInterval.TotalSeconds:0}s after a Move to {target}");
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw; // genuine sequencer cancellation — propagate so the run aborts
        } catch (TimeoutException ex) {
            // The wall-clock bound above: the blocking Move never returned — a stalled op (§42.4).
            LogMoveFailed(ex, target);
            PublishOpFault(client, EquipmentFaultKind.StallTimeout, ex.Message);
        } catch (Exception ex) {
            // Anything else, INCLUDING an OperationCanceledException the Alpaca HTTP client raises for
            // its OWN internal timeout (ct not cancelled), is a device fault: log it and report the
            // last known position rather than aborting the autonomous run as if cancelled.
            LogMoveFailed(ex, target);
            PublishOpFault(client, EquipmentFaultKind.OpError, $"focuser Move to {target} failed: {ex.Message}");
        }
        RefreshCacheOnce();
        // Prefer a direct device read for the returned position so a single-flight RefreshCacheOnce
        // that no-op'd against a concurrent timer tick can't make us report a stale cache value
        // instead of where the focuser actually settled. If both the live read and the cache are
        // unavailable, report 0 ("unknown") rather than the requested target — a failed move must not
        // hand back the target and let the caller infer it reached it.
        var settled = ReadPosition(client);
        lock (_gate) {
            return settled ?? _runtime.Position ?? 0;
        }
    }

    // Polls the device's IsMoving directly (not via the single-flight cache, which can no-op against a
    // concurrent timer tick) until the focuser settles or the bound elapses, refreshing the §32.4
    // cache each tick so GetInfo/GetAsync stay current. The leading delay gives firmware that asserts
    // IsMoving asynchronously a chance to do so before the first read, so a just-issued Move is not
    // mistaken for already-settled. Returns false ONLY on the exhausted bound with the device still
    // reporting motion — the §42.4 stall signal; a gone device (disconnected/superseded) returns true
    // because that's the Disconnected fault's territory, not a stall.
    private async Task<bool> WaitForMoveCompleteAsync(AlpacaFocuser client, CancellationToken ct) {
        var unknownStreak = 0;
        for (var i = 0; i < MoveSettleMaxPolls; i++) {
            await Task.Delay(MoveSettlePollInterval, ct).ConfigureAwait(false);
            // Stop if the device is gone (disconnected / superseded / disposed): a dropped connection
            // must NOT be mistaken for "settled" the way a swallowed IsMoving read would be.
            bool stillOurClient;
            lock (_gate) {
                stillOurClient = !_disposed && _state == EquipmentConnectionState.Connected
                    && ReferenceEquals(_client, client);
            }
            if (!stillOurClient) {
                return true;
            }
            var moving = ReadIsMoving(client);
            RefreshCacheOnce();
            if (moving == false) {
                return true; // confirmed settled
            }
            if (moving is null) {
                // A brief streak of nulls is a transient read blip — keep waiting. A persistent
                // streak means the driver doesn't implement IsMoving at all; for such drivers the
                // ASCOM Move() blocks until done, so by now it's complete — stop rather than burning
                // the full ~60s bound on a property that will never report motion. The trade-off: a
                // NON-blocking driver whose IsMoving is transient-error-prone could also produce a
                // streak this long mid-move and settle early; the MoveSettleMaxPolls hard bound and
                // the post-wait direct Position read backstop that (rare) case.
                if (++unknownStreak >= UnknownReadsBeforeSettled) {
                    return true;
                }
            } else {
                unknownStreak = 0; // moving == true: a real read; reset the transient counter
            }
        }
        return false; // bound exhausted with the focuser still reporting motion — a stall (§42.4)
    }

    // §42.4 — op-channel fault publish: snapshot the device under the gate, publish off-lock
    // (EquipmentFaultHub.Publish is non-blocking and never throws into the caller). A fault may
    // only be blamed on the LIVE client — an op whose client was superseded or disposed by a user
    // disconnect/reconnect mid-call must stay a log line (the §42.3 probe owns genuine disconnects),
    // so the liveness check and the device snapshot share one critical section.
    private void PublishOpFault(AlpacaFocuser client, EquipmentFaultKind kind, string details) {
        if (_faults is null) {
            return;
        }
        DiscoveredDeviceDto? device;
        lock (_gate) {
            if (!ReferenceEquals(_client, client)) {
                return;
            }
            device = _device;
        }
        _faults.Publish(new EquipmentFaultEvent(Contracts.DeviceType.Focuser, device?.UniqueId, device?.Name,
            kind, details, DateTimeOffset.UtcNow));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a transient/unsupported IsMoving read throws; report null ('unknown') so the settle-wait keeps polling rather than concluding 'settled', while a genuine disconnect is caught by the connection check in the loop. CA1031's log-and-recover boundary applies.")]
    private static bool? ReadIsMoving(AlpacaFocuser client) {
        try {
            return client.IsMoving;
        } catch (Exception) {
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a transient/unsupported Position read throws; report null so the caller falls back to the cached position rather than faulting. CA1031's log-and-recover boundary applies.")]
    private static int? ReadPosition(AlpacaFocuser client) {
        try {
            return client.Position;
        } catch (Exception) {
            return null;
        }
    }

    // Observes an abandoned (cancelled/timed-out) move task so a later fault can't surface as an
    // UnobservedTaskException, and logs it at Debug for post-mortem (the move is already abandoned —
    // nothing actionable, but a silent device fault should leave a trace).
    private void ObserveQuietly(Task task) {
        _ = task.ContinueWith(
            t => LogAbandonedMoveFaulted(t.Exception!),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "abandoned focuser move (cancelled/timed-out) later faulted")]
    private partial void LogAbandonedMoveFaulted(Exception ex);
}
