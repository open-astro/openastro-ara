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
/// Phase 13.11 — placeholder <see cref="IBackupService"/> for §43 ZIP
/// backups. Distinct from <see cref="IBackupStreamService"/> (§44, wired
/// in Phase 13.10) which fans out individual frames live.
///
/// <see cref="CreateZipAsync"/> + <see cref="RestoreZipAsync"/> accept
/// requests and return <see cref="OperationAcceptedDto"/>; real impl
/// runs an async worker that produces / consumes a ZIP rolling through
/// the §43 area selectors (sequences / profiles / frame-metadata /
/// logs). Snapshot list returns one fixture record so the §43 "Past
/// backups" UI has something to render. Clone-status returns a small
/// JSON blob the WILMA Settings → Backup view polls during a long-
/// running restore.
/// </summary>
public sealed class PlaceholderBackupService : IBackupService {
    private static readonly JsonDocument _idleStatus = JsonDocument.Parse(
        "{\"state\":\"idle\",\"progress_pct\":null,\"current_area\":null,\"message\":null}");

    private static readonly BackupZipDto SampleSnapshot = new(
        BackupId: Guid.Parse("77777777-7777-7777-7777-777777777771"),
        CreatedUtc: new DateTimeOffset(2026, 5, 30, 0, 0, 0, TimeSpan.Zero),
        SizeBytes: 12_345_678,
        Sha256: "placeholder-sha256-7777777777777777777777777777777777777777777777777777777777777777",
        DownloadUrl: new Uri("/api/v1/backup/snapshot/77777777-7777-7777-7777-777777777771/download", UriKind.Relative),
        IncludedAreas: new[] { "profiles", "sequences" });

    public Task<OperationAcceptedDto> CreateZipAsync(string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(new OperationAcceptedDto(
            OperationId: Guid.NewGuid(),
            OperationType: "backup.create-zip",
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey));

    public Task<OperationAcceptedDto> RestoreZipAsync(RestoreRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(new OperationAcceptedDto(
            OperationId: Guid.NewGuid(),
            OperationType: "backup.restore-zip",
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey));

    public Task<IReadOnlyList<BackupZipDto>> ListSnapshotsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BackupZipDto>>(new[] { SampleSnapshot });

    public Task<JsonElement> GetCloneStatusAsync(CancellationToken ct) =>
        // Idle status — no restore in progress. Real impl tracks the
        // §43.4 restore worker's state machine.
        Task.FromResult(_idleStatus.RootElement.Clone());
}