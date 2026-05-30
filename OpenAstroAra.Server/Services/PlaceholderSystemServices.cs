#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Text.Json;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Phase 13.10 — placeholder <see cref="IDataManagerService"/>. Surfaces
/// a small fixture catalog of §36.2 packages (sky catalog, dark library,
/// horizon profile) so the WILMA Data Manager modal renders sensible
/// entries. Mutating endpoints accept + return <see cref="OperationAcceptedDto"/>
/// (real download/install happens in Phase 14 + the §28 frame catalog DB).
/// </summary>
public sealed class PlaceholderDataManagerService : IDataManagerService {
    private static readonly DataPackageDto[] SamplePackages = new[] {
        new DataPackageDto(
            Id: "tycho-2",
            Name: "Tycho-2 star catalog",
            Description: "2.5M stars to mag 11 — required for plate solving + §36.13 Sky Atlas.",
            Category: "catalog",
            SizeBytes: 187_654_321,
            Version: "v2024.10",
            IsInstalled: true,
            InstalledUtc: new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            SourceUrl: "https://data.openastro.net/tycho-2/2024.10.tar.gz"),
        new DataPackageDto(
            Id: "horizon-default",
            Name: "Default 20° horizon profile",
            Description: "Flat 20° altitude horizon — sensible default; replace with site-specific in §37.12.",
            Category: "horizon",
            SizeBytes: 4_096,
            Version: "v1",
            IsInstalled: true,
            InstalledUtc: new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            SourceUrl: null),
        new DataPackageDto(
            Id: "gaia-edr3-bright",
            Name: "Gaia EDR3 (mag ≤ 13)",
            Description: "Plate-solve reference frame for deeper exposures; optional.",
            Category: "catalog",
            SizeBytes: 4_294_967_296,
            Version: "v2022",
            IsInstalled: false,
            InstalledUtc: null,
            SourceUrl: "https://data.openastro.net/gaia-edr3-bright/2022.tar.gz"),
    };

    public Task<IReadOnlyList<DataPackageDto>> ListPackagesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DataPackageDto>>(SamplePackages);

    public Task<OperationAcceptedDto> DownloadAsync(DownloadRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(new OperationAcceptedDto(
            OperationId: Guid.NewGuid(),
            OperationType: "data-manager.download",
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey));

    public Task<OperationAcceptedDto> CancelAsync(Guid downloadId, CancellationToken ct) =>
        Task.FromResult(new OperationAcceptedDto(
            OperationId: Guid.NewGuid(),
            OperationType: "data-manager.cancel",
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: null));

    public Task<bool> DeleteAsync(string packageId, CancellationToken ct) =>
        // Placeholder always reports "deleted" — real impl checks
        // the package exists + frees disk space.
        Task.FromResult(SamplePackages.Any(p => p.Id == packageId));

    public Task<DataManagerStateDto> GetStateAsync(CancellationToken ct) =>
        Task.FromResult(new DataManagerStateDto(
            InstalledPackageCount: SamplePackages.Count(p => p.IsInstalled),
            TotalInstalledBytes: SamplePackages.Where(p => p.IsInstalled).Sum(p => p.SizeBytes),
            ActiveDownloads: Array.Empty<DataManagerActiveDownloadDto>(),
            LastSyncUtc: new DateTimeOffset(2026, 5, 30, 0, 0, 0, TimeSpan.Zero)));
}

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
            DownloadUrl: $"/api/v1/profile/share/{profileId}/payload"));

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
