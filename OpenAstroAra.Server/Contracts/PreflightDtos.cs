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
using System.Collections.Generic;

namespace OpenAstroAra.Server.Contracts;

/// <summary>§58.14 — one line of the pre-sleep checklist. <c>Status</c> is <c>ok</c>, <c>warn</c>,
/// <c>fail</c> or <c>unknown</c> (the fact could not be read; never silently "ok").</summary>
public sealed record PreflightItemDto(string Key, string Status, string Title, string Detail);

/// <summary>§58.14 — a meridian crossing forecast for a target the running sequence will image.
/// <c>Standing</c> is <c>ok</c>, <c>soft_warning</c> (below the site's advisory altitude) or
/// <c>below_floor</c> (below the hard horizon: the flight check will refuse that flip).</summary>
public sealed record PreflightFlipDto(string Target, DateTimeOffset AtUtc, double AltitudeDeg, string Standing);

/// <summary>§58.14 — <c>GET /api/v1/preflight/today</c>: the pre-sleep self-test the client shows
/// once before the user goes to bed. <c>AllGood</c> is false when any item failed or a forecast flip
/// lands below the hard floor; warnings and unknowns leave it true but are shown.</summary>
public sealed record PreflightChecklistDto(
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<PreflightItemDto> Items,
    IReadOnlyList<PreflightFlipDto> Flips,
    // Targets in the running sequence that are not the active one: their flips are not forecast
    // (only the live target's coordinates are readable), so the card says "N more targets".
    int UnforecastTargets,
    // Site-local "HH:mm"; null when the window follows the sun (astronomical darkness).
    string? UnattendedWindowStart,
    string? UnattendedWindowEnd,
    string UnattendedWindowSource,   // "clock" | "sun"
    // The controlling client's hostname (the device that will sound the alarm), null when none.
    string? AlarmDevice,
    bool AlarmSoundEnabled,
    bool AllGood);
