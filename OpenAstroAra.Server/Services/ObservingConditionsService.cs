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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e — second real Alpaca-backed device service (after SafetyMonitor), replacing
/// <c>PlaceholderObservingConditionsService</c>. Connects to a discovered Alpaca
/// ObservingConditions device and serves its live sensor readings over the §6 REST surface.
///
/// Follows the SafetyMonitor template exactly: §60.5 202-Accepted connect lifecycle on a
/// background task; generation-based supersede; and a §32.4 background-refresh cache (a timer
/// reads every sensor every <see cref="RefreshInterval"/> while Connected) so <see cref="GetAsync"/>
/// serves the cached readings synchronously without a per-poll burst of blocking HTTP reads.
///
/// REST-only: unlike SafetyMonitor, no sequence instruction consumes the weather mediator's live
/// data (<c>IWeatherDataMediator</c> is only a connect/disconnect dependency), so this does not
/// unify with that mediator — it stays the headless stub.
/// </summary>
public sealed partial class ObservingConditionsService : IObservingConditionsService, IDisposable {

    // §32.4 — how often the background loop refreshes the cached readings while connected.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    // Snapshot of the device's sensors. Each is nullable: an ObservingConditions device only
    // implements the sensors it has, and an unimplemented sensor throws (mapped to null here).
    private sealed record Readings(
        double? TemperatureC,
        double? HumidityPct,
        double? DewPointC,
        double? PressureHpa,
        double? CloudCoverPct,
        double? WindSpeedMs,
        double? WindGustMs,
        double? WindDirectionDeg,
        double? RainRate);

    private static readonly Readings EmptyReadings =
        new(null, null, null, null, null, null, null, null, null);

    private readonly ILogger<ObservingConditionsService> _logger;
    private readonly EquipmentEventPublisher? _events;
    private readonly object _gate = new();
    private readonly Timer _refreshTimer;
    private AlpacaObservingConditions? _client;
    private readonly IEquipmentFaultSink? _faults;
    // §42.3 — consecutive-failure disconnect detection (the per-sensor reads
    // swallow failures by design, so without this a dead bridge left the
    // service claiming Connected with all-null readings forever).
    private readonly DeviceConnectionProbe _probe = new();
    private DiscoveredDeviceDto? _device;
    private EquipmentConnectionState _state = EquipmentConnectionState.Disconnected;
    private DateTimeOffset _lastTransition = DateTimeOffset.UtcNow;
    private Readings _cached = EmptyReadings;
    private DateTimeOffset _capturedAt = DateTimeOffset.UtcNow;
    private int _refreshing; // 0 idle, 1 a refresh read is in flight (Interlocked, not under _gate)
    private long _connectGeneration;
    private bool _disposed;

    public ObservingConditionsService(ILogger<ObservingConditionsService>? logger = null, EquipmentEventPublisher? events = null,
            IEquipmentFaultSink? faults = null) {
        _logger = logger ?? NullLogger<ObservingConditionsService>.Instance;
        _events = events;
        _faults = faults;
        _refreshTimer = new Timer(RefreshTick, state: null, dueTime: RefreshInterval, period: RefreshInterval);
    }

    // ct is unused by design: the readings are served from the cache the background loop maintains
    // (§32.4), so there is no per-call HTTP read to cancel. Returns a Task to satisfy the async
    // interface without an await.
    public Task<ObservingConditionsDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_device is null) {
                return Task.FromResult<ObservingConditionsDto?>(null);
            }
            var state = _state;
            var r = state == EquipmentConnectionState.Connected ? _cached : EmptyReadings;
            var dto = new ObservingConditionsDto(
                DeviceId: _device.UniqueId,
                Name: _device.Name,
                State: state,
                TemperatureC: r.TemperatureC,
                HumidityPct: r.HumidityPct,
                DewPointC: r.DewPointC,
                PressureHpa: r.PressureHpa,
                CloudCoverPct: r.CloudCoverPct,
                WindSpeedMs: r.WindSpeedMs,
                WindGustMs: r.WindGustMs,
                WindDirectionDeg: r.WindDirectionDeg,
                RainRate: r.RainRate,
                // ObservingConditions has no intrinsic safe/unsafe state (that is SafetyMonitor's
                // role); kept false here. WILMA can derive safety from thresholds if desired.
                Safe: false,
                CapturedAt: _capturedAt.ToString("O", CultureInfo.InvariantCulture));
            return Task.FromResult<ObservingConditionsDto?>(dto);
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
                return Task.FromResult(Accepted("observing-conditions.connect", idempotencyKey));
            }
            DisposeClientLocked();
            _device = device;
            generation = ++_connectGeneration;
            SetState(EquipmentConnectionState.Connecting);
        }
        _ = Task.Run(() => ConnectInBackground(device, generation), CancellationToken.None);
        return Task.FromResult(Accepted("observing-conditions.connect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) {
        AlpacaObservingConditions? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _connectGeneration++;
            client = _client;
            _client = null;
            if (_device is not null) {
                SetState(EquipmentConnectionState.Disconnected);
            }
        }
        if (client is not null) {
            _ = Task.Run(() => SafeDisconnectDispose(client), CancellationToken.None);
        }
        return Task.FromResult(Accepted("observing-conditions.disconnect", idempotencyKey));
    }

    private void RefreshTick(object? state) => RefreshCacheOnce();

    // Reads every sensor once and updates the cache. Used by BOTH the background timer and the
    // connect-time seed; the _refreshing guard makes them mutually exclusive so there is never
    // more than one concurrent burst of reads on the AlpacaObservingConditions instance.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Timer-callback boundary: an unhandled exception in a System.Threading.Timer callback crashes the process. The per-sensor reads already absorb device failures; this catch-all is a hard backstop so a refresh tick can never fault the daemon. CA1031's log-and-recover boundary applies.")]
    private void RefreshCacheOnce() {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) {
            return;
        }
        try {
            AlpacaObservingConditions? client;
            lock (_gate) {
                if (_disposed || _state != EquipmentConnectionState.Connected) {
                    return;
                }
                client = _client;
            }
            if (client is null) {
                return;
            }
            // §42.3 — the ONE deliberate connection probe per tick. The per-sensor reads below
            // deliberately swallow failures (an unimplemented sensor must stay benign), so this
            // probe is the only disconnect-detection source: Connected throws on transport death
            // and reads false on a driver-side disconnect; a consecutive-failure streak (not one
            // blip) trips the device to Error + publishes the §42.2 fault.
            if (!ProbeConnected(client)) {
                if (ObserveProbeIfLive(client, probeSucceeded: false) == ProbeVerdict.Lost) {
                    TripConnectionLost(client);
                }
                return; // the device didn't answer — skip this tick's reads
            }
            ObserveProbeIfLive(client, probeSucceeded: true);
            var readings = ReadSensors(client);
            var at = DateTimeOffset.UtcNow;
            lock (_gate) {
                if (_state == EquipmentConnectionState.Connected && ReferenceEquals(_client, client)) {
                    _cached = readings;
                    _capturedAt = at;
                }
            }
        } catch (Exception ex) {
            LogSensorReadFailed(ex);
        } finally {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    // Reads all nine sensors, each independently: an ObservingConditions device implements only
    // the sensors it has, and an unimplemented (or transiently failing) sensor yields null rather
    // than failing the whole snapshot. Per-sensor failures do NOT demote the connection — weather
    // sensors are independently optional, unlike SafetyMonitor's single IsSafe.
    private Readings ReadSensors(AlpacaObservingConditions c) => new(
        TemperatureC: ReadSensor("Temperature", () => c.Temperature),
        HumidityPct: ReadSensor("Humidity", () => c.Humidity),
        DewPointC: ReadSensor("DewPoint", () => c.DewPoint),
        PressureHpa: ReadSensor("Pressure", () => c.Pressure),
        CloudCoverPct: ReadSensor("CloudCover", () => c.CloudCover),
        WindSpeedMs: ReadSensor("WindSpeed", () => c.WindSpeed),
        WindGustMs: ReadSensor("WindGust", () => c.WindGust),
        WindDirectionDeg: ReadSensor("WindDirection", () => c.WindDirection),
        RainRate: ReadSensor("RainRate", () => c.RainRate));

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-sensor read boundary: an ObservingConditions sensor that is not implemented throws (ASCOM NotImplementedException) and a transient driver/HTTP error can too; either way the sensor is reported absent (null), never propagated. CA1031's log-and-recover boundary applies.")]
    private double? ReadSensor(string sensor, Func<double> read) {
        try {
            return read();
        } catch (Exception ex) {
            // Debug, not Warning: an unimplemented sensor throws every 2s by design, so this must
            // not be noisy — but it's available (with the sensor name) when diagnosing a sensor
            // that flips from working to failing, which a bare null would hide.
            LogSensorUnavailable(sensor, ex);
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background connect boundary: constructing the Alpaca client and setting Connected can throw arbitrary driver/HTTP/socket exceptions; any escape must surface as the Error state and be contained, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of the AlpacaObservingConditions is managed explicitly: on every non-adopt path SafeDisconnectDispose disposes it; on the adopt path it is stored in _client (local set to null) and disposed later by DisconnectAsync/Dispose. CA2000 cannot follow the transfer through the lock + helper.")]
    private void ConnectInBackground(DiscoveredDeviceDto device, long generation) {
        AlpacaObservingConditions? client = null;
        var adopted = false;
        try {
            var host = string.IsNullOrWhiteSpace(device.IpAddress) ? device.HostName : device.IpAddress;
            if (string.IsNullOrWhiteSpace(host)) {
                throw new InvalidOperationException(
                    $"discovered device '{device.Name}' carries neither an IP address nor a host name");
            }
            client = new AlpacaObservingConditions(
                device.UseHttps ? ServiceType.Https : ServiceType.Http,
                host, device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null);
            // Trust the authoritative Connected setter (throws on a real failure); do not re-GET.
            client.Connected = true;
            lock (_gate) {
                if (!_disposed && _connectGeneration == generation) {
                    _client = client;
                    // Clear before going Connected: otherwise GetAsync could serve the PRIOR
                    // device's readings under the NEW device's identity during the seed window.
                    _cached = EmptyReadings;
                    _capturedAt = DateTimeOffset.UtcNow;
                    _probe.Reset(); // §42.3 — a fresh session starts a fresh streak
                    SetState(EquipmentConnectionState.Connected);
                    adopted = true;
                }
            }
            if (!adopted) {
                SafeDisconnectDispose(client);
                return;
            }
            client = null; // ownership transferred to _client
            // Seed the readings cache immediately through the same guarded path as the timer.
            RefreshCacheOnce();
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
    private void SafeDisconnectDispose(AlpacaObservingConditions client) {
        try {
            client.Connected = false;
        } catch (Exception ex) {
            LogTeardownIgnored(ex);
        }
        DisposeQuietly(client);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort teardown: ASCOM/COM-backed clients have been known to throw from Dispose(); the throw must be swallowed (and logged) rather than escape into a fire-and-forget Task.Run as an unobserved exception. CA1031's log-and-recover boundary applies.")]
    private void DisposeQuietly(AlpacaObservingConditions client) {
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
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The probe's whole job is turning any transport/driver exception into a failed-probe observation. CA1031's log-and-recover boundary applies.")]
    private static bool ProbeConnected(AlpacaObservingConditions c) {
        try { return c.Connected; } catch (Exception) { return false; }
    }

    // The liveness check and the Observe are ONE operation under _gate so a concurrent
    // reconnect's Reset can't land between them; null = the probed client is stale.
    private ProbeVerdict? ObserveProbeIfLive(AlpacaObservingConditions client, bool probeSucceeded) {
        lock (_gate) {
            return ReferenceEquals(_client, client) ? _probe.Observe(probeSucceeded) : null;
        }
    }

    // §42.3 — declare the device lost: Connected → Error (keeps _device remembered so a
    // reconnect can find it) + publish the §42.2 fault event. Re-checks the probed client's
    // identity under the gate so a reconnect between probe and trip can't flip the new session.
    private void TripConnectionLost(AlpacaObservingConditions probed) {
        DiscoveredDeviceDto? device;
        lock (_gate) {
            if (_state != EquipmentConnectionState.Connected || !ReferenceEquals(_client, probed)) {
                return;
            }
            device = _device;
            SetState(EquipmentConnectionState.Error);
            _probe.Reset();
        }
        LogConnectionLost(device?.Name ?? "?");
        _faults?.Publish(new EquipmentFaultEvent(DeviceType.ObservingConditions, device?.UniqueId,
            device?.Name, EquipmentFaultKind.Disconnected,
            $"stopped answering {DeviceConnectionProbe.DefaultLostThreshold} consecutive connection probes",
            DateTimeOffset.UtcNow));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ObservingConditions '{Device}' stopped answering — marked Error (§42.3)")]
    private partial void LogConnectionLost(string device);

    private void SetState(EquipmentConnectionState state) {
        if (_state == state) {
            return;
        }
        _state = state;
        _lastTransition = DateTimeOffset.UtcNow;
        // Callers hold the service lock; the publisher's synchronous part only
        // serializes a small payload and hands off (see EquipmentEventPublisher).
        _events?.StateChanged(DeviceType.ObservingConditions, _device?.UniqueId, _device?.Name, state);
    }

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    public void Dispose() {
        AlpacaObservingConditions? client;
        lock (_gate) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            client = _client;
            _client = null;
        }
        _refreshTimer.Dispose();
        // Dispose the client directly (guarded) rather than via SafeDisconnectDispose: the
        // courtesy "Connected = false" is a blocking HTTP call (up to the ASCOM ~3s
        // establishConnectionTimeout) that would hang container shutdown if the device is
        // unreachable. DisposeQuietly releases the HttpClient resources without network I/O.
        if (client is not null) {
            DisposeQuietly(client);
        }
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ObservingConditions sensor read failed")]
    private partial void LogSensorReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ObservingConditions sensor {Sensor} unavailable (reported as null)")]
    private partial void LogSensorUnavailable(string sensor, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "ObservingConditions connected: {Name} at {Host}:{Port}/{Device}")]
    private partial void LogConnected(string name, string host, int port, int device);

    [LoggerMessage(Level = LogLevel.Error, Message = "ObservingConditions connect failed for {Name}")]
    private partial void LogConnectFailed(Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ignored error while disconnecting ObservingConditions during teardown")]
    private partial void LogTeardownIgnored(Exception ex);
}
