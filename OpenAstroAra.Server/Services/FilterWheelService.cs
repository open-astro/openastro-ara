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
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e — sixth real Alpaca-backed device service: connects to a discovered Alpaca FilterWheel, serves its
/// slots (position/name/focus-offset, read once) + live runtime (state + current slot, §32.4-cached) over
/// the §6 REST surface, and selects a slot via <see cref="ChangeFilterAsync"/> (a §60.5 202-Accepted
/// background operation). Its lifecycle (generation supersede, §32.4 background-refresh cache, the §42.3
/// probe and read health) is the shared <see cref="DeviceLifecycle{TDevice}"/> (ADR 0011); the #1066
/// first-connect home rides on its session hooks.
/// Mirrors the established control-device template. REST-only — the <c>IFilterWheelMediator</c>
/// unification (SwitchFilter instruction) is the follow-up.
/// </summary>
public sealed partial class FilterWheelService : IFilterWheelService, IRetainedDeviceSource, IRecoverableDevice, IDisposable {

    private static readonly FilterWheelStateDto IdleRuntime = new("idle", null);

    private readonly ILogger<FilterWheelService> _logger;
    private readonly IEquipmentFaultSink? _faults;
    // ADR 0011 — connect, disconnect, forget, refresh, probe, read health and the desired state.
    private readonly DeviceLifecycle<IFilterWheelDevice> _lifecycle;
    private Func<DiscoveredDeviceDto, IFilterWheelDevice> _createDevice = AlpacaFilterWheelDevice.Create;
    // The Sequencer-facing profile (null in REST-only unit tests): the §14e mediator partial imports
    // the connected wheel's slot list into ActiveProfile.FilterWheelSettings.FilterWheelFilters
    // (NINA's import-on-connect semantics) so SwitchFilter can resolve its filter by name/position.
    private readonly OpenAstroAra.Profile.Interfaces.IProfileService? _profileService;
    // #1075 — the Ara profile store (null in REST-only unit tests): the first-connect home is a
    // profile policy (FilterWheelPolicyDto.HomeOnFirstConnect) the user can turn off.
    private readonly IProfileStore? _profileStore;
    private readonly FilterFocusOffset? _focusOffset; // #1667 — null in tests that don't exercise offsets
    // #1675 — the saved offsets as the store's Changed last reported them: the store's getter takes the lock
    // its writes hold across the disk write, so the status poll must not read it.
    private volatile IReadOnlyList<FilterFocusOffsetDto>? _savedOffsets;
    // Session state below is guarded by the lifecycle's gate, like the connection state.
    // #1667 — the slot the focuser is compensated for; kept across an unconfirmed change so its retry still moves.
    private int? _focusBaseline;
    private IReadOnlyList<FilterSlotDto>? _slots; // configured names/offsets, read once on connect
    private FilterWheelStateDto _runtime = IdleRuntime;
    // #1066 — devices already homed to the default slot this daemon session (by UniqueId). Only the
    // FIRST connect of a wheel homes it: an auto-reconnect after a link blip must never yank a
    // running sequence's filter back to L.
    private readonly HashSet<string> _homedDevices = [];
    // #1066 — the first-connect home is CLAIMED at connect (so a reconnect can never home) and
    // DECIDED on the first refresh tick that reads a known position (a wheel mid-move at connect
    // reports -1 and would otherwise never be parked). Cleared, without a home, by: disconnect /
    // connection loss / a newer connect (TakePendingHomeNeverDecidedLocked, logged), an explicit filter change
    // (RetirePendingHome — the requested slot wins), and the tick bound (logged).
    private bool _pendingHome;
    // #1079 — the in-flight home token: the home's Position write is dispatched on a Task.Run after
    // the decision is taken under _gate, and a change accepted in that window used to race it. The
    // dispatch captures this generation; every explicit change, (dis)connect and the next connect
    // bump it, and the home task re-checks it right before its write and steps aside if it moved.
    private long _homeGeneration;
    private string _pendingHomeDevice = string.Empty;
    // Bounded window: the seed read plus a few 2 s ticks. A driver that reports -1 for longer is
    // not "just connected" any more; the decision is dropped (and logged) rather than homing the
    // wheel minutes later.
    private int _pendingHomeTicks;
    private const int MaxPendingHomeTicks = 4;

    public FilterWheelService(
        ILogger<FilterWheelService>? logger = null,
        OpenAstroAra.Profile.Interfaces.IProfileService? profileService = null,
        EquipmentEventPublisher? events = null,
        IEquipmentFaultSink? faults = null,
        IProfileStore? profileStore = null,
        FilterFocusOffset? focusOffset = null) {
        _focusOffset = focusOffset;
        _logger = logger ?? NullLogger<FilterWheelService>.Instance;
        _profileStore = profileStore;
        _faults = faults;
        _profileService = profileService;
        if (_profileStore is not null) {
            _profileStore.Changed += OnProfileChanged;
            OnProfileChanged(null, EventArgs.Empty);
        }
        _lifecycle = new DeviceLifecycle<IFilterWheelDevice>(this, DeviceType.FilterWheel, "filter wheel",
            d => _createDevice(d), ReadRuntime, ResetSessionLocked,
            DeviceHealthTracker.DefaultThreshold, _logger, events, faults, PrepareSession);
    }

    private object _gate => _lifecycle.Gate;

    // #1265 — bench-test knob: re-arms the refresh timer at a shorter cadence. Init-only, never used by the daemon.
    internal TimeSpan RefreshPeriod { init => _lifecycle.RefreshPeriod = value; }

    // Test seam (ADR 0011 rule 4): the adapter the lifecycle builds for a selected device.
    internal Func<DiscoveredDeviceDto, IFilterWheelDevice> DeviceFactory { init => _createDevice = value; }

    // #1127 — the record the card names. The remembered-selection store and this can diverge
    // (a store-only DELETE /remembered keeps the device here), so the reconnector falls back to it.
    public DiscoveredDeviceDto? RetainedDevice => _lifecycle.RetainedDevice;

    public DesiredConnection? DesiredConnection => _lifecycle.Desired;

    // #1246 — true once a refresh tick since the current connect read the essential read cleanly.
    internal bool HasCleanReadSinceConnect => _lifecycle.HasCleanReadSinceConnect;

    public bool TryRecover(DiscoveredDeviceDto device) => _lifecycle.TryRecover(device);

    private void OnProfileChanged(object? sender, EventArgs e) => _savedOffsets = _profileStore?.GetFilterFocusOffsets()?.Offsets;

    public Task<FilterWheelDto?> GetAsync(CancellationToken ct) {
        FilterWheelDto dto;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_lifecycle.IsDisposed, this);
            // null ONLY when no device has ever been selected (-> 404); a retained device after
            // disconnect returns Disconnected + idle runtime, consistent across the device services.
            var device = _lifecycle.Device;
            if (device is null) {
                return Task.FromResult<FilterWheelDto?>(null);
            }
            var state = _lifecycle.State;
            var connected = state == EquipmentConnectionState.Connected;
            var runtime = connected ? _runtime : IdleRuntime;
            var slots = (connected ? _slots : null) ?? Array.Empty<FilterSlotDto>();
            dto = new FilterWheelDto(device.UniqueId, device.Name, state, runtime, slots);
        }
        // #1675 — re-applied on every read so a Save shows at once.
        return Task.FromResult<FilterWheelDto?>(dto with { Slots = WithSavedOffsets(dto.Slots) });
    }

    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Device);
        return _lifecycle.ConnectAsync(request.Device, idempotencyKey);
    }

    public Task<bool> ForgetAsync(CancellationToken ct) => _lifecycle.ForgetAsync();

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) =>
        _lifecycle.DisconnectAsync(idempotencyKey);

    // Caller holds the gate: a connection that ends (disconnect, loss, a newer connect) forgets the last one's
    // session. The slot list must not outlive it, so a ChangeFilterAsync while disconnected validates against
    // the live (absent) slots and reports "not connected" rather than an ArgumentOutOfRange against a prior
    // session's slots. A home still pending leaves a journal line (off the gate: a slow log sink must never sit
    // on the device lock) and an in-flight home for the old wheel is stale (#1079).
    private void ResetSessionLocked() {
        var neverDecided = TakePendingHomeNeverDecidedLocked();
        _homeGeneration++;
        _slots = null;          // re-read for the new device
        _runtime = IdleRuntime; // don't serve a prior device's runtime
        _focusBaseline = null;
        if (neverDecided is not null) {
            _ = Task.Run(() => LogHomeNeverDecided(neverDecided), CancellationToken.None);
        }
    }

    // #1075 — the policy is read off the gate (the store has its own lock) once the connect succeeded. The
    // returned action runs under the gate on adopt: #1066 — claim the once-per-session home NOW, position known
    // or not, so a later reconnect of this wheel (auto or manual) can never home it. The claim is consumed
    // either way: turning the policy on later never homes an already-connected (or reconnecting) wheel
    // mid-session. A previous wheel's still-pending decision was already superseded by the session reset.
    private Action PrepareSession(IFilterWheelDevice client) {
        var homeEnabled = HomeOnFirstConnectEnabled();
        return () => {
            var device = _lifecycle.Device;
            if (device is null) {
                return;
            }
            var claimed = ClaimFirstConnectHome(device.UniqueId);
            _pendingHome = claimed && homeEnabled;
            if (claimed && !homeEnabled) {
                _ = Task.Run(() => LogHomeSkippedByPolicy(device.Name), CancellationToken.None);
            }
            _homeGeneration++; // #1079 — a previous wheel's in-flight home is stale now
            _pendingHomeDevice = device.Name;
            _pendingHomeTicks = 0;
        };
    }

    public Task<OperationAcceptedDto> ChangeFilterAsync(FilterChangeRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        // Dispose -> range -> connected ordering, aligned with the other device services.
        IFilterWheelDevice? client;
        IReadOnlyList<FilterSlotDto>? slots;
        lock (_gate) {
            client = _lifecycle.ConnectedDevice();
            slots = _slots;
            _focusBaseline ??= _runtime.CurrentSlot;
        }
        // ASCOM addresses slots by a short; validate before the narrowing cast so an out-of-range
        // position fails loudly (with a 4xx) instead of silently wrapping (e.g. (short)32768 ==
        // -32768) and getting a 202 for a logically-invalid request. This also covers the window
        // before _slots is loaded, where IsPositionOutOfRange defers to the device.
        if (request.Position is < 0 or > short.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(request), request.Position,
                "Position is out of range for an ASCOM FilterWheel (0..32767).");
        }
        if (IsPositionOutOfRange(slots, request.Position)) {
            throw new ArgumentOutOfRangeException(nameof(request), request.Position,
                $"Position is out of range (0..{(slots!.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}).");
        }
        if (client is null) {
            throw new InvalidOperationException("filter wheel is not connected");
        }
        var position = request.Position;
        // #1066 — an explicitly requested slot is a deliberate position: it retires the pending
        // first-connect home, so the change's own follow-up refresh can never override it with 0.
        RetirePendingHome();
        _ = Task.Run(() => ChangeInBackgroundAsync(client, position), CancellationToken.None);
        return Task.FromResult(Accepted("filter-wheel.change", idempotencyKey));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background change boundary: setting the blocking ASCOM Position can throw arbitrary driver/HTTP exceptions, and a concurrent Disconnect/Dispose can dispose the captured client before/during the change; any escape must be contained and logged, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    private async Task ChangeInBackgroundAsync(IFilterWheelDevice client, int position) {
        try {
            client.Position = (short)position; // triggers the wheel to rotate; Position reads -1 while moving
            _lifecycle.Refresh();
            if (_focusOffset is { Enabled: true }) {
                if (await WaitForPositionAsync(client, (short)position, CancellationToken.None).ConfigureAwait(false)) {
                    await ApplyFocusOffsetAsync(position, CancellationToken.None).ConfigureAwait(false);
                }
            } else {
                // Offsets off: no move, but the first change after they are turned on must start from this filter.
                lock (_gate) {
                    _focusBaseline = position;
                }
            }
        } catch (Exception ex) {
            LogChangeFailed(ex, position);
        }
    }

    /// <summary>The slot a freshly connected wheel is parked on: 0, conventionally L(uminance).</summary>
    internal const int DefaultSlot = 0;

    /// <summary>#1075 — the profile's first-connect home policy; on when no store is wired (the
    /// pre-#1075 behaviour) or the read fails (never let a profile hiccup change hardware policy
    /// silently — the default is the documented one).</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Profile read boundary: a throwing store must fall back to the documented default, never fault the connect. CA1031's log-and-recover boundary applies.")]
    private bool HomeOnFirstConnectEnabled() {
        try {
            return _profileStore?.GetFilterWheelPolicy().HomeOnFirstConnect ?? true;
        } catch (Exception ex) {
            LogPolicyReadFailed(ex);
            return true;
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "FilterWheel '{Device}' first connect: the profile's home-on-first-connect policy is off; leaving the wheel where it is (#1075)")]
    private partial void LogHomeSkippedByPolicy(string device);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "FilterWheel policy read failed; assuming home-on-first-connect (#1075)")]
    private partial void LogPolicyReadFailed(Exception ex);

    /// <summary>Whether a wheel seeded on <paramref name="currentSlot"/> needs the first-connect
    /// home. Only a KNOWN, non-default position does; already-at-default is a no-op that still
    /// counts as homed.</summary>
    internal static bool NeedsHomeToDefaultSlot(int currentSlot) => currentSlot != DefaultSlot;

    /// <summary>Claim the once-per-session first-connect home for <paramref name="deviceId"/>:
    /// true the first time, false on every later (re)connect of the same wheel.</summary>
    internal bool ClaimFirstConnectHome(string deviceId) {
        lock (_gate) {
            return _homedDevices.Add(deviceId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "FilterWheel '{Device}' first connect: homing from slot {Slot} to the default slot 0 (#1066)")]
    private partial void LogHomingToDefault(string device, int slot);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "FilterWheel '{Device}' disconnected before it ever reported a known position; the first-connect home to slot 0 was never issued and will not be retried this session (#1066)")]
    private partial void LogHomeNeverDecided(string device);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "FilterWheel '{Device}' still reported no known position on refresh {Ticks} after connect (seed read = 1); the first-connect home to slot 0 is dropped and will not be retried this session (#1066)")]
    private partial void LogHomeWindowExpired(string device, int ticks);

    /// <summary>An explicit filter change (REST or a sequence SwitchFilter) retires the pending
    /// first-connect home: the user or the plan chose a slot, so the daemon must not park the wheel
    /// on 0 behind their back.</summary>
    private void RetirePendingHome() {
        lock (_gate) {
            _pendingHome = false;
            _homeGeneration++; // #1079 — an already-dispatched home steps aside too
        }
    }

    /// <summary>#1079 — whether a home dispatched at <paramref name="dispatched"/> is still wanted
    /// now that the generation reads <paramref name="current"/>: any explicit change, disconnect,
    /// connection loss, newer connect or dispose in between retires it.</summary>
    internal static bool HomeStillWanted(long dispatched, long current) => dispatched == current;

    /// <summary>The current home generation and live client, for the bench test that invokes
    /// <see cref="HomeInBackground"/> with a stale token.</summary>
    internal (long Generation, IFilterWheelDevice? Client) HomeTokenForTest() {
        lock (_gate) {
            return (_homeGeneration, _lifecycle.Live);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background home boundary: same as ChangeInBackground — a throwing Position write or a client disposed mid-write must be contained and logged, never fault the fire-and-forget task. CA1031's log-and-recover boundary applies.")]
    internal void HomeInBackground(IFilterWheelDevice client, long dispatched) {
        try {
            bool superseded;
            lock (_gate) {
                // Re-check under the gate right before the write: a change accepted after the
                // decision (or a disconnect / newer connect) makes this home stale. The log line
                // is emitted after the gate is released (a slow log sink must never sit on the
                // device lock — same rule as LogHomeNeverDecided / LogConnectionLost).
                superseded = !HomeStillWanted(dispatched, _homeGeneration) || !_lifecycle.IsLive(client);
            }
            if (superseded) {
                LogHomeSuperseded();
                return;
            }
            client.Position = (short)DefaultSlot; // Position reads -1 while moving
            _lifecycle.Refresh();
        } catch (Exception ex) {
            LogChangeFailed(ex, DefaultSlot);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "FilterWheel first-connect home to slot 0 stepped aside: a filter change (or a (dis)connect) arrived before its write was issued (#1079)")]
    private partial void LogHomeSuperseded();

    // Caller holds _gate. A connection ending with the home still pending leaves a trace in the
    // journal so a wheel that never parked on L is diagnosable; the line itself is emitted by the
    // caller AFTER the gate is released (a slow log sink must never sit on the device lock).
    private string? TakePendingHomeNeverDecidedLocked() {
        if (!_pendingHome) {
            return null;
        }
        _pendingHome = false;
        return _pendingHomeDevice;
    }

    // The runtime read the lifecycle runs each tick. Position is the essential read (§42.3 second channel). The
    // slot list is read here too until it is known, and the #1066 first-connect home is decided in the commit
    // the first time the position reads as known.
    private DeviceRead ReadRuntime(IFilterWheelDevice client) {
        bool needSlots;
        lock (_gate) {
            needSlots = _slots is null;
        }
        var runtime = ReadRuntime(client, out var readError);
        var slots = needSlots ? ReadSlots(client) : null;
        var adoptedSlots = false;
        int? homeFrom = null;
        var homeDevice = string.Empty;
        long homeGeneration = 0;
        (string Device, int Ticks)? windowExpired = null;
        return new DeviceRead(readError,
            CommitLocked: () => {
                _runtime = runtime;
                if (slots is not null) {
                    _slots = slots;
                    adoptedSlots = true;
                }
                // #1066 — the first KNOWN position settles this session's home decision, whether
                // or not a move is needed (already-at-0 counts as homed). Unknown (-1 / unreadable)
                // leaves it pending for a few more ticks, then it is dropped.
                if (_pendingHome) {
                    if (runtime.CurrentSlot is int slot) {
                        _pendingHome = false;
                        if (NeedsHomeToDefaultSlot(slot)) {
                            homeFrom = slot;
                            homeDevice = _pendingHomeDevice;
                            homeGeneration = _homeGeneration; // #1079 — the token the write re-checks
                        }
                    } else if (++_pendingHomeTicks > MaxPendingHomeTicks) {
                        _pendingHome = false;
                        windowExpired = (_pendingHomeDevice, _pendingHomeTicks); // logged after the gate
                    }
                }
            },
            AfterCommit: () => {
                if (windowExpired is { } expired) {
                    LogHomeWindowExpired(expired.Device, expired.Ticks);
                }
                if (homeFrom is int from) {
                    // Daemon policy: happens with no client attached (boot auto-connect), never on a reconnect.
                    LogHomingToDefault(homeDevice, from);
                    _ = Task.Run(() => HomeInBackground(client, homeGeneration), CancellationToken.None);
                }
                if (adoptedSlots) {
                    // First slot read for this connection: import the device's filter list into the
                    // active profile (outside the lock — it walks an observable collection).
                    ImportProfileFilters(slots!);
                    MirrorSlotLabels(slots!);
                }
            });
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a transiently-failing Position read reports idle (and logs at Debug) rather than failing the snapshot. CA1031's log-and-recover boundary applies.")]
    private FilterWheelStateDto ReadRuntime(IFilterWheelDevice c, out string? readError) {
        short pos;
        readError = null;
        // Log at Debug, not silently: a persistent Position fault would otherwise present as a
        // steady "idle" with no diagnostic trail — and the health tracker turns a streak into a
        // degraded fault (§42.3 second channel).
        try { pos = c.Position; }
        catch (ASCOM.NotImplementedException ex) { LogPositionReadFailed(ex); return IdleRuntime; } // not a fault: the driver has no Position
        catch (Exception ex) { LogPositionReadFailed(ex); readError = ex.Message; return IdleRuntime; }
        // ASCOM reports Position == -1 while the wheel is moving between slots.
        return pos < 0 ? new FilterWheelStateDto("moving", null) : new FilterWheelStateDto("idle", pos);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Slots read boundary: if Names fails, return null so we DON'T cache an empty/partial slot list (the next tick retries). CA1031's log-and-recover boundary applies.")]
    private List<FilterSlotDto>? ReadSlots(IFilterWheelDevice c) {
        string[] names;
        try { names = c.Names; } catch (Exception) { return null; }
        if (names is null) {
            return null;
        }
        int[] offsets;
        // Log at Debug: unlike Names (which returns null and retries), a FocusOffsets failure
        // persists (offsets are read-once), so a driver that doesn't implement them would silently
        // show all-zero offsets forever with no diagnostic trail.
        try { offsets = c.FocusOffsets; } catch (Exception ex) { LogFocusOffsetsFailed(ex); offsets = Array.Empty<int>(); }
        var slots = new List<FilterSlotDto>(names.Length);
        for (var i = 0; i < names.Length; i++) {
            var offset = i < (offsets?.Length ?? 0) ? offsets![i] : 0;
            slots.Add(new FilterSlotDto(Position: i, Name: names[i] ?? string.Empty, FocusOffset: offset));
        }
        return WithSavedOffsets(slots);
    }

    /// <summary>#1675 — each slot's effective offset: the one the offset calibration saved with the profile,
    /// else the driver's.</summary>
    private List<FilterSlotDto> WithSavedOffsets(IReadOnlyList<FilterSlotDto> slots) {
        var saved = _savedOffsets;
        return slots.Select(s => saved?.LastOrDefault(o => o.Position == s.Position) is { } o ? s with { FocusOffset = o.Offset } : s).ToList();
    }

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    // Extracted (internal) for direct unit testing. A position is out of range only when the slot
    // list is known and non-empty; otherwise the device validates the bound.
    internal static bool IsPositionOutOfRange(IReadOnlyList<FilterSlotDto>? slots, int position) =>
        slots is not null && slots.Count > 0 && (position < 0 || position >= slots.Count);

    public void Dispose() {
        string? neverDecided;
        lock (_gate) {
            neverDecided = TakePendingHomeNeverDecidedLocked(); // #1079 — daemon shutdown leaves the same trace
            _homeGeneration++;
        }
        if (neverDecided is not null) {
            LogHomeNeverDecided(neverDecided);
        }
        _lifecycle.Dispose();
        if (_profileStore is not null) {
            _profileStore.Changed -= OnProfileChanged;
        }
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "FilterWheel Position read failed (reported idle)")]
    private partial void LogPositionReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "FilterWheel FocusOffsets read failed (offsets default to 0)")]
    private partial void LogFocusOffsetsFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "FilterWheel change to slot {Position} failed")]
    private partial void LogChangeFailed(Exception ex, int position);

}
