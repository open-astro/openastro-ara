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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services.Video {

    /// <summary>
    /// §77.3 pointing — solve where you can, capture where you can't. Planets don't
    /// plate-solve on the planetary chip (arcminute FOV, no stars); the guide scope
    /// does. Flow: ephemeris (NOVAS topocentric apparent place from the profile site)
    /// → slew → guide-camera capture + solve via the §45 machinery
    /// (<see cref="PolarAlignService.CaptureSolveGuideFrameAsync"/>) → sync + re-slew
    /// until the offset is inside tolerance → set the tracking rate (lunar for the
    /// Moon, sidereal for planets). The Sun is refused: §77's solar path requires a
    /// filter interlock that does not exist yet.
    /// </summary>
    public sealed partial class PlanetaryPointingService {
        private const int MaxIterations = 5;
        private const double DefaultToleranceArcmin = 2.0;

        private readonly ILogger logger;
        private readonly ITelescopeMediator telescope;
        private readonly PolarAlignService polarAlign;
        private readonly IProfileStore profileStore;

        public PlanetaryPointingService(
            ILogger<PlanetaryPointingService> logger,
            ITelescopeMediator telescope,
            PolarAlignService polarAlign,
            IProfileStore profileStore) {
            this.logger = logger;
            this.telescope = telescope;
            this.polarAlign = polarAlign;
            this.profileStore = profileStore;
        }

        /// <summary>Resolve the request to JNOW coordinates + tracking mode; throws
        /// ArgumentException for unknown/refused targets or missing site data.</summary>
        internal (Coordinates Target, TrackingMode Tracking, string TargetName) ResolveTarget(PlanetaryPointRequestDto request) {
            ArgumentNullException.ThrowIfNull(request);
            if (!string.IsNullOrWhiteSpace(request.Target)) {
                var token = request.Target.ToLowerInvariant();
                if (token == "sun") {
                    throw new ArgumentException(
                        "the Sun is refused — solar pointing requires the §77 solar-filter interlock, which does not exist yet",
                        nameof(request));
                }
                if (!Enum.TryParse<AstroUtil.SolarSystemBody>(token, ignoreCase: true, out var body)
                    || body == AstroUtil.SolarSystemBody.Sun || !Enum.IsDefined(body)) {
                    throw new ArgumentException(
                        $"unknown target '{request.Target}' (mercury|venus|mars|jupiter|saturn|uranus|neptune|pluto|moon, or explicit ra_hours + dec_degrees)",
                        nameof(request));
                }
                var site = profileStore.GetSiteSettings();
                if (!double.IsFinite(site.LatitudeDeg) || !double.IsFinite(site.LongitudeDeg)
                    || (site.LatitudeDeg == 0 && site.LongitudeDeg == 0)) {
                    throw new ArgumentException(
                        "the profile has no site location — set latitude/longitude before ephemeris pointing", nameof(request));
                }
                var observer = new ObserverInfo {
                    Latitude = site.LatitudeDeg,
                    Longitude = site.LongitudeDeg,
                    Elevation = site.ElevationM,
                };
                var now = DateTime.UtcNow;
                var position = AstroUtil.GetBodyPosition(body, now, AstroUtil.GetJulianDate(now), observer);
                if (!double.IsFinite(position.RA) || !double.IsFinite(position.Dec)) {
                    throw new ArgumentException($"ephemeris computation failed for '{token}'", nameof(request));
                }
                var coordinates = new Coordinates(
                    Angle.ByHours(position.RA), Angle.ByDegree(position.Dec), Epoch.JNOW);
                var tracking = body == AstroUtil.SolarSystemBody.Moon ? TrackingMode.Lunar : TrackingMode.Sidereal;
                return (coordinates, tracking, token);
            }
            if (request.RaHours is { } ra && request.DecDegrees is { } dec) {
                if (!(ra >= 0 && ra < 24) || !(dec >= -90 && dec <= 90)) {
                    throw new ArgumentException("ra_hours must be in [0,24) and dec_degrees in [-90,90]", nameof(request));
                }
                var coordinates = new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Hours);
                return (coordinates, TrackingMode.Sidereal, FormattableString.Invariant($"ra/dec {ra:0.###}h {dec:0.##}°"));
            }
            throw new ArgumentException("provide either target (a solar-system body) or ra_hours + dec_degrees", nameof(request));
        }

        /// <summary>
        /// The §77.3 loop. Reports one progress tick per completed solve attempt.
        /// Throws on refusal/config problems; a non-converging sky is a failure with
        /// the last measured offset in the message.
        /// </summary>
        public async Task PointAsync(PlanetaryPointRequestDto request, Action<int> progress, CancellationToken ct) {
            ArgumentNullException.ThrowIfNull(progress);
            var (target, tracking, name) = ResolveTarget(request);
            var toleranceArcmin = request.ToleranceArcmin is > 0 and <= 60 ? request.ToleranceArcmin.Value : DefaultToleranceArcmin;

            LogPointingStarted(logger, name, target.RA, target.Dec, toleranceArcmin);
            if (!await telescope.SlewToCoordinatesAsync(target, ct).ConfigureAwait(false)) {
                throw new InvalidOperationException("the initial slew was refused — is the mount connected and unparked?");
            }

            var lastOffsetArcmin = double.NaN;
            for (var attempt = 1; attempt <= MaxIterations; attempt++) {
                ct.ThrowIfCancellationRequested();
                var targetJnow = target.Transform(Epoch.JNOW);
                var outcome = await polarAlign.CaptureSolveGuideFrameAsync(
                    (targetJnow.RADegrees, targetJnow.Dec), ct).ConfigureAwait(false);
                progress(attempt);
                if (!outcome.Success) {
                    LogSolveFailed(logger, attempt);
                    continue;   // one failed solve is one lost attempt, not a hard fail
                }
                var solved = new Coordinates(
                    Angle.ByDegree(outcome.RaDegJnow), Angle.ByDegree(outcome.DecDegJnow), Epoch.JNOW);
                lastOffsetArcmin = SeparationArcmin(solved, targetJnow);
                LogSolveResult(logger, attempt, lastOffsetArcmin);
                if (lastOffsetArcmin <= toleranceArcmin) {
                    telescope.SetTrackingMode(tracking);
                    var trackingName = tracking.ToString();
                    LogPointingConverged(logger, name, attempt, lastOffsetArcmin, trackingName);
                    return;
                }
                // Classic solve-and-sync recenter (§28 pattern via the guide scope):
                // teach the mount where it actually points, then re-slew to the target.
                if (!await telescope.Sync(solved).ConfigureAwait(false)) {
                    throw new InvalidOperationException("mount sync was refused — cannot correct pointing");
                }
                if (!await telescope.SlewToCoordinatesAsync(target, ct).ConfigureAwait(false)) {
                    throw new InvalidOperationException("the correction slew was refused");
                }
            }
            var lastText = double.IsNaN(lastOffsetArcmin)
                ? "no solve succeeded"
                : string.Create(CultureInfo.InvariantCulture, $"last offset {lastOffsetArcmin:0.##}′");
            throw new InvalidOperationException(
                $"pointing did not converge within {MaxIterations} attempts ({lastText}, tolerance {toleranceArcmin}′) — " +
                "check guide-scope focus, solver setup, and mount pointing");
        }

        private static double SeparationArcmin(Coordinates a, Coordinates b) {
            var raA = a.RADegrees * Math.PI / 180;
            var raB = b.RADegrees * Math.PI / 180;
            var decA = a.Dec * Math.PI / 180;
            var decB = b.Dec * Math.PI / 180;
            var cosSep = Math.Sin(decA) * Math.Sin(decB) + Math.Cos(decA) * Math.Cos(decB) * Math.Cos(raA - raB);
            return Math.Acos(Math.Clamp(cosSep, -1, 1)) * 180 / Math.PI * 60;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Planetary pointing started: {Target} (RA {RaHours}h, Dec {DecDeg}°, tolerance {TolArcmin}′).")]
        private static partial void LogPointingStarted(ILogger logger, string target, double raHours, double decDeg, double tolArcmin);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Pointing solve attempt {Attempt} failed; retrying.")]
        private static partial void LogSolveFailed(ILogger logger, int attempt);

        [LoggerMessage(Level = LogLevel.Information, Message = "Pointing attempt {Attempt}: offset {OffsetArcmin}′.")]
        private static partial void LogSolveResult(ILogger logger, int attempt, double offsetArcmin);

        [LoggerMessage(Level = LogLevel.Information, Message = "Planetary pointing converged on {Target} in {Attempts} attempt(s), offset {OffsetArcmin}′; tracking {Tracking}.")]
        private static partial void LogPointingConverged(ILogger logger, string target, int attempts, double offsetArcmin, string tracking);
    }
}
