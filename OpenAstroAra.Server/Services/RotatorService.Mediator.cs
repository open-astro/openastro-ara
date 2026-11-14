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

    // ADR 0011 rule 5: the move's timeout values and terminal condition. Hard timeout = wall-clock
    // ceiling for the blocking MoveAbsolute/MoveMechanical CALL only (settle adds at most ~60s). IsMoving
    // is optional: 5 unreadable polls in a row mean the driver has none and its Move blocked until done.
    internal static readonly DeviceCommandPolicy MovePolicy = new(Contracts.DeviceType.Rotator,
        HardTimeout: TimeSpan.FromMinutes(5), PollInterval: TimeSpan.FromMilliseconds(100), MaxPolls: 600,
        MotionFlagOptional: true, UnreadableReadsBeforeSettled: 5);

    // Test seam: millisecond timeouts. Production keeps MovePolicy.
    internal DeviceCommandPolicy CommandPolicy { get; init; } = MovePolicy;
    private DeviceCommand? _command;
    private DeviceCommand Command => _command ??= new DeviceCommand(CommandPolicy, _faults, _logger);

    /// <summary>
    /// Synchronous live snapshot for the Sequencer, served from the §32.4 cache (no blocking HTTP on
    /// the sequence thread). Never throws after Dispose — a running sequence may poll during shutdown,
    /// in which case it reports "not connected". CanReverse/Reverse/StepSize/Synced are left at their
    /// defaults: the headless rotator instruction set only consumes Connected/Position, and surfacing
    /// the rest would need a separate capability read (deferred — no instruction depends on them).
    /// </summary>
    public RotatorInfo GetInfo() {
        lock (_gate) {
            var connected = _lifecycle.Live is not null;
            var runtime = _runtime;
            var device = _lifecycle.Device;
            return new RotatorInfo {
                Connected = connected,
                Name = device?.Name ?? string.Empty,
                DeviceId = device?.UniqueId ?? string.Empty,
                Position = connected ? (float)(runtime.SkyAngleDeg ?? 0) : 0,
                // NaN, not 0, while connected but not read yet: a frame taken right after the rotator
                // connects would otherwise record 0° and its flats would turn to the wrong angle.
                MechanicalPosition = connected ? (float)(runtime.MechanicalAngleDeg ?? double.NaN) : 0,
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
        var client = _lifecycle.Live;
        if (client is null) {
            return; // not connected: no-op (caller has already validated)
        }
        TrySync(client, NormalizeAngle(skyAngle));
        _lifecycle.Refresh();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort sky-angle sync: Sync can throw on a driver that doesn't support it; the failure is logged and ignored rather than faulting the sequence. CA1031's log-and-recover boundary applies.")]
    private void TrySync(IRotatorDevice client, float skyAngle) {
        try {
            client.Sync(skyAngle);
            ResetDriftIfLive(client); // sync redefines the sky-angle frame — the old expectation is stale
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
        var client = _lifecycle.Live;
        var live = client is not null ? ReadAngle(client, useMechanical: false) : null;
        if (live is float a) {
            return a;
        }
        lock (_gate) {
            return (float)(_runtime.SkyAngleDeg ?? 0);
        }
    }

    private async Task<float> MoveBlockingAsync(float target, bool useMechanical, CancellationToken ct) {
        if (_lifecycle.IsDisposed) {
            return 0;
        }
        var client = _lifecycle.Live;
        if (client is null) {
            // Not connected: the instruction's Validate has already blocked this; defensively report
            // the cached angle without moving rather than throwing into the sequencer.
            return CachedAngle(useMechanical);
        }
        try {
            var what = $"rotator move to {target}°";
            await Command.RunAsync(new DeviceCommandRequest(what,
                Call: () => { if (useMechanical) { client.MoveMechanical(target); } else { client.MoveAbsolute(target); } },
                IsLive: () => _lifecycle.IsLive(client),
                CurrentDevice: () => _lifecycle.DeviceIfCurrent(client),
                Terminal: () => ReadIsMoving(client) is bool moving ? !moving : null,
                // §42.2 item 4 — the move dispatched: arm the drift watch on the commanded target so a
                // rotator that settles off-target (or slips later) is caught by the refresh tick.
                Dispatched: () => RecordMoveIfLive(client, target, useMechanical),
                Polled: _lifecycle.Refresh), ct).ConfigureAwait(false);
        } catch (Exception) {
            // On every failure path the drift watch forgets the expectation: the module already
            // classified and published the fault, and where the rotator sits after a cancelled/failed/
            // stalled move is unknown — a lingering record would double-report it ~16s later.
            ResetDriftIfLive(client);
            throw;
        }
        _lifecycle.Refresh();
        // Prefer a direct device read for the returned angle so a single-flight RefreshCacheOnce that
        // no-op'd against a concurrent timer tick can't report a stale value. If neither the live read
        // nor the cache is available, report 0 ("unknown") rather than the requested target.
        var settled = ReadAngle(client, useMechanical);
        return settled ?? CachedAngle(useMechanical);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a transient/unsupported IsMoving read throws; report null ('unknown') so the settle-wait keeps polling rather than concluding 'settled', while a genuine disconnect is caught by the connection check in the loop. CA1031's log-and-recover boundary applies.")]
    private static bool? ReadIsMoving(IRotatorDevice client) {
        try {
            return client.IsMoving;
        } catch (Exception) {
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a transient/unsupported angle read throws; report null so the caller falls back to the cached angle rather than faulting. CA1031's log-and-recover boundary applies.")]
    private static float? ReadAngle(IRotatorDevice client, bool useMechanical) {
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

    // §42.4 — the drift watch's fault publish (a move's own faults go through the command module).
    // A fault may only be blamed on the LIVE client: the liveness check and the device snapshot share
    // one critical section, so a superseded client's drift stays a log line.
    private void PublishOpFault(IRotatorDevice client, EquipmentFaultKind kind, string details) {
        if (_faults is null || _lifecycle.DeviceIfCurrent(client) is not { } device) {
            return;
        }
        _faults.Publish(new EquipmentFaultEvent(Contracts.DeviceType.Rotator, device.UniqueId, device.Name,
            kind, details, DateTimeOffset.UtcNow));
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rotator sync ignored")]
    private partial void LogSyncIgnored(Exception ex);
}
