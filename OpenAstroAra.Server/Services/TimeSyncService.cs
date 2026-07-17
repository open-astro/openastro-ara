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
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>§31 — the time-sync state machine backing GET/POST <c>/api/v1/server/time-sync</c>.</summary>
public interface ITimeSyncService {
    Task<TimeSyncStateDto> GetStateAsync(CancellationToken ct);

    /// <summary>Apply a client-pushed sync (§31.1 waterfall steps 1/3/5). Throws
    /// <see cref="TimeSyncInvalidSourceException"/> for a source outside
    /// <c>client|gps-mobile|manual</c> (→ 422 at the endpoint).</summary>
    Task<TimeSyncPushResultDto> PushAsync(TimeSyncPushRequestDto request, CancellationToken ct);
}

/// <summary>Sets the OS clock. The real implementation needs CAP_SYS_TIME on the server binary
/// (DEPLOY.md's <c>setcap cap_sys_time+ep</c> step); a failed set is an expected condition on a
/// dev box or an un-capped install, never an exception.</summary>
public interface ISystemClockSetter {
    /// <summary>Set the system clock to <paramref name="utc"/>. Returns false when the platform
    /// or privileges don't allow it — the caller then tracks the offset instead.</summary>
    bool TrySet(DateTimeOffset utc);
}

/// <summary>
/// §31 time-sync service. Holds the last applied sync (source / trust / location / when), computes
/// the §31.1 "synced &amp; fresh" gate (&lt; 1 h old, trust ≥ medium), applies pushes: clamp the
/// requested trust to the source's §31.2 ceiling, set the OS clock via the CAP_SYS_TIME seam
/// (tracking the offset honestly when the set fails), and write a pushed location into the active
/// profile's §37 site settings so every consumer of lat/long (twilight, alt/az, Tonight's Sky)
/// picks it up through the store it already reads.
///
/// The USB-GPS self-sync (§31.1 step 2 — NMEA scan of /dev/ttyUSB*/ttyACM*) is the follow-up
/// slice; <see cref="NmeaSentenceParser"/> is already here so that slice is wiring, not parsing. Until it
/// lands, <c>internal_gps_available</c> reports device-file PRESENCE (a plugged dongle) without
/// claiming a fix.
/// </summary>
public sealed partial class TimeSyncService : ITimeSyncService {

    private static readonly TimeSpan Freshness = TimeSpan.FromHours(1);

    private readonly ILogger<TimeSyncService> _logger;
    private readonly ISystemClockSetter _clockSetter;
    private readonly IProfileStore? _profiles;
    private readonly object _gate = new();

    private string _source = "none";
    private string _trust = "none";
    private DateTimeOffset? _syncedAtUtc;
    // Freshness is measured on the MONOTONIC clock (#832 r1): comparing _syncedAtUtc (corrected,
    // pushed time) against the raw system clock breaks exactly when the clock-set fails — a Pi
    // booting near epoch would read as "fresh forever", one ahead by an hour as instantly stale.
    private long? _syncedAtTickMs;
    private TimeSyncLocationDto? _location;
    // Non-zero only when a push couldn't set the OS clock: pushed-time minus system-time at push,
    // re-reported on GET so a consumer that cares can correct. Zeroed by a successful set.
    private double _trackedOffsetSeconds;

    // Test seams. Now: the clock the state machine reasons with. GpsDeviceProbe: does a serial
    // device that could carry NMEA exist (presence only — see class remarks). InternetProbe:
    // deliberately defaults to false — a truthful internet probe needs an outbound request the
    // daemon shouldn't make unprompted, and false only makes the client push time it already has
    // (a harmless extra sync), never skip one.
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;
    internal Func<long> TickMs { get; set; } = () => Environment.TickCount64;
    internal Func<bool> GpsDeviceProbe { get; set; } = ProbeGpsDevices;
    internal Func<bool> InternetProbe { get; set; } = () => false;

    public TimeSyncService(ILogger<TimeSyncService> logger, ISystemClockSetter? clockSetter = null,
            IProfileStore? profiles = null) {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _clockSetter = clockSetter ?? new LinuxSystemClockSetter();
        _profiles = profiles;
    }

    public Task<TimeSyncStateDto> GetStateAsync(CancellationToken ct) {
        string source, trust;
        DateTimeOffset? syncedAt;
        long? syncedAtTick;
        TimeSyncLocationDto? location;
        double offset;
        lock (_gate) {
            source = _source;
            trust = _trust;
            syncedAt = _syncedAtUtc;
            syncedAtTick = _syncedAtTickMs;
            location = _location;
            offset = _trackedOffsetSeconds;
        }
        var now = Now();
        // §31.1: the waterfall proceeds only on a fresh (< 1 h), at-least-medium-trust sync.
        // Elapsed time comes from the monotonic clock, never from subtracting the (possibly
        // uncorrected) system clock from the (corrected) pushed instant.
        var synced = syncedAtTick is { } tick
            && TimeSpan.FromMilliseconds(TickMs() - tick) < Freshness
            && trust is "high" or "medium";
        return Task.FromResult(new TimeSyncStateDto(
            Synced: synced,
            Source: source,
            Trust: trust,
            CurrentTimeUtc: now,
            SystemTimeOffsetSeconds: offset,
            Location: location,
            InternetAvailableOnPi: InternetProbe(),
            InternalGpsAvailable: GpsDeviceProbe(),
            SyncedAtUtc: syncedAt));
    }

    // A pushed time outside this window is a malformed request, not a plausible clock: a request
    // body missing time_utc deserializes to year 1, and clock_settime would faithfully apply it
    // to the real Pi under CAP_SYS_TIME (#832 r1). GPS-era lower bound, generous upper.
    private static readonly DateTimeOffset MinPlausibleTime = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MaxPlausibleTime = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public Task<TimeSyncPushResultDto> PushAsync(TimeSyncPushRequestDto request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TimeUtc < MinPlausibleTime || request.TimeUtc > MaxPlausibleTime) {
            throw new TimeSyncInvalidRequestException(
                $"time_utc '{request.TimeUtc:O}' is outside the plausible window ({MinPlausibleTime:yyyy}–{MaxPlausibleTime:yyyy}) — refusing to set the system clock to it.");
        }
        if (request.Location is { } l && (l.Lat is < -90 or > 90 || l.Lng is < -180 or > 180)) {
            throw new TimeSyncInvalidRequestException(
                $"location ({l.Lat}, {l.Lng}) is outside valid latitude/longitude ranges.");
        }
        // §31.2 — the wire source maps to a state source + a trust CEILING the request can't
        // exceed (a client claiming "high" for a manual entry stays low).
        var (stateSource, maxTrust) = request.Source?.Trim().ToLowerInvariant() switch {
            "client" => ("client", "medium"),
            "gps-mobile" => ("gps-external", "medium"),
            "manual" => ("manual", "low"),
            _ => throw new TimeSyncInvalidSourceException(
                $"Unknown time-sync source '{request.Source}' — expected client, gps-mobile or manual."),
        };
        var trust = ClampTrust(request.Trust, maxTrust);
        return Task.FromResult(ApplyCore(stateSource, trust, request.TimeUtc, request.Location));
    }

    /// <summary>§31.1 step 2 — apply a USB-GPS self-sync (source <c>gps-internal</c>, trust
    /// <c>high</c> per §31.2). Called by the GPS worker, never from the wire; the same plausibility
    /// window applies (a receiver with a wrong almanac must not set the clock to nonsense).</summary>
    public Task<TimeSyncPushResultDto> ApplyGpsSyncAsync(DateTimeOffset timeUtc, TimeSyncLocationDto? location, CancellationToken ct) {
        if (timeUtc < MinPlausibleTime || timeUtc > MaxPlausibleTime) {
            throw new TimeSyncInvalidRequestException(
                $"GPS time '{timeUtc:O}' is outside the plausible window — refusing to set the system clock to it.");
        }
        if (location is { } l && (l.Lat is < -90 or > 90 || l.Lng is < -180 or > 180)) {
            location = null; // a bad position must not poison the profile; the time sync still applies
        }
        return Task.FromResult(ApplyCore("gps-internal", "high", timeUtc, location));
    }

    private TimeSyncPushResultDto ApplyCore(string stateSource, string trust, DateTimeOffset timeUtc, TimeSyncLocationDto? location) {
        // Normalize the fix to 2-decimal (~1 km) site precision ONCE, so the profile write and the
        // GET-state location (which clients echo into their own fill affordances) agree exactly.
        if (location is { } raw) {
            location = raw with { Lat = Math.Round(raw.Lat, 2), Lng = Math.Round(raw.Lng, 2) };
        }
        var before = Now();
        var beforeOffset = (timeUtc - before).TotalSeconds;
        var clockSet = TrySetClock(timeUtc);
        // A successful set leaves ~0 residual (the set itself takes microseconds); a failed one
        // leaves the whole pushed offset outstanding, tracked for GET.
        var afterOffset = clockSet ? 0.0 : beforeOffset;

        bool locationUpdated;
        lock (_gate) {
            // The site-settings read-modify-write rides inside the same gate as the state update:
            // there are two independent sync writers now (a wire push and the USB-GPS worker), and
            // an unserialized RMW would let one clobber fields the other just wrote (#834 r2).
            locationUpdated = location is not null && TryApplyLocation(location);
            _source = stateSource;
            _trust = trust;
            _syncedAtUtc = timeUtc;
            _syncedAtTickMs = TickMs();
            if (location is not null) {
                _location = location;
            }
            _trackedOffsetSeconds = afterOffset;
        }
        LogSyncApplied(stateSource, trust, beforeOffset, clockSet);
        return new TimeSyncPushResultDto(
            Before: new TimeSyncBeforeAfterDto(before, beforeOffset),
            After: new TimeSyncBeforeAfterDto(clockSet ? timeUtc : Now(), afterOffset),
            LocationUpdated: locationUpdated,
            ClockSet: clockSet);
    }

    private static string ClampTrust(string? requested, string max) {
        static int Rank(string t) => t switch { "high" => 3, "medium" => 2, "low" => 1, _ => 0 };
        var r = requested?.Trim().ToLowerInvariant();
        if (r is not ("high" or "medium" or "low")) {
            return max; // absent/garbage → the source's ceiling (its natural §31.2 value)
        }
        return Rank(r) > Rank(max) ? max : r;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort boundary: a clock-set fault (missing capability, exotic platform) must degrade to offset tracking, never fail the sync push. Log-and-recover.")]
    private bool TrySetClock(DateTimeOffset utc) {
        try {
            var ok = _clockSetter.TrySet(utc);
            if (!ok) {
                LogClockSetUnavailable();
            }
            return ok;
        } catch (Exception ex) {
            LogClockSetFailed(ex);
            return false;
        }
    }

    // Write the pushed location into the active profile's site settings — the single source every
    // lat/long consumer already reads. Best-effort: a store fault degrades to state-only location.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort boundary: a profile-store write fault must not fail the time sync that motivated the push. Log-and-recover.")]
    private bool TryApplyLocation(TimeSyncLocationDto loc) {
        if (_profiles is null) {
            return false;
        }
        try {
            var site = _profiles.GetSiteSettings();
            _profiles.PutSiteSettings(site with {
                LatitudeDeg = loc.Lat,   // already normalized to 2 decimals by ApplyCore
                LongitudeDeg = loc.Lng,
                // Null altitude = unknown (an RMC-only GPS fix) — keep whatever the profile has
                // rather than silently zeroing the elevation every twilight calc depends on.
                ElevationM = loc.Alt ?? site.ElevationM,
            });
            return true;
        } catch (Exception ex) {
            LogLocationWriteFailed(ex);
            return false;
        }
    }

    // §31.4 — a plugged USB GPS presents as /dev/ttyUSB* or /dev/ttyACM*. Presence only; the
    // NMEA fix probe is the follow-up slice.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort probe: any IO fault enumerating /dev (permissions, non-Linux) reads as 'no GPS', never an error surface.")]
    private static bool ProbeGpsDevices() {
        try {
            if (!Directory.Exists("/dev")) {
                return false;
            }
            return Directory.GetFiles("/dev", "ttyUSB*").Length > 0
                || Directory.GetFiles("/dev", "ttyACM*").Length > 0;
        } catch (Exception) {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Time sync applied: source={Source}, trust={Trust}, offset was {OffsetSeconds}s, clockSet={ClockSet}")]
    partial void LogSyncApplied(string source, string trust, double offsetSeconds, bool clockSet);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "System clock set unavailable (missing CAP_SYS_TIME or unsupported platform) — tracking the offset instead. DEPLOY.md: sudo setcap cap_sys_time+ep <server binary>")]
    partial void LogClockSetUnavailable();

    [LoggerMessage(Level = LogLevel.Warning, Message = "System clock set threw — tracking the offset instead")]
    partial void LogClockSetFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pushed location could not be written into the profile site settings")]
    partial void LogLocationWriteFailed(Exception ex);
}

/// <summary>Thrown by <see cref="TimeSyncService.PushAsync"/> for a malformed push — an
/// implausible time (a missing <c>time_utc</c> deserializes to year 1 and would be applied to
/// the real clock under CAP_SYS_TIME), an out-of-range location, or an unknown source (the
/// derived type). The endpoint maps it to <c>422 Unprocessable Entity</c>.</summary>
public class TimeSyncInvalidRequestException : Exception {
    public TimeSyncInvalidRequestException() { }
    public TimeSyncInvalidRequestException(string message) : base(message) { }
    public TimeSyncInvalidRequestException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>A source outside the §31.3 wire set (<c>client|gps-mobile|manual</c>).</summary>
public sealed class TimeSyncInvalidSourceException : TimeSyncInvalidRequestException {
    public TimeSyncInvalidSourceException() { }
    public TimeSyncInvalidSourceException(string message) : base(message) { }
    public TimeSyncInvalidSourceException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>§31.4 — sets the clock via libc <c>clock_settime(CLOCK_REALTIME)</c>. Needs
/// CAP_SYS_TIME on the process (the DEPLOY.md setcap step); without it the call returns EPERM
/// and this reports false. Non-Linux platforms report false without attempting.</summary>
internal sealed class LinuxSystemClockSetter : ISystemClockSetter {

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec {
        public long tv_sec;
        public long tv_nsec;
    }

    private const int ClockRealtime = 0;

    // Classic DllImport (not LibraryImport): the struct is blittable so no marshalling code is
    // generated, which keeps the call AOT-safe without enabling unsafe blocks project-wide.
    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int clock_settime(int clockId, ref Timespec tp);

    public bool TrySet(DateTimeOffset utc) {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            return false;
        }
        var unix = utc.ToUnixTimeMilliseconds();
        var ts = new Timespec {
            tv_sec = unix / 1000,
            tv_nsec = unix % 1000 * 1_000_000,
        };
        return clock_settime(ClockRealtime, ref ts) == 0;
    }
}
