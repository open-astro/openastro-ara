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

namespace OpenAstroAra.Server.Contracts;

/// <summary>§31.3 — a lat/long/altitude triple on the time-sync wire (degrees / degrees / meters).</summary>
public sealed record TimeSyncLocationDto(double Lat, double Lng, double Alt);

/// <summary>
/// §31.3 — <c>GET /api/v1/server/time-sync</c>: the server's sync state. <c>Synced</c> is the
/// §31.1 gate the client's waterfall branches on: a sync exists, is fresh (&lt; 1 h old), and
/// carries at least medium trust. <c>SyncedAtUtc</c>/<c>Trust</c> are exposed so a client can
/// show detail beyond the boolean (additive to the playbook shape).
/// </summary>
public sealed record TimeSyncStateDto(
    bool Synced,
    string Source,           // ntp | gps-internal | gps-external | client | manual | none
    string Trust,            // high | medium | low | none
    DateTimeOffset CurrentTimeUtc,
    double SystemTimeOffsetSeconds,
    TimeSyncLocationDto? Location,
    bool InternetAvailableOnPi,
    bool InternalGpsAvailable,
    DateTimeOffset? SyncedAtUtc);

/// <summary>§31.3 — <c>POST /api/v1/server/time-sync</c>: a client-pushed time (+ optional
/// location). <c>Source</c> is one of <c>client|gps-mobile|manual</c>; the requested
/// <c>Trust</c> is clamped server-side to the source's §31.2 ceiling.</summary>
public sealed record TimeSyncPushRequestDto(
    string Source,
    DateTimeOffset TimeUtc,
    TimeSyncLocationDto? Location = null,
    string? Trust = null);

public sealed record TimeSyncBeforeAfterDto(DateTimeOffset TimeUtc, double OffsetSeconds);

/// <summary>§31.3 — the push result: the clock before and after the sync, and whether the
/// pushed location was written into the active profile's site settings. <c>ClockSet</c> is
/// additive-honest: false means the CAP_SYS_TIME set failed (dev box / missing capability) and
/// the offset in <see cref="After"/> is being tracked rather than corrected.</summary>
public sealed record TimeSyncPushResultDto(
    TimeSyncBeforeAfterDto Before,
    TimeSyncBeforeAfterDto After,
    bool LocationUpdated,
    bool ClockSet);
