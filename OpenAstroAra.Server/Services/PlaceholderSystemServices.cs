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
using System.Text.Json;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Phase 13.10 — placeholder <see cref="IProfileShareService"/>. Export
/// returns a synthetic share record pointing at a non-existent download
/// URL; import preview returns a synthetic token that import-commit
/// resolves to a fresh GUID. Real §70 share-protocol manifest validation +
/// section-by-section merge land alongside multi-profile support.
/// </summary>
public sealed class PlaceholderProfileShareService : IProfileShareService {
    private static readonly JsonDocument _emptyManifest = JsonDocument.Parse("{}");

    public Task<ProfileShareDto> ExportAsync(Guid profileId, CancellationToken ct) =>
        Task.FromResult(new ProfileShareDto(
            ProfileId: profileId,
            ProfileName: "Sample profile",
            Manifest: _emptyManifest.RootElement.Clone(),
            PayloadBytes: 4_096,
            DownloadUrl: new Uri($"/api/v1/profiles/share/{profileId}/payload", UriKind.Relative)));

    public Task<ProfileShareImportPreviewDto> ImportPreviewAsync(JsonElement manifest, CancellationToken ct) =>
        Task.FromResult(new ProfileShareImportPreviewDto(
            ImportToken: Guid.NewGuid(),
            ProfileName: "Imported profile (placeholder)",
            Warnings: Array.Empty<string>(),
            DroppedFields: Array.Empty<string>(),
            ExpiresUtc: DateTimeOffset.UtcNow.AddMinutes(15)));

    public Task<Guid> ImportCommitAsync(Guid importToken, CancellationToken ct) =>
        // Real impl resolves the import-token → manifest, applies it,
        // returns the new profile's id. Placeholder mints one.
        Task.FromResult(Guid.NewGuid());
}

/// <summary>
/// Phase 13.10 — placeholder <see cref="IBackupStreamService"/>. Subscribe
/// returns a synthetic subscription id with a per-subscription ack topic;
/// Claim returns null (no frames in the queue). Real §44 fan-out lands
/// alongside the §28 frame catalog + the WS broadcaster.
/// </summary>
public sealed class PlaceholderBackupStreamService : IBackupStreamService {
    public Task<BackupSubscriptionDto> SubscribeAsync(CancellationToken ct) {
        var subId = Guid.NewGuid();
        return Task.FromResult(new BackupSubscriptionDto(
            SubscriptionId: subId,
            CreatedUtc: DateTimeOffset.UtcNow,
            AckTopic: $"backup.stream.{subId}.ack"));
    }

    public Task<BackupFrameDto?> ClaimAsync(BackupClaimRequestDto request, CancellationToken ct) =>
        // No frames available — real fan-out streams from the §28 frame
        // catalog as new frames complete capture.
        Task.FromResult<BackupFrameDto?>(null);
}