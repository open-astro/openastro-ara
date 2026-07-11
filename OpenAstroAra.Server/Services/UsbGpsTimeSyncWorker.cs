#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>Reads NMEA lines from a serial device. A seam so the worker is testable without
/// hardware; the real implementation opens the port at the NMEA-standard 9600-8N1.</summary>
public interface ISerialNmeaSource {
    /// <summary>Candidate GPS device paths, in probe order (Linux: /dev/ttyUSB* then /dev/ttyACM*).</summary>
    IReadOnlyList<string> EnumerateDevices();

    /// <summary>Stream raw lines from <paramref name="devicePath"/> until cancelled or the port
    /// errors out (the enumeration just ends — the worker treats a short read as "no fix here").</summary>
    IAsyncEnumerable<string> ReadLinesAsync(string devicePath, CancellationToken ct);
}

/// <summary>
/// §31.1 step 2 — the USB-GPS self-sync worker: on a schedule (fast while the daemon is unsynced,
/// slow once a sync exists) it probes the serial devices a GPS dongle presents as, reads NMEA for
/// a bounded window, and on the first valid <c>$GxRMC</c> fix applies a <c>gps-internal</c>/<c>high</c>
/// sync through <see cref="TimeSyncService.ApplyGpsSyncAsync"/> — no WILMA involvement, exactly
/// the §31.4 "server self-syncs" contract. Best-effort throughout: a missing/denied/garbled port
/// is a skipped device, never an error surface.
/// </summary>
public sealed partial class UsbGpsTimeSyncWorker : BackgroundService {

    private readonly TimeSyncService _timeSync;
    private readonly ISerialNmeaSource _source;
    private readonly ILogger<UsbGpsTimeSyncWorker> _logger;

    // Probe cadence: every 2 min while no fresh sync exists (a dongle can be plugged mid-session),
    // every 50 min once synced (re-syncs INSIDE the §31.1 1-hour staleness window, with margin).
    // Internal test seams, like the TimeSyncService ones.
    internal TimeSpan UnsyncedProbeInterval { get; set; } = TimeSpan.FromMinutes(2);
    internal TimeSpan SyncedProbeInterval { get; set; } = TimeSpan.FromMinutes(50);
    // How long to listen per device before giving up: a live GPS emits RMC once per second, so
    // 10 s is generous; a silent port (some other serial gadget) costs at most this per probe.
    internal TimeSpan PerDeviceListenWindow { get; set; } = TimeSpan.FromSeconds(10);

    public UsbGpsTimeSyncWorker(TimeSyncService timeSync, ILogger<UsbGpsTimeSyncWorker> logger,
            ISerialNmeaSource? source = null) {
        ArgumentNullException.ThrowIfNull(timeSync);
        ArgumentNullException.ThrowIfNull(logger);
        _timeSync = timeSync;
        _source = source ?? new SerialPortNmeaSource();
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            var synced = false;
            try {
                synced = await ProbeOnceAsync(stoppingToken).ConfigureAwait(false)
                    || (await _timeSync.GetStateAsync(stoppingToken).ConfigureAwait(false)).Synced;
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            }
            try {
                await Task.Delay(synced ? SyncedProbeInterval : UnsyncedProbeInterval, stoppingToken).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                return;
            }
        }
    }

    /// <summary>One probe pass over every candidate device. Returns true when a GPS fix was
    /// applied. Internal so the tests drive single passes without the scheduling loop.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort per-device boundary: any serial/IO/permission fault on one device must skip to the next, never kill the background worker. Log-and-recover.")]
    internal async Task<bool> ProbeOnceAsync(CancellationToken ct) {
        IReadOnlyList<string> devices;
        try {
            devices = _source.EnumerateDevices();
        } catch (Exception ex) {
            LogEnumerationFailed(ex);
            return false;
        }
        foreach (var device in devices) {
            ct.ThrowIfCancellationRequested();
            try {
                using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
                window.CancelAfter(PerDeviceListenWindow);
                // Altitude only rides GGA sentences (RMC has none), so remember the most recent
                // GGA altitude and pair it with the RMC fix that supplies the instant (#834 r1).
                double? lastGgaAltitude = null;
                await foreach (var line in _source.ReadLinesAsync(device, window.Token).ConfigureAwait(false)) {
                    var fix = NmeaSentenceParser.Parse(line);
                    if (fix is null) {
                        continue;
                    }
                    if (fix.AltitudeM is { } alt) {
                        lastGgaAltitude = alt;
                    }
                    if (fix.TimeUtc is not { } timeUtc) {
                        continue; // not an RMC with an active fix — keep listening
                    }
                    var location = fix.LatitudeDeg is { } lat && fix.LongitudeDeg is { } lng
                        ? new Contracts.TimeSyncLocationDto(lat, lng, fix.AltitudeM ?? lastGgaAltitude)
                        : null;
                    var result = await _timeSync.ApplyGpsSyncAsync(timeUtc, location, ct).ConfigureAwait(false);
                    LogGpsSyncApplied(device, result.ClockSet);
                    return true;
                }
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                // The per-device listen window elapsed with no fix — silent port or no satellites
                // yet. Move on; the next probe pass retries.
            } catch (Exception ex) {
                LogDeviceReadFailed(device, ex);
            }
        }
        return false;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "USB GPS sync applied from {Device} (clockSet={ClockSet})")]
    partial void LogGpsSyncApplied(string device, bool clockSet);

    [LoggerMessage(Level = LogLevel.Debug, Message = "USB GPS device {Device} could not be read — skipping this probe")]
    partial void LogDeviceReadFailed(string device, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "USB GPS device enumeration failed — skipping this probe")]
    partial void LogEnumerationFailed(Exception ex);
}

/// <summary>Real serial source: /dev/ttyUSB* + /dev/ttyACM* at 9600-8N1 (the NMEA 0183 default
/// virtually every USB GPS dongle ships with). Reads are pushed to a worker thread — SerialPort's
/// ReadLine is blocking — and surfaced as an async stream.</summary>
internal sealed class SerialPortNmeaSource : ISerialNmeaSource {

    public IReadOnlyList<string> EnumerateDevices() {
        if (!Directory.Exists("/dev")) {
            return [];
        }
        return [
            .. Directory.GetFiles("/dev", "ttyUSB*").OrderBy(p => p, StringComparer.Ordinal),
            .. Directory.GetFiles("/dev", "ttyACM*").OrderBy(p => p, StringComparer.Ordinal),
        ];
    }

    public async IAsyncEnumerable<string> ReadLinesAsync(string devicePath,
            [EnumeratorCancellation] CancellationToken ct) {
        using var port = new SerialPort(devicePath, 9600, Parity.None, 8, StopBits.One) {
            ReadTimeout = 2000,
            NewLine = "\n",
        };
        port.Open();
        while (!ct.IsCancellationRequested) {
            string line;
            try {
                // ReadLine blocks up to ReadTimeout (2 s); running it off the async path keeps the
                // caller's await responsive, but an in-flight blocking read only observes
                // cancellation at the next ReadTimeout boundary — so the listen window resolves
                // within ~2 s of its deadline, not instantly. Fine at a 10 s window.
                line = await Task.Run(port.ReadLine, ct).ConfigureAwait(false);
            } catch (TimeoutException) {
                continue; // no data this interval — the caller's window decides when to stop
            }
            yield return line;
        }
    }
}
