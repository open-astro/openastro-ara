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

/// <summary>
/// §42.2 — the fault taxonomy. Wire tokens are the snake_case of the name
/// (<c>disconnected</c>, <c>tracking_lost</c>, <c>stall_timeout</c>, <c>value_mismatch</c>,
/// <c>op_error</c>, <c>cooling_drift</c>).
/// </summary>
public enum EquipmentFaultKind {
    /// <summary>The device stopped answering its Alpaca connection probe (§42.3 rows 1/6).</summary>
    Disconnected,

    /// <summary>The mount reports tracking off while a session expects it on (§42.2 row 4).</summary>
    TrackingLost,

    /// <summary>A bounded op-wait expired: focuser never reached position, filter wheel never
    /// settled on the slot, dome never finished (§42.2 rows 7/8).</summary>
    StallTimeout,

    /// <summary>A commanded value read back outside tolerance: switch state/value (§42.4),
    /// rotator angle drift (§42.2 rows 9/15/16).</summary>
    ValueMismatch,

    /// <summary>A device operation threw (slew error, capture error) — the generic §42.1 row.</summary>
    OpError,

    /// <summary>Camera cooling can't hold the setpoint (§42.2 row 2) — advisory.</summary>
    CoolingDrift,
}

/// <summary>
/// §42.2 — one detected equipment fault, published by the device services into the
/// in-proc fault sink. Detection-side only: what reacted to it (retries, pause,
/// abort+park) is the §42.3 reaction slice's concern and is reported separately.
/// </summary>
/// <param name="Details">A short human-readable specifics line (last error, streak length,
/// commanded vs read-back values); free text, for the log/WS payload — never parsed.</param>
public sealed record EquipmentFaultEvent(
    DeviceType DeviceType,
    string? DeviceId,
    string? DeviceName,
    EquipmentFaultKind Kind,
    string? Details,
    DateTimeOffset DetectedUtc);
