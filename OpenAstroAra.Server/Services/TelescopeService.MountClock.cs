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
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>#2071 — whether the mount's clock has been set from the synced rig clock and checked, so a
/// GoTo, a Sync or a polar alignment points at the right sky.</summary>
public interface IMountClockGate {
    /// <summary>Null when the mount's clock is verified; otherwise the English reason, for the
    /// <c>mount-clock-not-verified</c> refusal or the failed sequencer step.</summary>
    string? MountClockMotionRefusal();

    /// <summary>The same answer, but while a clock push is running (the app's time sync just after it
    /// connects) or a host clock step is about to start one, it first waits for that push, up to about
    /// 10 s, instead of refusing a move the push is about to allow.</summary>
    Task<string?> MountClockRefusalAfterPushAsync(CancellationToken token);
}

// #2071 — the §31 push corrected the Pi's clock and never the mount's. On RC51 (2026-11-19) the mount
// kept the time AlpacaBridge sent at boot from the stale Pi clock, 6 h 48 min slow, so its sidereal
// time was about 102° off. After a trusted time sync, and on every connect, the daemon now writes the
// mount's UTCDate from the synced clock, reads the mount back and compares its UTCDate and SiderealTime
// with the synced clock and the LST computed for the profile's site. GoTo, Sync and polar alignment wait
// for that check. Park and Home do not: iOptron parks and homes by encoder, and a safety park must never
// wait for a clock.
public sealed partial class TelescopeService : IMountClockGate {

    /// <summary>The mount's clock as Alpaca exposes it (a seam over <see cref="ITelescopeDevice"/>).</summary>
    internal interface IMountClock {
        /// <summary>UTC; throws ASCOM's not-implemented family when the driver has no clock to report.</summary>
        DateTime UtcDate { get; }
        void SetUtcDate(DateTime utc);
        double SiderealTime { get; }
        double Latitude { get; }
        double Longitude { get; }
        bool Slewing { get; }
    }

    private sealed class DeviceMountClock(ITelescopeDevice mount) : IMountClock {
        public DateTime UtcDate => mount.UTCDate;
        public void SetUtcDate(DateTime utc) => mount.UTCDate = utc;
        public double SiderealTime => mount.SiderealTime;
        public double Latitude => mount.SiteLatitude;
        public double Longitude => mount.SiteLongitude;
        public bool Slewing => mount.Slewing;
    }

    /// <summary>A check of the mount's clock, and the wall and monotonic clocks when it was made: a host
    /// clock step after it shows up as the two drifting apart. <paramref name="WriteOwed"/>: no trusted time
    /// has landed in the mount's clock yet (never written, the write failed, or the mount still cannot report
    /// its clock), so the 30 s recheck writes it again instead of only reading it.</summary>
    private sealed record MountClockCheck(bool Verified, string Reason, DateTimeOffset WallAtCheck, long TickAtCheck, bool WriteOwed);

    // Tolerances. The mount's UTC and LST within 5 s (Ara's LST is mean sidereal time; a driver's may be
    // apparent, about 1 s apart). The site within 0.01° (the profile holds two decimals).
    internal static readonly TimeSpan MountClockTolerance = TimeSpan.FromSeconds(5);
    internal const double MountSiteToleranceDeg = 0.01 + 1e-6;
    private static readonly TimeSpan HostStepTolerance = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MountClockReverifyPeriod = TimeSpan.FromSeconds(30);

    internal const string NoTrustedTimeReason = "the rig clock has not been synced from the app, a GPS or NTP";
    private const string SlewingReason = "the mount was still slewing when Ara went to set its clock";
    internal const string SlewWaitRefusal =
        "Mount clock not verified: Ara is waiting for the mount to stop slewing before it sets the mount's clock. Try again when it has stopped.";

    // Null: no time-sync service is wired (unit tests, tools), and the gate is off.
    private readonly Func<ITimeSyncService?>? _timeSyncResolver;
    // Under _gate. Cleared, and the session bumped, by ResetSessionLocked: a push begun for an earlier
    // session must not open the gate for this one.
    private MountClockCheck? _mountClock;
    private long _mountClockSession;
    private int _mountClockPushes;
    // 1 while a clock push waits for the mount to stop slewing before it writes the clock.
    private int _mountClockSlewWait;

    /// <summary>The connected mount's clock, null when none is connected; tests swap in a fake.</summary>
    internal Func<IMountClock?>? MountClockSource { get; set; }

    /// <summary>The monotonic clock the host-step check compares <see cref="UtcNow"/> with; tests drive it.</summary>
    internal Func<long> TickMs { get; set; } = () => Environment.TickCount64;

    /// <summary>How long a sequencer GoTo waits for a clock push already in flight (a §42.3 reconnect
    /// just before it) before it fails the step.</summary>
    internal TimeSpan MountClockPushWait { get; set; } = TimeSpan.FromSeconds(10);

    private DeviceMountClock? ConnectedDeviceClock() {
        lock (_gate) {
            return _lifecycle.Live is { } client ? new DeviceMountClock(client) : null;
        }
    }

    private IMountClock? CurrentMountClock() => MountClockSource is { } source ? source() : ConnectedDeviceClock();

    private void ForgetMountClockLocked() {
        _mountClock = null;
        _mountClockSession++;
    }

    /// <summary>Connect: the profile's site goes in first, then the clock is set and checked against it,
    /// in one body under the site-push gate so the LST check sees the longitude just written.</summary>
    internal void OnMountConnected() {
        var store = _siteStore;
        var site = store is null ? null : MountSiteSource is { } siteSource ? siteSource() : ConnectedDeviceSite();
        var clock = _timeSyncResolver is null ? null : CurrentMountClock();
        if (site is null && clock is null) {
            return;
        }
        long session;
        lock (_gate) {
            session = _mountClockSession;
        }
        if (clock is not null) {
            Interlocked.Increment(ref _mountClockPushes);
        }
        _ = RunSitePush(async () => {
            try {
                await _sitePushGate.WaitAsync().ConfigureAwait(false);
                try {
                    try {
                        if (store is not null && site is not null) {
                            await PushSiteCoreAsync(store, site, force: true).ConfigureAwait(false);
                        }
                    } finally {
                        // A site push that throws must not skip the clock: with no check recorded the
                        // gate would stay closed for the whole session (the 2 s tick only rechecks a
                        // check that exists).
                        if (clock is not null) {
                            await PushMountClockCoreAsync(clock, "connect", session).ConfigureAwait(false);
                        }
                    }
                } finally {
                    _sitePushGate.Release();
                }
            } finally {
                if (clock is not null) {
                    Interlocked.Decrement(ref _mountClockPushes);
                }
            }
        });
    }

    /// <summary>Follow the time sync: a sync of medium or high trust sets the connected mount's clock.</summary>
    internal void FollowTimeSync(ITimeSyncService timeSync) {
        ArgumentNullException.ThrowIfNull(timeSync);
        timeSync.TrustedTimeApplied += (_, _) => PushTrustedMountClock("time sync");
    }

    /// <summary>Write the synced time into the connected mount's clock and check it, off the caller's
    /// thread and after any site push in flight.</summary>
    internal void PushTrustedMountClock(string reason) => RunMountClockJob(reason, write: true);

    private void RunMountClockJob(string reason, bool write) {
        if (_timeSyncResolver is null) {
            return;
        }
        var clock = CurrentMountClock();
        if (clock is null) {
            return;
        }
        long session;
        lock (_gate) {
            session = _mountClockSession;
        }
        Interlocked.Increment(ref _mountClockPushes);
        _ = RunSitePush(async () => {
            try {
                await _sitePushGate.WaitAsync().ConfigureAwait(false);
                try {
                    if (write) {
                        await PushMountClockCoreAsync(clock, reason, session).ConfigureAwait(false);
                    } else {
                        VerifyMountClockCore(clock, session, utcRead: null, alwaysLog: false, writeFailed: false);
                    }
                } finally {
                    _sitePushGate.Release();
                }
            } finally {
                Interlocked.Decrement(ref _mountClockPushes);
            }
        });
    }

    // The caller holds _sitePushGate. Never writes a time that is not from a trusted sync (the
    // AlpacaBridge#2419 rule: an unverified host clock never goes to the mount).
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort write boundary: a driver that refuses UTCDate (any ASCOM or transport exception) is logged, and the check that follows decides whether the mount's own clock is right anyway.")]
    private async Task PushMountClockCoreAsync(IMountClock clock, string reason, long session) {
        var timeSync = _timeSyncResolver?.Invoke();
        if (timeSync is null || !timeSync.TryGetTrustedUtc(out _)) {
            LogMountClockNoTrustedTime(reason);
            RecordMountClock(session, verified: false, NoTrustedTimeReason, alwaysLog: false, writeOwed: true);
            return;
        }
        // #2087 review — while this waits out a slew (up to ~3 min), a GoTo that times out waiting for it
        // says so. Pushes run one at a time under _sitePushGate, so one flag is enough.
        bool slewEnded;
        try {
            slewEnded = await SlewEndedAsync(() => {
                var moving = clock.Slewing;
                if (moving) {
                    Volatile.Write(ref _mountClockSlewWait, 1);
                }
                return moving;
            }).ConfigureAwait(false);
        } finally {
            Volatile.Write(ref _mountClockSlewWait, 0);
        }
        if (!slewEnded) {
            LogMountClockSlewing(reason);
            RecordMountClock(session, verified: false, SlewingReason, alwaysLog: false, writeOwed: true);
            return;
        }
        var before = ReadMountUtc(clock);
        // Read again after the slew wait: a manual sync meanwhile drops the trust, and the default
        // (year 1) must never be written into the mount.
        if (!timeSync.TryGetTrustedUtc(out var pushed)) {
            LogMountClockNoTrustedTime(reason);
            RecordMountClock(session, verified: false, NoTrustedTimeReason, alwaysLog: false, writeOwed: true);
            return;
        }
        string? refused = null;
        // #2087 review — a write that failed (a transient bridge error) is tried again by the 30 s recheck; one the
        // driver does not implement never will succeed, so its rechecks only read.
        var writeFailed = false;
        try {
            clock.SetUtcDate(pushed.UtcDateTime);
        } catch (ASCOM.NotImplementedException ex) {
            refused = ex.Message;
        } catch (Exception ex) {
            refused = ex.Message;
            writeFailed = true;
        }
        var after = ReadMountUtc(clock);
        var afterAt = TrustedUtcOrNull(timeSync);
        var beforeText = Describe(before);
        var afterText = Describe(after);
        LogMountClockPushed(reason, beforeText, pushed.UtcDateTime, afterText);
        if (refused is not null) {
            LogMountClockWriteRefused(refused);
        }
        VerifyMountClockCore(clock, session, (after, afterAt), alwaysLog: true, writeFailed);
    }

    private static DateTimeOffset? TrustedUtcOrNull(ITimeSyncService? timeSync) =>
        timeSync is not null && timeSync.TryGetTrustedUtc(out var utc) ? utc : null;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort profile read: a store fault leaves the check to the mount's own longitude.")]
    /// <param name="writeFailed">The push just before this check tried to write the clock and the write failed.</param>
    private void VerifyMountClockCore(IMountClock clock, long session,
            ((DateTime? Utc, bool NotImplemented) Read, DateTimeOffset? At)? utcRead, bool alwaysLog, bool writeFailed) {
        var timeSync = _timeSyncResolver?.Invoke();
        // Each mount reading is compared with the trusted time taken right after it: on a slow bridge the
        // round trips of the reads that follow would otherwise count as clock error (5 s tolerance).
        var (utc, utcAt) = utcRead ?? (ReadMountUtc(clock), TrustedUtcOrNull(timeSync));
        var lst = TryRead(() => clock.SiderealTime);
        var lstAt = TrustedUtcOrNull(timeSync);
        var lat = TryRead(() => clock.Latitude);
        var lng = TryRead(() => clock.Longitude);
        if (utcAt is not { } now || lstAt is not { } lstNow) {
            RecordMountClock(session, verified: false, NoTrustedTimeReason, alwaysLog, writeOwed: true);
            return;
        }
        double? siteLat = null, siteLng = null;
        try {
            if ((_siteStore ?? _profileStore)?.GetSiteSettings() is { } site && HasSite(site.LatitudeDeg, site.LongitudeDeg)) {
                (siteLat, siteLng) = (site.LatitudeDeg, site.LongitudeDeg);
            }
        } catch (Exception) {
            // no profile site: the LST is checked against the mount's own longitude
        }
        var mountUtc = utc.Utc is { } u ? new DateTimeOffset(AlpacaTelescopeDevice.AsUtc(u)) : (DateTimeOffset?)null;
        var (verified, why) = EvaluateMountClock(mountUtc, utc.NotImplemented, lst, lat, lng, now, siteLat, siteLng, lstNow);
        // #2087 review — a mount that cannot report its clock (AlpacaBridge's "not established" after a write that
        // failed, or before any) is written again by the recheck; a read-only recheck would never open the gate.
        var unreadable = utc.Utc is null && !utc.NotImplemented;
        RecordMountClock(session, verified, why, alwaysLog, writeOwed: !verified && (writeFailed || unreadable));
    }

    /// <summary>The check itself, pure. Verified when the mount's UTC is within 5 s of the synced clock,
    /// its SiderealTime within 5 s of the LST computed for the profile's longitude (the mount's own when
    /// the profile has no site), and, when both are known, its site within 0.01° of the profile's. A
    /// UTCDate the driver cannot report (not implemented) leaves the SiderealTime check to decide; one it
    /// fails to report otherwise is not verified (AlpacaBridge answers "not established" when it does not
    /// know the mount's clock). <paramref name="trustedAtLst"/> is the trusted time when the SiderealTime was
    /// read (<paramref name="trustedNow"/>, the one at the UTCDate read, when null).</summary>
    internal static (bool Verified, string Reason) EvaluateMountClock(DateTimeOffset? mountUtc, bool utcNotImplemented,
            double? mountLstHours, double? mountLat, double? mountLng, DateTimeOffset trustedNow, double? siteLat, double? siteLng,
            DateTimeOffset? trustedAtLst = null) {
        var inv = CultureInfo.InvariantCulture;
        if (mountUtc is null && !utcNotImplemented) {
            return (false, "the mount did not report its clock (UTCDate)");
        }
        if (mountUtc is { } m && (m - trustedNow).Duration() > MountClockTolerance) {
            return (false, string.Create(inv, $"the mount's clock is {Span(m - trustedNow)} off the synced rig clock"));
        }
        if (mountLstHours is not double lst || !double.IsFinite(lst)) {
            return (false, "the mount did not report its sidereal time");
        }
        var longitude = siteLng ?? mountLng;
        if (longitude is not double lngDeg || !double.IsFinite(lngDeg)) {
            return (false, "neither the profile nor the mount has a longitude to check the sidereal time against");
        }
        var computed = SiteAstrometry.LocalSiderealTimeDeg(trustedAtLst ?? trustedNow, lngDeg) / 15.0;
        var lstOff = lst - computed;
        lstOff -= 24.0 * Math.Round(lstOff / 24.0); // wrap to ±12 h
        if (Math.Abs(lstOff) * 3600.0 > MountClockTolerance.TotalSeconds) {
            return (false, string.Create(inv, $"the mount's sidereal time is {Span(TimeSpan.FromHours(lstOff))} off the one computed for the site"));
        }
        if (siteLat is double pLat && siteLng is double pLng && mountLat is double mLat && mountLng is double mLng
                && (Math.Abs(mLat - pLat) > MountSiteToleranceDeg || Math.Abs(WrapDeg(mLng - pLng)) > MountSiteToleranceDeg)) {
            return (false, string.Create(inv, $"the mount's site ({mLat:F4}°, {mLng:F4}°) is not the profile's ({pLat:F2}°, {pLng:F2}°)"));
        }
        return mountUtc is { } at
            ? (true, string.Create(inv, $"the mount's clock is {Span(at - trustedNow)} from the synced rig clock and its sidereal time agrees"))
            : (true, "the mount cannot report UTCDate, and its sidereal time agrees with the synced rig clock");
    }

    private static double WrapDeg(double d) => d - 360.0 * Math.Round(d / 360.0);

    private static string Span(TimeSpan off) {
        var s = off.TotalSeconds;
        var sign = s < 0 ? "-" : "+";
        var a = Math.Abs(s);
        return a < 120
            ? string.Create(CultureInfo.InvariantCulture, $"{sign}{a:F1} s")
            : string.Create(CultureInfo.InvariantCulture, $"{sign}{(int)(a / 3600)} h {(int)(a % 3600 / 60)} min");
    }

    private void RecordMountClock(long session, bool verified, string reason, bool alwaysLog, bool writeOwed) {
        bool flipped;
        lock (_gate) {
            if (session != _mountClockSession) {
                return; // a reconnect since: that session runs its own check
            }
            flipped = _mountClock is null || _mountClock.Verified != verified;
            _mountClock = new MountClockCheck(verified, reason, UtcNow(), TickMs(), writeOwed);
        }
        if (!alwaysLog && !flipped) {
            return;
        }
        if (verified) {
            LogMountClockVerified(reason);
        } else {
            LogMountClockNotVerified(reason);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an optional or failing property reads as unknown.")]
    private static (DateTime? Utc, bool NotImplemented) ReadMountUtc(IMountClock clock) {
        try {
            return (clock.UtcDate, false);
        } catch (ASCOM.NotImplementedException) {
            return (null, true);
        } catch (Exception) {
            return (null, false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an optional or failing property reads as unknown.")]
    private static double? TryRead(Func<double> read) {
        try {
            var v = read();
            return double.IsFinite(v) ? v : null;
        } catch (Exception) {
            return null;
        }
    }

    private static string Describe((DateTime? Utc, bool NotImplemented) read) =>
        read.Utc is { } u ? AlpacaTelescopeDevice.AsUtc(u).ToString("O", CultureInfo.InvariantCulture)
            : read.NotImplemented ? "not reported (not implemented)" : "unreadable";

    public string? MountClockMotionRefusal() {
        var refusal = MountClockRefusalNow();
        // A push in flight that waits out a slew: name the slew, not just "setting the clock".
        return refusal is not null && Volatile.Read(ref _mountClockSlewWait) != 0 ? SlewWaitRefusal : refusal;
    }

    private string? MountClockRefusalNow() {
        if (_timeSyncResolver is null) {
            return null;
        }
        MountClockCheck? check;
        lock (_gate) {
            check = _mountClock;
        }
        if (check is null) {
            return Volatile.Read(ref _mountClockPushes) > 0
                ? "Mount clock not verified: Ara is setting and checking the mount's clock. Try again in a moment."
                : "Mount clock not verified: the mount's clock has not been checked since it connected.";
        }
        if (!check.Verified) {
            return check.Reason == NoTrustedTimeReason
                ? $"Mount clock not verified: {check.Reason}. Open the app, or sync the time in Settings, and Ara sets the mount's clock."
                : $"Mount clock not verified: {check.Reason}.";
        }
        if (HostClockStepped(check)) {
            return "Mount clock not verified: the rig clock stepped since the mount's clock was checked. Ara is checking it again.";
        }
        return null;
    }

    private bool HostClockStepped(MountClockCheck check) {
        var wall = (UtcNow() - check.WallAtCheck).TotalMilliseconds;
        var mono = (double)(TickMs() - check.TickAtCheck);
        return Math.Abs(wall - mono) > HostStepTolerance.TotalMilliseconds;
    }

    /// <summary>The 2 s refresh: a host clock step after a check closes the gate and sets the mount's
    /// clock again; an unverified mount with a synced rig clock is checked again about every
    /// 30 s, so a clock another client or the bridge fixed opens the gate without a push.</summary>
    internal void WatchMountClock() {
        if (_timeSyncResolver is null) {
            return;
        }
        MountClockCheck? check;
        long session;
        lock (_gate) {
            check = _mountClock;
            session = _mountClockSession;
        }
        if (check is null || Volatile.Read(ref _mountClockPushes) > 0) {
            return; // the connect's own push has not finished, or another is in flight
        }
        if (check.Verified) {
            if (!HostClockStepped(check)) {
                return;
            }
            var stepMs = (UtcNow() - check.WallAtCheck).TotalMilliseconds - (TickMs() - check.TickAtCheck);
            LogHostClockStepped(stepMs / 1000.0);
            // #2087 review — counted as a push before the check goes unverified, so a GoTo that reads the gate in
            // between waits for the push (MountClockSettling) instead of being refused. The push counts itself too.
            Interlocked.Increment(ref _mountClockPushes);
            try {
                RecordMountClock(session, verified: false, "the rig clock stepped since the mount's clock was checked", alwaysLog: false, writeOwed: true);
                PushTrustedMountClock("rig clock stepped");
            } finally {
                Interlocked.Decrement(ref _mountClockPushes);
            }
            return;
        }
        // Each check records a fresh anchor, so this runs about every 30 s while the clock stays wrong.
        if (TickMs() - check.TickAtCheck < MountClockReverifyPeriod.TotalMilliseconds
                || _timeSyncResolver.Invoke() is not { } timeSync || !timeSync.TryGetTrustedUtc(out _)) {
            return;
        }
        // A clock no trusted time has landed in (the mount was slewing, no trusted time had reached it, the
        // write failed, or the mount could not report its clock) is written now; otherwise the check is
        // read-only. The write itself still takes only trusted time.
        RunMountClockJob("recheck", write: check.WriteOwed);
    }

    /// <summary>A sequencer GoTo right after a reconnect, a REST GoTo or Sync and a Center wait, up to
    /// <see cref="MountClockPushWait"/>, for the clock push already running, then answer like
    /// <see cref="MountClockMotionRefusal"/>. With no push running or about to start, the answer is immediate.</summary>
    public async Task<string?> MountClockRefusalAfterPushAsync(CancellationToken token) {
        var refusal = MountClockMotionRefusal();
        var deadline = TickMs() + (long)MountClockPushWait.TotalMilliseconds;
        while (refusal is not null && MountClockSettling() && TickMs() < deadline) {
            await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            refusal = MountClockMotionRefusal();
        }
        return refusal;
    }

    // A push is running, or a verified check has seen a host clock step that the next 2 s tick turns
    // into a push: either way the answer is about to change.
    private bool MountClockSettling() {
        if (Volatile.Read(ref _mountClockPushes) > 0) {
            return true;
        }
        MountClockCheck? check;
        lock (_gate) {
            check = _mountClock;
        }
        return check is { Verified: true } && HostClockStepped(check);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Mount clock ({Reason}): mount UTC before {Before}, pushed {Pushed:O}, mount UTC after {After}")]
    private partial void LogMountClockPushed(string reason, string before, DateTime pushed, string after);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The mount refused the UTCDate write: {Error}. Its own clock is checked instead")]
    private partial void LogMountClockWriteRefused(string error);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Mount clock not set ({Reason}): the rig clock has not been synced; GoTo, Sync and polar alignment wait for a time sync")]
    private partial void LogMountClockNoTrustedTime(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mount clock not set ({Reason}): the mount was still slewing")]
    private partial void LogMountClockSlewing(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mount clock verified: {Detail}")]
    private partial void LogMountClockVerified(string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mount clock not verified: {Detail}. GoTo, Sync and polar alignment are refused until it is")]
    private partial void LogMountClockNotVerified(string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The rig clock stepped {Seconds:F1} s since the mount's clock was checked; setting it again")]
    private partial void LogHostClockStepped(double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Op} refused: {Reason}")]
    private partial void LogMountOpRejectedClock(string op, string reason);
}
