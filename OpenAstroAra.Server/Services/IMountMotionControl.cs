#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Astrometry;
using PierSide = OpenAstroAra.Core.Enums.PierSide;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #2066 (ARA-5) — the narrow seam a routine that drives the mount's motors itself (Polar Align) moves it through: the
/// stop count it obeys, the mount session it started in, the tagged abort, the leased motor move, the pose-trust check and
/// a fresh, timestamped reading. <see cref="ITelescopeService"/> extends it, so the daemon's telescope service is the one
/// implementation; a routine takes the seam and never casts its mount to the service. The single motion owner
/// (<c>AcquireMotion</c>, #2048 ARA-2) joins it with the <c>mount_busy</c> change.
/// </summary>
public interface IMountMotionControl {

    /// <summary>Counts every slew abort (Stop Mount, Emergency Stop, the watchdog), and every park and Find Home (#2066). A
    /// routine that drives the motors itself reads it when its run starts, and treats any change it did not raise as a stop
    /// it must obey.</summary>
    long AbortEpoch => 0;

    /// <summary>#2089 — counts every mount session the daemon starts or ends: a connect, a disconnect, a fault trip. A
    /// routine that keeps a pose across runs (Polar Align's sweep origin) reads it and forgets the pose once it changes,
    /// and a run that sees it change mid-run ends (#2066 ARA-5): a reconnected mount may have been moved, re-homed or
    /// swapped meanwhile.</summary>
    long SessionEpoch => 0;

    /// <summary>#2066 — what raised the first count after <paramref name="abortEpoch"/> other than
    /// <paramref name="except"/>: <c>user_request</c> (Stop Mount), <c>emergency_stop</c>, <c>watchdog</c>, <c>park</c>,
    /// <c>findhome</c>, <c>polar_align</c>. Null when there is none, or it is no longer known.</summary>
    string? AbortReasonSince(long abortEpoch, string? except) => null;

    /// <summary>The abort, with the caller named in the abort's log line and its <c>slew_aborted</c> event
    /// (<c>user_request</c> for Stop Mount; <c>polar_align</c> for the routine's own stop). The abort reaches the driver even
    /// when <paramref name="ct"/> is already cancelled.</summary>
    Task AbortSlewAsync(string reason, CancellationToken ct);

    /// <summary>Start (rate != 0) or stop (rate 0) constant-rate motion on one mount axis (0 = primary/RA, 1 = secondary/Dec),
    /// on a lease: a non-zero <paramref name="rate"/> with <paramref name="leaseMs"/> stops by itself when no renewal comes
    /// within the lease. Refused (InvalidOperationException, nothing reaches the driver) when a non-zero rate would start
    /// the motor after an abort that came since <paramref name="unlessAbortedSince"/> (an <see cref="AbortEpoch"/>
    /// reading). A stop always reaches the driver, whatever <paramref name="ct"/> says.</summary>
    Task MoveAxisAsync(int axis, double rate, int? leaseMs, long? unlessAbortedSince, CancellationToken ct);

    /// <summary>#2070 — null when the mount's reported position may be trusted for a GoTo, Park, Home or polar alignment;
    /// otherwise why not (a <c>position_unverified_reason</c> token, or <c>not_read_yet</c> before the session's first
    /// position read).</summary>
    Task<string?> PositionUnverifiedReasonAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>#2066 (ARA-5) — the mount's state read from the driver now, never the daemon's 2 s refresh cache: RA, Dec
    /// (raw, never folded over the pole), Slewing and pier side, stamped with when the read began and the mount session it
    /// came from. Null when not connected or the position read fails; a failed Slewing read is a null
    /// <see cref="MountReading.Slewing"/>, a failed pier-side read <c>pierUnknown</c>.</summary>
    Task<MountReading?> ReadNowAsync(CancellationToken ct) => Task.FromResult<MountReading?>(null);
}

/// <summary>One fresh reading of the mount (#2066 ARA-5): RA (hours) and Dec (degrees) as the driver reports them in its
/// own <paramref name="Frame"/> (Dec past ±90° when the Dec axis is over the pole), its Slewing (null when that read
/// failed), its reported pier side, when the read began (<paramref name="ReadAtUtc"/>; a read is never younger than
/// that), and the mount session (<see cref="IMountMotionControl.SessionEpoch"/>) it was read in.</summary>
public sealed record MountReading(double RaHours, double DecDegrees, bool? Slewing, PierSide SideOfPier,
    DateTimeOffset ReadAtUtc, long SessionEpoch, Epoch Frame = Epoch.JNOW);
