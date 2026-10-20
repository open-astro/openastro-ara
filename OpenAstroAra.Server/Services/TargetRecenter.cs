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
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Astrometry;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

public enum TargetRecenterOutcome {
    /// <summary>No coordinate-bearing target to centre on (or no sequencer to ask).</summary>
    NotAttempted,
    /// <summary>No centering service is wired on this daemon.</summary>
    NoCenteringService,
    /// <summary>The plate solver is not configured (<see cref="PlateSolverConfigurationException"/>).</summary>
    Unconfigured,
    /// <summary>§38.10a — the target sits below the horizon limit; nothing was slewed.</summary>
    TargetTooLow,
    Recentered,
    Failed,
    /// <summary>The caller's own bound expired.</summary>
    Timeout,
    /// <summary>The caller's token was cancelled.</summary>
    Cancelled,
}

public readonly record struct TargetRecenterResult(TargetRecenterOutcome Outcome, double AltitudeDeg = double.NaN, double LimitDeg = double.NaN) {
    public static TargetRecenterResult Of(TargetRecenterOutcome outcome) => new(outcome);
}

/// <summary>
/// The ONE bounded, best-effort "plate solve and re-center on the run's target" used by the §35
/// safety auto-resume, the §38.10 resume refinement, the §42.2 star-lost recovery and a dither
/// restart after a long guiding gap. The §38.10a horizon guard runs first (a target that has sunk
/// below the limit is never slewed to), then <see cref="ICenteringService.CenterOnTarget"/> under
/// the caller's timeout. Nothing here throws except on a programming error: every rig fault maps
/// to an outcome the caller turns into an honest notification.
/// </summary>
public sealed partial class TargetRecenter {

    private readonly Func<ICenteringService?> _centering;
    private readonly IProfileStore? _profiles;
    private readonly ILogger<TargetRecenter> _logger;

    public TargetRecenter(Func<ICenteringService?> centering, IProfileStore? profiles = null, ILogger<TargetRecenter>? logger = null) {
        _centering = centering ?? throw new ArgumentNullException(nameof(centering));
        _profiles = profiles;
        _logger = logger ?? NullLogger<TargetRecenter>.Instance;
    }

    /// <summary>The first of <paramref name="runIds"/> whose live plan carries a coordinate target is
    /// centred on; with a single mount the other runs inherit whatever pointing results.</summary>
    public async Task<TargetRecenterResult> RecenterActiveTargetAsync(ISequencerService? sequencer, IReadOnlyList<Guid> runIds, TimeSpan timeout, CancellationToken ct) {
        if (sequencer is null) {
            return TargetRecenterResult.Of(TargetRecenterOutcome.NotAttempted);
        }
        Coordinates? target = null;
        foreach (var id in runIds) {
            if (ct.IsCancellationRequested) {
                return TargetRecenterResult.Of(TargetRecenterOutcome.Cancelled);
            }
            try {
                target = await sequencer.GetActiveTargetCoordinatesAsync(id, ct).ConfigureAwait(false);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return TargetRecenterResult.Of(TargetRecenterOutcome.Cancelled);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                LogTargetReadFailed(ex, id);
            }
            if (target is not null) {
                break;
            }
        }
        if (target is null) {
            return TargetRecenterResult.Of(TargetRecenterOutcome.NotAttempted);
        }
        return await RecenterAsync(target, timeout, ct).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort refinement boundary: a centering fault (camera busy, driver error, solver crash) degrades to a Failed outcome the caller reports; logged, never rethrown.")]
    public async Task<TargetRecenterResult> RecenterAsync(Coordinates target, TimeSpan timeout, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(target);
        if (ct.IsCancellationRequested) {
            return TargetRecenterResult.Of(TargetRecenterOutcome.Cancelled);
        }
        if (TargetBelowHorizon(target) is { } low) {
            LogTargetTooLow(low.AltitudeDeg, low.LimitDeg);
            return new TargetRecenterResult(TargetRecenterOutcome.TargetTooLow, low.AltitudeDeg, low.LimitDeg);
        }
        ICenteringService? centering;
        try {
            centering = _centering();
        } catch (Exception ex) {
            LogCenteringFailed(ex);
            return TargetRecenterResult.Of(TargetRecenterOutcome.Failed);
        }
        if (centering is null) {
            return TargetRecenterResult.Of(TargetRecenterOutcome.NoCenteringService);
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        try {
            LogStarted(target.RADegrees, target.Dec);
            var result = await centering.CenterOnTarget(target, solveProgress: null, progress: null, bounded.Token).ConfigureAwait(false);
            return TargetRecenterResult.Of(result?.Success == true ? TargetRecenterOutcome.Recentered : TargetRecenterOutcome.Failed);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return TargetRecenterResult.Of(TargetRecenterOutcome.Cancelled);
        } catch (OperationCanceledException) {
            LogTimedOut((int) timeout.TotalSeconds);
            return TargetRecenterResult.Of(TargetRecenterOutcome.Timeout);
        } catch (PlateSolverConfigurationException ex) {
            LogUnconfigured(ex);
            return TargetRecenterResult.Of(TargetRecenterOutcome.Unconfigured);
        } catch (Exception ex) {
            LogCenteringFailed(ex);
            return TargetRecenterResult.Of(TargetRecenterOutcome.Failed);
        }
    }

    /// <summary>§38.10a — the target's current altitude vs. the profile's horizon limit at its
    /// azimuth (custom-horizon interpolation when enabled, else the flat default floor). Null =
    /// fine to proceed: above the limit, or the altitude/limit can't be determined — the guard
    /// only ever SKIPS a slew, never blocks one on a missing profile.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A profile read or transform fault must degrade to 'unknown — proceed', never fault the caller. Log-and-recover boundary.")]
    internal (double AltitudeDeg, double LimitDeg)? TargetBelowHorizon(Coordinates coords) {
        try {
            var site = _profiles?.GetSiteSettings();
            if (site is null) {
                return null;
            }
            // SiteAstrometry — the server's ONE managed sky model (shared with the §58.9 flip
            // predictor and Tonight's Sky); sub-degree precision is irrelevant to a horizon floor.
            var lst = SiteAstrometry.LocalSiderealTimeDeg(DateTimeOffset.UtcNow, site.LongitudeDeg);
            var hourAngle = (lst - coords.RADegrees % 360.0 + 360.0) % 360.0;
            var altitude = SiteAstrometry.AltitudeFromHourAngleDeg(coords.Dec, site.LatitudeDeg, hourAngle);
            var azimuth = SiteAstrometry.AzimuthFromHourAngleDeg(coords.Dec, site.LatitudeDeg, hourAngle);
            var limit = HorizonLimitDeg(site, azimuth);
            return altitude < limit ? (altitude, limit) : null;
        } catch (Exception ex) {
            LogAltitudeCheckFailed(ex);
            return null;
        }
    }

    /// <summary>The horizon altitude at <paramref name="azimuthDeg"/>: the §36 custom-horizon
    /// skyline interpolated via the ONE shared implementation
    /// (<see cref="CustomHorizonValidator.AltitudeAtAzimuth"/>), else the flat default floor.</summary>
    private double HorizonLimitDeg(SiteSettingsDto site, double azimuthDeg) {
        if (!site.UseCustomHorizon) {
            return site.DefaultHorizonAltitudeDeg;
        }
        var (normalized, _) = CustomHorizonValidator.Normalize(_profiles?.GetCustomHorizon());
        var points = normalized?.Points;
        if (points is null || points.Count == 0) {
            return site.DefaultHorizonAltitudeDeg;
        }
        return CustomHorizonValidator.AltitudeAtAzimuth(points, azimuthDeg);
    }

    [LoggerMessage(EventId = 4240, Level = LogLevel.Information, Message = "Re-centering on the run's target (RA {RaDeg:F3}°, Dec {DecDeg:F3}°) by plate solve")]
    private partial void LogStarted(double raDeg, double decDeg);

    [LoggerMessage(EventId = 4241, Level = LogLevel.Warning, Message = "Re-center timed out after {TimeoutSec} s")]
    private partial void LogTimedOut(int timeoutSec);

    [LoggerMessage(EventId = 4242, Level = LogLevel.Warning, Message = "Re-center skipped: no plate solver configured")]
    private partial void LogUnconfigured(Exception exception);

    [LoggerMessage(EventId = 4243, Level = LogLevel.Warning, Message = "Re-center failed")]
    private partial void LogCenteringFailed(Exception exception);

    [LoggerMessage(EventId = 4244, Level = LogLevel.Warning, Message = "Re-center: reading run {RunId}'s target failed")]
    private partial void LogTargetReadFailed(Exception exception, Guid runId);

    [LoggerMessage(EventId = 4245, Level = LogLevel.Information, Message = "§38.10a re-center skipped: target at {AltitudeDeg:F1}° is below the {LimitDeg:F1}° horizon limit")]
    private partial void LogTargetTooLow(double altitudeDeg, double limitDeg);

    [LoggerMessage(EventId = 4246, Level = LogLevel.Warning, Message = "§38.10a altitude check failed; proceeding with the re-center")]
    private partial void LogAltitudeCheckFailed(Exception exception);
}
