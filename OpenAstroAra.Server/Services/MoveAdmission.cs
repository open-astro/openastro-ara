#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenAstroAra.Server.Services;

/// <summary>What an asker wants the mount to do (#2139).</summary>
public enum MoveKind { Slew, Park, Home }

/// <summary>A pre-move rule (#2139, record 0026). <see cref="MoveAdmission.RulesFor"/> says which ones bind an asker.</summary>
[Flags]
public enum MoveRule {
    None = 0,
    /// <summary>The target's RA is in [0, 24) and its Dec in [-90, 90].</summary>
    Range = 1,
    /// <summary>The mount is not parked.</summary>
    Parked = 2,
    /// <summary>The mount's clock is verified (#2071; a clock push that is running is waited for).</summary>
    Clock = 4,
    /// <summary>The mount's position is trusted (#2070).</summary>
    Pose = 8,
    /// <summary>No routine holds the mount's motion (#2066).</summary>
    MotionOwner = 16,
    /// <summary>Polar Align is not turning the mount, and its stop is confirmed (#2066).</summary>
    PolarAlignMoving = 32,
    /// <summary>No sequence run is active (not finished, not aborting); <c>force</c> skips it (<c>run_in_progress</c>).</summary>
    RunActive = 64,
    /// <summary>The target is above the profile's lowest altitude (#2044).</summary>
    Horizon = 128,
    /// <summary>The target does not put the counterweight up (#2073).</summary>
    Counterweight = 256,
}

/// <summary>A person's GoTo refused while a sequence run is active (#2139: the <c>run_in_progress</c> rule, as before).
/// The REST endpoint answers it as the 409 Problem <c>run_in_progress</c>.</summary>
public sealed class RunInProgressException : InvalidOperationException {
    public RunInProgressException() { }
    public RunInProgressException(string message) : base(message) { }
    public RunInProgressException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Move Admission (#2139, record 0026): the rig's one answer to "may this asker move the mount there?". Every slew to a
/// target, park and home in the Telescope service asks <see cref="RulesFor"/> which rules bind its asker before anything
/// reaches the mount; the stateless rules' bodies live here, and the ones that read the mount (clock, position, motion
/// owner) stay in the service and run only where the table says.
/// </summary>
public static class MoveAdmission {

    /// <summary>
    /// The one table: the rules each asker's move is checked for. This slice reproduces what each asker was checked for
    /// before the module (record 0026); #2140 changes the policy here, and only here. An asker with no row for a kind
    /// cannot ask for it (a programming error, not a refusal).
    /// </summary>
    public static MoveRule RulesFor(MoveAsker asker, MoveKind kind) => (asker, kind) switch {
        (MoveAsker.Person, MoveKind.Slew) => MoveRule.Range | MoveRule.Clock | MoveRule.Pose | MoveRule.MotionOwner
            | MoveRule.PolarAlignMoving | MoveRule.RunActive | MoveRule.Horizon | MoveRule.Counterweight,
        (MoveAsker.Person, MoveKind.Park or MoveKind.Home) => MoveRule.Pose | MoveRule.MotionOwner | MoveRule.PolarAlignMoving,
        (MoveAsker.Session or MoveAsker.Center or MoveAsker.MeridianFlip, MoveKind.Slew) =>
            MoveRule.Range | MoveRule.Parked | MoveRule.Clock | MoveRule.Pose | MoveRule.MotionOwner,
        (MoveAsker.Session or MoveAsker.Shutdown or MoveAsker.MeridianFlip, MoveKind.Park or MoveKind.Home) => MoveRule.Pose | MoveRule.MotionOwner,
        (MoveAsker.SafetyPark, MoveKind.Park) => MoveRule.Pose,
        (MoveAsker.TargetRecenter or MoveAsker.ResumeRefinement, MoveKind.Slew) => MoveRule.Horizon,
        _ => throw new ArgumentOutOfRangeException(nameof(asker), $"{asker} has no Move Admission row for a {kind}."),
    };

    /// <summary>Whether <paramref name="rule"/> binds <paramref name="asker"/>'s <paramref name="kind"/>.</summary>
    public static bool Binds(MoveAsker asker, MoveKind kind, MoveRule rule) => (RulesFor(asker, kind) & rule) != 0;

    /// <summary>The <c>run_in_progress</c> text for a person's slew while runs are active, or null when it may go ahead:
    /// no run, a sync (the mount does not move), or the person forced it.</summary>
    internal static string? RunActiveRefusal(SlewRequestDto request, int activeRuns) {
        ArgumentNullException.ThrowIfNull(request);
        if (activeRuns == 0 || request.Sync == true || request.Force == true) {
            return null;
        }
        return activeRuns == 1
            ? "a sequence run is in progress: slewing now stops guiding and pauses the run awaiting you. Repeat with force to slew anyway."
            : $"{activeRuns} sequence runs are in progress: slewing now stops guiding and pauses them awaiting you. Repeat with force to slew anyway.";
    }

    /// <summary>The <c>below-horizon-limit</c> Refusal (#2044) when the target is under the profile's lowest altitude.</summary>
    internal static void ThrowIfBelowHorizon(IProfileStore? profiles, Coordinates target, ILogger? logger) {
        if (HorizonGuard.TargetBelowHorizon(profiles, target, logger) is { } low) {
            throw new RefusalException(RefusalCatalog.BelowHorizonLimit,
                string.Create(CultureInfo.InvariantCulture, $"The target is at {low.AltitudeDeg:F1}°, below the {low.LimitDeg:F1}° horizon limit."),
                new Dictionary<string, object?> { ["altitude_deg"] = low.AltitudeDeg, ["limit_deg"] = low.LimitDeg });
        }
    }

    /// <summary>The <c>counterweight-limit</c> Refusal (#2073) for a target below the pole near the lower meridian on a
    /// German equatorial mount (or one whose type is unknown).</summary>
    internal static void ThrowIfPastCounterweight(IProfileStore? profiles, Coordinates target, ASCOM.Common.DeviceInterfaces.AlignmentMode? alignmentMode, ILogger? logger) {
        if (CounterweightGuard.TargetPastCounterweightLimit(profiles, target, DateTimeOffset.UtcNow, alignmentMode, logger) is { } cw) {
            throw new RefusalException(RefusalCatalog.CounterweightLimit,
                string.Create(CultureInfo.InvariantCulture, $"The target is below the pole at hour angle {cw.HourAngleHours:F1} h: the counterweight would be {cw.CounterweightAngleDeg:F0}° from down, past the {cw.LimitDeg:F0}° limit."),
                new Dictionary<string, object?> { ["hour_angle_hours"] = cw.HourAngleHours, ["counterweight_angle_deg"] = cw.CounterweightAngleDeg, ["limit_deg"] = cw.LimitDeg });
        }
    }

    /// <summary>#2066 — the <c>polar-align-moving-mount</c> Refusal while Polar Align turns the mount or its stop is not
    /// confirmed yet; with <paramref name="mount"/>, also while a routine holds the mount's motion owner. A pad stop and
    /// Stop Mount never come here.</summary>
    internal static void ThrowIfPolarAlignMovesTheMount(IPolarAlignService? polarAlign, string what, IMountMotionControl? mount = null) {
        if (polarAlign is { IsMovingMount: true }) {
            throw new RefusalException(RefusalCatalog.PolarAlignMovingMount,
                $"Polar alignment is turning the mount, so the {what} was not sent. Stop polar alignment first (or tap Stop Mount, which ends it).");
        }
        if (mount?.MotionOwner is { } owner) {
            throw TelescopeService.MotionOwnedRefusal(owner, what);
        }
    }

    /// <summary>The up-front question of an asker that skips its work rather than be refused mid-way (target recenter,
    /// resume refinement): the target's altitude and the limit when the horizon rule binds it and the target is under the
    /// floor, else null.</summary>
    public static (double AltitudeDeg, double LimitDeg)? TargetTooLow(MoveAsker asker, IProfileStore? profiles, Coordinates target, ILogger? logger) =>
        Binds(asker, MoveKind.Slew, MoveRule.Horizon) ? HorizonGuard.TargetBelowHorizon(profiles, target, logger) : null;
}
