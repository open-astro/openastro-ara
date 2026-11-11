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
/// the dome is turned by hand, parked, homed, aborted or disconnected, when its azimuth cannot be set, and
/// when the safety policy reacts to unsafe conditions.
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
                if (!IsFollowing || !dome.Connected || !tel.Connected || double.IsNaN(dome.Azimuth)) {
                    return false;
                }
                return IsDomeWithinTolerance(Angle.ByDegree(dome.Azimuth), GetSynchronizedDomeCoordinates(tel));
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
        CancellationTokenSource old;
        lock (_lock) {
            old = _followCts;
            _followCts = new CancellationTokenSource();
        }
        old.Cancel();
        old.Dispose();
        _store.PutDomeSettings(current with { FollowMount = false });
        LogStopped(reason);
        RaiseChanged();
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
        TopocentricCoordinates target;
        try {
            target = GetSynchronizedDomeCoordinates(tel);
        } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) {
            if (!_geometryFailureLogged) {
                _geometryFailureLogged = true;
                LogGeometryFailed(ex);
            }
            return;
        }
        _geometryFailureLogged = false;
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Follow-slew boundary: the dome call can throw arbitrary driver/HTTP exceptions; a failure is logged and shown once as a message pill, and the next tick retries. Cancellation by TurnOff() is swallowed. CA1031's log-and-recover boundary applies.")]
    private async Task MoveAsync(double azimuth, string domeName, CancellationToken token) {
        await _moveGate.WaitAsync(token).ConfigureAwait(false);
        try {
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
            _moveGate.Release();
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

    public async Task<bool> SyncToScopeCoordinates(Coordinates coordinates, PierSide sideOfPier, CancellationToken cancellationToken) {
        var dome = _dome.GetInfo();
        if (!dome.Connected || !dome.CanSetAzimuth) {
            return false;
        }
        var target = TargetFor(coordinates, sideOfPier);
        await _moveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            return await _dome.FollowSlewAsync(target.Azimuth.Degree, cancellationToken).ConfigureAwait(false);
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
        var lst = AstroUtil.GetLocalSiderealTime(DateTime.UtcNow, site.Longitude);
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

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Dome did not reach the mount's azimuth within {Seconds} s; carrying on")]
    private partial void LogWaitTimedOut(int seconds);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Dome follower tick failed")]
    private partial void LogTickFailed(Exception ex);
}