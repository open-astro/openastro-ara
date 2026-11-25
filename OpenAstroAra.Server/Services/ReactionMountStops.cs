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
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #2107 — the quiet mount stops the safety reaction (<see cref="SafetyReactionService"/>) and the equipment-fault
/// reaction (<see cref="FaultReactionService"/>) share, so their two parks stay in step: every routine that holds the
/// mount (#2142) and the Polar Aligner stop before the park, and a park refused for an unverified position stops the mount's motion and tracking
/// instead. Every step is best effort: a fault is handed to <c>rungFailed</c> (the caller's log line) and never thrown.
/// </summary>
internal static class ReactionMountStops {

    /// <summary>#2066, #2142 — the park's first step: every routine that holds the mount ends through its own stop in
    /// <see cref="RigOccupancy"/> (polar alignment leaves the mount to the park, with no stop confirmation of its own that
    /// would hold the park back; a Center is cancelled), then the Polar Aligner stops. Each step is bounded by
    /// <paramref name="bound"/>; a stop that does not return within the bound, or throws, is logged and the park goes ahead.</summary>
    /// <param name="takeoverBy">The park's name for the routines' logs (<see cref="MountStopReasons.SafetyPark"/> or
    /// <see cref="MountStopReasons.FaultPark"/>).</param>
    public static async Task StopMountHoldersBeforeParkAsync(RigOccupancy? occupancy, IPolarAlignerService? aligner,
            string takeoverBy, TimeSpan bound, Action<string, Exception> rungFailed) {
        if (occupancy is not null) {
            foreach (var stop in await occupancy.StopAsync(RigHolding.Mount, takeoverBy, bound).ConfigureAwait(false)) {
                if (!stop.Stopped) {
                    rungFailed(stop.FailedRung, stop.Fault!);
                }
            }
        }
        if (aligner is { IsPresent: true }) {
            await BoundedQuietlyAsync("stop_polar_aligner", () => aligner.StopAsync(null, CancellationToken.None), bound, rungFailed)
                .ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort step before the park: an aligner that faults or hangs must not block the park; logged, never rethrown.")]
    private static async Task BoundedQuietlyAsync(string rung, Func<Task> step, TimeSpan bound, Action<string, Exception> rungFailed) {
        try {
            await step().WaitAsync(bound).ConfigureAwait(false);
        } catch (Exception ex) {
            rungFailed(rung, ex);
        }
    }

    /// <summary>#2070 — the park was refused because the mount's position is unverified: stop its motion (AbortSlew, which
    /// also ends any axis lease, a pad move the refused park no longer cancels) and its tracking. The abort is counted under
    /// <paramref name="reason"/> (#2107), so a polar alignment it ends says which reaction stopped it, not "Stop Mount".</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort rung: a motion or tracking stop that fails is logged and named in the caller's alert, never thrown.")]
    public static async Task<(bool MotionStopped, bool TrackingStopped)> StopMotionAndTrackingAsync(ITelescopeService? telescope, string reason,
            Action<string, Exception> rungFailed) {
        var motionStopped = false;
        var trackingStopped = false;
        if (telescope is null) {
            return (motionStopped, trackingStopped);
        }
        try {
            await telescope.AbortSlewAsync(reason, CancellationToken.None).ConfigureAwait(false);
            motionStopped = true;
        } catch (Exception ex) {
            rungFailed("abort_slew", ex);
        }
        try {
            await telescope.SetTrackingAsync(false, CancellationToken.None).ConfigureAwait(false);
            trackingStopped = true;
        } catch (Exception ex) {
            rungFailed("tracking_off", ex);
        }
        return (motionStopped, trackingStopped);
    }
}
