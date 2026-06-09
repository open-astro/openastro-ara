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
/// Phase 13.9 — placeholder <see cref="IBugReportService"/> for the §54
/// "Send me a bug report" UI flow. <see cref="PrepareAsync"/> returns
/// an immediate "ready" preparation record with a synthetic id;
/// <see cref="OpenDownloadAsync"/> returns null since there's no real
/// bundle on disk yet. Real impl collects journal logs + profile.json +
/// recent CHANGELOG + system info into a ZIP per §54.3 when that lands.
/// </summary>
public sealed class PlaceholderBugReportService : IBugReportService {
    public Task<BugReportPreparationDto> PrepareAsync(string? idempotencyKey, CancellationToken ct) =>
        // Synthetic prep — 256 KB estimate so the UI's "approx size"
        // hint has something plausible to render. Status "ready" so
        // WILMA's Bug Report dialog doesn't poll; download endpoint
        // returns 404 to signal the bundle isn't actually available
        // yet (real impl ships in Phase 14 §54.3).
        Task.FromResult(new BugReportPreparationDto(
            PreparationId: Guid.NewGuid(),
            Status: "ready",
            EstimatedSizeBytes: 256 * 1024,
            DownloadUrl: null,
            CompletedUtc: DateTimeOffset.UtcNow));

    public Task<(Stream Stream, string FileName)?> OpenDownloadAsync(Guid preparationId, CancellationToken ct) =>
        // Real impl streams the prepared ZIP. 404 here matches the
        // "feature gated on infra" pattern used by stats CSV (13.6) +
        // log download (13.8).
        Task.FromResult<(Stream Stream, string FileName)?>(null);
}