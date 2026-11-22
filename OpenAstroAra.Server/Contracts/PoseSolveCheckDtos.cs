#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections.Generic;

namespace OpenAstroAra.Server.Contracts;

/// <summary>#2173 — <c>GET /equipment/telescope/verify-position-by-solve</c>: whether the plate-solve check can run now.
/// <c>Available</c> is true only while the mount's position is unverified, a camera and ASTAP are set up and the camera is
/// idle. <c>Camera</c> is the one it would use (<c>main</c>, or <c>guide</c> only when there is no main camera), null when
/// none. <c>UnavailableReason</c> is null when available, else one of <c>not_needed</c>, <c>mount_not_connected</c>,
/// <c>no_camera</c>, <c>camera_busy</c>, <c>solver_not_set</c>, <c>optics_not_set</c>.</summary>
public sealed record PoseSolveAvailabilityDto(bool Available, string? Camera, string? UnavailableReason);

/// <summary>#2173 — <c>POST /equipment/telescope/verify-position-by-solve/sync</c> body: the <c>check_id</c> of the
/// mismatch result whose Sync the user tapped. A missing, stale or other id is refused (<c>pose-sync-not-offered</c>).</summary>
public sealed record PoseSolveSyncRequestDto(string? CheckId);

/// <summary>#2173 — one plate-solve check of an unverified mount position (it sends no motion).
/// <para><c>Outcome</c>: <c>trusted</c> (the flag cleared), <c>mismatch</c> (the sky is elsewhere; <c>CheckId</c> offers
/// one Sync), <c>pier_side_disagrees</c> (no trust, no Sync: Home is the way out), <c>pier_side_unknown</c> (the pier
/// side could not be checked: no trust), <c>solve_failed</c> (no stars, clouds, a failed capture), <c>mount_moved</c>
/// (the mount moved, was commanded, reconnected or did not answer during the check), <c>sync_refused</c> (the mount or a guard refused the Sync), or
/// <c>sync_not_taken</c> (after the Sync the mount's reading or a second solve still disagrees).</para>
/// <para>Positions are in the mount's own frame (<c>Frame</c>, <c>J2000</c> or <c>JNOW</c>), the solve put in it; a Dec
/// past ±90° is folded over the pole first. <c>SeparationDegrees</c> is the angle between them, <c>ToleranceDegrees</c>
/// max(0.5°, 2 × the solve's uncertainty). <c>SideOfPier</c> is what the mount reports (<c>east</c>, <c>west</c>,
/// <c>unknown</c>), <c>ExpectedSideOfPier</c> the side the solved hour angle gives (null when it gives none: near the
/// pole, or no site). <c>PositionAngleCheck</c>: <c>agrees</c>, <c>flipped</c> (about 180° from the last trusted solve's
/// position angle for this camera and rotator angle: the other pier side), <c>turned</c> (the camera was turned: no
/// answer), <c>none_stored</c>. <c>ClearedReason</c> is the reason the trust cleared. <c>Detail</c> is an English
/// sentence for the log.</para></summary>
public sealed record PoseSolveCheckResultDto(
    string Outcome,
    string Camera,
    string? Frame,
    double? MountRaHours,
    double? MountDecDegrees,
    double? SolvedRaHours,
    double? SolvedDecDegrees,
    double? SeparationDegrees,
    double ToleranceDegrees,
    string SideOfPier,
    string? ExpectedSideOfPier,
    double? PositionAngleDegrees,
    string PositionAngleCheck,
    string? ClearedReason,
    string? CheckId,
    string Detail);

/// <summary>#2173 — the last trusted plate solve's position angle, per mount, camera and rotator angle
/// (<c>pose-solve-pa.json</c>). <c>SideOfPier</c> is the side the mount was trusted on when it was taken.</summary>
public sealed record PoseSolvePaEntry(string Key, double PositionAngleDeg, string SideOfPier);

public sealed record PoseSolvePaFile(IReadOnlyList<PoseSolvePaEntry> Entries);
