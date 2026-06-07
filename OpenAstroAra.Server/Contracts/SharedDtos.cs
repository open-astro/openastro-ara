#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// Cross-cutting DTOs used by multiple endpoint groups. Created in Phase 7
// (sequence + calibration + mosaic scaffold) and reused from Phase 8 onward.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Cursor-paginated response wrapper per §60.2. <c>NextCursor</c> is null when there are no more pages.
/// </summary>
public sealed record CursorPage<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    bool HasMore);

/// <summary>
/// Long-running operation acceptance envelope per §60.5. Server returns 202 + this
/// payload; client subscribes to <c>operation.{op_id}</c> events on the WebSocket
/// channel for progress + terminal status.
/// </summary>
public sealed record OperationAcceptedDto(
    Guid OperationId,
    string OperationType,
    DateTimeOffset AcceptedUtc,
    string? IdempotencyKey);

/// <summary>
/// §65.5 batch-job state. Returned by GET /api/v1/jobs/{id}.
/// </summary>
public sealed record BatchJobDto(
    Guid JobId,
    string JobType,
    string State,
    int Done,
    int Total,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    string? ErrorMessage);