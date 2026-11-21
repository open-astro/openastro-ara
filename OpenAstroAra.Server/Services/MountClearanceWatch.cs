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
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1589 — holds the mount at the profile's clearance limit while it tracks. Every
/// <see cref="PollInterval"/> it reads the mount's cached position; when the clearance limit is on and a
/// tracking, unparked, still mount points at or above it (<see cref="ClearanceGuard"/>), it stops tracking,
/// stops guiding (a guider chasing the star would push the mount on), pauses every active run at its next
/// instruction boundary, and posts one Critical notification that says why. It never slews, parks or
/// unparks: the mount stays where it is (no unasked motion). A mount that keeps tracking after the stop
/// (the write failed) gets the stop again each tick, without a second notification; the episode ends when
/// tracking reads off or the mount points under the limit, so tracking turned back on above it starts a new
/// one. A GoTo in flight is left to its own target check; the watch judges where the mount settles.
/// <para>The mount's RA follows the mount's own clock. While the #2071 mount clock check has not verified that
/// clock, the rig clock's sidereal time would put the hour angle off by the clock difference (15° an hour), so
/// the watch reads the mount's own sidereal time instead; a mount that cannot report one is not judged (logged
/// once) until its clock is verified.</para>
/// <para>The pause is a plain daemon pause (not "awaiting you"): the §58.12 unattended shutdown, which parks,
/// does not start from it. The user resumes the run.</para>
/// </summary>
public sealed partial class MountClearanceWatch : IHostedService, IDisposable {

    private readonly ITelescopeMediator? _telescope;
    private readonly IProfileStore? _profiles;
    private readonly Func<ISequencerService?>? _sequencerResolver;
    private readonly IGuiderService? _guider;
    private readonly INotificationService? _notifications;
    private readonly IMountClockGate? _mountClock;
    private readonly ILogger<MountClearanceWatch> _logger;

    private readonly object _lock = new();
    private Timer? _timer;
    private bool _disposed;
    private int _ticking;
    // True from the stop until tracking reads off or the mount points under the limit.
    private bool _episode;
    // True while the altitude cannot be judged (the mount's clock is unverified and it reports no sidereal
    // time), so the log says it once.
    private bool _unjudged;

    /// <summary>Poll cadence. At sidereal rate the altitude moves at most 0.25° a minute, so 5 s overshoots the
    /// limit by about 0.02°. Tests compress it.</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Injectable clock for the altitude (tests pin the instant).</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    public MountClearanceWatch(
            ITelescopeMediator? telescope = null,
            IProfileStore? profiles = null,
            Func<ISequencerService?>? sequencerResolver = null,
            IGuiderService? guider = null,
            INotificationService? notifications = null,
            ILogger<MountClearanceWatch>? logger = null,
            IMountClockGate? mountClock = null) {
        _telescope = telescope;
        _profiles = profiles;
        _sequencerResolver = sequencerResolver;
        _guider = guider;
        _notifications = notifications;
        _mountClock = mountClock;
        _logger = logger ?? NullLogger<MountClearanceWatch>.Instance;
    }

    private void OnTick(object? state) {
        // Timer callback boundary: nothing here may throw, and an overlapping tick is skipped.
        _ = TickAsync();
    }

    /// <summary>One observation. Returns true when this tick stopped the mount's tracking.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Timer-callback boundary: any fault is logged and the next tick retries; an escaped exception would be an unobserved task fault.")]
    internal async Task<bool> TickAsync() {
        if (_telescope is null || _profiles is null || Interlocked.Exchange(ref _ticking, 1) == 1) {
            return false;
        }
        try {
            if (ClearanceGuard.LimitDeg(_profiles.GetSafetyPolicies()) is not double limit) {
                _episode = false;
                return false;
            }
            var info = _telescope.GetInfo();
            if (!info.Connected || info.AtPark || !info.TrackingEnabled || info.Coordinates is null) {
                _episode = false;
                return false;
            }
            if (info.Slewing) {
                return false;
            }
            var site = _profiles.GetSiteSettings();
            if (site is null || !double.IsFinite(site.LatitudeDeg) || !double.IsFinite(site.LongitudeDeg)) {
                return false;
            }
            if (await MountAltitudeAsync(site, info.Coordinates).ConfigureAwait(false) is not double altitude) {
                return false;
            }
            if (!ClearanceGuard.Reached(altitude, limit)) {
                _episode = false;
                return false;
            }
            // The mount tracks at or above the limit: stop it first, then the guider, then say why.
            var stopped = await Task.Run(() => _telescope.SetTrackingEnabled(false)).ConfigureAwait(false);
            var guidingStopped = await StopGuidingQuietlyAsync().ConfigureAwait(false);
            if (_episode) {
                LogStopRepeated(altitude, limit, stopped);
                return stopped;
            }
            _episode = true;
            var paused = await PauseRunsQuietlyAsync().ConfigureAwait(false);
            LogLimitReached(altitude, limit, stopped, guidingStopped, paused);
            await NotifyQuietlyAsync(altitude, limit, stopped, paused).ConfigureAwait(false);
            return stopped;
        } catch (Exception ex) {
            LogTickFailed(ex);
            return false;
        } finally {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>Where the mount points, in degrees of altitude; null when it cannot be judged. With the mount's
    /// clock verified (or no clock check wired) the rig clock gives the sidereal time; otherwise the mount's own
    /// sidereal time does, because the mount's RA follows the mount's clock.</summary>
    private async Task<double?> MountAltitudeAsync(SiteSettingsDto site, Coordinates position) {
        var gate = _mountClock;
        if (gate?.MountClockMotionRefusal() is not string unverified) {
            _unjudged = false;
            return ClearanceGuard.AltitudeDeg(site, position.RA, position.Dec, UtcNow());
        }
        var mountLst = await Task.Run(gate.MountSiderealTimeHours).ConfigureAwait(false);
        if (mountLst is not double lst) {
            if (!_unjudged) {
                _unjudged = true;
                LogUnjudged(unverified);
            }
            return null;
        }
        _unjudged = false;
        return ClearanceGuard.AltitudeAtLstDeg(site.LatitudeDeg, lst, position.RA, position.Dec);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort step: a disconnected or faulting guider must not keep the tracking stop from being reported; logged, never rethrown.")]
    private async Task<bool> StopGuidingQuietlyAsync() {
        if (_guider is null) {
            return false;
        }
        try {
            // A guider that is not connected or already stopped is left alone (no refusal to log every episode).
            var guider = await _guider.GetAsync(CancellationToken.None).ConfigureAwait(false);
            if (guider is not { State: EquipmentConnectionState.Connected } || guider.Runtime.State == "stopped") {
                return false;
            }
            await _guider.StopGuidingAsync(null, CancellationToken.None).ConfigureAwait(false);
            return true;
        } catch (Exception ex) {
            LogStepFailed("stop_guiding", ex);
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort step: a sequencer fault must not keep the tracking stop from being reported; logged, never rethrown.")]
    private async Task<int> PauseRunsQuietlyAsync() {
        var sequencer = _sequencerResolver?.Invoke();
        if (sequencer is null) {
            return 0;
        }
        try {
            return (await sequencer.PauseActiveRunsAsync(CancellationToken.None).ConfigureAwait(false)).Count;
        } catch (Exception ex) {
            LogStepFailed("pause", ex);
            return 0;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Notification store faults must never mask the stop itself (the §58.7 precedent). Log-and-recover boundary.")]
    private async Task NotifyQuietlyAsync(double altitude, double limit, bool stopped, int paused) {
        if (_notifications is null) {
            return;
        }
        var what = stopped ? "Ara stopped tracking and guiding" : "Ara could NOT stop tracking (it keeps trying); guiding was stopped";
        var runs = paused > 0 ? " and paused the run" : string.Empty;
        var message = string.Create(CultureInfo.InvariantCulture,
            $"The mount reached {altitude:0.#}° altitude, at the {limit:0.#}° clearance limit set for this rig. {what}{runs}. The mount was not moved. Slew to a lower target before you turn tracking back on.");
        try {
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: DateTimeOffset.UtcNow,
                Severity: NotificationSeverity.Critical,
                Category: NotificationCategory.Safety,
                Title: "Clearance limit reached: tracking stopped",
                Message: message,
                Read: false,
                Dismissed: false,
                DismissedUtc: null,
                Payload: null,
                RelatedEntityType: null,
                RelatedEntityId: null), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogStepFailed("notify", ex);
        }
    }

    // ─── lifecycle ───

    Task IHostedService.StartAsync(CancellationToken cancellationToken) {
        lock (_lock) {
            if (!_disposed && _telescope is not null && _profiles is not null) {
                _timer = new Timer(OnTick, null, PollInterval, PollInterval);
            }
        }
        return Task.CompletedTask;
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken) {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    // ─── logging ───

    [LoggerMessage(EventId = 4256, Level = LogLevel.Warning, Message = "#1589 the mount reached {AltitudeDeg:F1}°, at the {LimitDeg:F1}° clearance limit: tracking stopped={Stopped}, guiding stopped={GuidingStopped}, runs paused={Paused}")]
    private partial void LogLimitReached(double altitudeDeg, double limitDeg, bool stopped, bool guidingStopped, int paused);

    [LoggerMessage(EventId = 4257, Level = LogLevel.Warning, Message = "#1589 the mount still tracks at {AltitudeDeg:F1}°, at the {LimitDeg:F1}° clearance limit: tracking stop sent again (stopped={Stopped})")]
    private partial void LogStopRepeated(double altitudeDeg, double limitDeg, bool stopped);

    [LoggerMessage(EventId = 4258, Level = LogLevel.Warning, Message = "#1589 clearance watch step {Step} failed")]
    private partial void LogStepFailed(string step, Exception exception);

    [LoggerMessage(EventId = 4260, Level = LogLevel.Warning, Message = "#1589 clearance watch cannot judge the mount's altitude: its clock is not verified ({Reason}) and it reports no sidereal time; it is not stopped until its clock is verified")]
    private partial void LogUnjudged(string reason);

    [LoggerMessage(EventId = 4259, Level = LogLevel.Warning, Message = "#1589 clearance watch tick failed; the next tick retries")]
    private partial void LogTickFailed(Exception exception);
}
