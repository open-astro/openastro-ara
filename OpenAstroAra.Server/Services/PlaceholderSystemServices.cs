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