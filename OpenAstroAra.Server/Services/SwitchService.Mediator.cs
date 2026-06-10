// The device-event members satisfy the equipment mediator interface but are never raised server-side
// (the Flutter client drives state over REST/WS), so CS0067 "event is never used" is expected here
// and intentionally suppressed for the whole file — same as the other *Service.Mediator.cs partials.
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
using OpenAstroAra.Equipment.Equipment.MySwitch;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Full per-port snapshot cached by the §32.4 refresh. Superset of the REST
/// <see cref="SwitchPortDto"/>: Description + StepSize exist only on the mediator surface
/// (<c>SetSwitchValue.Validate</c> reads each writable switch's Minimum/Maximum/StepSize).
/// </summary>
internal readonly record struct SwitchPortSnapshot(
    short Id, string Name, string Description, double Value, double Min, double Max,
    double StepSize, bool CanWrite);

/// <summary>
/// §14e — the real <see cref="SwitchService"/> also serves the Sequencer's
/// <see cref="ISwitchMediator"/> (playbook §8.1: one singleton backs both the REST service and the
/// mediator), so the <c>SetSwitchValue</c> instruction drives the live Alpaca switch hub instead of
/// the <c>HeadlessSwitchMediator</c> stub.
///
/// The instruction reads <see cref="GetInfo"/> (Validate → <c>Connected</c> +
/// <c>WritableSwitches[i].Minimum/Maximum/StepSize</c>) and calls
/// <see cref="SetSwitchValue(short, double, IProgress{ApplicationStatus}, CancellationToken)"/>
/// (Execute), where <c>switchIndex</c> addresses the <em>WritableSwitches collection</em>, not the
/// ASCOM port id — the wrappers preserve the cache order so the index→port mapping is stable
/// between the Validate that selected the switch and the Execute that writes it.
/// </summary>
public sealed partial class SwitchService : ISwitchMediator {

    // Wall-clock ceiling for the blocking ASCOM write: switch writes are prompt (a relay/PWM set),
    // so a call that exceeds this is a hung driver, not a slow device. Cancellation cuts it short.
    private static readonly TimeSpan SwitchWriteHardTimeout = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Synchronous live snapshot for the Sequencer, served from the §32.4 cache (no blocking HTTP on
    /// the sequence thread). Never throws after Dispose — a running sequence may poll during
    /// shutdown, in which case it reports "not connected" with empty port collections. The wrappers
    /// read their <c>Value</c> live from the cache (so a stored reference stays current as the
    /// 2s refresh ticks), while their range/step fields are the snapshot's (static per ASCOM).
    /// </summary>
    public SwitchInfo GetInfo() {
        lock (_gate) {
            var connected = !_disposed && _state == EquipmentConnectionState.Connected && _client is not null;
            var snapshots = connected ? _cachedSnapshots : Array.Empty<SwitchPortSnapshot>();
            var (writable, readonlySwitches) = BuildSwitchCollections(this, snapshots);
            return new SwitchInfo {
                Connected = connected,
                Name = _device?.Name ?? string.Empty,
                DeviceId = _device?.UniqueId ?? string.Empty,
                WritableSwitches = writable,
                ReadonlySwitches = readonlySwitches,
            };
        }
    }

    // Splits the cached ports into NINA's writable/read-only collections, preserving cache order so
    // SetSwitchValue's collection index stays aligned with what Validate showed. Extracted (internal,
    // static) for direct sim-free unit testing against fabricated snapshots.
    internal static (ReadOnlyCollection<IWritableSwitch> Writable, ReadOnlyCollection<ISwitch> ReadOnly)
        BuildSwitchCollections(SwitchService owner, IReadOnlyList<SwitchPortSnapshot> snapshots) {
        var writable = new List<IWritableSwitch>();
        var readonlySwitches = new List<ISwitch>();
        foreach (var snapshot in snapshots) {
            if (snapshot.CanWrite) {
                writable.Add(new CachedWritableSwitch(owner, snapshot));
            } else {
                readonlySwitches.Add(new CachedSwitch(owner, snapshot));
            }
        }
        return (new ReadOnlyCollection<IWritableSwitch>(writable),
                new ReadOnlyCollection<ISwitch>(readonlySwitches));
    }

    /// <summary>
    /// Writes <paramref name="value"/> to the <paramref name="switchIndex"/>-th switch of the
    /// writable collection (the instruction's addressing scheme). Not-connected / out-of-range
    /// resolve to a logged no-op — the instruction's Validate has already blocked both, so a race
    /// (e.g. a disconnect between Validate and Execute) degrades gracefully instead of faulting the
    /// run. Genuine sequencer cancellation propagates.
    /// </summary>
    public async Task SetSwitchValue(short switchIndex, double value, IProgress<ApplicationStatus> progress, CancellationToken ct) {
        AlpacaSwitch? client;
        short portId;
        lock (_gate) {
            client = !_disposed && _state == EquipmentConnectionState.Connected ? _client : null;
            var id = WritablePortIdAt(_cachedSnapshots, switchIndex);
            if (client is null || id is null) {
                LogSwitchWriteSkipped(switchIndex);
                return;
            }
            portId = id.Value;
        }
        await RunSwitchWriteAsync(client, portId, value, ct).ConfigureAwait(false);
        RefreshCacheOnce();
    }

    // Maps a writable-collection index to its ASCOM port id (cache order, writable ports only).
    // Extracted (internal) for direct unit testing. Null = out of range / no such writable port.
    internal static short? WritablePortIdAt(IReadOnlyList<SwitchPortSnapshot> snapshots, short switchIndex) {
        if (switchIndex < 0) {
            return null;
        }
        var seen = 0;
        foreach (var snapshot in snapshots) {
            if (!snapshot.CanWrite) {
                continue;
            }
            if (seen == switchIndex) {
                return snapshot.Id;
            }
            seen++;
        }
        return null;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Sequencer switch-write boundary: the blocking ASCOM SetSwitchValue can throw arbitrary driver/HTTP exceptions and a concurrent Disconnect/Dispose can dispose the captured client mid-write; genuine sequencer cancellation is rethrown but any other escape (including a device/HTTP-timeout OCE) is logged as a failed write rather than faulting the autonomous run. CA1031's log-and-recover boundary applies.")]
    private async Task RunSwitchWriteAsync(AlpacaSwitch client, short portId, double value, CancellationToken ct) {
        try {
            // Race the blocking ASCOM call against both the sequencer token and a wall-clock bound so
            // a hung HTTP call can't pin the sequence thread. The abandoned write is observed.
            var writeTask = Task.Run(() => client.SetSwitchValue(portId, value), CancellationToken.None);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                linked.CancelAfter(SwitchWriteHardTimeout);
                var completed = await Task.WhenAny(writeTask, Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);
                if (completed != writeTask) {
                    ObserveQuietly(writeTask);
                    ct.ThrowIfCancellationRequested();      // sequencer cancel → propagate
                    throw new TimeoutException(
                        $"switch write to port {portId} did not complete within {SwitchWriteHardTimeout.TotalSeconds:0}s");
                }
                await linked.CancelAsync().ConfigureAwait(false);
            }
            await writeTask.ConfigureAwait(false); // observe the write's result / surface its exception
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw; // genuine sequencer cancellation — propagate so the run aborts
        } catch (Exception ex) {
            LogSwitchWriteFailed(ex, portId, value);
        }
    }

    // Observes an abandoned (cancelled/timed-out) write task so a later fault can't surface as an
    // UnobservedTaskException, and logs it at Debug for post-mortem.
    private void ObserveQuietly(Task task) {
        _ = task.ContinueWith(
            t => LogAbandonedWriteFaulted(t.Exception!),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // Current cached value for a port id; NaN when unknown (disconnected / port no longer present).
    internal double CachedPortValue(short portId) {
        lock (_gate) {
            if (!_disposed && _state == EquipmentConnectionState.Connected) {
                foreach (var snapshot in _cachedSnapshots) {
                    if (snapshot.Id == portId) {
                        return snapshot.Value;
                    }
                }
            }
            return double.NaN;
        }
    }

    internal bool MediatorConnected {
        get {
            lock (_gate) {
                return !_disposed && _state == EquipmentConnectionState.Connected && _client is not null;
            }
        }
    }

    // Best-effort write used by IWritableSwitch.SetValue (no caller in the headless instruction set —
    // SetSwitchValue.Execute goes through the mediator method — but the wrapper contract must work).
    internal void WriteSwitchValueBestEffort(short portId, double value) {
        AlpacaSwitch? client;
        lock (_gate) {
            client = !_disposed && _state == EquipmentConnectionState.Connected ? _client : null;
        }
        if (client is null) {
            LogSwitchWriteSkipped(portId);
            return;
        }
        _ = Task.Run(async () => {
            await RunSwitchWriteAsync(client, portId, value, CancellationToken.None).ConfigureAwait(false);
            RefreshCacheOnce();
        }, CancellationToken.None);
    }

    /// <summary>Read-only cache-backed <see cref="ISwitch"/> over one Alpaca switch port.</summary>
    internal class CachedSwitch : ISwitch {
        private protected readonly SwitchService Owner;

        internal CachedSwitch(SwitchService owner, SwitchPortSnapshot snapshot) {
            Owner = owner;
            Id = snapshot.Id;
            Name = snapshot.Name;
            Description = snapshot.Description;
        }

        public short Id { get; }
        public string Name { get; }
        public string Description { get; }

        // Live from the §32.4 cache so a stored reference stays current across refresh ticks.
        public double Value => Owner.CachedPortValue(Id);

        // The 2s timer keeps the cache fresh; Poll additionally nudges an immediate refresh.
        public bool Poll() {
            Owner.RefreshCacheOnce();
            return Owner.MediatorConnected;
        }
    }

    /// <summary>Writable cache-backed switch port (adds the range/step + TargetValue surface).</summary>
    internal sealed class CachedWritableSwitch : CachedSwitch, IWritableSwitch {

        internal CachedWritableSwitch(SwitchService owner, SwitchPortSnapshot snapshot)
            : base(owner, snapshot) {
            Maximum = snapshot.Max;
            Minimum = snapshot.Min;
            StepSize = snapshot.StepSize;
            TargetValue = snapshot.Value;
        }

        public double Maximum { get; }
        public double Minimum { get; }
        public double StepSize { get; }
        public double TargetValue { get; set; }

        public void SetValue() => Owner.WriteSwitchValueBestEffort(Id, TargetValue);
    }

    // Connection lifecycle is REST-driven; these mirror the headless stub. The instruction never
    // calls them.
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());

    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(ISwitchConsumer consumer) { }
    public void RemoveConsumer(ISwitchConsumer consumer) { }
    public void Broadcast(SwitchInfo deviceInfo) { }

    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }

    public IDevice GetDevice() =>
        throw new NotSupportedException(
            "SwitchService does not expose a raw ASCOM IDevice; the Sequencer uses GetInfo()/SetSwitchValue and connection is driven through the REST surface.");

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Switch mediator write to port {Port} (value {Value}) failed")]
    private partial void LogSwitchWriteFailed(Exception ex, short port, double value);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Switch mediator write skipped (not connected or no writable port at index {Index})")]
    private partial void LogSwitchWriteSkipped(short index);

    [LoggerMessage(Level = LogLevel.Debug, Message = "abandoned switch write (cancelled/timed-out) later faulted")]
    private partial void LogAbandonedWriteFaulted(Exception ex);
}
