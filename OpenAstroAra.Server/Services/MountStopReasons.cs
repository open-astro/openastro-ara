#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #2066, #2107 — the reasons a stop is counted with in the mount's stop count
/// (<see cref="ITelescopeService.AbortEpoch"/>, read back by <see cref="ITelescopeService.AbortReasonSince"/>). Each sender
/// counts its stop under its own name, so a routine the stop ends (Polar Align) can say what stopped it, and the
/// <c>slew_aborted</c> event names it too. The tokens are on the wire (<c>polar_align.error</c>'s <c>stopped_by</c>, the
/// <c>slew_aborted</c> event's <c>reason</c>): never rename one.
/// </summary>
public static class MountStopReasons {

    /// <summary>Stop Mount (<c>POST /equipment/telescope/abort</c>), and any caller that names no reason.</summary>
    public const string UserRequest = "user_request";

    /// <summary>Emergency Stop's latch (its step 0) and its mount rungs.</summary>
    public const string EmergencyStop = "emergency_stop";

    /// <summary>The meridian-flip watchdog's own abort.</summary>
    public const string Watchdog = "watchdog";

    /// <summary>A park: it ends any routine that drives the motors.</summary>
    public const string Park = "park";

    /// <summary>A Find Home: it ends any routine that drives the motors.</summary>
    public const string FindHome = "findhome";

    /// <summary>Polar Align's own stop of the motor moves it made.</summary>
    public const string PolarAlign = "polar_align";

    /// <summary>A MoveAxis 0 from the direction pad (any client of <c>/moveaxis</c>) while Polar Align turns the mount.</summary>
    public const string PadStop = "pad_stop";

    /// <summary>The equipment-fault reaction stopping the mount when its park was refused (the position is unverified).</summary>
    public const string FaultReaction = "fault_reaction";

    /// <summary>The safety reaction stopping the mount when its unsafe-conditions park was refused because the mount's
    /// position is unverified (#2070).</summary>
    public const string PoseUnverified = "pose_unverified";

    /// <summary>The safety park taking the mount from Polar Align (<see cref="IPolarAlignService.StopForTakeoverAsync"/>).</summary>
    public const string SafetyPark = "safety_park";

    /// <summary>The equipment-fault park taking the mount from Polar Align (<see cref="IPolarAlignService.StopForTakeoverAsync"/>).</summary>
    public const string FaultPark = "fault_park";

    /// <summary>Who stopped the mount, for an English sentence: "the mount was stopped (…)". Null or unknown reads as
    /// Stop Mount, the stop with no other name.</summary>
    public static string Describe(string? reason) => reason switch {
        EmergencyStop => "Emergency Stop",
        Watchdog => "the meridian-flip watchdog",
        Park => "a park",
        FindHome => "a Find Home",
        PolarAlign => "Polar Align's own stop",
        PadStop => "the direction pad",
        FaultReaction => "the equipment-fault reaction",
        PoseUnverified => "the safety reaction, the mount's position unverified",
        _ => "Stop Mount",
    };
}
