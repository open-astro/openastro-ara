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
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §42.5 fault log — every detected equipment fault persisted to the session
/// database (<c>faults</c> table) with the §42.3 reaction outcome stamped onto
/// the same row as the episode progresses. Write-side callers (the fault hub,
/// the reaction service) treat both record methods as best-effort: a store
/// fault must never propagate back into a device path or a reaction episode.
/// </summary>
public interface IFaultLogService {

    /// <summary>Persist a newly detected fault. Idempotent per fault event —
    /// if the row already exists (a reaction action landed first, see
    /// <see cref="RecordActionAsync"/>), the detection insert is a no-op.</summary>
    Task RecordFaultAsync(EquipmentFaultEvent fault, CancellationToken ct);

    /// <summary>Stamp the §42.3 reaction outcome onto <paramref name="fault"/>'s row
    /// (last write wins: <c>reconnecting</c> → <c>recovered</c>/<c>gave_up:*</c>).
    /// Upserts — detection persists asynchronously, so an action may arrive before
    /// the insert; the row is then created with the action already set.</summary>
    /// <param name="resolvedUtc">When the action resolves the fault (the reaction
    /// service passes the recovery time on <c>recovered</c>); null leaves the row
    /// unresolved.</param>
    Task RecordActionAsync(EquipmentFaultEvent fault, string action, DateTimeOffset? resolvedUtc, CancellationToken ct);

    /// <summary>Stamp <c>resolved_at</c> on the device type's unresolved
    /// <c>disconnected</c> rows — the device reported connected again, so a
    /// standing disconnect fault is over regardless of how it healed (the
    /// §42.3 ladder stamps `recovered` itself; this covers the gave-up-then-
    /// manually-fixed path, where no `recovered` ever fires and the row would
    /// otherwise read unresolved forever). Other kinds are untouched:
    /// a connect doesn't prove tracking, and advisories are one-shots.
    /// Returns the number of rows resolved.</summary>
    Task<int> ResolveOnReconnectAsync(DeviceType deviceType, DateTimeOffset resolvedUtc, CancellationToken ct);

    /// <summary>Stamp <c>resolved_at</c> on ONE fault's row (natural-keyed like
    /// <see cref="RecordActionAsync"/>) — the device behind that specific fault
    /// reported itself back (#1191: the guide camera behind a camera-drop row,
    /// via the guider's EquipmentReconnected event, while the guider link never
    /// went down). <c>action_taken</c> is untouched, and an already-resolved row
    /// keeps its earlier stamp. Upserts like the action path: the detection insert
    /// is fire-and-forget, so a reconnect that lands first creates the row
    /// resolved and the late insert no-ops. Returns true when a row was stamped.</summary>
    Task<bool> ResolveAsync(EquipmentFaultEvent fault, DateTimeOffset resolvedUtc, CancellationToken ct);

    /// <summary>Fault history, newest first. All filters are optional and AND-combined;
    /// <paramref name="equipmentType"/> and <paramref name="faultType"/> take the
    /// lowercase wire tokens (see <see cref="FaultDto"/>).</summary>
    Task<CursorPage<FaultDto>> ListAsync(
        int limit, string? cursor, string? equipmentType, Guid? sessionId,
        bool? unresolvedOnly, string? faultType, CancellationToken ct);

    /// <summary>One fault by id, or null when unknown.</summary>
    Task<FaultDto?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>§42.5 retention (#1145): delete every fault row detected before
    /// <paramref name="cutoffUtc"/>, resolved or not. Returns the number of rows removed.
    /// The §42.6 <c>affected_frames</c> list goes with the row; frames themselves are untouched.</summary>
    Task<int> PruneBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken ct);
}
