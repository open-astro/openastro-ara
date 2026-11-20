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
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>§31 — the time-sync state machine backing GET/POST <c>/api/v1/server/time-sync</c>.</summary>
public interface ITimeSyncService {
    Task<TimeSyncStateDto> GetStateAsync(CancellationToken ct);

    /// <summary>Apply a client-pushed sync (§31.1 waterfall steps 1/3/5). Throws
    /// <see cref="TimeSyncInvalidSourceException"/> for a source outside
    /// <c>client|gps-client|gps-mobile|manual</c> (→ 422 at the endpoint).</summary>
    Task<TimeSyncPushResultDto> PushAsync(TimeSyncPushRequestDto request, CancellationToken ct);

    /// <summary>#2071 — a time the rig can trust. The OS clock while the kernel reports it NTP-synchronised
    /// (<see cref="IKernelClockSyncProbe"/>, AlpacaBridge #2426's rule); otherwise the time from the last
    /// applied sync of medium or high trust, carried forward on the monotonic clock: right even when the
    /// OS clock set failed or the clock has stepped since. False when the kernel is not synchronised and
    /// no such sync came (a manual, low-trust entry never counts). A client sync does not expire after the
    /// §31.1 hour: the mount clock written from it is checked again on every connect and host clock step.
    /// A client sync over a day old, with no NTP, is logged as a warning once a day (it drifts with the Pi's clock).</summary>
    bool TryGetTrustedUtc(out DateTimeOffset utc);

    /// <summary>#2071 — raised after a sync of medium or high trust is applied, so the mount's clock can
    /// follow it. Never raised for a manual (low-trust) sync.</summary>
    event EventHandler? TrustedTimeApplied;
}

/// <summary>Sets the OS clock. The real implementation needs CAP_SYS_TIME and, under the packaged
/// unit, a SystemCallFilter that allows <c>clock_settime</c> (<c>@clock</c>); a failed set is an
/// expected condition on a dev box or an un-capped or filtered install, never an exception.</summary>
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
/// The daemon never reads a GPS receiver itself (#1305): a USB dongle belongs on the computer
/// running the client, which relays the fix as <c>gps-client</c>. A blind serial probe on the SBC
/// collided with the mount's port, and the SBC's USB ports are for equipment.
/// </summary>
public sealed partial class TimeSyncService : ITimeSyncService {

    private static readonly TimeSpan Freshness = TimeSpan.FromHours(1);
    // #2087 review — a trusted time carried from a client push this old (no NTP) is logged, once a day.
    private static readonly TimeSpan StaleClientSyncAge = TimeSpan.FromDays(1);

    private readonly ILogger<TimeSyncService> _logger;
    private readonly ISystemClockSetter _clockSetter;
    private readonly IProfileStore? _profiles;
    // #2071 — null: only client and GPS pushes are trusted (tests, and platforms without the probe).
    private readonly IKernelClockSyncProbe? _kernelClock;
    // 0 = not read yet, 1 = synchronised, 2 = not: a change is logged once.
    private int _kernelClockState;
    private readonly object _gate = new();

    private string _source = "none";
    private string _trust = "none";
    private DateTimeOffset? _syncedAtUtc;
    // Freshness is measured on the MONOTONIC clock (#832 r1): comparing _syncedAtUtc (corrected,
    // pushed time) against the raw system clock breaks exactly when the clock-set fails — a Pi
    // booting near epoch would read as "fresh forever", one ahead by an hour as instantly stale.
    private long? _syncedAtTickMs;
    private TimeSyncLocationDto? _location;
    // Monotonic time of the last day-old-sync warning; cleared by every applied sync.
    private long? _staleSyncWarnedAtTickMs;
    // Non-zero only when a push couldn't set the OS clock: pushed-time minus system-time at push,
    // re-reported on GET so a consumer that cares can correct. Zeroed by a successful set.
    private double _trackedOffsetSeconds;

    // Test seams. Now: the clock the state machine reasons with. InternetProbe:
    // deliberately defaults to false — a truthful internet probe needs an outbound request the
    // daemon shouldn't make unprompted, and false only makes the client push time it already has
    // (a harmless extra sync), never skip one.
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;
    internal Func<long> TickMs { get; set; } = () => Environment.TickCount64;
    internal Func<bool> InternetProbe { get; set; } = () => false;

    public TimeSyncService(ILogger<TimeSyncService> logger, ISystemClockSetter? clockSetter = null,
            IProfileStore? profiles = null, IKernelClockSyncProbe? kernelClock = null) {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _clockSetter = clockSetter ?? new LinuxSystemClockSetter();
        _profiles = profiles;
        _kernelClock = kernelClock;
    }

    public event EventHandler? TrustedTimeApplied;

    public bool TryGetTrustedUtc(out DateTimeOffset utc) {
        // Read fresh every time (one syscall): the trust ends the moment the kernel loses the sync, and a
        // client push that steps the clock clears it until NTP disciplines the clock again.
        if (KernelClockSynchronized()) {
            utc = Now();
            return true;
        }
        double? staleHours = null;
        bool trusted;
        lock (_gate) {
            if (_syncedAtUtc is { } at && _syncedAtTickMs is { } tick && IsTrusted(_trust)) {
                // Never Now() + the tracked offset: that offset goes stale as soon as anything else
                // (NTP, or AlpacaBridge's own UTCDate handling) steps the OS clock after the push.
                var tickNow = TickMs();
                var age = TimeSpan.FromMilliseconds(tickNow - tick);
                utc = at + age;
                trusted = true;
                // #2087 review — with no NTP the push is carried on the Pi's crystal (tens of ppm), so a push
                // days old has drifted. It stays trusted (no expiry); a warning once a day says so.
                if (age >= StaleClientSyncAge
                        && (_staleSyncWarnedAtTickMs is not { } warned || tickNow - warned >= StaleClientSyncAge.TotalMilliseconds)) {
                    _staleSyncWarnedAtTickMs = tickNow;
                    staleHours = age.TotalHours;
                }
            } else {
                utc = default;
                trusted = false;
            }
        }
        if (staleHours is { } hours) {
            LogClientSyncStale((int)hours);
        }
        return trusted;
    }

    private static bool IsTrusted(string trust) => trust is "high" or "medium";

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A probe fault is 'not synchronised': the rig clock is then trusted only from a client push.")]
    private bool KernelClockSynchronized() {
        if (_kernelClock is null) {
            return false;
        }
        bool synced;
        try {
            synced = _kernelClock.IsSynchronized();
        } catch (Exception) {
            synced = false;
        }
        var state = synced ? 1 : 2;
        var was = Interlocked.Exchange(ref _kernelClockState, state);
        if (was != state && (synced || was != 0)) {
            LogKernelClockTrust(synced);
        }
        return synced;
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
            && IsTrusted(trust);
        return Task.FromResult(new TimeSyncStateDto(
            Synced: synced,
            Source: source,
            Trust: trust,
            CurrentTimeUtc: now,
            SystemTimeOffsetSeconds: offset,
            Location: location,
            InternetAvailableOnPi: InternetProbe(),
            SyncedAtUtc: syncedAt,
            // Today's offset: a start scheduled across a DST change lands an hour off (rare; accepted).
            UtcOffsetMinutes: (int)TimeZoneInfo.Local.GetUtcOffset(now).TotalMinutes));
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
            // A USB GPS dongle on the client computer: the client reads NMEA itself and relays the
            // receiver's UTC + fix. Same trust as a dongle on the Pi (the LAN hop is milliseconds),
            // which is why it is a separate wire source from gps-mobile (a phone's own location
            // service, whose timestamp is the phone clock, stays medium).
            "gps-client" => ("gps-external", "high"),
            "manual" => ("manual", "low"),
            _ => throw new TimeSyncInvalidSourceException(
                $"Unknown time-sync source '{request.Source}' — expected client, gps-client, gps-mobile or manual."),
        };
        var trust = ClampTrust(request.Trust, maxTrust);
        return Task.FromResult(ApplyCore(stateSource, trust, request.TimeUtc, request.Location));
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
            // concurrent wire pushes (two clients, or a client's GPS loop racing its connect push)
            // must not let one clobber fields the other just wrote (#834 r2).
            locationUpdated = location is not null && TryApplyLocation(location);
            _source = stateSource;
            _trust = trust;
            _syncedAtUtc = timeUtc;
            _syncedAtTickMs = TickMs();
            _staleSyncWarnedAtTickMs = null;
            if (location is not null) {
                _location = location;
            }
            _trackedOffsetSeconds = afterOffset;
        }
        LogSyncApplied(stateSource, trust, beforeOffset, clockSet);
        if (IsTrusted(trust)) {
            // Outside the gate: the mount-clock push the handler starts reads TryGetTrustedUtc.
            TrustedTimeApplied?.Invoke(this, EventArgs.Empty);
        }
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
        Justification = "Best-effort boundary: a clock-set fault (missing capability, blocked syscall, exotic platform) must degrade to offset tracking, never fail the sync push. Log-and-recover.")]
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

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Time sync applied: source={Source}, trust={Trust}, offset was {OffsetSeconds}s, clockSet={ClockSet}")]
    partial void LogSyncApplied(string source, string trust, double offsetSeconds, bool clockSet);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "System clock set unavailable (missing CAP_SYS_TIME, a systemd SystemCallFilter without @clock, or unsupported platform) — tracking the offset instead. Packaged unit: check CapabilityBoundingSet/AmbientCapabilities and SystemCallFilter. Bare binary: sudo setcap cap_sys_time+ep <server binary>")]
    partial void LogClockSetUnavailable();

    [LoggerMessage(Level = LogLevel.Warning, Message = "System clock set threw — tracking the offset instead")]
    partial void LogClockSetFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pushed location could not be written into the profile site settings")]
    partial void LogLocationWriteFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Rig clock NTP-synchronised per the kernel: {Synchronized} (#2071; while true the rig clock is trusted without a time sync from the app or a GPS)")]
    partial void LogKernelClockTrust(bool synchronized);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The rig clock was last synced from the app {Hours} h ago and NTP is not running; open the app to sync it again (#2071; the mount's clock is set from that sync, which drifts with the Pi's clock)")]
    partial void LogClientSyncStale(int hours);
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

/// <summary>A source outside the §31.3 wire set (<c>client|gps-client|gps-mobile|manual</c>).</summary>
public sealed class TimeSyncInvalidSourceException : TimeSyncInvalidRequestException {
    public TimeSyncInvalidSourceException() { }
    public TimeSyncInvalidSourceException(string message) : base(message) { }
    public TimeSyncInvalidSourceException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>§31.4 — sets the clock via libc <c>clock_settime(CLOCK_REALTIME)</c>. Needs
/// CAP_SYS_TIME on the process (the DEPLOY.md setcap step) and, under systemd, a filter that allows @clock; without either the call returns EPERM
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
