#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace OpenAstroAra.Server.Services;

public interface IEquipmentIdentityReconciler {
    /// <summary>Re-read the identity of every device that is connected right now (and the guide camera when
    /// the guider is connected), bounded by <paramref name="ct"/>; never throws. The sequence start awaits
    /// this so a swap made since the last connect is caught before the first frame.</summary>
    Task ReconcileConnectedAsync(CancellationToken ct);

    /// <summary>Record what a slot answers now: a first sighting is stored quietly, a changed UniqueID posts
    /// the gear-change notice and clears the derived values measured through the old device.</summary>
    Task<EquipmentIdentityOutcome> ObserveAsync(GearSlot slot, AlpacaConfiguredDevice live, CancellationToken ct);
}

/// <summary>
/// #1362 — re-reads each device's Alpaca identity when it connects and when a sequence starts, and reconciles
/// it with the profile. The device services already take the driver's word for the facts it can report
/// (sensor geometry and electronics on the camera, slot names on the wheel, pixel size on the guide camera);
/// this is the part that notices the device itself changed behind the same slot: it stores the
/// <c>UniqueID</c> per gear slot, tells the user in the inbox when it differs, and clears the calibrations
/// that were measured through the old device so nothing stale feeds the next run.
/// <para>Hooked on <see cref="EquipmentEventPublisher.Transition"/>: a Connected transition queues a
/// reconcile off the service's lock; the connect's outcome never depends on it. Reads only the local
/// Alpaca server's management API (offline-first), best effort, 5 s bounded.</para>
/// </summary>
public sealed partial class EquipmentIdentityReconciler : IEquipmentIdentityReconciler, IDisposable {

    private static readonly TimeSpan ReconcileTimeout = TimeSpan.FromSeconds(5);

    private readonly IProfileStore _store;
    private readonly IAlpacaManagementClient _alpaca;
    private readonly Func<DeviceType, DiscoveredDeviceDto?> _retained;
    private readonly INotificationService? _notifications;
    private readonly Func<IGuiderLiveService?>? _guiderLive;
    private readonly Func<CancellationToken, Task>? _guideCameraReconcile;
    private readonly EquipmentEventPublisher? _events;
    private readonly ILogger<EquipmentIdentityReconciler> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<DeviceType, byte> _connected = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _starts;

    /// <param name="retained">The device an Ara service holds for a type (host, port, number), or null.</param>
    /// <param name="guiderLive">Resolved lazily (the live service needs the guider): clears the guider's own
    /// calibration after a mount or guide-camera swap.</param>
    /// <param name="guideCameraReconcile">The guider's own connect-time reconcile, re-run by
    /// <see cref="ReconcileConnectedAsync"/> when the guider is connected.</param>
    public EquipmentIdentityReconciler(IProfileStore store, IAlpacaManagementClient alpaca,
            Func<DeviceType, DiscoveredDeviceDto?> retained, INotificationService? notifications = null,
            Func<IGuiderLiveService?>? guiderLive = null, Func<CancellationToken, Task>? guideCameraReconcile = null,
            EquipmentEventPublisher? events = null, ILogger<EquipmentIdentityReconciler>? logger = null,
            Func<DateTimeOffset>? clock = null) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _alpaca = alpaca ?? throw new ArgumentNullException(nameof(alpaca));
        _retained = retained ?? throw new ArgumentNullException(nameof(retained));
        _notifications = notifications;
        _guiderLive = guiderLive;
        _guideCameraReconcile = guideCameraReconcile;
        _events = events;
        _logger = logger ?? NullLogger<EquipmentIdentityReconciler>.Instance;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (_events is not null) {
            _events.Transition += OnTransition;
        }
    }

    /// <summary>How many connect-triggered reconciles started, and the last one, for tests to await.</summary>
    internal int Starts => Volatile.Read(ref _starts);
    internal Task LastReconcile { get; private set; } = Task.CompletedTask;

    /// <summary>The device types this reconciler currently believes connected (from the publisher's transitions).</summary>
    internal IReadOnlyCollection<DeviceType> Connected => _connected.Keys.ToArray();

    private void OnTransition(object? sender, EquipmentTransitionEventArgs e) {
        var (type, state) = (e.DeviceType, e.State);
        if (EquipmentIdentityReconcile.SlotOf(type) is null) {
            return;
        }
        if (state != EquipmentConnectionState.Connected) {
            _connected.TryRemove(type, out _);
            return;
        }
        _connected[type] = 0;
        // The publisher is called under the device service's lock: hand off before touching the network.
        LastReconcile = Task.Run(() => ReconcileDeviceAsync(type, CancellationToken.None));
        Interlocked.Increment(ref _starts);
    }

    public async Task ReconcileConnectedAsync(CancellationToken ct) {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ReconcileTimeout);
        var work = _connected.Keys.Select(t => ReconcileDeviceAsync(t, cts.Token)).ToList();
        if (_guideCameraReconcile is not null) {
            work.Add(RunGuideCameraAsync(cts.Token));
        }
        await Task.WhenAll(work).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort pre-start pass: the guider's reconcile contains its own errors, and a stand-in that throws must not fail the sequence start. Log-and-recover.")]
    private async Task RunGuideCameraAsync(CancellationToken ct) {
        try {
            await _guideCameraReconcile!(ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            LogReconcileFailed(ex, "guidecamera");
        }
    }

    /// <summary>Read one device's live identity from its server's management API and observe it.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort boundary after a successful connect: an unreachable management API, a parse error or a store failure must leave the profile as it is and never surface into the connect. Log-and-recover.")]
    internal async Task ReconcileDeviceAsync(DeviceType type, CancellationToken ct) {
        if (EquipmentIdentityReconcile.SlotOf(type) is not { } slot) {
            return;
        }
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ReconcileTimeout);
            if (_retained(type) is not { } device) {
                return;
            }
            var host = string.IsNullOrWhiteSpace(device.IpAddress) ? device.HostName : device.IpAddress;
            var devices = await _alpaca.GetConfiguredDevicesAsync(host, device.IpPort, cts.Token).ConfigureAwait(false);
            if (!devices.TryGetValue(EquipmentIdentityReconcile.DeviceKey(type, device.AlpacaDeviceNumber), out var live)) {
                return; // unreachable or not listed: the identity is unknown, keep what the profile has
            }
            await ObserveAsync(slot, live, cts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            var key = EquipmentIdentityReconcile.Key(slot);
            LogReconcileFailed(ex, key);
        }
    }

    public async Task<EquipmentIdentityOutcome> ObserveAsync(GearSlot slot, AlpacaConfiguredDevice live, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(live);
        var key = EquipmentIdentityReconcile.Key(slot);
        DeviceIdentityDto? stored;
        EquipmentIdentityOutcome outcome;
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            var identity = _store.GetEquipmentIdentity();
            identity.Devices.TryGetValue(key, out stored);
            outcome = EquipmentIdentityReconcile.Decide(stored, live);
            if (outcome == EquipmentIdentityOutcome.Unchanged) {
                return outcome;
            }
            var next = new Dictionary<string, DeviceIdentityDto>(identity.Devices) {
                [key] = new DeviceIdentityDto(live.UniqueId.Trim(), live.Name ?? string.Empty, _clock()),
            };
            _store.PutEquipmentIdentity(new EquipmentIdentityDto(next));
            if (outcome == EquipmentIdentityOutcome.Changed) {
                Invalidate(slot);
            }
        } finally {
            _writeGate.Release();
        }
        var oldName = GuiderCameraReconcile.ForLog(stored?.Name);
        var newName = GuiderCameraReconcile.ForLog(live.Name);
        var oldId = GuiderCameraReconcile.ForLog(stored?.UniqueId);
        var newId = GuiderCameraReconcile.ForLog(live.UniqueId);
        if (outcome == EquipmentIdentityOutcome.FirstSeen) {
            LogFirstSeen(key, newName, newId);
            return outcome;
        }
        LogChanged(key, oldName, oldId, newName, newId);
        await ClearGuiderCalibrationAsync(slot, ct).ConfigureAwait(false);
        await NotifyAsync(slot, oldName, newName, ct).ConfigureAwait(false);
        return outcome;
    }

    // The derived values measured through the old device (playbook §30.7.2). Under the write gate.
    private void Invalidate(GearSlot slot) {
        switch (slot) {
            case GearSlot.Camera:
                _store.PutFocusCalibration(null);
                _store.PutTrackingCalibration(null);
                _store.PutSkyModelCalibration(null);
                break;
            case GearSlot.Focuser:
                _store.PutFocusCalibration(null);
                break;
            case GearSlot.Telescope:
                _store.PutTrackingCalibration(null);
                break;
            case GearSlot.GuideCamera:
                var state = _store.GetCalibrationState();
                _store.PutCalibrationState(new CalibrationStateDto(
                    DarkLibrary: state.DarkLibrary with { Valid = false },
                    DefectMap: state.DefectMap with { Valid = false }));
                break;
            default:
                break;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The guider may be disconnected or mid-recovery; a failed clear is logged and the notice still tells the user the calibration is suspect.")]
    private async Task ClearGuiderCalibrationAsync(GearSlot slot, CancellationToken ct) {
        if (slot is not (GearSlot.Telescope or GearSlot.GuideCamera) || _guiderLive?.Invoke() is not { } live) {
            return;
        }
        try {
            await live.ClearCalibrationAsync(ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogGuiderClearFailed(ex);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The inbox is best effort: a store fault must not undo the profile update that already happened.")]
    private async Task NotifyAsync(GearSlot slot, string oldName, string newName, CancellationToken ct) {
        if (_notifications is null) {
            return;
        }
        var label = EquipmentIdentityReconcile.Label(slot);
        var from = oldName.Length == 0 ? "the previous device" : oldName;
        var to = newName.Length == 0 ? "a new device" : newName;
        try {
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(), PostedUtc: _clock(), Severity: NotificationSeverity.Warning,
                Category: NotificationCategory.Equipment, Title: $"{label} changed: {from} → {to}",
                Message: $"The {label.ToLowerInvariant()} behind the same connection is a different device than last time. "
                    + EquipmentIdentityReconcile.ChangeAdvice(slot),
                Read: false, Dismissed: false, DismissedUtc: null, Payload: null,
                RelatedEntityType: "equipment", RelatedEntityId: EquipmentIdentityReconcile.Key(slot)), ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogNotifyFailed(ex);
        }
    }

    public void Dispose() {
        if (_events is not null) {
            _events.Transition -= OnTransition;
        }
        _writeGate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Gear slot {Slot} first seen: '{Name}' ({UniqueId}) recorded in the profile (#1362)")]
    private partial void LogFirstSeen(string slot, string name, string uniqueId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Gear slot {Slot} changed: '{OldName}' ({OldUniqueId}) -> '{NewName}' ({NewUniqueId}); derived calibrations cleared, user told (#1362)")]
    private partial void LogChanged(string slot, string oldName, string oldUniqueId, string newName, string newUniqueId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Identity reconcile for {Slot} failed; profile left unchanged (#1362)")]
    private partial void LogReconcileFailed(Exception ex, string slot);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not clear the guider's calibration after a gear change (#1362)")]
    private partial void LogGuiderClearFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not post the gear-change notice (#1362)")]
    private partial void LogNotifyFailed(Exception ex);
}
