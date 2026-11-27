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
using OpenAstroAra.Server.Contracts.Refusals;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
    /// <summary>#2140 — nothing holds the mount in Rig Occupancy (record 0025: a Session, polar alignment, Center; End
    /// Night); <c>force</c> skips only a Session, for a GoTo (<c>run_in_progress</c>; any other holder is <c>mount_held</c>).</summary>
    MountHeld = 64,
    /// <summary>A position limit: the target is above the profile's lowest altitude (#2044).</summary>
    Horizon = 128,
    /// <summary>A position limit: the target does not put the counterweight up (#2073).</summary>
    Counterweight = 256,
}

/// <summary>A person's move refused while something holds the mount (#2140: Rig Occupancy). The REST endpoints answer it
/// as a 409 Problem with <see cref="Title"/>: <c>run_in_progress</c> when only a Session holds the mount (a GoTo may repeat
/// forced), <c>mount_held</c> for any other holder (nothing forces past it). Its text names the holder.</summary>
public sealed class MountHeldException : InvalidOperationException {
    /// <summary>The 409 title for a Session: the client may ask the user and repeat the GoTo forced.</summary>
    public const string RunInProgress = "run_in_progress";
    /// <summary>The 409 title for a holder other than a Session: <c>force</c> does not skip it.</summary>
    public const string MountHeld = "mount_held";

    public MountHeldException() { }
    public MountHeldException(string message) : base(message) { }
    public MountHeldException(string message, Exception innerException) : base(message, innerException) { }
    public MountHeldException(string title, string message) : base(message) => Title = title;

    /// <summary>The 409 Problem title.</summary>
    public string Title { get; } = RunInProgress;
}

/// <summary>#2140 — a Find Home asked of a parked mount. The REST endpoint answers it as the 409 Problem
/// <c>mount_parked</c>; a park on a parked mount is allowed (it does nothing).</summary>
public sealed class MountParkedException : InvalidOperationException {
    public MountParkedException() { }
    public MountParkedException(string message) : base(message) { }
    public MountParkedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Move Admission (#2139, record 0026): the rig's one answer to "may this asker move the mount there?". Every slew to a
/// target, park and home in the Telescope service asks <see cref="RulesFor"/> which rules bind its asker before anything
/// reaches the mount; the stateless rules' bodies live here, and the ones that read the mount (clock, position, motion
/// owner) stay in the service and run only where the table says.
/// </summary>
public static class MoveAdmission {

    /// <summary>
    /// The one table: the rules each asker's move is checked for (record 0026). #2140: the position limits (horizon,
    /// counterweight) bind every asker's GoTo but the safety moves, which have no GoTo; a park and a home are the way out
    /// of a bad pose, so no position limit binds them, only the motion owner, a person's "the mount is held", the position
    /// (#2070) and, for a home, parked. An asker with no row for a kind cannot ask for it (a programming error, not a
    /// refusal).
    /// </summary>
    public static MoveRule RulesFor(MoveAsker asker, MoveKind kind) => (asker, kind) switch {
        (MoveAsker.Person, MoveKind.Slew) => MoveRule.Range | MoveRule.Clock | MoveRule.Pose | MoveRule.MotionOwner
            | MoveRule.PolarAlignMoving | MoveRule.MountHeld | MoveRule.Horizon | MoveRule.Counterweight,
        (MoveAsker.Person, MoveKind.Park) => MoveRule.Pose | MoveRule.MotionOwner | MoveRule.PolarAlignMoving | MoveRule.MountHeld,
        (MoveAsker.Person, MoveKind.Home) => MoveRule.Pose | MoveRule.MotionOwner | MoveRule.PolarAlignMoving | MoveRule.MountHeld | MoveRule.Parked,
        (MoveAsker.Session or MoveAsker.Center or MoveAsker.MeridianFlip, MoveKind.Slew) =>
            MoveRule.Range | MoveRule.Parked | MoveRule.Clock | MoveRule.Pose | MoveRule.MotionOwner | MoveRule.Horizon | MoveRule.Counterweight,
        (MoveAsker.Session or MoveAsker.Shutdown or MoveAsker.MeridianFlip, MoveKind.Park) => MoveRule.Pose | MoveRule.MotionOwner,
        (MoveAsker.Session or MoveAsker.Shutdown or MoveAsker.MeridianFlip, MoveKind.Home) => MoveRule.Pose | MoveRule.MotionOwner | MoveRule.Parked,
        (MoveAsker.SafetyPark, MoveKind.Park) => MoveRule.Pose,
        (MoveAsker.TargetRecenter or MoveAsker.ResumeRefinement, MoveKind.Slew) => MoveRule.Horizon,
        _ => throw new ArgumentOutOfRangeException(nameof(asker), $"{asker} has no Move Admission row for a {kind}."),
    };

    /// <summary>Whether <paramref name="rule"/> binds <paramref name="asker"/>'s <paramref name="kind"/>.</summary>
    public static bool Binds(MoveAsker asker, MoveKind kind, MoveRule rule) => (RulesFor(asker, kind) & rule) != 0;

    /// <summary>#2140 — the refusal of a person's move while <paramref name="holders"/> (the <see cref="RigOccupancy"/>
    /// names of what holds the mount, in registration order) are running, or null when it may go ahead: nothing holds it,
    /// or the person forced a GoTo and only a Session holds it (<paramref name="forced"/>; nothing else can be forced). The
    /// text names the holder: a routine beside the Session before the Session, as <c>mount_held</c>.</summary>
    internal static MountHeldException? MountHeldRefusal(IEnumerable<string> holders, bool forced, MoveKind kind) {
        ArgumentNullException.ThrowIfNull(holders);
        var held = holders.ToList();
        if (held.FirstOrDefault(h => h != RigOccupancy.Session) is { } other) {
            var what = kind switch { MoveKind.Slew => "GoTo", MoveKind.Park => "park", _ => "Find Home" };
            return new MountHeldException(MountHeldException.MountHeld,
                $"{other} is running and holds the mount, so the {what} was not sent. Stop it first.");
        }
        if (held.Count == 0) {
            return null;
        }
        var text = kind switch {
            MoveKind.Slew when forced => null,
            MoveKind.Slew => "a sequence is running: slewing now stops guiding and pauses the run awaiting you. Repeat with force to slew anyway.",
            MoveKind.Park => "a sequence is running: stop it before you park the mount.",
            _ => "a sequence is running: stop it before you send the mount home.",
        };
        return text is null ? null : new MountHeldException(MountHeldException.RunInProgress, text);
    }

    /// <summary>#2140 — the position-limit Refusal for <paramref name="asker"/>'s GoTo to <paramref name="target"/>, or
    /// null: <c>below-horizon-limit</c> (#2044) when the target is under the profile's lowest altitude, then
    /// <c>counterweight-limit</c> (#2073) for a target below the pole near the lower meridian on a German equatorial mount
    /// (or one whose type is unknown), each where the table binds the asker. <paramref name="atUtc"/> is when the move
    /// lands (the flip asks for the flip time); null is now.</summary>
    public static RefusalException? LimitRefusal(MoveAsker asker, IProfileStore? profiles, Coordinates target,
            ASCOM.Common.DeviceInterfaces.AlignmentMode? alignmentMode, ILogger? logger, DateTimeOffset? atUtc = null) {
        ArgumentNullException.ThrowIfNull(target);
        if (Binds(asker, MoveKind.Slew, MoveRule.Horizon) && HorizonGuard.TargetBelowHorizon(profiles, target, logger, atUtc) is { } low) {
            return new RefusalException(RefusalCatalog.BelowHorizonLimit,
                string.Create(CultureInfo.InvariantCulture, $"The target is at {low.AltitudeDeg:F1}°, below the {low.LimitDeg:F1}° horizon limit."),
                new Dictionary<string, object?> { ["altitude_deg"] = low.AltitudeDeg, ["limit_deg"] = low.LimitDeg });
        }
        if (Binds(asker, MoveKind.Slew, MoveRule.Counterweight)
                && CounterweightGuard.TargetPastCounterweightLimit(profiles, target, atUtc ?? DateTimeOffset.UtcNow, alignmentMode, logger) is { } cw) {
            return new RefusalException(RefusalCatalog.CounterweightLimit,
                string.Create(CultureInfo.InvariantCulture, $"The target is below the pole at hour angle {cw.HourAngleHours:F1} h: the counterweight would be {cw.CounterweightAngleDeg:F0}° from down, past the {cw.LimitDeg:F0}° limit."),
                new Dictionary<string, object?> { ["hour_angle_hours"] = cw.HourAngleHours, ["counterweight_angle_deg"] = cw.CounterweightAngleDeg, ["limit_deg"] = cw.LimitDeg });
        }
        return null;
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
