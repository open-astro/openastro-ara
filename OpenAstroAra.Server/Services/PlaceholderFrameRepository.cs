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

    public Task<CursorPage<FrameListItemDto>> ListAsync(int limit, string? cursor, Guid? sessionId, string? targetName, CancellationToken ct) =>
        Task.FromResult(new CursorPage<FrameListItemDto>(Array.Empty<FrameListItemDto>(), null, false));

    public Task<FrameDto?> GetAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<FrameDto?>(null);

    public Task<(byte[] Bytes, string ContentType)?> GetPreviewAsync(Guid id, FramePreviewRequestDto request, CancellationToken ct) =>
        Task.FromResult<(byte[] Bytes, string ContentType)?>((PlaceholderJpegBytes, "image/jpeg"));

    public Task<(byte[] Bytes, string ContentType)?> GetThumbnailAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<(byte[] Bytes, string ContentType)?>((PlaceholderJpegBytes, "image/jpeg"));

    public Task<(Stream FitsStream, string FileName)?> OpenDownloadAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<(Stream FitsStream, string FileName)?>(null);

    public Task<OperationAcceptedDto> BulkRateAsync(BulkRateRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        throw new NotImplementedException("BulkRate lands with the §28 frame catalog DB in Phase 13.x");

    public Task<OperationAcceptedDto> BulkTagAsync(BulkTagRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        throw new NotImplementedException("BulkTag lands with the §28 frame catalog DB in Phase 13.x");

    public Task<OperationAcceptedDto> BulkDeleteAsync(BulkDeleteRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        throw new NotImplementedException("BulkDelete lands with the §28 frame catalog DB in Phase 13.x");
}
