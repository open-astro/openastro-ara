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
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Equipment.MyFilterWheel;
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
/// §14e — the real <see cref="FilterWheelService"/> also serves the Sequencer's
/// <see cref="IFilterWheelMediator"/> (playbook §8.1: one singleton backs both the REST service and
/// the mediator), so the <c>SwitchFilter</c> instruction drives the live Alpaca filter wheel instead
/// of the <c>HeadlessFilterWheelMediator</c> stub.
///
/// <c>SwitchFilter</c> resolves its target filter by name/position against
/// <c>ActiveProfile.FilterWheelSettings.FilterWheelFilters</c>, so on the first slot read of each
/// connection the service imports the device's filter list into the active profile (NINA's
/// import-on-connect semantics): entries at positions the device still has are preserved (they may
/// carry user-edited autofocus settings), entries beyond the device's slot count are removed, and
/// missing positions are added from the device's names/offsets. The instruction then reads
/// <see cref="GetInfo"/> (Validate → <c>Connected</c>) and calls <see cref="ChangeFilter"/>
/// (Execute), which blocks on a bounded wait until the wheel reports the target position.
/// </summary>
public sealed partial class FilterWheelService : IFilterWheelMediator {

    private const int FilterChangeSettleMaxPolls = 600; // ~60s at the poll interval — wheels take seconds
    private static readonly TimeSpan FilterChangePollInterval = TimeSpan.FromMilliseconds(100);
    // Wall-clock ceiling for the blocking ASCOM Position write (the rotation itself is tracked by
    // the settle-wait): a hung HTTP call must not pin the sequence thread.
    private static readonly TimeSpan FilterChangeHardTimeout = TimeSpan.FromMinutes(1);
    // Guards every access to the active profile's FilterWheelFilters collection within the daemon:
    // ObservableCollection is not safe for concurrent read+write, the import-on-connect mutates it
    // on the refresh thread, and GetInfo/ChangeFilter iterate it on the sequence thread. Deliberately
    // separate from _gate (the import must not run inside _gate — observable-callback re-entry) and
    // never nested inside it (readers snapshot under this lock BEFORE taking _gate).
    private readonly object _profileFiltersLock = new();

    /// <summary>
    /// Synchronous live snapshot for the Sequencer, served from the §32.4 cache (no blocking HTTP on
    /// the sequence thread). Never throws after Dispose — a running sequence may poll during
    /// shutdown, in which case it reports "not connected". <c>SelectedFilter</c> resolves the cached
    /// wheel position against the active profile's filter list (falling back to the device slot
    /// names), mirroring how NINA surfaces the current filter.
    /// </summary>
    public FilterWheelInfo GetInfo() {
        var profileSnapshot = SnapshotProfileFilters();
        lock (_gate) {
            var connected = !_disposed && _state == EquipmentConnectionState.Connected && _client is not null;
            var runtime = _runtime;
            var slots = _slots;
            var info = new FilterWheelInfo {
                Connected = connected,
                Name = _device?.Name ?? string.Empty,
                DeviceId = _device?.UniqueId ?? string.Empty,
                IsMoving = connected && runtime.State == "moving",
            };
            if (connected && runtime.CurrentSlot is int position) {
                var resolved = ResolveFilter(profileSnapshot, slots, position);
                if (resolved is not null) {
                    info.SelectedFilter = resolved;
                }
            }
            return info;
        }
    }

    /// <summary>
    /// Drives the wheel to <paramref name="inputFilter"/>'s position and blocks on a bounded wait
    /// until the device reports it (ASCOM reads <c>Position == -1</c> while rotating). Returns the
    /// filter actually reached (profile/slot-resolved), or <paramref name="inputFilter"/> unchanged
    /// when the change could not be confirmed — not-connected and out-of-range resolve to a logged
    /// no-op (the instruction's Validate has already blocked those; a race degrades gracefully).
    /// An unconfirmed change is safe to retry: re-issuing the same target against a wheel that did
    /// reach it (e.g. the write outlived the hard timeout but landed) settles immediately.
    /// Genuine sequencer cancellation propagates.
    /// </summary>
    public async Task<FilterInfo> ChangeFilter(FilterInfo inputFilter, IProgress<ApplicationStatus>? progress = null, CancellationToken token = default) {
        ArgumentNullException.ThrowIfNull(inputFilter);
        AlpacaFilterWheel? client;
        IReadOnlyList<FilterSlotDto>? slots;
        lock (_gate) {
            client = !_disposed && _state == EquipmentConnectionState.Connected ? _client : null;
            slots = _slots;
        }
        var target = inputFilter.Position;
        // slots == null means the first slot read hasn't landed yet (fresh connect): without the
        // slot count the upper bound can't be validated, so skip explicitly rather than writing a
        // possibly-out-of-range position to the device.
        if (client is null || slots is null || target < 0 || target >= slots.Count) {
            LogFilterChangeSkipped(inputFilter.Name, target);
            return inputFilter;
        }
        var reached = await RunFilterChangeAsync(client, target, token).ConfigureAwait(false);
        if (!reached) {
            return inputFilter;
        }
        var profileSnapshot = SnapshotProfileFilters();
        lock (_gate) {
            return ResolveFilter(profileSnapshot, _slots, target) ?? inputFilter;
        }
    }

    // Copy-on-read under _profileFiltersLock so iteration can never observe a concurrent import
    // mutating the observable collection. The only in-daemon writer is ImportProfileFilters (same
    // lock); the snapshot is tiny (filter count).
    private List<FilterInfo>? SnapshotProfileFilters() {
        lock (_profileFiltersLock) {
            var filters = ProfileFilters();
            return filters is null ? null : new List<FilterInfo>(filters);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Sequencer filter-change boundary: the blocking ASCOM Position write can throw arbitrary driver/HTTP exceptions and a concurrent Disconnect/Dispose can dispose the captured client mid-change; genuine sequencer cancellation is rethrown but any other escape (including a device/HTTP-timeout OCE) is logged as a failed change rather than faulting the autonomous run. CA1031's log-and-recover boundary applies.")]
    private async Task<bool> RunFilterChangeAsync(AlpacaFilterWheel client, short target, CancellationToken ct) {
        try {
            // Race the blocking ASCOM write against both the sequencer token and a wall-clock bound
            // so a hung HTTP call can't pin the sequence thread. The abandoned write is observed.
            var changeTask = Task.Run(() => client.Position = target, CancellationToken.None);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                linked.CancelAfter(FilterChangeHardTimeout);
                var completed = await Task.WhenAny(changeTask, Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);
                if (completed != changeTask) {
                    ObserveQuietly(changeTask);
                    ct.ThrowIfCancellationRequested();      // sequencer cancel → propagate
                    throw new TimeoutException(
                        $"filter change to position {target} did not complete within {FilterChangeHardTimeout.TotalSeconds:0}s");
                }
                await linked.CancelAsync().ConfigureAwait(false);
            }
            await changeTask.ConfigureAwait(false); // observe the write's result / surface its exception
            return await WaitForPositionAsync(client, target, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw; // genuine sequencer cancellation — propagate so the run aborts
        } catch (Exception ex) {
            LogFilterChangeFailed(ex, target);
            return false;
        }
    }

    // Polls the device directly until it reports the target position (ASCOM: -1 while rotating),
    // folding each polled value straight into the §32.4 runtime cache so GetInfo stays current
    // WITHOUT a second Position read per tick (one network call per poll). Delay-before-check gives
    // the driver one poll interval to leave the old position before the first read. False on
    // timeout / a dropped-or-superseded connection.
    private async Task<bool> WaitForPositionAsync(AlpacaFilterWheel client, short target, CancellationToken ct) {
        var loggedReadFailure = false;
        for (var i = 0; i < FilterChangeSettleMaxPolls; i++) {
            await Task.Delay(FilterChangePollInterval, ct).ConfigureAwait(false);
            bool stillOurClient;
            lock (_gate) {
                stillOurClient = !_disposed && _state == EquipmentConnectionState.Connected
                    && ReferenceEquals(_client, client);
            }
            if (!stillOurClient) {
                return false; // disconnected / superseded — the change can't be confirmed
            }
            var position = ReadPositionSafe(client);
            if (position is null) {
                if (!loggedReadFailure) {
                    LogPositionPollFailed();
                    loggedReadFailure = true;
                }
                continue; // failed read: keep the prior cached runtime, poll again
            }
            lock (_gate) {
                if (_state == EquipmentConnectionState.Connected && ReferenceEquals(_client, client)) {
                    _runtime = position < 0
                        ? new FilterWheelStateDto("moving", null)
                        : new FilterWheelStateDto("idle", position);
                }
            }
            if (position == target) {
                return true;
            }
        }
        LogFilterChangeTimedOut(target); // distinct from the pre-check "skipped" log for post-mortem
        return false;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-poll read boundary: a transient Position read throws; report null ('unknown') so the wait keeps polling (and logs once) rather than faulting. CA1031's log-and-recover boundary applies.")]
    private static short? ReadPositionSafe(AlpacaFilterWheel client) {
        try {
            return client.Position;
        } catch (Exception) {
            return null;
        }
    }

    // Observes an abandoned (cancelled/timed-out) change task so a later fault can't surface as an
    // UnobservedTaskException, and logs it at Debug for post-mortem.
    private void ObserveQuietly(Task task) {
        _ = task.ContinueWith(
            t => LogAbandonedChangeFaulted(t.Exception!),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private OpenAstroAra.Core.Utility.ObserveAllCollection<FilterInfo>? ProfileFilters() =>
        _profileService?.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters;

    // Resolves a wheel position to its FilterInfo: the active profile's entry wins (it carries the
    // user-edited autofocus settings SwitchFilter round-trips), then the device slot name, then null
    // ("unknown position"). Extracted (internal) for direct sim-free unit testing.
    internal static FilterInfo? ResolveFilter(IList<FilterInfo>? profileFilters, IReadOnlyList<FilterSlotDto>? slots, int position) {
        if (profileFilters is not null) {
            foreach (var filter in profileFilters) {
                if (filter.Position == position) {
                    return filter;
                }
            }
        }
        if (slots is not null && position >= 0 && position < slots.Count) {
            var slot = slots[position];
            return new FilterInfo(slot.Name, slot.FocusOffset, (short)slot.Position);
        }
        return null;
    }

    private void ImportProfileFilters(List<FilterSlotDto> slots) {
        // Resolve the collection reference INSIDE the lock (consistent with SnapshotProfileFilters)
        // so a concurrent ActiveProfile swap can't land the import in an orphaned collection.
        lock (_profileFiltersLock) {
            var filters = ProfileFilters();
            if (filters is null) {
                return; // REST-only construction (unit tests): no profile to import into
            }
            SyncProfileFilters(filters, slots);
        }
        LogProfileFiltersImported(slots.Count);
    }

    // NINA import-on-connect semantics: the device is the truth for slot COUNT and for the names of
    // newly-seen positions; entries the profile already has at a still-valid position are preserved
    // verbatim (they may carry user-edited autofocus exposure/binning/offsets). Extracted (internal,
    // static) for direct sim-free unit testing.
    internal static void SyncProfileFilters(IList<FilterInfo> profileFilters, List<FilterSlotDto> slots) {
        for (var i = profileFilters.Count - 1; i >= 0; i--) {
            if (profileFilters[i].Position >= slots.Count || profileFilters[i].Position < 0) {
                profileFilters.RemoveAt(i);
            }
        }
        foreach (var slot in slots) {
            var known = false;
            foreach (var filter in profileFilters) {
                if (filter.Position == slot.Position) {
                    known = true;
                    break;
                }
            }
            if (!known) {
                profileFilters.Add(new FilterInfo(slot.Name, slot.FocusOffset, (short)slot.Position));
            }
        }
    }

    // Connection lifecycle is REST-driven; these mirror the headless stub. The instruction never
    // calls them.
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());

    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(IFilterWheelConsumer consumer) { }
    public void RemoveConsumer(IFilterWheelConsumer consumer) { }
    public void Broadcast(FilterWheelInfo deviceInfo) { }

    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }

    public IDevice GetDevice() =>
        throw new NotSupportedException(
            "FilterWheelService does not expose a raw ASCOM IDevice; the Sequencer uses GetInfo()/ChangeFilter and connection is driven through the REST surface.");

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;
    public event Func<object, FilterChangedEventArgs, Task>? FilterChanged;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Filter change to position {Position} failed")]
    private partial void LogFilterChangeFailed(Exception ex, short position);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Filter change to '{Name}' (position {Position}) skipped (not connected or position out of range)")]
    private partial void LogFilterChangeSkipped(string? name, short position);

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported {Count} filters from the connected wheel into the active profile")]
    private partial void LogProfileFiltersImported(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "filter change to position {Position} timed out waiting for the wheel to settle")]
    private partial void LogFilterChangeTimedOut(short position);

    [LoggerMessage(Level = LogLevel.Debug, Message = "filter-wheel position poll failed during settle-wait (will keep polling)")]
    private partial void LogPositionPollFailed();

    [LoggerMessage(Level = LogLevel.Debug, Message = "abandoned filter change (cancelled/timed-out) later faulted")]
    private partial void LogAbandonedChangeFaulted(Exception ex);
}
