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

// ────────────────────────────────────────────────────────────────────────────
// §27 single-client connection policy DTOs.
//
// POST /api/v1/server/connect        → ClientConnectRequestDto / ClientConnectResponseDto
// POST /api/v1/server/disconnect     → ClientDisconnectRequestDto
// GET  /api/v1/server/session        → ClientSessionInfoDto
//
// Plus the two WS control frames of the §27.1 takeover dance. These are
// CONTROL frames like the §60.9 resume request/response — sent bare, not
// wrapped in the seq'd WsEventEnvelopeDto — so they deliberately do NOT
// appear in WsEventCatalog (which lists envelope event types only).
// ────────────────────────────────────────────────────────────────────────────

/// <summary>Body of <c>POST /api/v1/server/connect</c>. The hostname identifies the
/// connecting client to the current holder's takeover modal ("ipad.local wants to
/// connect") — it is display-only, never used for addressing.</summary>
public sealed record ClientConnectRequestDto(string? Hostname);

/// <summary>Success shape of <c>POST /api/v1/server/connect</c>. The session id is the
/// caller's capability for <c>disconnect</c> and for binding its WS upgrade
/// (<c>X-Ara-Session</c> header) to the session's liveness tracking.</summary>
public sealed record ClientConnectResponseDto(
    Guid SessionId,
    string Hostname,
    DateTimeOffset ConnectedAt);

/// <summary>Body of <c>POST /api/v1/server/disconnect</c> — the session id returned by
/// <c>connect</c> proves the caller owns the slot it is releasing.</summary>
public sealed record ClientDisconnectRequestDto(Guid SessionId);

/// <summary>Shape of <c>GET /api/v1/server/session</c> per §27.3: who controls the
/// daemon, since when, and how long since the daemon last heard from them. The
/// session id itself is deliberately omitted — it is a bearer capability, and this
/// endpoint is readable by anyone on the LAN.</summary>
public sealed record ClientSessionInfoDto(
    bool Connected,
    string? Hostname,
    DateTimeOffset? ConnectedAt,
    double? IdleSeconds);

/// <summary>§27.1 server→client control frame asking the CURRENT holder whether
/// <paramref name="From"/> may take over. <c>Type</c> is always
/// <c>"connection.request"</c>.</summary>
public sealed record WsConnectionRequestDto(
    string Type,
    string From,
    string RequestId);

/// <summary>Client→server WS text frame, parsed permissively (§27.1 + §60.9
/// heartbeat). Recognized shapes: <c>{"type":"pong"}</c> and
/// <c>{"type":"connection.response","request_id":"...","action":"allow"|"reject"}</c>.
/// Anything else — unknown type, missing fields, non-JSON — is ignored for
/// forward compatibility.</summary>
public sealed record WsClientFrameDto(
    string? Type,
    string? RequestId,
    string? Action);
