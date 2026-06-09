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
using ASCOM.Common.Alpaca;
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
/// §14e — seventh real Alpaca-backed device service. Replaces <c>PlaceholderFlatDeviceService</c>:
/// connects to a discovered Alpaca CoverCalibrator (NINA's "flat device"), serves its live runtime
/// (cover/light state + brightness, §32.4-cached) over the §6 REST surface, and drives the cover +
/// calibrator light via <see cref="ApplyFlatPanelAsync"/> (a §60.5 202-Accepted background op).
/// Mirrors the established control-device template. REST-only — the mediator unification is a
/// follow-up.
/// </summary>
public sealed partial class FlatDeviceService : IFlatDeviceService, IDisposable {

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly FlatDeviceStateDto IdleRuntime = new("cover_closed", CoverOpen: false, LightOn: false, Brightness: 0);

    private readonly ILogger<FlatDeviceService> _logger;
    private readonly object _gate = new();
    private readonly Timer _refreshTimer;
    private AlpacaCoverCalibrator? _client;
    private DiscoveredDeviceDto? _device;
    private EquipmentConnectionState _state = EquipmentConnectionState.Disconnected;
    private int? _maxBrightness; // read once on connect (for brightness validation)
    private FlatDeviceStateDto _runtime = IdleRuntime;
    private int _refreshing;
    private long _connectGeneration;
    private bool _disposed;

    public FlatDeviceService(ILogger<FlatDeviceService>? logger = null) {
        _logger = logger ?? NullLogger<FlatDeviceService>.Instance;
        _refreshTimer = new Timer(RefreshTick, state: null, dueTime: RefreshInterval, period: RefreshInterval);
    }

    public Task<FlatDeviceDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // null ONLY when no device has ever been selected (-> 404); a retained device after
            // disconnect returns Disconnected + idle runtime, consistent across the device services.
            if (_device is null) {
                return Task.FromResult<FlatDeviceDto?>(null);
            }
            var runtime = _state == EquipmentConnectionState.Connected ? _runtime : IdleRuntime;
            return Task.FromResult<FlatDeviceDto?>(new FlatDeviceDto(_device.UniqueId, _device.Name, _state, runtime));
        }
    }

    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Device);
        var device = request.Device;
        long generation;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((_state == EquipmentConnectionState.Connecting || _state == EquipmentConnectionState.Connected)
                && _device?.UniqueId == device.UniqueId) {
                return Task.FromResult(Accepted("flat-device.connect", idempotencyKey));
            }
            DisposeClientLocked();
            _device = device;
            generation = ++_connectGeneration;
            SetState(EquipmentConnectionState.Connecting);
        }
        _ = Task.Run(() => ConnectInBackground(device, generation), CancellationToken.None);
        return Task.FromResult(Accepted("flat-device.connect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) {
        AlpacaCoverCalibrator? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _connectGeneration++;
            client = _client;
            _client = null;
            _maxBrightness = null;
            if (_device is not null) {
                SetState(EquipmentConnectionState.Disconnected);
            }
        }
        if (client is not null) {
            _ = Task.Run(() => SafeDisconnectDispose(client), CancellationToken.None);
        }
        return Task.FromResult(Accepted("flat-device.disconnect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> ApplyFlatPanelAsync(FlatPanelRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        // Dispose -> range -> connected ordering, aligned with the other device services.
        AlpacaCoverCalibrator? client;
        int? maxBrightness;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _state == EquipmentConnectionState.Connected ? _client : null;
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
    private void ApplyInBackground(AlpacaCoverCalibrator client, FlatPanelRequestDto req, int? maxBrightness) {
        try {
            var changingLight = req.LightOn is not null || req.Brightness is not null;
            if (req.OpenCover is bool open) {
                if (open) {
                    client.OpenCover();
                } else {
                    client.CloseCover();
                }
                // If the same request also changes the light, wait for the cover to stop moving
                // first: some panels reject a calibrator change while the cover is in motion.
                if (changingLight) {
                    WaitForCoverSettle(client);
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
            RefreshCacheOnce();
        } catch (Exception ex) {
            LogApplyFailed(ex);
        }
    }

    private void RefreshTick(object? state) => RefreshCacheOnce();

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Timer-callback boundary: an unhandled exception in a System.Threading.Timer callback crashes the process. The per-field reads already absorb device failures; this catch-all is a hard backstop so a refresh tick can never fault the daemon. CA1031's log-and-recover boundary applies.")]
    private void RefreshCacheOnce() {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) {
            return;
        }
        try {
            AlpacaCoverCalibrator? client;
            bool needMax;
            lock (_gate) {
                if (_disposed || _state != EquipmentConnectionState.Connected) {
                    return;
                }
                client = _client;
                needMax = _maxBrightness is null;
            }
            if (client is null) {
                return;
            }
            var runtime = ReadRuntime(client);
            int? max = needMax ? ReadMaxBrightness(client) : null;
            lock (_gate) {
                if (_state == EquipmentConnectionState.Connected && ReferenceEquals(_client, client)) {
                    _runtime = runtime;
                    if (max is int m) {
                        _maxBrightness = m;
                    }
                }
            }
        } catch (Exception ex) {
            LogRuntimeReadFailed(ex);
        } finally {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported cover/calibrator property throws; that field falls back to a default rather than failing the whole runtime read. CA1031's log-and-recover boundary applies.")]
    private static FlatDeviceStateDto ReadRuntime(AlpacaCoverCalibrator c) {
        CoverStatus cover;
        try { cover = c.CoverState; } catch (Exception) { cover = CoverStatus.Unknown; }
        CalibratorStatus cal;
        try { cal = c.CalibratorState; } catch (Exception) { cal = CalibratorStatus.Unknown; }
        int brightness;
        try { brightness = c.Brightness; } catch (Exception) { brightness = 0; }

        var coverOpen = cover == CoverStatus.Open;
        // Only Ready counts as "on": CalibratorStatus.NotReady (the calibrator is warming up /
        // changing) is treated as not-on and is transient — the next 2s poll resolves to Ready
        // (light_on) or Off. The FlatDeviceStateDto.State contract is a fixed token set
        // (cover_*/light_on/error) with no "warming" value, so a distinct warming label is deferred
        // to the FlatDevice mediator work, where the DTO can be extended deliberately.
        var lightOn = cal == CalibratorStatus.Ready;
        var state =
            cover == CoverStatus.Moving ? "cover_moving"
            : cover == CoverStatus.Error || cal == CalibratorStatus.Error ? "error"
            : lightOn ? "light_on"
            : coverOpen ? "cover_open"
            : "cover_closed";
        return new FlatDeviceStateDto(state, coverOpen, lightOn, lightOn ? brightness : 0);
    }

    // Bounded best-effort wait (~6s) for the cover to stop moving before a subsequent light op.
    // Runs on the fire-and-forget apply thread; a CoverState read that throws propagates to
    // ApplyInBackground's catch.
    private static void WaitForCoverSettle(AlpacaCoverCalibrator c) {
        for (var i = 0; i < 30; i++) {
            if (c.CoverState != CoverStatus.Moving) {
                return;
            }
            Thread.Sleep(200);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Capability read boundary: a device without a calibrator throws for MaxBrightness; return null so we don't cache a bogus 0 max (the next tick retries). CA1031's log-and-recover boundary applies.")]
    private static int? ReadMaxBrightness(AlpacaCoverCalibrator c) {
        try {
            return c.MaxBrightness;
        } catch (Exception) {
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background connect boundary: constructing the Alpaca client and setting Connected can throw arbitrary driver/HTTP/socket exceptions; any escape must surface as the Error state and be contained, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of the AlpacaCoverCalibrator is managed explicitly: on every non-adopt path SafeDisconnectDispose disposes it; on the adopt path it is stored in _client (local set to null) and disposed later by DisconnectAsync/Dispose. CA2000 cannot follow the transfer through the lock + helper.")]
    private void ConnectInBackground(DiscoveredDeviceDto device, long generation) {
        AlpacaCoverCalibrator? client = null;
        var adopted = false;
        try {
            var host = string.IsNullOrWhiteSpace(device.IpAddress) ? device.HostName : device.IpAddress;
            if (string.IsNullOrWhiteSpace(host)) {
                throw new InvalidOperationException(
                    $"discovered device '{device.Name}' carries neither an IP address nor a host name");
            }
            client = new AlpacaCoverCalibrator(
                device.UseHttps ? ServiceType.Https : ServiceType.Http,
                host, device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null);
            client.Connected = true;
            lock (_gate) {
                if (!_disposed && _connectGeneration == generation) {
                    _client = client;
                    _maxBrightness = null;  // re-read for the new device
                    _runtime = IdleRuntime; // don't serve a prior device's runtime
                    SetState(EquipmentConnectionState.Connected);
                    adopted = true;
                }
            }
            if (!adopted) {
                SafeDisconnectDispose(client);
                return;
            }
            client = null; // ownership transferred to _client
            RefreshCacheOnce(); // seed runtime + max brightness through the guarded path
            bool stillConnected;
            lock (_gate) {
                stillConnected = _state == EquipmentConnectionState.Connected;
            }
            if (stillConnected) {
                LogConnected(device.Name, host, device.IpPort, device.AlpacaDeviceNumber);
            }
        } catch (Exception ex) {
            if (!adopted) {
                if (client is not null) {
                    SafeDisconnectDispose(client);
                }
                lock (_gate) {
                    if (!_disposed && _connectGeneration == generation) {
                        SetState(EquipmentConnectionState.Error);
                    }
                }
                LogConnectFailed(ex, device.Name);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort teardown: Connected = false can throw driver/HTTP exceptions, which must be swallowed so Dispose always runs and cleanup completes. CA1031's log-and-recover boundary applies.")]
    private void SafeDisconnectDispose(AlpacaCoverCalibrator client) {
        try {
            client.Connected = false;
        } catch (Exception ex) {
            LogTeardownIgnored(ex);
        }
        DisposeQuietly(client);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort teardown: ASCOM/COM-backed clients have been known to throw from Dispose(); the throw must be swallowed (and logged) rather than escape into a fire-and-forget Task.Run as an unobserved exception. CA1031's log-and-recover boundary applies.")]
    private void DisposeQuietly(AlpacaCoverCalibrator client) {
        try {
            client.Dispose();
        } catch (Exception ex) {
            LogTeardownIgnored(ex);
        }
    }

    private void DisposeClientLocked() {
        var c = _client;
        _client = null;
        if (c is not null) {
            _ = Task.Run(() => SafeDisconnectDispose(c), CancellationToken.None);
        }
    }

    // Caller must hold _gate (every call site already does), so no inner lock here.
    private void SetState(EquipmentConnectionState state) {
        _state = state;
    }

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    // Extracted (internal) for direct unit testing. Out of range only when a real max is known
    // (> 0); otherwise the device validates the bound.
    internal static bool IsBrightnessOutOfRange(int? maxBrightness, int brightness) =>
        brightness < 0 || (maxBrightness is int max && max > 0 && brightness > max);

    public void Dispose() {
        AlpacaCoverCalibrator? client;
        lock (_gate) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            client = _client;
            _client = null;
        }
        _refreshTimer.Dispose();
        // Dispose the client directly (guarded), not via SafeDisconnectDispose: the courtesy
        // Connected=false is a blocking HTTP call (up to ~3s ASCOM timeout) that would hang
        // container shutdown if the device is unreachable. DisposeQuietly releases resources only.
        if (client is not null) {
            DisposeQuietly(client);
        }
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "FlatDevice runtime read failed")]
    private partial void LogRuntimeReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "FlatDevice apply (cover/light) failed")]
    private partial void LogApplyFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "FlatDevice connected: {Name} at {Host}:{Port}/{Device}")]
    private partial void LogConnected(string name, string host, int port, int device);

    [LoggerMessage(Level = LogLevel.Error, Message = "FlatDevice connect failed for {Name}")]
    private partial void LogConnectFailed(Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ignored error while disconnecting FlatDevice during teardown")]
    private partial void LogTeardownIgnored(Exception ex);
}
