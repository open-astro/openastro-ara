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
/// Phase 13.1 — placeholder <see cref="IFrameRepository"/> so the wire
/// protocol for <c>POST /api/v1/frames/{id}/preview</c> is testable
/// end-to-end before the real FITS → JPEG pipeline lands.
///
/// Only <see cref="GetPreviewAsync"/> and <see cref="GetThumbnailAsync"/>
/// return real data — both serve the same precomputed 64×64 mid-gray
/// baseline JPEG (the smallest reasonable image that exercises the full
/// JFIF wire path). Every other method returns null or throws — Phase
/// 13.2+ swaps this in for a real implementation backed by the §28 frame
/// catalog DB + §66 OpenCvSharp4 preview generator (per PORT_PLAYBOOK §12.5).
/// </summary>
public sealed class PlaceholderFrameRepository : IFrameRepository {
    // 64×64 mid-gray baseline JPEG, JFIF 1.01, ~291 bytes. Embedded
    // base64 so we don't ship a binary fixture file separately. Verified
    // with `file(1)`: "JPEG image data, JFIF standard 1.01, baseline,
    // precision 8, 64x64". Replace with the real FITS-to-JPEG output
    // once the OpenCvSharp4 pipeline lands.
    private const string PlaceholderJpegBase64 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDABALDA4MChAODQ4SERATGCgaGBYWGDEjJR0oOjM9PDkz" +
        "ODdASFxOQERXRTc4UG1RV19iZ2hnPk1xeXBkeFxlZ2P/2wBDARESEhgVGC8aGi9jQjhCY2NjY2Nj" +
        "Y2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2P/wAARCABAAEADASIA" +
        "AhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAr/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFAEB" +
        "AAAAAAAAAAAAAAAAAAAAAP/EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/AKgAAAAA" +
        "AAAAA//Z";

    private static readonly byte[] PlaceholderJpegBytes =
        Convert.FromBase64String(PlaceholderJpegBase64);

    // Phase 13.2 — fake fixture frames so the WILMA Library view + the
    // §65 preview/Detail panes have real wire shapes to render. Three
    // frames covering common scenarios: a Light with a quality score, a
    // Light without one, and a Dark. All point at the same placeholder
    // session so the §40 "frames by session" UI has something to group.
    // Real frame catalog (§28 DB-backed) lands in Phase 13.3+.
    private static readonly Guid SampleSessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid[] SampleFrameIds = new[] {
        Guid.Parse("22222222-2222-2222-2222-222222222221"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("22222222-2222-2222-2222-222222222223"),
    };

    private static readonly FrameDto[] SampleFrames = new[] {
        new FrameDto(
            Id: SampleFrameIds[0],
            SessionId: SampleSessionId,
            TargetName: "M31",
            FrameType: FrameType.Light,
            FilterName: "L",
            ExposureSeconds: 180,
            Gain: 100,
            Offset: 50,
            TemperatureC: -10.0,
            CapturedUtc: new DateTimeOffset(2026, 5, 30, 3, 14, 0, TimeSpan.Zero),
            FilePath: "/media/openastroara/M31/2026-05-30/light_180s_L_001.fits",
            FileSizeBytes: 33_554_432,
            Width: 4144, Height: 2822, BitDepth: 16,
            Hfr: 1.85, StarCount: 412, Eccentricity: 0.32,
            GuidingRmsArcsec: 0.74, SnrEstimate: 45.2,
            // Populated quality score so the §65 Frame Detail UI can
            // develop against the non-null path. Component breakdown
            // matches the per-light metrics above; composite is the §50.10
            // weighted average shape WILMA renders as a 0-1 bar.
            QualityScore: new QualityScoreBreakdownDto(
                Composite: 0.87,
                HfrComponent: 0.92,
                StarCountComponent: 0.84,
                EccentricityComponent: 0.78,
                GuidingRmsComponent: 0.88,
                SnrComponent: 0.91,
                Explanation: "Good seeing + low RMS; HFR comfortably under target."),
            Rating: 4,
            Tags: new[] { "good-seeing" }),
        new FrameDto(
            Id: SampleFrameIds[1],
            SessionId: SampleSessionId,
            TargetName: "M31",
            FrameType: FrameType.Light,
            FilterName: "R",
            ExposureSeconds: 180,
            Gain: 100,
            Offset: 50,
            TemperatureC: -10.0,
            CapturedUtc: new DateTimeOffset(2026, 5, 30, 3, 17, 30, TimeSpan.Zero),
            FilePath: "/media/openastroara/M31/2026-05-30/light_180s_R_002.fits",
            FileSizeBytes: 33_554_432,
            Width: 4144, Height: 2822, BitDepth: 16,
            Hfr: 2.10, StarCount: 388, Eccentricity: 0.41,
            GuidingRmsArcsec: 0.82, SnrEstimate: 38.5,
            QualityScore: null,
            Rating: 3,
            Tags: Array.Empty<string>()),
        new FrameDto(
            Id: SampleFrameIds[2],
            SessionId: SampleSessionId,
            TargetName: "Dark library",
            FrameType: FrameType.Dark,
            FilterName: null,
            ExposureSeconds: 180,
            Gain: 100,
            Offset: 50,
            TemperatureC: -10.0,
            CapturedUtc: new DateTimeOffset(2026, 5, 30, 4, 0, 0, TimeSpan.Zero),
            FilePath: "/media/openastroara/darks/2026-05/dark_180s_001.fits",
            FileSizeBytes: 33_554_432,
            Width: 4144, Height: 2822, BitDepth: 16,
            Hfr: null, StarCount: null, Eccentricity: null,
            GuidingRmsArcsec: null, SnrEstimate: null,
            QualityScore: null,
            Rating: 0,
            Tags: Array.Empty<string>()),
    };

    private static FrameListItemDto ToListItem(FrameDto f) =>
        new(f.Id, f.SessionId, f.TargetName, f.FrameType, f.FilterName,
            f.ExposureSeconds, f.CapturedUtc, f.Hfr, f.StarCount,
            CompositeQualityScore: null, Rating: f.Rating);

    public Task<CursorPage<FrameListItemDto>> ListAsync(int limit, string? cursor, Guid? sessionId, string? targetName, CancellationToken ct) {
        IEnumerable<FrameDto> q = SampleFrames;
        if (sessionId is Guid sid) q = q.Where(f => f.SessionId == sid);
        if (!string.IsNullOrEmpty(targetName))
            q = q.Where(f => string.Equals(f.TargetName, targetName, StringComparison.OrdinalIgnoreCase));
        var items = q.Take(Math.Max(1, limit)).Select(ToListItem).ToList();
        return Task.FromResult(new CursorPage<FrameListItemDto>(items, NextCursor: null, HasMore: false));
    }

    public Task<FrameDto?> GetAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<FrameDto?>(Array.Find(SampleFrames, f => f.Id == id));

    public Task<(byte[] Bytes, string ContentType)?> GetPreviewAsync(Guid id, FramePreviewRequestDto request, CancellationToken ct) =>
        Task.FromResult<(byte[] Bytes, string ContentType)?>((PlaceholderJpegBytes, "image/jpeg"));

    public Task<(byte[] Bytes, string ContentType)?> GetThumbnailAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<(byte[] Bytes, string ContentType)?>((PlaceholderJpegBytes, "image/jpeg"));

    public Task<(Stream FitsStream, string FileName)?> OpenDownloadAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<(Stream FitsStream, string FileName)?>(null);

    public Task<OperationAcceptedDto> BulkRateAsync(BulkRateRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("frames.bulk-rate", idempotencyKey));

    public Task<OperationAcceptedDto> BulkTagAsync(BulkTagRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("frames.bulk-tag", idempotencyKey));

    public Task<OperationAcceptedDto> BulkDeleteAsync(BulkDeleteRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("frames.bulk-delete", idempotencyKey));
}
