#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts.Refusals;

/// <summary>
/// A declared refusal (ADR 0022): the rig declining a request for a reason the user can fix. Only
/// <see cref="RefusalCatalog"/> creates one, so a module cannot refuse with an undeclared string. The
/// token travels as the problem's <c>reason</c>; <see cref="Title"/> is the human title and never a token.
/// </summary>
public readonly record struct RefusalCause {
    public string Token { get; }
    public int Status { get; }
    public string Title { get; }
    /// <summary>The extension names a refusal of this cause may carry (snake_case).</summary>
    public IReadOnlyList<string> ParameterNames { get; }
    internal RefusalCause(string token, int status, string title, params string[] parameterNames) {
        Token = token;
        Status = status;
        Title = title;
        ParameterNames = parameterNames;
    }
    public override string ToString() => Token;
}

/// <summary>Every declared <see cref="RefusalCause"/>, once (ADR 0022). The <c>RefusalReason</c> schema in
/// <c>openapi.yaml</c> is built from <see cref="All"/>; the client's <c>RefusalCause</c> enum must equal it.</summary>
public static class RefusalCatalog {
    // Slice 1: the pointing endpoints (Check, solve a frame, Center).
    public static readonly RefusalCause NoCamera = new("no-camera", 409, "No camera is connected");
    public static readonly RefusalCause CameraBusy = new("camera-busy", 409, "The camera is busy");
    public static readonly RefusalCause GateStuck = new("gate-stuck", 409, "The camera's capture gate is stuck");
    public static readonly RefusalCause CenterInProgress = new("center-in-progress", 409, "Another centring is running", "job_id");
    public static readonly RefusalCause OpticsNotSet = new("optics-not-set", 422, "Optics not set", "missing");
    public static readonly RefusalCause SolverNotSet = new("solver-not-set", 422, "The plate solver is not set up");
    public static readonly RefusalCause CaptureSettingsUnsupported = new("capture-settings-unsupported", 422, "The camera cannot take that exposure");
    public static readonly RefusalCause StorageUnavailable = new("storage-unavailable", 409, "The storage disk is missing");

    // Slice 2: a GoTo under the profile's horizon limit (#2044).
    public static readonly RefusalCause BelowHorizonLimit = new("below-horizon-limit", 409, "The target is below the horizon limit", "altitude_deg", "limit_deg");

    // Slice 2: a GoTo below the pole that would turn the counterweight past its limit (#2073).
    public static readonly RefusalCause CounterweightLimit = new("counterweight-limit", 409, "The target would turn the counterweight past its limit", "hour_angle_hours", "counterweight_angle_deg", "limit_deg");
    // A GoTo, Sync, Center or polar alignment before the mount's clock is set from the synced rig clock and checked (#2071).
    public static readonly RefusalCause MountClockNotVerified = new("mount-clock-not-verified", 409, "Mount clock not verified");

    // Slice 2: End Night (#1901).
    public static readonly RefusalCause EndNightInProgress = new("end-night-in-progress", 409, "End Night is already running");

    // Slice 2: a mount whose reported position is not trusted after a power loss (#2070).
    public static readonly RefusalCause MountPositionUnverified = new("mount-position-unverified", 409, "The mount's position is unverified", "position_unverified_reason", "home_is_sensor_search");

    // Polar Align never unparks the mount: unpark it and send it Home first.
    public static readonly RefusalCause PolarAlignMountParked = new("polar-align-mount-parked", 409, "The mount is parked");
    // Polar Align starts from the mount's Home position: a mount that can find Home by itself and is not there is refused.
    // Send it Home first, then start again.
    public static readonly RefusalCause PolarAlignNotHome = new("polar-align-not-home", 409, "The mount is not at Home");
    // #2066 — Polar Align holds the mount's motion owner for its whole run (its solves and the adjust loop included): a GoTo,
    // a sync, Center, Park or Home meanwhile is refused, a REST Park or Home whose checks were still running when it took the
    // mount too. A pad move is refused only while a turn runs (or its stop is not confirmed yet). Stop Polar Align first. A
    // pad stop (rate 0) and Stop Mount are never refused; the safety, fault and shutdown parks end Polar Align first.
    public static readonly RefusalCause PolarAlignMovingMount = new("polar-align-moving-mount", 409, "Polar alignment is moving the mount");
    // Another routine holds the mount's motion owner, or the guider guides or calibrates: Polar Align would turn the
    // mount beside another motion. Let it end, or stop guiding, and start again.
    public static readonly RefusalCause PolarAlignMountBusy = new("polar-align-mount-busy", 409, "The mount or the guider is busy");

    // #1589 — the user confirms a profile's meridian flip only after one ran to the end.
    public static readonly RefusalCause FirstFlipNotCompleted = new("first-flip-not-completed", 409, "No completed meridian flip to confirm");

    // #1589 — a GoTo to a target at or above the profile's clearance limit (clearance_limit_enabled on): the camera
    // or rotator could reach the pier or tripod there.
    public static readonly RefusalCause AboveClearanceLimit = new("above-clearance-limit", 409, "The target is above the clearance limit", "altitude_deg", "limit_deg");

    // #2173 — the plate-solve check of the mount's position while it is trusted already (or not read yet): nothing to check.
    public static readonly RefusalCause PoseCheckNotNeeded = new("pose-check-not-needed", 409, "The mount's position is not unverified");
    // #2173 — the plate-solve check's Sync with no current mismatch offer (no check_id, a stale one, or another mount session):
    // check the position again, then tap Sync.
    public static readonly RefusalCause PoseSyncNotOffered = new("pose-sync-not-offered", 409, "No plate-solve Sync is on offer");
    // #2173 — the plate-solve check with no mount connected.
    public static readonly RefusalCause MountNotConnected = new("mount-not-connected", 409, "No mount is connected");

    public static readonly IReadOnlyList<RefusalCause> All = [
        NoCamera, CameraBusy, GateStuck, CenterInProgress, OpticsNotSet, SolverNotSet, CaptureSettingsUnsupported, StorageUnavailable,
        EndNightInProgress, BelowHorizonLimit, MountPositionUnverified, CounterweightLimit,
        PolarAlignMountParked, PolarAlignNotHome, PolarAlignMovingMount, PolarAlignMountBusy,
        FirstFlipNotCompleted, MountClockNotVerified,
        AboveClearanceLimit,
        PoseCheckNotNeeded, PoseSyncNotOffered, MountNotConnected,
    ];
}