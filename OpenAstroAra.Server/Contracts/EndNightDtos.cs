#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts;

// #1901 — End Night: one daemon action that puts the rig to bed (design/API_CONTRACT.md, "End Night").
// States and step ids travel as lowercase tokens, in the constants below.

/// <summary>POST /api/v1/end-night body. The body is optional; a bare POST shuts the host down too.</summary>
public sealed record EndNightStartRequestDto(bool ShutDownHost = true);

/// <summary>One step of the action: its token, where it stands and a sentence for the progress view.</summary>
public sealed record EndNightStepDto(string Id, string State, string? Detail);

/// <summary>GET /api/v1/end-night/status, and the answer to start and cancel. <c>state</c> is idle (nothing ran since
/// the daemon started), running, complete or cancelled. <c>cancellable</c> is false once the host poweroff has
/// started and while nothing runs. The finished action stays readable until the next start.</summary>
public sealed record EndNightStatusDto(
    string State,
    Guid? RunId,
    bool ShutDownHost,
    bool Cancellable,
    bool CancelRequested,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? EndedUtc,
    IReadOnlyList<EndNightStepDto> Steps);

/// <summary>The <c>end_night.state</c> tokens.</summary>
public static class EndNightStates {
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Complete = "complete";
    public const string Cancelled = "cancelled";
}

/// <summary>The <c>end_night.step</c> tokens, in the order the steps run.</summary>
public static class EndNightStepIds {
    public const string AbortRun = "abort_run";
    public const string StopGuiding = "stop_guiding";
    public const string FindHome = "find_home";
    public const string ParkMount = "park_mount";
    public const string DisconnectDevices = "disconnect_devices";
    public const string WarmCooler = "warm_cooler";
    public const string DisconnectCamera = "disconnect_camera";
    public const string Notify = "notify";
    public const string HostPoweroff = "host_poweroff";

    public static readonly IReadOnlyList<string> All = [
        AbortRun, StopGuiding, FindHome, ParkMount, DisconnectDevices, WarmCooler, DisconnectCamera, Notify, HostPoweroff,
    ];
}

/// <summary>The <c>end_night.step_state</c> tokens.</summary>
public static class EndNightStepStates {
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Done = "done";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}