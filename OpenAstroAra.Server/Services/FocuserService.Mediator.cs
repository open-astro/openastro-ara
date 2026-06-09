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
    // Consecutive unreadable IsMoving polls (~each MoveSettlePollInterval) after which we treat the
    // property as unsupported and stop waiting, rather than burning the full bound. Small enough to
    // tolerate a transient blip, large enough not to give up on a real device prematurely.
    private const int UnknownReadsBeforeSettled = 3;
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
            // The blocking ASCOM Move is launched on the pool; race it against the sequencer token so
            // a hung HTTP call (unresponsive device / dropped link) can't pin the sequence thread
            // indefinitely "regardless of cancellation". A non-cancelled hang is still bounded by the
            // Alpaca client's own HTTP timeout. The abandoned move is observed so it can't surface as
            // an unobserved task exception.
            var moveTask = Task.Run(() => client.Move(target), CancellationToken.None);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                var completed = await Task.WhenAny(moveTask, Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);
                await linked.CancelAsync().ConfigureAwait(false); // release the pending delay (whichever path) so it can't leak
                if (completed != moveTask) {
                    ObserveQuietly(moveTask);
                    ct.ThrowIfCancellationRequested();
                }
            }
            await moveTask.ConfigureAwait(false); // observe Move()'s result / surface its exception
            await WaitForMoveCompleteAsync(client, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw; // genuine sequencer cancellation — propagate so the run aborts
        } catch (Exception ex) {
            // Anything else, INCLUDING an OperationCanceledException the Alpaca HTTP client raises for
            // its OWN internal timeout (ct not cancelled), is a device fault: log it and report the
            // last known position rather than aborting the autonomous run as if cancelled.
            LogMoveFailed(ex, target);
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
    // mistaken for already-settled.
    private async Task WaitForMoveCompleteAsync(AlpacaFocuser client, CancellationToken ct) {
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
                return;
            }
            var moving = ReadIsMoving(client);
            RefreshCacheOnce();
            if (moving == false) {
                return; // confirmed settled
            }
            if (moving is null) {
                // A brief streak of nulls is a transient read blip — keep waiting. A persistent
                // streak means the driver doesn't implement IsMoving at all; for such drivers the
                // ASCOM Move() blocks until done, so by now it's complete — stop rather than burning
                // the full ~60s bound on a property that will never report motion.
                if (++unknownStreak >= UnknownReadsBeforeSettled) {
                    return;
                }
            } else {
                unknownStreak = 0; // moving == true: a real read; reset the transient counter
            }
        }
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

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Observes an abandoned (cancelled-past) move task purely to swallow any later fault so it cannot surface as an unobserved task exception; nothing actionable to do with it. CA1031's log-and-recover boundary applies.")]
    private static void ObserveQuietly(Task task) {
        _ = task.ContinueWith(
            t => { _ = t.Exception; },
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
}
