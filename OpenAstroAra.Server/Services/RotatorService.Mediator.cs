// The device-event members (Connected/Disconnected/Synced/Moved/MovedMechanical) satisfy the
// equipment mediator interface but are never raised server-side (the Flutter client drives state over
// REST/WS), so CS0067 "event is never used" is expected here and intentionally suppressed for the
// whole file — same as SafetyMonitorService.Mediator.cs / HeadlessRotatorMediator.
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
using OpenAstroAra.Equipment.Equipment.MyRotator;
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
/// §14e — the real <see cref="RotatorService"/> also serves the Sequencer's
/// <see cref="IRotatorMediator"/> (playbook §8.1: one singleton backs both the REST service and the
/// mediator), so the <c>MoveRotatorMechanical</c> instruction drives the live Alpaca rotator instead
/// of the <c>HeadlessRotatorMediator</c> no-op stub.
///
/// The instruction calls <see cref="GetInfo"/> (Validate → <c>Connected</c>) and
/// <see cref="MoveMechanical"/> (Execute), so those carry real behaviour, mirroring the hardened
/// focuser-mediator move path (cancellation-bounded blocking move + settle-wait + direct read-back).
/// Connection lifecycle is REST-driven, so the parameterless mediator <c>Connect()</c>/
/// <c>Disconnect()</c>, command members and broadcast/event surface mirror the headless stub.
/// </summary>
public sealed partial class RotatorService : IRotatorMediator {

    private const int MoveSettleMaxPolls = 600; // ~60s at MoveSettlePollInterval
    // Consecutive unreadable IsMoving polls after which we treat the property as unsupported and stop
    // waiting (such drivers complete Move synchronously), rather than burning the full bound. Large
    // enough to ride out a transient read-blip streak on a non-blocking driver.
    private const int UnknownReadsBeforeSettled = 5;
    private static readonly TimeSpan MoveSettlePollInterval = TimeSpan.FromMilliseconds(100);
    // Wall-clock ceiling for the blocking MoveAbsolute/MoveMechanical CALL only (not the whole op): a
    // silent device must not park a sequence thread until the OS TCP timeout. The settle-wait adds at
    // most ~60s, so worst-case total is ~MoveHardTimeout + 60s.
    private static readonly TimeSpan MoveHardTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Synchronous live snapshot for the Sequencer, served from the §32.4 cache (no blocking HTTP on
    /// the sequence thread). Never throws after Dispose — a running sequence may poll during shutdown,
    /// in which case it reports "not connected". CanReverse/Reverse/StepSize/Synced are left at their
    /// defaults: the headless rotator instruction set only consumes Connected/Position, and surfacing
    /// the rest would need a separate capability read (deferred — no instruction depends on them).
    /// </summary>
    public RotatorInfo GetInfo() {
        lock (_gate) {
            var connected = !_disposed && _state == EquipmentConnectionState.Connected && _client is not null;
            var runtime = _runtime;
            return new RotatorInfo {
                Connected = connected,
                Name = _device?.Name ?? string.Empty,
                DeviceId = _device?.UniqueId ?? string.Empty,
                Position = connected ? (float)(runtime.SkyAngleDeg ?? 0) : 0,
                MechanicalPosition = connected ? (float)(runtime.MechanicalAngleDeg ?? 0) : 0,
                IsMoving = connected && runtime.State == "moving",
            };
        }
    }

    // Sky-angle move: drive to the offset-corrected sky angle and return the settled sky angle.
    public Task<float> Move(float position, CancellationToken ct) =>
        MoveBlockingAsync(NormalizeAngle(position), useMechanical: false, ct);

    // Mechanical-angle move: drive to the raw mechanical angle and return the settled mechanical angle.
    public Task<float> MoveMechanical(float position, CancellationToken ct) =>
        MoveBlockingAsync(NormalizeAngle(position), useMechanical: true, ct);

    // Relative move: current sky angle + signed offset, normalized.
    public Task<float> MoveRelative(float position, CancellationToken ct) =>
        MoveBlockingAsync(NormalizeAngle(CurrentSkyAngle() + position), useMechanical: false, ct);

    // Pure target helpers: ARA applies no extra offset model headless, so they just normalize the
    // requested angle into [0, 360).
    public float GetTargetPosition(float position) => NormalizeAngle(position);
    public float GetTargetMechanicalPosition(float position) => NormalizeAngle(position);

    public void Sync(float skyAngle) {
        AlpacaRotator? client;
        lock (_gate) {
            client = _state == EquipmentConnectionState.Connected ? _client : null;
        }
        if (client is null) {
            return; // not connected: no-op (caller has already validated)
        }
        TrySync(client, NormalizeAngle(skyAngle));
        RefreshCacheOnce();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort sky-angle sync: Sync can throw on a driver that doesn't support it; the failure is logged and ignored rather than faulting the sequence. CA1031's log-and-recover boundary applies.")]
    private void TrySync(AlpacaRotator client, float skyAngle) {
        try {
            client.Sync(skyAngle);
        } catch (Exception ex) {
            LogSyncIgnored(ex);
        }
    }

    // Base sky angle for a relative move — prefer a fresh DIRECT device read over the §32.4 cache so
    // the computed absolute target doesn't drift by a polling interval. Falls back to the cache.
    // NOTE: this single read runs synchronously on the caller's thread (before the move is offloaded);
    // a slow-but-not-failing HTTP call could briefly block it, bounded by the Alpaca client's socket
    // timeout. Alpaca reads are normally sub-ms and any exception falls back to the cache, so this is
    // an accepted tradeoff (parity with the focuser-mediator temperature read).
    private float CurrentSkyAngle() {
        AlpacaRotator? client;
        lock (_gate) {
            client = _state == EquipmentConnectionState.Connected ? _client : null;
        }
        var live = client is not null ? ReadAngle(client, useMechanical: false) : null;
        if (live is float a) {
            return a;
        }
        lock (_gate) {
            return (float)(_runtime.SkyAngleDeg ?? 0);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Sequencer move boundary: the blocking ASCOM MoveAbsolute/MoveMechanical can throw arbitrary driver/HTTP exceptions, and a concurrent Disconnect/Dispose can dispose the captured client mid-move; genuine sequencer cancellation is rethrown, and any other escape (including a device/HTTP timeout OCE) is logged, published as a §42.4 fault, and rethrown as SequenceEntityFailedException so the instruction's retry/failure machinery engages (§42.2). CA1031's catch-classify-rethrow boundary applies.")]
    private async Task<float> MoveBlockingAsync(float target, bool useMechanical, CancellationToken ct) {
        AlpacaRotator? client;
        lock (_gate) {
            if (_disposed) {
                return 0;
            }
            client = _state == EquipmentConnectionState.Connected ? _client : null;
        }
        if (client is null) {
            // Not connected: the instruction's Validate has already blocked this; defensively report
            // the cached angle without moving rather than throwing into the sequencer.
            return CachedAngle(useMechanical);
        }
        try {
            // Race the blocking ASCOM move against BOTH the sequencer token and an explicit wall-clock
            // bound so a hung HTTP call can't pin the sequence thread regardless of cancellation. The
            // abandoned move is observed so it can't surface as an unobserved task exception.
            var moveTask = Task.Run(
                () => { if (useMechanical) { client.MoveMechanical(target); } else { client.MoveAbsolute(target); } },
                CancellationToken.None);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                linked.CancelAfter(MoveHardTimeout);
                var completed = await Task.WhenAny(moveTask, Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);
                if (completed != moveTask) {
                    ObserveQuietly(moveTask);
                    ct.ThrowIfCancellationRequested();          // sequencer cancel → propagate
                    throw new TimeoutException(                 // wall-clock bound → device fault
                        $"rotator move to {target}° did not complete within {MoveHardTimeout.TotalSeconds:0}s");
                }
                await linked.CancelAsync().ConfigureAwait(false); // move won the race: cancel the timer so it can't leak
            }
            await moveTask.ConfigureAwait(false); // observe the move's result / surface its exception
            if (!await WaitForMoveCompleteAsync(client, ct).ConfigureAwait(false)) {
                // §42.2 — the §42.2 "position drift / runaway" family: the move dispatched fine
                // but the rotator still reports moving after the full settle bound. Fail the
                // instruction — a rotator at an unknown angle must not read as a completed move.
                var msg = $"rotator still reports moving {MoveSettleMaxPolls * MoveSettlePollInterval.TotalSeconds:0}s after a move to {target}°";
                PublishOpFault(client, EquipmentFaultKind.StallTimeout, msg);
                throw new SequenceEntityFailedException(msg);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw; // genuine sequencer cancellation — propagate so the run aborts
        } catch (SequenceEntityFailedException) {
            throw; // already classified + published above
        } catch (TimeoutException ex) {
            // The wall-clock bound above: the blocking move never returned — a stalled op (§42.4).
            // §42.2: publish AND fail the instruction so retries/instruction_failed engage.
            LogMoveFailed(ex, target);
            PublishOpFault(client, EquipmentFaultKind.StallTimeout, ex.Message);
            throw new SequenceEntityFailedException(ex.Message, ex);
        } catch (Exception ex) {
            LogMoveFailed(ex, target);
            PublishOpFault(client, EquipmentFaultKind.OpError, $"rotator move to {target}° failed: {ex.Message}");
            throw new SequenceEntityFailedException($"rotator move to {target}° failed: {ex.Message}", ex);
        }
        RefreshCacheOnce();
        // Prefer a direct device read for the returned angle so a single-flight RefreshCacheOnce that
        // no-op'd against a concurrent timer tick can't report a stale value. If neither the live read
        // nor the cache is available, report 0 ("unknown") rather than the requested target.
        var settled = ReadAngle(client, useMechanical);
        return settled ?? CachedAngle(useMechanical);
    }

    // Polls the device's IsMoving directly (not via the single-flight cache, which can no-op against a
    // concurrent timer tick) until the rotator settles or the bound elapses, refreshing the §32.4
    // cache each tick so GetInfo/GetAsync stay current. Returns false ONLY on settle exhaustion —
    // the device still reports moving after the full bound (§42.2: the caller fails the instruction).
    // A dropped/superseded connection and a driver without IsMoving both report true ("not a stall"):
    // the §42.3 probe owns disconnects, and a blocking-Move driver settled when the call returned.
    private async Task<bool> WaitForMoveCompleteAsync(AlpacaRotator client, CancellationToken ct) {
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
                // Brief null streak = transient blip (keep waiting); a persistent streak means the
                // driver doesn't implement IsMoving (its Move blocks until done), so stop early.
                if (++unknownStreak >= UnknownReadsBeforeSettled) {
                    return true;
                }
            } else {
                unknownStreak = 0; // moving == true: real read; reset the transient counter
            }
        }
        return false; // still reports moving after the full settle bound
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a transient/unsupported IsMoving read throws; report null ('unknown') so the settle-wait keeps polling rather than concluding 'settled', while a genuine disconnect is caught by the connection check in the loop. CA1031's log-and-recover boundary applies.")]
    private static bool? ReadIsMoving(AlpacaRotator client) {
        try {
            return client.IsMoving;
        } catch (Exception) {
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a transient/unsupported angle read throws; report null so the caller falls back to the cached angle rather than faulting. CA1031's log-and-recover boundary applies.")]
    private static float? ReadAngle(AlpacaRotator client, bool useMechanical) {
        try {
            return useMechanical ? client.MechanicalPosition : client.Position;
        } catch (Exception) {
            return null;
        }
    }

    private float CachedAngle(bool useMechanical) {
        lock (_gate) {
            return (float)((useMechanical ? _runtime.MechanicalAngleDeg : _runtime.SkyAngleDeg) ?? 0);
        }
    }

    // §42.4 — op-channel fault publish: snapshot the device under the gate, publish off-lock
    // (EquipmentFaultHub.Publish is non-blocking and never throws into the caller). A fault may
    // only be blamed on the LIVE client — an op whose client was superseded or disposed by a user
    // disconnect/reconnect mid-call must stay a log line (the §42.3 probe owns genuine disconnects),
    // so the liveness check and the device snapshot share one critical section.
    private void PublishOpFault(AlpacaRotator client, EquipmentFaultKind kind, string details) {
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
        _faults.Publish(new EquipmentFaultEvent(Contracts.DeviceType.Rotator, device?.UniqueId, device?.Name,
            kind, details, DateTimeOffset.UtcNow));
    }

    // Observes an abandoned (cancelled/timed-out) move task so a later fault can't surface as an
    // UnobservedTaskException, and logs it at Debug for post-mortem.
    private void ObserveQuietly(Task task) {
        _ = task.ContinueWith(
            t => LogAbandonedMoveFaulted(t.Exception!),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // Normalize any angle into [0, 360).
    private static float NormalizeAngle(float angle) {
        var a = angle % 360f;
        return a < 0 ? a + 360f : a;
    }

    // Connection lifecycle is REST-driven; these mirror the headless stub. The instruction never
    // calls them.
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());

    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(IRotatorConsumer consumer) { }
    public void RemoveConsumer(IRotatorConsumer consumer) { }
    public void Broadcast(RotatorInfo deviceInfo) { }

    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }

    public IDevice GetDevice() =>
        throw new NotSupportedException(
            "RotatorService does not expose a raw ASCOM IDevice; the Sequencer uses GetInfo()/Move* and connection is driven through the REST surface.");

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;
    public event EventHandler<RotatorEventArgs>? Synced;
    public event Func<object, RotatorEventArgs, Task>? Moved;
    public event Func<object, RotatorEventArgs, Task>? MovedMechanical;

    [LoggerMessage(Level = LogLevel.Debug, Message = "abandoned rotator move (cancelled/timed-out) later faulted")]
    private partial void LogAbandonedMoveFaulted(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rotator sync ignored")]
    private partial void LogSyncIgnored(Exception ex);
}
