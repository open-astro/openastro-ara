#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"


using ASCOM.Common.DeviceInterfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e — seventh real Alpaca-backed device service: connects to a discovered Alpaca CoverCalibrator
/// (NINA's "flat device"), serves its live runtime (cover/light state + brightness, §32.4-cached) over
/// the §6 REST surface, and drives the cover + calibrator light via <see cref="ApplyFlatPanelAsync"/> (a
/// §60.5 202-Accepted background op). Its lifecycle (generation supersede, §32.4 background-refresh cache,
/// the §42.3 probe and read health) is the shared <see cref="DeviceLifecycle{TDevice}"/> (ADR 0011).
/// REST-only — the mediator unification is a follow-up.
/// </summary>
public sealed partial class FlatDeviceService : IFlatDeviceService, IRetainedDeviceSource, IRecoverableDevice, IDisposable {

    // ~40s of cover travel at SettlePollInterval — real motorised covers take 10-30s end to end.
    internal const int MaxSettlePolls = 200;
    private static readonly TimeSpan SettlePollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly FlatDeviceStateDto IdleRuntime = new("cover_closed", CoverOpen: false, LightOn: false, Brightness: 0);

    private readonly ILogger<FlatDeviceService> _logger;
    // ADR 0011 — connect, disconnect, forget, refresh, probe, read health and the desired state.
    private readonly DeviceLifecycle<IFlatDeviceDevice> _lifecycle;
    private Func<DiscoveredDeviceDto, IFlatDeviceDevice> _createDevice = AlpacaFlatDevice.Create;
    // Guarded by the lifecycle's gate, like the connection state.
    private int? _maxBrightness; // read once on connect (for brightness validation)
    private FlatDeviceStateDto _runtime = IdleRuntime;

    public FlatDeviceService(ILogger<FlatDeviceService>? logger = null, EquipmentEventPublisher? events = null,
            IEquipmentFaultSink? faults = null) {
        _logger = logger ?? NullLogger<FlatDeviceService>.Instance;
        _lifecycle = new DeviceLifecycle<IFlatDeviceDevice>(this, DeviceType.FlatDevice, "flat device",
            d => _createDevice(d), ReadRuntime, ResetSessionLocked,
            DeviceHealthTracker.DefaultThreshold, _logger, events, faults);
    }

    private object _gate => _lifecycle.Gate;

    // #1265 — bench-test knob: re-arms the refresh timer at a shorter cadence. Init-only, never used by the daemon.
    internal TimeSpan RefreshPeriod { init => _lifecycle.RefreshPeriod = value; }

    // Test seam (ADR 0011 rule 4): the adapter the lifecycle builds for a selected device.
    internal Func<DiscoveredDeviceDto, IFlatDeviceDevice> DeviceFactory { init => _createDevice = value; }

    // #1127 — the record the card names. The remembered-selection store and this can diverge
    // (a store-only DELETE /remembered keeps the device here), so the reconnector falls back to it.
    public DiscoveredDeviceDto? RetainedDevice => _lifecycle.RetainedDevice;

    public DesiredConnection? DesiredConnection => _lifecycle.Desired;

    public bool IsCleanlyConnected => _lifecycle.HasCleanReadSinceConnect;

    // #1246 — true once a refresh tick since the current connect read the essential read cleanly.
    internal bool HasCleanReadSinceConnect => _lifecycle.HasCleanReadSinceConnect;

    public bool TryRecover(DiscoveredDeviceDto device) => _lifecycle.TryRecover(device);

    public Task<FlatDeviceDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_lifecycle.IsDisposed, this);
            // null ONLY when no device has ever been selected (-> 404); a retained device after
            // disconnect returns Disconnected + idle runtime, consistent across the device services.
            var device = _lifecycle.Device;
            if (device is null) {
                return Task.FromResult<FlatDeviceDto?>(null);
            }
            var state = _lifecycle.State;
            var runtime = state == EquipmentConnectionState.Connected ? _runtime : IdleRuntime;
            return Task.FromResult<FlatDeviceDto?>(new FlatDeviceDto(device.UniqueId, device.Name, state, runtime));
        }
    }

    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Device);
        return _lifecycle.ConnectAsync(request.Device, idempotencyKey);
    }

    public Task<bool> ForgetAsync(CancellationToken ct) => _lifecycle.ForgetAsync();

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) =>
        _lifecycle.DisconnectAsync(idempotencyKey);

    // Caller holds the gate: a fresh, ended or tripped session forgets the last one's reads.
    private void ResetSessionLocked() {
        _maxBrightness = null;  // re-read for the new device
        _runtime = IdleRuntime; // don't serve a prior device's runtime
    }

    public Task<OperationAcceptedDto> ApplyFlatPanelAsync(FlatPanelRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        // Dispose -> range -> connected ordering, aligned with the other device services.
        IFlatDeviceDevice? client;
        int? maxBrightness;
        lock (_gate) {
            client = _lifecycle.ConnectedDevice();
            maxBrightness = _maxBrightness;
        }
        if (request.Brightness is int b && IsBrightnessOutOfRange(maxBrightness, b)) {
            throw new ArgumentOutOfRangeException(nameof(request), b,
                $"Brightness is out of range (0..{(maxBrightness ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}).");
        }
        if (client is null) {
            throw new InvalidOperationException("flat device is not connected");
        }
        _ = Task.Run(() => ApplyInBackground(client, request, maxBrightness), CancellationToken.None);
        return Task.FromResult(Accepted("flat-device.apply", idempotencyKey));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background apply boundary: the blocking ASCOM OpenCover/CloseCover/CalibratorOn/Off can throw arbitrary driver/HTTP exceptions, and a concurrent Disconnect/Dispose can dispose the captured client mid-apply; any escape must be contained and logged, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    private void ApplyInBackground(IFlatDeviceDevice client, FlatPanelRequestDto req, int? maxBrightness) {
        try {
            var changingLight = NeedsCoverSettle(req);
            if (req.OpenCover is bool open) {
                if (open) {
                    client.OpenCover();
                } else {
                    client.CloseCover();
                }
                // If the same request also changes the light, wait for the cover to stop moving
                // first: some panels reject a calibrator change while the cover is in motion.
                if (changingLight && !WaitForCoverSettle(client)) {
                    LogCoverNeverSettled();
                }
            } else if (changingLight) {
                // A STANDALONE light command while the cover is still travelling from an
                // EARLIER request: panels reject a calibrator change mid-motion
                // ("A cover open/close operation is already in progress"), and the caller
                // already has its 202 — so the failure would be invisible. Wait the cover
                // out and then apply, which is what the user asked for.
                if (!WaitForCoverSettle(client)) {
                    LogCoverNeverSettled();
                }
            }
            if (req.LightOn is bool lit) {
                if (lit) {
                    // Turn on at the requested brightness, else full. ASCOM CalibratorOn wants
                    // [1, MaxBrightness]; treat an explicit 0 the same as unspecified (LightOn=true
                    // means "on", so 0 can't be honoured literally). Read MaxBrightness fresh if it
                    // isn't cached yet (a LightOn right after connect can beat the first refresh);
                    // fall back to 1 only if the device has no readable max — never CalibratorOn(0).
                    var level = (req.Brightness is int reqB && reqB > 0)
                        ? reqB
                        : maxBrightness ?? ReadMaxBrightness(client) ?? 1;
                    client.CalibratorOn(level);
                } else {
                    client.CalibratorOff();
                }
            } else if (req.Brightness is int b) {
                // Brightness alone changes the level; CalibratorOn implies on. ASCOM CalibratorOn
                // wants [1, MaxBrightness], so brightness 0 means "off" -> CalibratorOff (never
                // CalibratorOn(0)).
                if (b > 0) {
                    client.CalibratorOn(b);
                } else {
                    client.CalibratorOff();
                }
            }
            _lifecycle.Refresh();
        } catch (Exception ex) {
            LogApplyFailed(ex);
        }
    }

    // The runtime read the lifecycle runs each tick. Cover and calibrator state are per-field tolerant (an
    // unsupported property must stay benign), so the essential read is CoverState: a device that answers
    // Connected while its cover state keeps throwing is degraded (§42.3 second channel). A driver without
    // a cover or a calibrator says NotImplemented, which is not a fault.
    private DeviceRead ReadRuntime(IFlatDeviceDevice client) {
        int? cachedMax;
        lock (_gate) {
            cachedMax = _maxBrightness;
        }
        int? max = cachedMax ?? ReadMaxBrightness(client);
        var runtime = ReadRuntime(client, max, out var readError);
        return new DeviceRead(readError, CommitLocked: () => {
            _runtime = runtime;
            if (max is int m) {
                _maxBrightness = m;
            }
        });
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported cover/calibrator property throws; that field falls back to a default rather than failing the whole runtime read. CA1031's log-and-recover boundary applies.")]
    private static FlatDeviceStateDto ReadRuntime(IFlatDeviceDevice c, int? maxBrightness, out string? readError) {
        CoverStatus cover;
        readError = null;
        try {
            cover = c.CoverState;
        } catch (ASCOM.NotImplementedException) {
            cover = CoverStatus.Unknown; // not a fault: the driver has no cover state
        } catch (Exception ex) {
            cover = CoverStatus.Unknown;
            readError = ex.Message;
        }
        CalibratorStatus cal;
        try { cal = c.CalibratorState; } catch (Exception) { cal = CalibratorStatus.Unknown; }
        int brightness;
        try { brightness = c.Brightness; } catch (Exception) { brightness = 0; }
        return MapRuntime(cover, cal, brightness, maxBrightness);
    }

    /// <summary>
    /// Pure ASCOM-status -> DTO mapping, split out of <see cref="ReadRuntime"/> so the capability
    /// and warming rules are unit-testable without an Alpaca device.
    /// </summary>
    internal static FlatDeviceStateDto MapRuntime(
            CoverStatus cover, CalibratorStatus cal, int brightness, int? maxBrightness) {
        var coverOpen = cover == CoverStatus.Open;
        // Only Ready counts as "on": CalibratorStatus.NotReady (the calibrator is warming up /
        // changing) is not yet on. It is reported separately as LightWarming rather than folded
        // into LightOn, because a client that reads warming as "off" reads a command still in
        // flight as a FAILED one. The State token set stays fixed (cover_*/light_on/error) — the
        // warming distinction rides on its own field.
        var lightOn = cal == CalibratorStatus.Ready;
        var state =
            cover == CoverStatus.Moving ? "cover_moving"
            : cover == CoverStatus.Error || cal == CalibratorStatus.Error ? "error"
            : lightOn ? "light_on"
            : coverOpen ? "cover_open"
            : "cover_closed";
        return new FlatDeviceStateDto(state, coverOpen, lightOn, lightOn ? brightness : 0,
            MaxBrightness: maxBrightness ?? 0,
            // NotPresent is the only reading that proves the feature is absent; Unknown (an
            // unsupported/failed read) stays "present" so a working control is never hidden.
            HasCover: cover != CoverStatus.NotPresent,
            HasCalibrator: cal != CalibratorStatus.NotPresent,
            LightWarming: cal == CalibratorStatus.NotReady);
    }

    // Bounded best-effort wait (~40s) for the cover to stop moving before a subsequent light op.
    // Sized for real hardware: a motorised cover takes 10-30s end to end (the earlier 6s budget
    // expired mid-travel and the light op then failed with "already in progress"). Runs on the
    // fire-and-forget apply thread; a CoverState read that throws propagates to
    // ApplyInBackground's catch.
    private static bool WaitForCoverSettle(IFlatDeviceDevice c) =>
        WaitForCoverSettle(() => c.CoverState, () => Thread.Sleep(SettlePollInterval), MaxSettlePolls);

    /// <summary>
    /// True when [req] changes the calibrator light and therefore needs the cover at rest first —
    /// panels reject a calibrator change while the cover is in motion, whether that motion was
    /// commanded by this same request or by an earlier one.
    /// </summary>
    internal static bool NeedsCoverSettle(FlatPanelRequestDto req) {
        ArgumentNullException.ThrowIfNull(req);
        return req.LightOn is not null || req.Brightness is not null;
    }

    /// <summary>
    /// The settle loop itself, over an injected state reader + sleeper so it can be unit-tested
    /// without a device or a 40s wall clock. Returns true when the cover came to rest, false when
    /// the budget ran out (the caller proceeds anyway — best effort, never a hang).
    /// </summary>
    internal static bool WaitForCoverSettle(Func<CoverStatus> readCoverState, Action sleep, int maxPolls) {
        ArgumentNullException.ThrowIfNull(readCoverState);
        ArgumentNullException.ThrowIfNull(sleep);
        for (var i = 0; i < maxPolls; i++) {
            if (readCoverState() != CoverStatus.Moving) {
                return true;
            }
            sleep();
        }
        return false;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Capability read boundary: a device without a calibrator throws for MaxBrightness; return null so we don't cache a bogus 0 max (the next tick retries). CA1031's log-and-recover boundary applies.")]
    private static int? ReadMaxBrightness(IFlatDeviceDevice c) {
        try {
            return c.MaxBrightness;
        } catch (Exception) {
            return null;
        }
    }

    // Extracted (internal) for direct unit testing. Out of range only when a real max is known
    // (> 0); otherwise the device validates the bound.
    internal static bool IsBrightnessOutOfRange(int? maxBrightness, int brightness) =>
        brightness < 0 || (maxBrightness is int max && max > 0 && brightness > max);

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    public void Dispose() {
        _lifecycle.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "FlatDevice apply (cover/light) failed")]
    private partial void LogApplyFailed(Exception ex);

    // Distinct from LogApplyFailed: the light op is still attempted after the budget expires
    // (best effort), so without this line a jammed cover would look like a clean apply right up
    // until the driver's own exception — or produce no server-side trace at all if the panel
    // silently ignores the command.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "FlatDevice cover never settled within the wait budget — applying the light change anyway")]
    private partial void LogCoverNeverSettled();
}
