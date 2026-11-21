#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts.WsEvents;

// ADR 0014 step 2: the typed payload of telescope.limit_reached, tied to its token in WsEventCatalog.Payloads.
// Property names serialize snake_case through AraJsonSerializerContext.

/// <summary>Payload of <c>telescope.limit_reached</c> (#2044). <c>limit</c> names the limit that tripped: today only
/// <c>stop_before_meridian</c> (the flip is off and the meridian is <c>minutes</c> away). <c>tracking_stopped</c> is
/// false when the mount did not take the tracking-off command. <c>runs_paused</c> counts the runs a pause was asked
/// on (0 with no plan running). <c>detail</c> is the English sentence.</summary>
public sealed record TelescopeLimitReachedPayload(
    string Limit, int Minutes, bool TrackingStopped, int RunsPaused, string Detail);
