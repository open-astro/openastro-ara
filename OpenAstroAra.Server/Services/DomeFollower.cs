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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility.Notification;
using OpenAstroAra.Equipment.Equipment.MyDome;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1689 — keeps the dome slit in front of the mount. "Follow the mount" is the profile's
/// <c>DomeSettingsDto.FollowMount</c> (so it survives a restart and a profile switch); this service turns
/// the dome when the mount has finished a slew, flipped side of the pier, or drifted past the profile's
/// azimuth tolerance. The azimuth itself comes from <see cref="IDomeSynchronization"/> (the ported NINA
/// geometry) and the move from <see cref="DomeService"/>. Following stops (and the setting goes off) when
/// the dome is turned by hand, parked, homed, aborted or disconnected, when its azimuth cannot be set, when
/// the safety policy reacts to unsafe conditions, on an emergency stop, and when the dome geometry puts the
/// scope outside the dome.
/// </summary>
public sealed partial class DomeFollower : IDomeFollower, IHostedService, IDisposable {

    private readonly DomeService _dome;
    private readonly ITelescopeMediator _telescope;
    private readonly IDomeSynchronization _geometry;
    private readonly IProfileService _profile;
    private readonly IProfileStore _store;
    private readonly ILogger<DomeFollower> _logger;
    // One dome move at a time: the tick, a sequencer "Synchronize dome" and the meridian flip share it.
    private readonly SemaphoreSlim _moveGate = new(1, 1);
    private readonly object _lock = new();
    private CancellationTokenSource _followCts = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private bool _disposed;
    private bool _mountWasSlewing;
    private PierSide? _lastPierSide;
    private bool _failureShown;
    private bool _geometryFailureLogged;
    private bool _lastReportedFollowing;

    /// <summary>Tick cadence; production 2 s, tests drive <see cref="TickAsync"/> directly.</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Poll inside <see cref="WaitForDomeSynchronization"/>.</summary>
    internal TimeSpan WaitPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    /// <summary>The clock the sidereal time is read from; tests pin it so two pointings are a known distance apart.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public DomeFollower(DomeService dome, ITelescopeMediator telescope, IDomeSynchronization geometry,
            IProfileService profile, IProfileStore store, ILogger<DomeFollower>? logger = null) {
        _dome = dome;
        _telescope = telescope;
        _geometry = geometry;
        _profile = profile;
        _store = store;
        _logger = logger ?? NullLogger<DomeFollower>.Instance;
        dome.AttachFollower(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>True while the setting is on and the dome is connected.</summary>
    public bool IsFollowing => _store.GetDomeSettings().FollowMount && _dome.GetInfo().Connected;

    public bool IsSynchronized {
        get {
            try {
                var dome = _dome.GetInfo();
                var tel = _telescope.GetInfo();
                if (!IsFollowing || !dome.Connected || !tel.Connected || tel.Coordinates is null || double.IsNaN(dome.Azimuth)) {
                    return false;
                }
                return TryFollowTarget(tel, out var target) && IsDomeWithinTolerance(Angle.ByDegree(dome.Azimuth), target);
            } catch (InvalidOperationException) {
                return false;
            }
        }
    }

    /// <summary>Turn following on. Throws InvalidOperationException with the reason when it cannot.</summary>
    public Task Start() {
        var dome = _dome.GetInfo();
        if (!dome.Connected) {
            throw new InvalidOperationException("The dome is not connected.");
        }
        if (!dome.CanSetAzimuth) {
            throw new InvalidOperationException("This dome cannot turn to an azimuth, so it cannot follow the mount.");
        }
        var next = _store.GetDomeSettings() with { FollowMount = true };
        if (DomeSettingsValidator.Error(next) is { } reason) {
            throw new InvalidOperationException(reason);
        }
        lock (_lock) {
            _failureShown = false;
            _lastPierSide = null;
            _mountWasSlewing = false;
        }
        _store.PutDomeSettings(next);
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task StopAsync() {
        TurnOff("stopped");
        return Task.CompletedTask;
    }

    /// <summary>Turn following off and drop a move in flight. A no-op when it is already off.</summary>
    internal void TurnOff(string reason) {
        var current = _store.GetDomeSettings();
        if (!current.FollowMount) {
            return;
        }
        CancelMove();
        _store.PutDomeSettings(current with { FollowMount = false });
        LogStopped(reason);
        RaiseChanged();
    }

    /// <summary>Drop a move in flight and leave the setting alone (a profile switch disconnecting the old dome).</summary>
    internal void CancelMove() {
        CancellationTokenSource old;
        lock (_lock) {
            old = _followCts;
            _followCts = new CancellationTokenSource();
        }
        old.Cancel();
        old.Dispose();
    }

    /// <summary>One look at the mount and the dome; sends at most one dome slew.</summary>
    internal async Task TickAsync() {
        if (_disposed || !_store.GetDomeSettings().FollowMount) {
            return;
        }
        var dome = _dome.GetInfo();
        if (!dome.Connected) {
            return;
        }
        if (!dome.CanSetAzimuth) {
            TurnOff("the dome cannot set its azimuth");
            return;
        }
        if (dome.Slewing || dome.ShutterStatus is ShutterState.ShutterOpening or ShutterState.ShutterClosing || double.IsNaN(dome.Azimuth)) {
            return;
        }
        var tel = _telescope.GetInfo();
        if (!tel.Connected || tel.AtPark || tel.Coordinates is null) {
            return;
        }
        var settings = _profile.ActiveProfile.DomeSettings;
        if (tel.Slewing) {
            _mountWasSlewing = true;
            if (!settings.SynchronizeDuringMountSlew) {
                return;
            }
        }
        var flipped = _lastPierSide is { } last && tel.SideOfPier != PierSide.pierUnknown && last != tel.SideOfPier;
        if (tel.SideOfPier != PierSide.pierUnknown) {
            _lastPierSide = tel.SideOfPier;
        }
        if (!TryFollowTarget(tel, out var target)) {
            return;
        }
        var force = !tel.Slewing && (_mountWasSlewing || flipped);
        if (!force && IsDomeWithinTolerance(Angle.ByDegree(dome.Azimuth), target)) {
            return;
        }
        CancellationToken token;
        lock (_lock) {
            token = _followCts.Token;
        }
        await MoveAsync(target.Azimuth.Degree, dome.Name, token).ConfigureAwait(false);
        if (!tel.Slewing) {
            _mountWasSlewing = false;
        }
    }

    // Start() refuses following without a dome radius, but a profile switch or import brings the setting back as
    // saved: turn following off here, before the geometry's radius check shows its error pill.
    // A German mount's dome azimuth needs the side of the pier, which the mount does not report for a moment after
    // a connect, or ever when its driver has no SideOfPier: wait (exposures keep waiting) and retry on the next tick.
    // With those checked here and the position checked by both callers, the only InvalidOperationException left is
    // the geometry's radius check (the scope outside the dome for this pointing). It shows an error pill each time,
    // so it turns following off: one pill, not one per tick or per wait poll.
    private bool TryFollowTarget(TelescopeInfo tel, out TopocentricCoordinates target) {
        target = null!;
        if (_profile.ActiveProfile.DomeSettings.DomeRadiusMm <= 0) {
            TurnOff("no dome radius is set");
            return false;
        }
        if (PierSideMissing(tel.SideOfPier)) {
            if (!_geometryFailureLogged) {
                _geometryFailureLogged = true;
                LogPierSideUnknown();
            }
            return false;
        }
        try {
            target = GetSynchronizedDomeCoordinates(tel);
            _geometryFailureLogged = false;
            return true;
        } catch (InvalidOperationException ex) {
            LogGeometryFailed(ex);
            TurnOff("the dome geometry puts the scope outside the dome");
        } catch (ArgumentException ex) {
            if (!_geometryFailureLogged) {
                _geometryFailureLogged = true;
                LogGeometryFailed(ex);
            }
        }
        return false;
    }

    private bool PierSideMissing(PierSide sideOfPier) =>
        _profile.ActiveProfile.DomeSettings.MountType == MountType.EQUATORIAL && sideOfPier == PierSide.pierUnknown;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Follow-slew boundary: the dome call can throw arbitrary driver/HTTP exceptions; a failure is logged and shown once as a message pill, and the next tick retries. Cancellation by TurnOff() is swallowed. CA1031's log-and-recover boundary applies.")]
    private async Task MoveAsync(double azimuth, string domeName, CancellationToken token) {
        var acquired = false;
        try {
            // Inside the try: TurnOff() while another move holds the gate must not end the follower loop.
            await _moveGate.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            await _dome.FollowSlewAsync(azimuth, token).ConfigureAwait(false);
            _failureShown = false;
        } catch (OperationCanceledException) {
            // TurnOff() or the daemon shutting down.
        } catch (Exception ex) {
            LogSlewFailed(ex);
            if (!_failureShown) {
                _failureShown = true;
                Notifier.ShowError(string.IsNullOrWhiteSpace(domeName)
                    ? "The dome could not turn to follow the mount: " + ex.Message
                    : domeName + " could not turn to follow the mount: " + ex.Message);
            }
        } finally {
            if (acquired) {
                _moveGate.Release();
            }
        }
    }

    public async Task WaitForDomeSynchronization(CancellationToken cancellationToken) {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _profile.ActiveProfile.DomeSettings.DomeSyncTimeoutSeconds));
        var started = DateTime.UtcNow;
        while (IsFollowing && !IsSettled()) {
            if (DateTime.UtcNow - started >= timeout) {
                LogWaitTimedOut((int)timeout.TotalSeconds);
                return;
            }
            await Task.Delay(WaitPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsSettled() {
        var dome = _dome.GetInfo();
        var tel = _telescope.GetInfo();
        return !dome.Slewing && !tel.Slewing && IsSynchronized;
    }

    public async Task<bool> TriggerTelescopeSync(CancellationToken cancellationToken) {
        var tel = _telescope.GetInfo();
        if (!tel.Connected || tel.Coordinates is null) {
            return false;
        }
        return await SyncToScopeCoordinates(tel.Coordinates, tel.SideOfPier, cancellationToken).ConfigureAwait(false);
    }

    // Centering and the sequencer's Synchronize dome act only on a false result, so a sync the geometry cannot work
    // out or the dome does not finish is false, not an exception. A cancel still throws.
    public async Task<bool> SyncToScopeCoordinates(Coordinates coordinates, PierSide sideOfPier, CancellationToken cancellationToken) {
        var dome = _dome.GetInfo();
        // No dome radius: the geometry was never set, and its radius check would refuse it with an error pill.
        if (!dome.Connected || !dome.CanSetAzimuth || _profile.ActiveProfile.DomeSettings.DomeRadiusMm <= 0) {
            return false;
        }
        if (PierSideMissing(sideOfPier)) {
            LogSyncPierSideUnknown();
            return false;
        }
        TopocentricCoordinates target;
        try {
            target = TargetFor(coordinates, sideOfPier);
        } catch (InvalidOperationException ex) {
            LogGeometryFailed(ex);
            return false;
        }
        await _moveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            return await _dome.FollowSlewAsync(target.Azimuth.Degree, cancellationToken).ConfigureAwait(false);
        } catch (SequenceEntityFailedException ex) {
            LogSyncFailed(ex);
            return false;
        } finally {
            _moveGate.Release();
        }
    }

    public TopocentricCoordinates GetSynchronizedDomeCoordinates(TelescopeInfo telescopeInfo) {
        ArgumentNullException.ThrowIfNull(telescopeInfo);
        if (telescopeInfo.Coordinates is null) {
            throw new InvalidOperationException("The mount reports no position.");
        }
        return TargetFor(telescopeInfo.Coordinates, telescopeInfo.SideOfPier);
    }

    private TopocentricCoordinates TargetFor(Coordinates coordinates, PierSide sideOfPier) {
        var site = _profile.ActiveProfile.AstrometrySettings;
        var lst = AstroUtil.GetLocalSiderealTime(UtcNow(), site.Longitude);
        return _geometry.TargetDomeCoordinates(coordinates, lst, Angle.ByDegree(site.Latitude), Angle.ByDegree(site.Longitude), site.Elevation, sideOfPier);
    }

    public bool IsDomeWithinTolerance(Angle currentDomeAzimuth, TopocentricCoordinates targetDomeCoordinates) {
        var diff = Math.Abs(((targetDomeCoordinates.Azimuth.Degree - currentDomeAzimuth.Degree + 540.0) % 360.0) - 180.0);
        return diff <= _profile.ActiveProfile.DomeSettings.AzimuthToleranceDegrees;
    }

    private void RaiseChanged() {
        var now = IsFollowing;
        lock (_lock) {
            if (now == _lastReportedFollowing) {
                return;
            }
            _lastReportedFollowing = now;
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFollowing)));
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) {
        lock (_lock) {
            _loopCts = new CancellationTokenSource();
            _loop = RunLoopAsync(_loopCts.Token);
        }
        return Task.CompletedTask;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Tick boundary: any escape is logged and the loop keeps polling; it must never take the daemon down. CA1031's log-and-recover boundary applies.")]
    private async Task RunLoopAsync(CancellationToken ct) {
        using var timer = new PeriodicTimer(PollInterval);
        try {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) {
                try {
                    await TickAsync().ConfigureAwait(false);
                    RaiseChanged();
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    LogTickFailed(ex);
                }
            }
        } catch (OperationCanceledException) {
            // shutting down
        }
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken) {
        Task? loop;
        CancellationTokenSource? loopCts;
        lock (_lock) {
            loopCts = _loopCts;
            loop = _loop;
        }
        if (loopCts is not null) {
            await loopCts.CancelAsync().ConfigureAwait(false);
        }
        if (loop is not null) {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }
        _disposed = true;
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _followCts.Cancel();
        _followCts.Dispose();
        _moveGate.Dispose();
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Dome stopped following the mount: {Reason}")]
    private partial void LogStopped(string reason);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Dome follow slew failed")]
    private partial void LogSlewFailed(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Dome azimuth for the mount's position could not be worked out; check the dome geometry")]
    private partial void LogGeometryFailed(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Dome cannot follow the mount yet: the mount reports no side of the pier, which a German equatorial mount needs; trying again")]
    private partial void LogPierSideUnknown();

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Dome not turned to the scope: the mount reports no side of the pier, which a German equatorial mount needs")]
    private partial void LogSyncPierSideUnknown();

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Dome could not turn to the scope")]
    private partial void LogSyncFailed(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Dome did not reach the mount's azimuth within {Seconds} s; carrying on")]
    private partial void LogWaitTimedOut(int seconds);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Dome follower tick failed")]
    private partial void LogTickFailed(Exception ex);
}