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
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e — second real Alpaca-backed device service (after SafetyMonitor). Connects to a discovered Alpaca
/// ObservingConditions device and serves its live sensor readings over the §6 REST surface. Its lifecycle
/// (§60.5 202-Accepted connect, generation supersede, the §32.4 background-refresh cache, the §42.3 probe
/// and read health) is the shared <see cref="DeviceLifecycle{TDevice}"/> (ADR 0011); the timer reads every
/// sensor every refresh while Connected so <see cref="GetAsync"/> serves the cached readings synchronously.
///
/// REST-only: unlike SafetyMonitor, no sequence instruction consumes the weather mediator's live
/// data (<c>IWeatherDataMediator</c> is only a connect/disconnect dependency), so this does not
/// unify with that mediator — it stays the headless stub.
/// </summary>
public sealed partial class ObservingConditionsService : IObservingConditionsService, IRetainedDeviceSource, IRecoverableDevice, IDisposable {

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
        double? RainRate,
        double? SkyQualityMagArcsec2,
        double? SkyTemperatureC);

    private static readonly Readings EmptyReadings =
        new(null, null, null, null, null, null, null, null, null, null, null);

    private readonly ILogger<ObservingConditionsService> _logger;
    // ADR 0011 — connect, disconnect, forget, refresh, probe, read health and the desired state.
    private readonly DeviceLifecycle<IObservingConditionsDevice> _lifecycle;
    private Func<DiscoveredDeviceDto, IObservingConditionsDevice> _createDevice = AlpacaObservingConditionsDevice.Create;
    // Guarded by the lifecycle's gate, like the connection state.
    private Readings _cached = EmptyReadings;
    private DateTimeOffset _capturedAt = DateTimeOffset.UtcNow;

    public ObservingConditionsService(ILogger<ObservingConditionsService>? logger = null, EquipmentEventPublisher? events = null,
            IEquipmentFaultSink? faults = null) {
        _logger = logger ?? NullLogger<ObservingConditionsService>.Instance;
        _lifecycle = new DeviceLifecycle<IObservingConditionsDevice>(this, DeviceType.ObservingConditions, "weather station",
            d => _createDevice(d), ReadRuntime, ResetSessionLocked,
            DeviceHealthTracker.DefaultThreshold, _logger, events, faults);
    }

    private object _gate => _lifecycle.Gate;

    // #1265 — bench-test knob: re-arms the refresh timer at a shorter cadence. Init-only, never used by the daemon.
    internal TimeSpan RefreshPeriod { init => _lifecycle.RefreshPeriod = value; }

    // Test seam (ADR 0011 rule 4): the adapter the lifecycle builds for a selected device.
    internal Func<DiscoveredDeviceDto, IObservingConditionsDevice> DeviceFactory { init => _createDevice = value; }

    // #1127 — the record the card names. The remembered-selection store and this can diverge
    // (a store-only DELETE /remembered keeps the device here), so the reconnector falls back to it.
    public DiscoveredDeviceDto? RetainedDevice => _lifecycle.RetainedDevice;

    public DesiredConnection? DesiredConnection => _lifecycle.Desired;

    public bool IsCleanlyConnected => _lifecycle.HasCleanReadSinceConnect;

    // #1246 — true once a refresh tick since the current connect read the essential read cleanly.
    internal bool HasCleanReadSinceConnect => _lifecycle.HasCleanReadSinceConnect;

    public bool TryRecover(DiscoveredDeviceDto device) => _lifecycle.TryRecover(device);

    // ct is unused by design: the readings are served from the cache the background loop maintains
    // (§32.4), so there is no per-call HTTP read to cancel. Returns a Task to satisfy the async
    // interface without an await.
    public Task<ObservingConditionsDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_lifecycle.IsDisposed, this);
            var device = _lifecycle.Device;
            if (device is null) {
                return Task.FromResult<ObservingConditionsDto?>(null);
            }
            var state = _lifecycle.State;
            var r = state == EquipmentConnectionState.Connected ? _cached : EmptyReadings;
            var dto = new ObservingConditionsDto(
                DeviceId: device.UniqueId,
                Name: device.Name,
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
                SkyQualityMagArcsec2: r.SkyQualityMagArcsec2,
                SkyTemperatureC: r.SkyTemperatureC,
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
        return _lifecycle.ConnectAsync(request.Device, idempotencyKey);
    }

    public Task<bool> ForgetAsync(CancellationToken ct) => _lifecycle.ForgetAsync();

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) =>
        _lifecycle.DisconnectAsync(idempotencyKey);

    // Caller holds the gate: a fresh, ended or tripped session forgets the last one's reads
    // (otherwise GetAsync could serve the PRIOR device's readings under the NEW device's identity).
    private void ResetSessionLocked() {
        _cached = EmptyReadings;
        _capturedAt = DateTimeOffset.UtcNow;
    }

    // The runtime read the lifecycle runs each tick: every sensor once. The essential read is "some sensor
    // answers": a device that answers Connected while EVERY sensor read fails (other than NotImplemented, which
    // is how a sensor the device lacks says so) is degraded (§42.3 second channel).
    private DeviceRead ReadRuntime(IObservingConditionsDevice client) {
        var outcome = new SensorOutcome();
        var readings = ReadSensors(client, outcome);
        var at = DateTimeOffset.UtcNow;
        var readError = outcome.Answered == 0 ? outcome.FirstError : null;
        return new DeviceRead(readError, CommitLocked: () => {
            _cached = readings;
            _capturedAt = at;
        });
    }

    private sealed class SensorOutcome {
        public int Answered { get; set; }
        public string? FirstError { get; set; }
    }

    // Reads all eleven sensors, each independently: an ObservingConditions device implements only
    // the sensors it has, and an unimplemented (or transiently failing) sensor yields null rather
    // than failing the whole snapshot. Per-sensor failures do NOT demote the connection — weather
    // sensors are independently optional, unlike SafetyMonitor's single IsSafe.
    private Readings ReadSensors(IObservingConditionsDevice c, SensorOutcome outcome) => new(
        TemperatureC: ReadSensor(outcome, "Temperature", () => c.Temperature),
        HumidityPct: ReadSensor(outcome, "Humidity", () => c.Humidity),
        DewPointC: ReadSensor(outcome, "DewPoint", () => c.DewPoint),
        PressureHpa: ReadSensor(outcome, "Pressure", () => c.Pressure),
        CloudCoverPct: ReadSensor(outcome, "CloudCover", () => c.CloudCover),
        WindSpeedMs: ReadSensor(outcome, "WindSpeed", () => c.WindSpeed),
        WindGustMs: ReadSensor(outcome, "WindGust", () => c.WindGust),
        WindDirectionDeg: ReadSensor(outcome, "WindDirection", () => c.WindDirection),
        RainRate: ReadSensor(outcome, "RainRate", () => c.RainRate),
        // SQM meter (or a WeeWX-fed bridge sensor): sky brightness in mag/arcsec².
        SkyQualityMagArcsec2: ReadSensor(outcome, "SkyQuality", () => c.SkyQuality),
        SkyTemperatureC: ReadSensor(outcome, "SkyTemperature", () => c.SkyTemperature));

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-sensor read boundary: an ObservingConditions sensor that is not implemented throws (ASCOM NotImplementedException) and a transient driver/HTTP error can too; either way the sensor is reported absent (null), never propagated. CA1031's log-and-recover boundary applies.")]
    private double? ReadSensor(SensorOutcome outcome, string sensor, Func<double> read) {
        try {
            var value = read();
            outcome.Answered++;
            return value;
        } catch (Exception ex) {
            if (ex is not ASCOM.NotImplementedException) {
                outcome.FirstError ??= ex.Message;
            }
            // Debug, not Warning: an unimplemented sensor throws every 2s by design, so this must
            // not be noisy — but it's available (with the sensor name) when diagnosing a sensor
            // that flips from working to failing, which a bare null would hide.
            LogSensorUnavailable(sensor, ex);
            return null;
        }
    }

    public void Dispose() {
        _lifecycle.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "ObservingConditions sensor {Sensor} unavailable (reported as null)")]
    private partial void LogSensorUnavailable(string sensor, Exception ex);
}
