#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;

namespace OpenAstroAra.Server.Contracts;

/// <summary>
/// §42.5 — one persisted fault-history row. <c>EquipmentType</c> and
/// <c>FaultType</c> carry the same lowercase wire tokens as the
/// <c>equipment.fault</c> WS event (<c>camera</c>…, <c>disconnected</c>/
/// <c>tracking_lost</c>/<c>stall_timeout</c>/<c>value_mismatch</c>/
/// <c>op_error</c>/<c>cooling_drift</c>), so a client can correlate live
/// events with history without a second vocabulary.
/// </summary>
/// <param name="SessionId">The §40 catalog session active when the fault was
/// detected, or null when no sequence run was in flight.</param>
/// <param name="ActionTaken">The §42.3 reaction outcome for the fault
/// (<c>notify_only</c>, <c>sequence_paused</c>, <c>reconnecting</c>,
/// <c>recovered</c>, <c>gave_up:&lt;terminal&gt;</c>); null while an episode
/// is still deciding, or when no reaction applies.</param>
/// <param name="ResolvedUtc">When the fault recovered; null if unresolved
/// (or resolution isn't tracked for the kind, e.g. one-shot advisories).</param>
/// <param name="AffectedFrames">Frame ids captured inside the fault window
/// (§42.6 image-library marking). Recorded as empty until the §42.6 frame
/// correlation slice lands.</param>
public sealed record FaultDto(
    Guid Id,
    Guid? SessionId,
    DateTimeOffset DetectedUtc,
    string EquipmentType,
    string? EquipmentId,
    string? EquipmentName,
    string FaultType,
    string? Details,
    string? ActionTaken,
    DateTimeOffset? ResolvedUtc,
    IReadOnlyList<Guid> AffectedFrames);
