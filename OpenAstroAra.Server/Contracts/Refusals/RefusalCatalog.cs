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
    public static readonly RefusalCause MountPositionUnverified = new("mount-position-unverified", 409, "The mount's position is unverified", "position_unverified_reason");

    // #2048 — Polar Align starts like NINA's: from where the user pointed the mount, never from a parked mount, and only
    // after the user confirmed the start and its RA turn direction (direction = the way the run would turn now).
    public static readonly RefusalCause PolarAlignMountParked = new("polar-align-mount-parked", 409, "The mount is parked");
    public static readonly RefusalCause PolarAlignUnconfirmed = new("polar-align-unconfirmed", 409, "Polar alignment needs your confirmation before the mount moves", "direction");
    // #2089 — the RA axis has turned too far from where Polar Align started this session (offset_deg from the origin,
    // limit_deg the bound): slew it back near the pole and confirm again.
    public static readonly RefusalCause PolarAlignSweepLimit = new("polar-align-sweep-limit", 409, "The mount has turned too far from where Polar Align started", "offset_deg", "limit_deg");
    // #2066 — a GoTo, a pad move, Home or Park while Polar Align turns the mount (or its stop is not confirmed yet): stop
    // Polar Align first. A pad stop (rate 0) and Stop Mount are never refused.
    public static readonly RefusalCause PolarAlignMovingMount = new("polar-align-moving-mount", 409, "Polar alignment is moving the mount");
    // #2082 — the scope points within 3° of the pole: the camera would look along the RA axis, and nothing tilts it off the
    // axis any more. Point it 2–5° from the pole and start again.
    public static readonly RefusalCause PolarAlignOnAxis = new("polar-align-on-axis", 409, "The scope points too close to the pole");

    // #1589 — the user confirms a profile's meridian flip only after one ran to the end.
    public static readonly RefusalCause FirstFlipNotCompleted = new("first-flip-not-completed", 409, "No completed meridian flip to confirm");

    public static readonly IReadOnlyList<RefusalCause> All = [
        NoCamera, CameraBusy, GateStuck, CenterInProgress, OpticsNotSet, SolverNotSet, CaptureSettingsUnsupported, StorageUnavailable,
        EndNightInProgress, BelowHorizonLimit, MountPositionUnverified, CounterweightLimit,
        PolarAlignMountParked, PolarAlignUnconfirmed, PolarAlignSweepLimit, PolarAlignMovingMount, PolarAlignOnAxis,
        FirstFlipNotCompleted, MountClockNotVerified,
    ];
}