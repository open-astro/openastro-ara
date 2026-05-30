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
/// Phase 13.3 — placeholder <see cref="ISessionService"/>. Returns one
/// fake session matching the sample frames in
/// <see cref="PlaceholderFrameRepository"/> so the WILMA Library view +
/// the §40 session-drilldown UI has a real wire shape to render.
///
/// Composes on <see cref="IFrameRepository"/> so the "frames in this
/// session" listing routes through the same fixture data — no double-
/// counting, no fakery drift between the two endpoints. Phase 13.4+
/// swaps both in for the real §28 DB-backed catalog.
///
/// Mutating endpoints (<see cref="ResumeTargetAsync"/>,
/// <see cref="RestretchAsync"/>) throw — the §28 frame catalog DB +
/// the §38 sequence orchestrator are prerequisites for a real
/// implementation and we don't want a placeholder silently accepting
/// operations it can't perform.
/// </summary>
public sealed class PlaceholderSessionService : ISessionService {
    // Same session id as PlaceholderFrameRepository.SampleSessionId so
    // the cross-endpoint joins work. Keep this in sync if either side
    // moves to a different id.
    private static readonly Guid SampleSessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly SessionDto SampleSession = new(
        Id: SampleSessionId,
        Name: "2026-05-30 M31",
        TargetName: "M31",
        SessionStartUtc: new DateTimeOffset(2026, 5, 30, 3, 0, 0, TimeSpan.Zero),
        SessionEndUtc: new DateTimeOffset(2026, 5, 30, 4, 30, 0, TimeSpan.Zero),
        TotalFrames: 3,
        LightFrames: 2,
        CalibrationFrames: 1,
        FiltersUsed: new[] { "L", "R" },
        ProfileId: null,
        StretchPaletteUsed: "auto");

    private readonly IFrameRepository _frames;

    public PlaceholderSessionService(IFrameRepository frames) {
        _frames = frames;
    }

    public Task<CursorPage<SessionDto>> ListAsync(int limit, string? cursor, CancellationToken ct) =>
        Task.FromResult(new CursorPage<SessionDto>(
            new[] { SampleSession }, NextCursor: null, HasMore: false));

    public Task<SessionDto?> GetAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<SessionDto?>(id == SampleSessionId ? SampleSession : null);

    public Task<CursorPage<FrameListItemDto>> GetFramesAsync(Guid sessionId, int limit, string? cursor, CancellationToken ct) =>
        // Delegate to the frame repo so the "frames in this session" list
        // stays consistent with /api/v1/frames?sessionId=…
        _frames.ListAsync(limit, cursor, sessionId, targetName: null, ct);

    public Task<OperationAcceptedDto> ResumeTargetAsync(Guid sessionId, ResumeTargetRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        throw new NotImplementedException("ResumeTarget lands with the §38 sequence orchestrator in Phase 13.x");

    public Task<OperationAcceptedDto> RestretchAsync(Guid sessionId, SessionRestretchRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        throw new NotImplementedException("Restretch lands with the OpenCvSharp4 preview generator in Phase 13.x");

    public async Task<HfrAnalysisDto?> GetHfrAnalysisAsync(Guid sessionId, CancellationToken ct) {
        if (sessionId != SampleSessionId) return null;

        // Pull the frames for this session and aggregate HFR for the
        // lights with a non-null measurement. Dark/flat frames don't carry
        // HFR and stay out of the time series.
        // Page size 200 covers any plausible session (sample has 3 frames;
        // real sessions cap well below this until §28 DB-backed pagination
        // lands).
        var page = await _frames.ListAsync(limit: 200, cursor: null, sessionId, targetName: null, ct);
        var points = page.Items
            .Where(f => f.Hfr.HasValue)
            .OrderBy(f => f.CapturedUtc)
            .Select(f => new HfrTimeSeriesPointDto(
                Timestamp: f.CapturedUtc,
                Hfr: f.Hfr!.Value,
                StarCount: f.StarCount,
                FrameId: f.Id))
            .ToList();

        if (points.Count == 0) {
            // Session has no HFR-bearing frames yet — match the §40.7
            // "no data" shape rather than a 404 (the session itself
            // exists; the analysis just hasn't accumulated).
            return new HfrAnalysisDto(
                SessionId: sessionId,
                FrameCount: 0,
                MeanHfr: 0,
                StandardDeviation: 0,
                TrendSlopePerHour: 0,
                Trend: "insufficient-data",
                TimeSeries: Array.Empty<HfrTimeSeriesPointDto>());
        }

        var mean = points.Average(p => p.Hfr);
        var variance = points.Average(p => (p.Hfr - mean) * (p.Hfr - mean));
        var stdDev = Math.Sqrt(variance);

        // Simple least-squares slope: x = hours-since-first-frame, y = HFR.
        var t0 = points[0].Timestamp;
        var xs = points.Select(p => (p.Timestamp - t0).TotalHours).ToList();
        var ys = points.Select(p => p.Hfr).ToList();
        var xMean = xs.Average();
        var yMean = ys.Average();
        var numerator = 0.0;
        var denominator = 0.0;
        for (int i = 0; i < points.Count; i++) {
            var dx = xs[i] - xMean;
            numerator += dx * (ys[i] - yMean);
            denominator += dx * dx;
        }
        var slopePerHour = denominator > 0 ? numerator / denominator : 0;

        // Trend label per §40.7: "improving" if HFR is decreasing,
        // "degrading" if increasing, "stable" within ±0.05 px/hr.
        var trend = slopePerHour > 0.05 ? "degrading"
                  : slopePerHour < -0.05 ? "improving"
                  : "stable";

        return new HfrAnalysisDto(
            SessionId: sessionId,
            FrameCount: points.Count,
            MeanHfr: mean,
            StandardDeviation: stdDev,
            TrendSlopePerHour: slopePerHour,
            Trend: trend,
            TimeSeries: points);
    }
}
