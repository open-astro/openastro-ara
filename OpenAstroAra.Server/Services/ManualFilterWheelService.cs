#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

// The device-event members satisfy the equipment mediator interface but are never raised server-side
// (clients drive state over REST/WS) — same suppression as the *Service.Mediator.cs partials.
#pragma warning disable CS0067

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Equipment.MyFilterWheel;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using DeviceType = OpenAstroAra.Server.Contracts.DeviceType;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1298 — the manual filter wheel: a filter drawer, slider or hand-turned wheel with no motor and no
/// Alpaca driver. It is offered by discovery like any filter wheel (<see cref="Descriptor"/>), so it
/// connects, is remembered and reconnects at boot through the ordinary paths.
///
/// <para>Its slots are the profile's filter labels (<c>/profile/filter-wheel/labels</c>, trailing
/// blanks trimmed), synced into <c>ActiveProfile.FilterWheelSettings.FilterWheelFilters</c> so
/// <c>SwitchFilter</c>, focus offsets and autofocus-per-filter resolve exactly as for a motorised
/// wheel. A filter change does not move anything: it raises a hand-swap prompt (WS
/// <c>equipment.filter_wheel.manual_swap</c> + a §46 notification) and the wheel reads
/// <c>awaiting_user</c> until the user reports the filter installed
/// (<see cref="ReportInstalledAsync"/>). A sequencer change blocks until then, with no time limit:
/// an unattended run waits instead of failing.</para>
///
/// <para>The installed filter persists in <c>{profileDir}/manual-filter-wheel.json</c>, so a daemon
/// restart does not forget what is in the train.</para>
/// </summary>
public sealed partial class ManualFilterWheelService : IFilterWheelService, IFilterWheelMediator, IRetainedDeviceSource, IDisposable {

    public const string DeviceUniqueId = "ara-manual-filter-wheel";
    public const string DeviceName = "Manual filter wheel";

    /// <summary>The discovery row for the manual wheel. No host or port: nothing is dialled.</summary>
    public static DiscoveredDeviceDto Descriptor { get; } = new(
        UniqueId: DeviceUniqueId,
        Name: DeviceName,
        Type: DeviceType.FilterWheel,
        HostName: string.Empty,
        IpAddress: string.Empty,
        IpPort: 0,
        AlpacaDeviceNumber: 0,
        UseHttps: false);

    public static bool IsManual(DiscoveredDeviceDto? device) =>
        device is not null && string.Equals(device.UniqueId, DeviceUniqueId, StringComparison.Ordinal);

    internal const string StateFileName = "manual-filter-wheel.json";

    private readonly ILogger<ManualFilterWheelService> _logger;
    private readonly IProfileStore? _profileStore;
    private readonly OpenAstroAra.Profile.Interfaces.IProfileService? _profileService;
    private readonly EquipmentEventPublisher? _events;
    private readonly INotificationService? _notifications;
    private readonly string? _stateFile;
    private readonly object _gate = new();
    // Guards the NINA profile's observable filter collection (not safe for concurrent read+write);
    // never taken inside _gate.
    private readonly object _profileFiltersLock = new();

    private DiscoveredDeviceDto? _device;
    private EquipmentConnectionState _state = EquipmentConnectionState.Disconnected;
    private int? _current;
    private int? _pending;
    // Completed and replaced on every change of _current / _pending / _state, so a waiting
    // sequencer change re-checks exactly when something it cares about moved.
    private TaskCompletionSource _changed = NewSignal();
    private bool _disposed;

    public ManualFilterWheelService(
        ILogger<ManualFilterWheelService>? logger = null,
        IProfileStore? profileStore = null,
        OpenAstroAra.Profile.Interfaces.IProfileService? profileService = null,
        EquipmentEventPublisher? events = null,
        INotificationService? notifications = null,
        string? profileDir = null) {
        _logger = logger ?? NullLogger<ManualFilterWheelService>.Instance;
        _profileStore = profileStore;
        _profileService = profileService;
        _events = events;
        _notifications = notifications;
        _stateFile = string.IsNullOrEmpty(profileDir) ? null : Path.Combine(profileDir, StateFileName);
    }

    public DiscoveredDeviceDto? RetainedDevice {
        get {
            lock (_gate) {
                return _disposed ? null : _device;
            }
        }
    }

    // ─── REST surface ────────────────────────────────────────────────────────────────────────

    public Task<FilterWheelDto?> GetAsync(CancellationToken ct) {
        var slots = RefreshSlots();
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_device is null) {
                return Task.FromResult<FilterWheelDto?>(null);
            }
            var connected = _state == EquipmentConnectionState.Connected;
            var runtime = connected
                ? new FilterWheelStateDto(_pending is null ? "idle" : "awaiting_user", _current, _pending)
                : new FilterWheelStateDto("idle", null);
            return Task.FromResult<FilterWheelDto?>(new FilterWheelDto(
                _device.UniqueId, _device.Name, _state, runtime,
                connected ? slots : Array.Empty<FilterSlotDto>(), Manual: true));
        }
    }

    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsManual(request.Device)) {
            throw new ArgumentException("not the manual filter wheel", nameof(request));
        }
        var slots = RefreshSlots();
        var restored = ReadPersistedSlot();
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state == EquipmentConnectionState.Connected) {
                return Task.FromResult(Accepted("filter-wheel.connect", idempotencyKey));
            }
            _device = Descriptor;
            // A remembered filter outside the current label list is not trusted: the user is
            // asked which filter is in rather than handed a slot that no longer exists.
            _current = restored is int r && r >= 0 && r < slots.Count ? r : null;
            _pending = null;
            SetStateLocked(EquipmentConnectionState.Connected);
            SignalLocked();
        }
        // Now that the wheel is connected, sync the NINA filter list straight away: a sequence
        // loaded right after a boot auto-connect resolves SwitchFilter by name against it.
        RefreshSlots();
        LogConnected(slots.Count, restored);
        return Task.FromResult(Accepted("filter-wheel.connect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) {
        bool hadPending;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            hadPending = _pending is not null;
            _pending = null;
            if (_device is not null) {
                SetStateLocked(EquipmentConnectionState.Disconnected);
            }
            SignalLocked(); // a waiting sequencer change fails: the wheel is gone
        }
        if (hadPending) {
            _events?.ManualFilterSwap(null, null, null, null);
        }
        return Task.FromResult(Accepted("filter-wheel.disconnect", idempotencyKey));
    }

    public Task<bool> ForgetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_device is null) {
                return Task.FromResult(false);
            }
            if (_state == EquipmentConnectionState.Connected) {
                throw new InvalidOperationException("the filter wheel is connected — disconnect it before removing it");
            }
            var removed = _device;
            _device = null;
            _events?.Removed(DeviceType.FilterWheel, removed.UniqueId, removed.Name);
            return Task.FromResult(true);
        }
    }

    /// <summary>A REST change (the Equipment card, the Imaging tab): raise the hand-swap prompt and
    /// return at once. Asking for the filter already installed is a no-op.</summary>
    public Task<OperationAcceptedDto> ChangeFilterAsync(FilterChangeRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        var slots = RefreshSlots();
        ValidatePosition(slots, request.Position);
        RequestSwap(slots, request.Position);
        return Task.FromResult(Accepted("filter-wheel.change", idempotencyKey));
    }

    /// <summary>The user reports <paramref name="position"/> installed. Completes a pending change to
    /// that slot (a sequencer change waiting on it resumes); any other slot just records what is
    /// in the train and leaves a pending prompt standing.</summary>
    public Task ReportInstalledAsync(int position, CancellationToken ct) {
        var slots = RefreshSlots();
        ValidatePosition(slots, position);
        int? pending;
        lock (_gate) {
            _current = position;
            if (_pending == position) {
                _pending = null;
            }
            pending = _pending;
            SignalLocked();
        }
        WritePersistedSlot(position);
        var installedName = NameAt(slots, position);
        LogInstalled(installedName, position);
        _events?.ManualFilterSwap(pending, pending is int p ? NameAt(slots, p) : null, position, installedName);
        return Task.CompletedTask;
    }

    /// <summary>Drop a standing hand-swap prompt without a swap. A sequencer change waiting on it
    /// fails (§42.2 retry/failure), as a jammed motorised wheel would.</summary>
    public Task CancelPendingAsync(CancellationToken ct) {
        int? current;
        bool had;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            had = _pending is not null;
            _pending = null;
            current = _current;
            SignalLocked();
        }
        if (had) {
            var slots = RefreshSlots();
            _events?.ManualFilterSwap(null, null, current, current is int c ? NameAt(slots, c) : null);
        }
        return Task.CompletedTask;
    }

    private void ValidatePosition(List<FilterSlotDto> slots, int position) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != EquipmentConnectionState.Connected) {
                throw new InvalidOperationException("filter wheel is not connected");
            }
        }
        if (slots.Count == 0) {
            throw new InvalidOperationException(
                "the manual filter wheel has no filters — add them under Settings → Equipment → Filter wheel");
        }
        if (position < 0 || position >= slots.Count) {
            throw new ArgumentOutOfRangeException(nameof(position), position,
                $"Position is out of range (0..{(slots.Count - 1).ToString(CultureInfo.InvariantCulture)}).");
        }
    }

    // Raise (or re-raise) the prompt for `position`. Returns false when it is already installed.
    private bool RequestSwap(List<FilterSlotDto> slots, int position) {
        int? current;
        lock (_gate) {
            if (_current == position) {
                if (_pending is null) {
                    return false;
                }
                // Asking for what is in the train cancels a prompt for something else.
                _pending = null;
                SignalLocked();
                current = _current;
                _events?.ManualFilterSwap(null, null, current, NameAt(slots, position));
                return false;
            }
            var unchanged = _pending == position;
            _pending = position;
            current = _current;
            SignalLocked();
            if (unchanged) {
                return true; // same prompt already standing: no second notification
            }
        }
        var targetName = NameAt(slots, position);
        var currentName = current is int c ? NameAt(slots, c) : null;
        LogSwapRequested(targetName, position, currentName ?? "unknown");
        _events?.ManualFilterSwap(position, targetName, current, currentName);
        _ = NotifyQuietlyAsync(targetName, currentName);
        return true;
    }

    // ─── Sequencer surface (IFilterWheelMediator) ────────────────────────────────────────────

    public FilterWheelInfo GetInfo() {
        var slots = RefreshSlots();
        var profile = SnapshotProfileFilters();
        lock (_gate) {
            var connected = !_disposed && _state == EquipmentConnectionState.Connected;
            var info = new FilterWheelInfo {
                Connected = connected,
                Name = _device?.Name ?? string.Empty,
                DeviceId = _device?.UniqueId ?? string.Empty,
                // A standing prompt is the manual wheel's "moving": the train is about to change.
                IsMoving = connected && _pending is not null,
            };
            if (connected && _current is int position
                    && FilterWheelService.ResolveFilter(profile, slots, position) is { } resolved) {
                info.SelectedFilter = resolved;
            }
            return info;
        }
    }

    /// <summary>Raise the hand-swap prompt for <paramref name="inputFilter"/> and wait — without a
    /// time limit — until the user reports it installed. Fails the instruction
    /// (<see cref="SequenceEntityFailedException"/>, §42.2) if the prompt is cancelled or replaced
    /// by a different filter, or the wheel is disconnected. Not connected / out of range is a
    /// logged no-op, as for the Alpaca wheel. Sequencer cancellation propagates.</summary>
    public async Task<FilterInfo> ChangeFilter(FilterInfo inputFilter, IProgress<ApplicationStatus>? progress = null, CancellationToken token = default) {
        ArgumentNullException.ThrowIfNull(inputFilter);
        var slots = RefreshSlots();
        var target = (int)inputFilter.Position;
        bool connected;
        lock (_gate) {
            connected = !_disposed && _state == EquipmentConnectionState.Connected;
        }
        if (!connected || target < 0 || target >= slots.Count) {
            LogFilterChangeSkipped(inputFilter.Name, target);
            return inputFilter;
        }
        var name = NameAt(slots, target);
        RequestSwap(slots, target);
        while (true) {
            Task changed;
            lock (_gate) {
                if (_disposed || _state != EquipmentConnectionState.Connected) {
                    throw new SequenceEntityFailedException(
                        $"the manual filter wheel was disconnected while waiting for the {name} filter");
                }
                if (_current == target && _pending is null) {
                    break;
                }
                if (_pending != target) {
                    throw new SequenceEntityFailedException(
                        $"the swap to the {name} filter was cancelled before it was confirmed");
                }
                changed = _changed.Task;
            }
            progress?.Report(new ApplicationStatus { Status = $"Waiting for you to install the {name} filter" });
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
        return FilterWheelService.ResolveFilter(SnapshotProfileFilters(), slots, target) ?? inputFilter;
    }

    // Connection lifecycle is REST-driven; the instruction never calls these.
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
        throw new NotSupportedException("The manual filter wheel has no device driver.");

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;
    public event Func<object, FilterChangedEventArgs, Task>? FilterChanged;

    // ─── Slots: the profile's labels, synced into the NINA filter list ──────────────────────

    /// <summary>The label list as slots: trailing blanks dropped (the default 8-slot list ends in
    /// one), an inner blank named "Filter N" so positions stay stable. Pure, for unit tests.</summary>
    internal static List<string> SlotNames(IReadOnlyList<string>? labels) {
        var names = new List<string>();
        if (labels is null) {
            return names;
        }
        var last = labels.Count - 1;
        while (last >= 0 && string.IsNullOrWhiteSpace(labels[last])) {
            last--;
        }
        for (var i = 0; i <= last; i++) {
            var label = labels[i]?.Trim();
            names.Add(string.IsNullOrEmpty(label)
                ? "Filter " + (i + 1).ToString(CultureInfo.InvariantCulture)
                : label);
        }
        return names;
    }

    /// <summary>Make the NINA filter list match the manual wheel: one entry per slot, named from the
    /// labels. Entries at a still-valid position keep their focus offset and autofocus settings
    /// (they may have been imported from a motorised wheel or edited); extra ones go. Pure.</summary>
    internal static bool SyncProfileFilters(IList<FilterInfo> filters, IReadOnlyList<string> names) {
        var changed = false;
        for (var i = filters.Count - 1; i >= 0; i--) {
            if (filters[i].Position < 0 || filters[i].Position >= names.Count) {
                filters.RemoveAt(i);
                changed = true;
            }
        }
        for (var position = 0; position < names.Count; position++) {
            FilterInfo? existing = null;
            foreach (var f in filters) {
                if (f.Position == position) {
                    existing = f;
                    break;
                }
            }
            if (existing is null) {
                filters.Add(new FilterInfo(names[position], 0, (short)position));
                changed = true;
            } else if (!string.Equals(existing.Name, names[position], StringComparison.Ordinal)) {
                existing.Name = names[position];
                changed = true;
            }
        }
        return changed;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Profile read boundary: a throwing store must degrade to an empty slot list (the card then says to add filters), never fault a status read. CA1031's log-and-recover boundary applies.")]
    private List<FilterSlotDto> RefreshSlots() {
        List<string> names;
        try {
            names = SlotNames(_profileStore?.GetFilterWheelLabels().Labels);
        } catch (Exception ex) {
            LogLabelsReadFailed(ex);
            names = [];
        }
        var offsets = new Dictionary<int, int>();
        lock (_profileFiltersLock) {
            var filters = _profileService?.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters;
            bool connected;
            lock (_gate) {
                connected = _state == EquipmentConnectionState.Connected;
            }
            // Only the CONNECTED manual wheel owns the profile list; a disconnected one must not
            // rewrite what a motorised wheel imported.
            if (filters is not null && connected) {
                SyncProfileFilters(filters, names);
            }
            if (filters is not null) {
                foreach (var f in filters) {
                    offsets[f.Position] = f.FocusOffset;
                }
            }
        }
        var slots = new List<FilterSlotDto>(names.Count);
        for (var i = 0; i < names.Count; i++) {
            slots.Add(new FilterSlotDto(i, names[i], offsets.TryGetValue(i, out var o) ? o : 0));
        }
        return slots;
    }

    private List<FilterInfo>? SnapshotProfileFilters() {
        lock (_profileFiltersLock) {
            var filters = _profileService?.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters;
            return filters is null ? null : new List<FilterInfo>(filters);
        }
    }

    private static string NameAt(List<FilterSlotDto> slots, int position) =>
        position >= 0 && position < slots.Count
            ? slots[position].Name
            : "Filter " + (position + 1).ToString(CultureInfo.InvariantCulture);

    // ─── Persistence of the installed filter ────────────────────────────────────────────────

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "State-file read boundary: a missing, corrupt or unreadable file means 'unknown filter' (the user is asked), never a failed connect. CA1031's log-and-recover boundary applies.")]
    private int? ReadPersistedSlot() {
        if (_stateFile is null || !File.Exists(_stateFile)) {
            return null;
        }
        try {
            var node = JsonNode.Parse(File.ReadAllText(_stateFile));
            return node?["current_slot"]?.GetValue<int>();
        } catch (Exception ex) {
            LogStateReadFailed(ex);
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "State-file write boundary: failing to remember the filter across a restart must not fail the user's confirm; it is logged. CA1031's log-and-recover boundary applies.")]
    private void WritePersistedSlot(int position) {
        if (_stateFile is null) {
            return;
        }
        try {
            var tmp = _stateFile + ".tmp";
            File.WriteAllText(tmp, new JsonObject { ["current_slot"] = position }.ToJsonString());
            File.Move(tmp, _stateFile, overwrite: true);
        } catch (Exception ex) {
            LogStateWriteFailed(ex);
        }
    }

    // ─── Notification ────────────────────────────────────────────────────────────────────────

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Notification store faults must never fail or block a filter change. Log-and-recover boundary.")]
    private async Task NotifyQuietlyAsync(string target, string? current) {
        if (_notifications is null) {
            return;
        }
        try {
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: DateTimeOffset.UtcNow,
                Severity: NotificationSeverity.Warning,
                Category: NotificationCategory.Equipment,
                Title: $"Install the {target} filter",
                Message: current is null
                    ? $"The manual filter wheel needs the {target} filter. Swap it in, then confirm in Ara."
                    : $"The manual filter wheel needs the {target} filter (now {current}). Swap it in, then confirm in Ara.",
                Read: false,
                Dismissed: false,
                DismissedUtc: null,
                Payload: null,
                RelatedEntityType: null,
                RelatedEntityId: null), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogNotifyFailed(ex);
        }
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────────────────

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Caller holds _gate.
    private void SignalLocked() {
        var old = _changed;
        _changed = NewSignal();
        old.TrySetResult();
    }

    // Caller holds _gate.
    private void SetStateLocked(EquipmentConnectionState state) {
        if (_state == state) {
            return;
        }
        _state = state;
        _events?.StateChanged(DeviceType.FilterWheel, _device?.UniqueId, _device?.Name, state);
    }

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    public void Dispose() {
        lock (_gate) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            SignalLocked();
        }
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Manual filter wheel connected: {Count} filters, installed slot {Slot} (from the last session)")]
    private partial void LogConnected(int count, int? slot);

    [LoggerMessage(Level = LogLevel.Information, Message = "Manual filter wheel: asking the user to install '{Name}' (slot {Position}); installed now: {Current}")]
    private partial void LogSwapRequested(string name, int position, string current);

    [LoggerMessage(Level = LogLevel.Information, Message = "Manual filter wheel: the user reports '{Name}' (slot {Position}) installed")]
    private partial void LogInstalled(string name, int position);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Filter change to '{Name}' (position {Position}) skipped on the manual filter wheel (not connected or position out of range)")]
    private partial void LogFilterChangeSkipped(string? name, int position);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Manual filter wheel: reading the filter labels failed; no slots this read")]
    private partial void LogLabelsReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Manual filter wheel: reading the installed filter from the state file failed")]
    private partial void LogStateReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Manual filter wheel: writing the installed filter to the state file failed")]
    private partial void LogStateWriteFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Manual filter wheel: posting the swap notification failed")]
    private partial void LogNotifyFailed(Exception ex);
}
