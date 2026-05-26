#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAstroAra.Server.Contracts.WsEvents;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// Phase 9 WebSocket endpoint per PORT_PLAYBOOK.md §10.9 + §60.9.
///
/// Endpoint: <c>GET /api/v1/ws</c>. Stub returns 501 NotImplemented (the
/// actual WS handler comes online with <c>IWsBroadcaster</c> +
/// <c>IWsEventChannel</c> implementations). Also exposes the read-only
/// <c>/api/v1/ws/catalog</c> endpoint required by §60.9.4 — that one is
/// functional immediately since it just dumps <see cref="WsEventCatalog.All"/>.
/// </summary>
public static class WebSocketEndpoints {

    public static IEndpointRouteBuilder MapWebSocketEndpoints(this IEndpointRouteBuilder app) {
        var ws = app.MapGroup("/api/v1/ws").WithTags("WebSocket");

        // §60.9.4 — exposes the catalog without requiring a WS upgrade.
        // Lets the WILMA client validate "the daemon advertises this event type"
        // before subscribing.
        ws.MapGet("/catalog", () => Results.Ok(new { events = WsEventCatalog.All }));

        // Actual WS handler at /api/v1/ws — stubbed at the HTTP layer until
        // app.UseWebSockets() + the IWsBroadcaster impl land. Returns 501
        // for plain HTTP GETs and 426 with an Upgrade-Required header for
        // attempted WS handshakes.
        ws.MapGet("", (HttpContext http) => {
            if (http.WebSockets.IsWebSocketRequest) {
                return Results.Problem(
                    type: "https://openastro.net/errors/ws-not-implemented",
                    title: "WebSocket endpoint not yet implemented",
                    statusCode: StatusCodes.Status501NotImplemented,
                    detail: "The /api/v1/ws upgrade is registered but the broadcaster + event channel are not yet running. WS goes live with Phase 9 service implementations.");
            }
            return Results.Problem(
                type: "https://openastro.net/errors/upgrade-required",
                title: "WebSocket upgrade required",
                statusCode: StatusCodes.Status426UpgradeRequired,
                detail: "This endpoint serves WebSocket connections only. See §60.9 for the connection protocol (X-Ara-WS-Version: 1, 30s ping / 60s pong, resume protocol with last-seen seq).");
        });

        return app;
    }
}
