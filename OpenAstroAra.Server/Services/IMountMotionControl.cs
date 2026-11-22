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
/// a fresh, timestamped reading, and the single motion owner (<see cref="AcquireMotion"/>, #2048 ARA-2).
/// <see cref="ITelescopeService"/> extends it, so the daemon's telescope service is the one implementation; a routine takes
/// the seam and never casts its mount to the service.
/// </summary>
public interface IMountMotionControl {

    /// <summary>Counts every slew abort (Stop Mount, Emergency Stop, the watchdog), and every park and Find Home (#2066). A
    /// routine that drives the motors itself reads it when its run starts, and treats any change it did not raise as a stop
    /// it must obey.</summary>
    long AbortEpoch => 0;

    /// <summary>#2073 — the connected mount's ASCOM <c>AlignmentMode</c>, read once per connection; null when
    /// not connected, not yet read, or the driver would not answer. The counterweight check runs only for a
    /// German equatorial mount, and treats null as one (fail-safe). #2083 — Polar Align refuses an alt-az mount.</summary>
    ASCOM.Common.DeviceInterfaces.AlignmentMode? MountAlignmentMode => null;

    /// <summary>#2083 — whether the mount can turn its RA axis by rate (ASCOM <c>CanMoveAxis(Primary)</c>), read once per
    /// connection; null when not connected, not read yet, or the driver would not answer. Polar Align's motor turn needs it.</summary>
    bool? CanMovePrimaryAxis => null;

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

    /// <summary>#2066 — <see cref="ReadNowAsync"/> with the position only: RA and Dec read from the driver now, stamped and
    /// tagged the same way, with <see cref="MountReading.Slewing"/> null and the pier side <c>pierUnknown</c> (not asked).
    /// For a routine that polls its own motor move several times a second: two driver round trips more per poll would
    /// stretch the poll, and the stop on the reading would land further past its target.</summary>
    Task<MountReading?> ReadPositionNowAsync(CancellationToken ct) => ReadNowAsync(ct);

    /// <summary>#2067 — <see cref="ReadNowAsync"/> with the position and the pier side, and no Slewing (null): what a
    /// routine checks on every poll of its own motor move (a pier side that changes mid-turn ends it). One driver round
    /// trip more than <see cref="ReadPositionNowAsync"/>; a failed or unsupported pier-side read is <c>pierUnknown</c>.</summary>
    Task<MountReading?> ReadPoseNowAsync(CancellationToken ct) => ReadNowAsync(ct);

    /// <summary>#2067, #2083 — the rate <see cref="MoveAxisAsync"/> runs <paramref name="axis"/> at for a routine that asks
    /// for <paramref name="requested"/> degrees per second: the fastest rate the mount offers at or below it, sign kept,
    /// never one above it (NINA's rule: a rate is never rounded up). Null when the mount offers no rate at or below it, or
    /// its rates are not known yet. A routine sends the matched rate and bounds its move by the time the move takes at it.</summary>
    double? MatchMoveAxisRate(int axis, double requested) => requested;

    /// <summary>#2066 (ARA-2) — the routine that holds the mount's single motion owner (<see cref="MotionOwners"/>), or
    /// null. While one holds it, a GoTo (the REST one, Center, the sequencer's and every other mediator GoTo), a sync, a
    /// Park and a Home (REST or mediator) are refused. A stop never is: Emergency Stop, Stop Mount, AbortSlew, a pad stop
    /// (MoveAxis rate 0) and tracking off always reach the mount; the safety, fault and shutdown parks end the routine
    /// through its takeover first.</summary>
    string? MotionOwner => null;

    /// <summary>#2066 (ARA-2) — take the motion owner for <paramref name="owner"/> (a <see cref="MotionOwners"/> name)
    /// until the returned hold is disposed. A new hold of the same owner replaces its old one (a new run of the routine),
    /// and disposing the old one then gives nothing back. Throws <see cref="InvalidOperationException"/> while another
    /// owner holds it.</summary>
    IDisposable AcquireMotion(string owner) => MotionHold.None;
}

/// <summary>#2066 (ARA-2) — the names a routine takes the motion owner (<see cref="IMountMotionControl.AcquireMotion"/>)
/// with.</summary>
public static class MotionOwners {

    /// <summary>The Polar Align routine: it holds the owner for its whole run, the adjust loop included, and gives it back
    /// once its exit has stopped the mount. The same token as its own stop's reason (<see cref="MountStopReasons.PolarAlign"/>).</summary>
    public const string PolarAlign = MountStopReasons.PolarAlign;
}

/// <summary>A hold of the motion owner that gives nothing back: a mount that keeps no owner.</summary>
internal sealed class MotionHold : IDisposable {

    public static readonly MotionHold None = new();

    public void Dispose() {
        // Nothing to give back.
    }
}

/// <summary>One fresh reading of the mount (#2066 ARA-5): RA (hours) and Dec (degrees) as the driver reports them in its
/// own <paramref name="Frame"/> (Dec past ±90° when the Dec axis is over the pole), its Slewing (null when that read
/// failed), its reported pier side, when the read began (<paramref name="ReadAtUtc"/>; a read is never younger than
/// that), and the mount session (<see cref="IMountMotionControl.SessionEpoch"/>) it was read in.</summary>
public sealed record MountReading(double RaHours, double DecDegrees, bool? Slewing, PierSide SideOfPier,
    DateTimeOffset ReadAtUtc, long SessionEpoch, Epoch Frame = Epoch.JNOW);
