#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test.PolarAlign {

    /// <summary>
    /// #2048 ARA-1 — the narrow seam Polar Align will move its mount calls behind, as planned there: the awaitable abort
    /// with a reason, the leased motor move #2063 uses, a fresh reading that carries its time, and the single motion
    /// owner with the inhibit latch (ARA-2). It does not exist in the daemon yet (#2066 adds it to
    /// <c>OpenAstroAra.Server</c> and makes <c>TelescopeService</c> implement it). It is declared here so the fake GEM
    /// mount (#2064) models every member now, and the guards' tests can be written against it before the seam lands.
    /// The members that already exist on <c>ITelescopeService</c> keep its exact signatures.
    /// </summary>
    internal interface IMountMotionControl {

        /// <summary>Counts every slew abort; a motion that sees it change must stop and stay stopped.</summary>
        long AbortEpoch { get; }

        /// <summary>The abort, tagged with its caller (<c>polar_align</c>, <c>user_request</c>). It reaches the driver
        /// even when <paramref name="ct"/> is already cancelled.</summary>
        Task AbortSlewAsync(string reason, CancellationToken ct);

        /// <summary>The leased motor move: a non-zero <paramref name="rate"/> (degrees per second) runs until a stop, or
        /// until <paramref name="leaseMs"/> passes without a renewal. Refused when an abort came since
        /// <paramref name="unlessAbortedSince"/>, or while the inhibit latch is set.</summary>
        Task MoveAxisAsync(int axis, double rate, int? leaseMs, long? unlessAbortedSince, CancellationToken ct);

        /// <summary>The mount's state read from the driver now, with the time the driver took it (a driver that caches
        /// answers an older time); null when not connected or the read fails.</summary>
        Task<MountReading?> ReadNowAsync(CancellationToken ct);

        /// <summary>Takes the single motion owner for <paramref name="owner"/> until the result is disposed. Throws
        /// <see cref="InvalidOperationException"/> while the inhibit latch is set or another owner holds it.</summary>
        IDisposable AcquireMotion(string owner);
    }

    /// <summary>One fresh reading of the mount: RA (hours) and Dec (degrees) in the mount's frame, its Slewing (null when
    /// that read failed), its reported pier side, when the driver took it, and the mount session it came from.</summary>
    internal sealed record MountReading(double RaHours, double DecDegrees, bool? Slewing, PierSide SideOfPier,
        DateTimeOffset ReadAtUtc, long SessionEpoch);
}
