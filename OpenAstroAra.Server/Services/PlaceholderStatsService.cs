#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Phase 13.6 — placeholder <see cref="IStatsService"/> covering all 8
/// stats views (§50) with synthetic fixture data that matches the §13.2
/// sample frames + §13.3 sample session (M31, 3 frames, 9 minutes of
/// integration). Real DB-backed aggregations land alongside the §28
/// frame catalog DB.
///
/// The numbers here are intentionally small so the WILMA Stats tab
/// renders something sensible without claiming the system has acquired
/// 50 nights of data. Each view returns whatever shape the §50 chart
/// expects, with enough data points to populate the visualization
/// without padding to fake density.
///
/// CSV export returns null — needs the §28 catalog to enumerate
/// real frames.
/// </summary>
public sealed class PlaceholderStatsService : IStatsService {
    private static readonly DateTimeOffset SessionStart =
        new(2026, 5, 30, 3, 0, 0, TimeSpan.Zero);

    public Task<StatsOverviewDto> GetOverviewAsync(CancellationToken ct) {
        // Synthetic last-30-days sparkline — 5 nights with data,
        // others zero. Reflects a typical light-imaging cadence.
        var dates = new List<DateTimeOffset>();
        var hours = new List<double>();
        for (int dayOffset = 29; dayOffset >= 0; dayOffset--) {
            var date = SessionStart.AddDays(-dayOffset);
            dates.Add(date);
            // Slot a few "real" nights in: today, -3, -7, -14, -21.
            hours.Add(dayOffset switch {
                0 => 0.15,   // today's 9-minute fixture session
                3 => 2.5,
                7 => 4.2,
                14 => 1.8,
                21 => 3.6,
                _ => 0.0,
            });
        }
        return Task.FromResult(new StatsOverviewDto(
            TotalSessions: 5,
            TotalFrames: 184,
            TotalLightFrames: 142,
            TotalIntegrationHours: 12.25,
            UniqueTargetsImaged: 4,
            FirstImageUtc: SessionStart.AddDays(-21),
            LastImageUtc: SessionStart.AddMinutes(20),
            LastSessionScore: 0.84,
            Last30DaysIntegrationHours: new StatsSparklineDto<DateTimeOffset, double>(
                Label: "Integration hours per night (last 30 days)",
                X: dates,
                Y: hours)));
    }

    public Task<StatsTargetsDto> GetTargetsAsync(CancellationToken ct) =>
        Task.FromResult(new StatsTargetsDto(new[] {
            new StatsTargetSummaryDto("M31", 142, 7.1, 0.87, SessionStart.AddMinutes(20)),
            new StatsTargetSummaryDto("M81", 22, 1.83, 0.81, SessionStart.AddDays(-7)),
            new StatsTargetSummaryDto("NGC 7000", 12, 2.0, 0.76, SessionStart.AddDays(-14)),
            new StatsTargetSummaryDto("M42", 8, 1.32, 0.72, SessionStart.AddDays(-21)),
        }));

    public Task<StatsFocusTempDto> GetFocusTempAsync(DateTimeOffset? since, CancellationToken ct) {
        // Tight linear-ish correlation: focuser steps trend down ~12 per
        // 1°C rise. R² ~0.87 mimics what a typical AF/temp dataset shows.
        var samples = new[] {
            new FocusTempPointDto(-12.5, 14860, SessionStart.AddDays(-21)),
            new FocusTempPointDto( -8.0, 14770, SessionStart.AddDays(-14)),
            new FocusTempPointDto( -5.5, 14720, SessionStart.AddDays(-7)),
            new FocusTempPointDto( -3.0, 14660, SessionStart.AddDays(-3)),
            new FocusTempPointDto(-10.5, 14820, SessionStart),
        };
        return Task.FromResult(new StatsFocusTempDto(samples, CorrelationR2: 0.87));
    }

    public Task<StatsGuidingDto> GetGuidingAsync(DateTimeOffset? since, CancellationToken ct) {
        // Six points over the fixture session — sub-arcsec guiding with
        // one excursion to mimic a brief seeing dip.
        var samples = new[] {
            new GuidingRmsPointDto(SessionStart.AddMinutes(2),  0.72, 0.51, 0.50),
            new GuidingRmsPointDto(SessionStart.AddMinutes(5),  0.68, 0.49, 0.47),
            new GuidingRmsPointDto(SessionStart.AddMinutes(10), 1.24, 0.92, 0.83),  // excursion
            new GuidingRmsPointDto(SessionStart.AddMinutes(13), 0.81, 0.58, 0.56),
            new GuidingRmsPointDto(SessionStart.AddMinutes(17), 0.74, 0.52, 0.52),
            new GuidingRmsPointDto(SessionStart.AddMinutes(20), 0.69, 0.49, 0.48),
        };
        return Task.FromResult(new StatsGuidingDto(samples, MeanRmsArcsec: 0.81, P95RmsArcsec: 1.18));
    }

    public Task<StatsFrameQualityDto> GetFrameQualityAsync(string? filter, CancellationToken ct) {
        // 5-bucket histogram across [0.0..1.0]. Skewed right (most
        // frames decent) which matches a normal night.
        var dist = new[] {
            new FrameQualityBucketDto(0.0, 0.2, 3),
            new FrameQualityBucketDto(0.2, 0.4, 8),
            new FrameQualityBucketDto(0.4, 0.6, 24),
            new FrameQualityBucketDto(0.6, 0.8, 67),
            new FrameQualityBucketDto(0.8, 1.0, 40),
        };
        return Task.FromResult(new StatsFrameQualityDto(dist, MeanScore: 0.71, StdDev: 0.18));
    }

    public Task<StatsBestFramesDto> GetBestFramesAsync(int limit, CancellationToken ct) {
        // Top-N best frames; matches the §13.2 fixture Frame 0 + a few
        // synthetic high-scorers for variety.
        var frames = new[] {
            new BestFrameDto(
                FrameId: Guid.Parse("22222222-2222-2222-2222-222222222221"),
                TargetName: "M31", CapturedUtc: SessionStart.AddMinutes(14),
                CompositeScore: 0.87, FilterName: "L"),
            new BestFrameDto(
                FrameId: Guid.Parse("66666666-6666-6666-6666-666666666661"),
                TargetName: "M81", CapturedUtc: SessionStart.AddDays(-7),
                CompositeScore: 0.84, FilterName: "Ha"),
            new BestFrameDto(
                FrameId: Guid.Parse("66666666-6666-6666-6666-666666666662"),
                TargetName: "NGC 7000", CapturedUtc: SessionStart.AddDays(-14),
                CompositeScore: 0.81, FilterName: "OIII"),
        };
        return Task.FromResult(new StatsBestFramesDto(frames.Take(Math.Max(1, limit)).ToList()));
    }

    public Task<StatsCalendarDto> GetCalendarAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct) {
        // Only emit days with non-zero activity — the §50 calendar
        // heatmap renders zero-cells from the date range itself, so we
        // don't have to send 30 empty rows.
        var days = new[] {
            new StatsCalendarDayDto(new DateOnly(2026, 5, 30), 3, 0.15, new[] { "M31" }),
            new StatsCalendarDayDto(new DateOnly(2026, 5, 27), 50, 2.5,  new[] { "M81" }),
            new StatsCalendarDayDto(new DateOnly(2026, 5, 23), 84, 4.2,  new[] { "M81", "M31" }),
            new StatsCalendarDayDto(new DateOnly(2026, 5, 16), 36, 1.8,  new[] { "NGC 7000" }),
            new StatsCalendarDayDto(new DateOnly(2026, 5,  9), 72, 3.6,  new[] { "M42", "NGC 7000" }),
        };
        return Task.FromResult(new StatsCalendarDto(
            days.Where(d => d.Date >= fromDate && d.Date <= toDate).ToList()));
    }

    public Task<(Stream Stream, string FileName)?> OpenCsvExportAsync(string scope, CancellationToken ct) =>
        // Real CSV export needs the §28 frame catalog to enumerate — null
        // signals "feature not available yet" without 501-stubbing the
        // whole route.
        Task.FromResult<(Stream Stream, string FileName)?>(null);
}
