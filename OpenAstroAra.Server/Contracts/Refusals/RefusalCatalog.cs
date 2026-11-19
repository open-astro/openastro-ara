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

    // Slice 2: End Night (#1901).
    public static readonly RefusalCause EndNightInProgress = new("end-night-in-progress", 409, "End Night is already running");

    public static readonly IReadOnlyList<RefusalCause> All = [
        NoCamera, CameraBusy, GateStuck, CenterInProgress, OpticsNotSet, SolverNotSet, CaptureSettingsUnsupported, StorageUnavailable,
        EndNightInProgress, BelowHorizonLimit,
    ];
}