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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #2044 — with the meridian flip off and "Stop before meridian" set to N minutes, nothing else stops a mount that
/// tracks into the pier (the flip trigger is inert with AutoFlip off). Every tick this watch reads the profile and
/// the mount; when the target is N minutes or less before the meridian and the mount tracks, it stops tracking
/// (<see cref="ITelescopeMediator.SetTrackingEnabled"/>), pauses the active runs
/// (<see cref="ISequencerService.PauseActiveRunsAsync(CancellationToken)"/>) and publishes
/// <see cref="WsEventCatalog.TelescopeLimitReached"/>. It runs with or without a plan, never slews or parks, and
/// does nothing with the flip on. One trip per approach: it re-arms once the target is outside the window again
/// (past the meridian, or a slew elsewhere), so a user who turns tracking back on inside the window is not fought.
/// </summary>
public sealed partial class MeridianStopWatch : BackgroundService {

    /// <summary>The tick. The smallest stop-before is 5 min, so 15 s costs at most a quarter of a minute.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);

    /// <summary>The <c>limit</c> token of the event payload.</summary>
    public const string StopBeforeMeridianLimit = "stop_before_meridian";

    // A sidereal hour of hour angle passes in this many solar hours.
    private const double SolarPerSiderealHour = 0.99726956633;

    private readonly IProfileStore _profiles;
    private readonly ITelescopeMediator _telescope;
    private readonly Func<ISequencerService?> _sequencer;
    private readonly IWsBroadcaster? _ws;
    private readonly ILogger<MeridianStopWatch> _logger;
    private readonly TimeSpan _interval;
    private bool _tripped;

    public MeridianStopWatch(IProfileStore profiles, ITelescopeMediator telescope, Func<ISequencerService?> sequencer,
            IWsBroadcaster? ws, ILogger<MeridianStopWatch> logger, TimeSpan? interval = null) {
        _profiles = profiles;
        _telescope = telescope;
        _sequencer = sequencer;
        _ws = ws;
        _logger = logger;
        _interval = interval ?? DefaultInterval;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A background watch: a profile, mount or broadcaster fault on one tick is logged and the next tick tries again; it must never end the loop.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
            try {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                LogTickFailed(ex);
            }
        }
    }

    /// <summary>One look at the profile and the mount (internal for tests).</summary>
    internal async Task TickAsync(CancellationToken ct) {
        var policies = _profiles.GetSafetyPolicies();
        var stopBefore = !policies.MeridianFlipAuto && policies.StopBeforeMeridianMin is int n && n > 0 ? n : (int?)null;
        if (stopBefore is null) {
            _tripped = false;
            return;
        }
        var info = _telescope.GetInfo();
        if (MinutesToMeridian(info) is not double minutes || minutes > stopBefore.Value) {
            _tripped = false;
            return;
        }
        if (_tripped || !info.TrackingEnabled || info.Slewing || info.AtPark) {
            return;
        }
        _tripped = true;
        var stopped = _telescope.SetTrackingEnabled(false);
        var paused = _sequencer() is { } sequencer ? (await sequencer.PauseActiveRunsAsync(ct).ConfigureAwait(false)).Count : 0;
        var detail = Detail(stopBefore.Value, stopped, paused);
        LogTripped(detail);
        if (_ws is not null) {
            var payload = new TelescopeLimitReachedPayload(StopBeforeMeridianLimit, stopBefore.Value, stopped, paused, detail);
            await _ws.PublishAsync(WsEventCatalog.TelescopeLimitReached,
                JsonSerializer.SerializeToElement(payload, AraJsonSerializerContext.Default.TelescopeLimitReachedPayload),
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>Solar minutes until the pointing reaches the upper meridian (0 to 24 h; just past it reads ~24 h), or
    /// null when it cannot be said: not connected, no position, no site (sidereal time NaN), or the JNOW transform's
    /// natives missing.</summary>
    internal static double? MinutesToMeridian(TelescopeInfo info) {
        if (!info.Connected || info.Coordinates is null || double.IsNaN(info.SiderealTime)) {
            return null;
        }
        try {
            var ra = info.Coordinates.Transform(Epoch.JNOW).RA;
            return AstroUtil.EuclidianModulus(ra - info.SiderealTime, 24) * 60 * SolarPerSiderealHour;
        } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                or BadImageFormatException or TypeInitializationException) {
            return null;
        }
    }

    internal static string Detail(int minutes, bool trackingStopped, int runsPaused) =>
        (trackingStopped
            ? string.Create(CultureInfo.InvariantCulture, $"Tracking stopped {minutes} min before the meridian (meridian flip off).")
            : string.Create(CultureInfo.InvariantCulture, $"The mount did not stop tracking {minutes} min before the meridian (meridian flip off); stop it by hand."))
        + (runsPaused > 0 ? " Plan paused." : "") + " Ara didn't move the mount.";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Meridian stop (#2044): {Detail}")]
    private partial void LogTripped(string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Meridian stop watch tick failed (#2044); retrying next tick")]
    private partial void LogTickFailed(Exception ex);
}
