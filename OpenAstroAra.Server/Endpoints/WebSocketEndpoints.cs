#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// §60.9 WebSocket endpoint. <c>GET /api/v1/ws</c> accepts the protocol
/// upgrade and drains <see cref="IWsEventChannel"/> to the client as
/// JSON text frames using the §60.9.3 envelope shape.
///
/// First sub-PR (accept + drain): no version validation, no heartbeat,
/// no resume — those layer on in subsequent sub-PRs. The handler is
/// already wired to the §13.17 broadcaster, so every placeholder
/// service that calls <c>broadcaster.PublishAsync(...)</c> reaches the
/// connected clients today.
///
/// <c>/api/v1/ws/catalog</c> stays the read-only event-catalog endpoint
/// per §60.9.4 — independent of the upgrade path.
/// </summary>
public static class WebSocketEndpoints {

    /// <summary>Catalog payload returned by GET /api/v1/ws/catalog.</summary>
    public sealed record WsCatalogResponse(IReadOnlyList<string> Events);

    /// <summary>
    /// Current §60.9 WS protocol version. Bumped only on breaking changes
    /// to the on-wire envelope shape or framing (not for additive event
    /// types — those are negotiated via /api/v1/ws/catalog).
    /// </summary>
    private const string ProtocolVersion = "1";

    public static IEndpointRouteBuilder MapWebSocketEndpoints(this IEndpointRouteBuilder app) {
        var ws = app.MapGroup("/api/v1/ws").WithTags("WebSocket");

        // §60.9.4 — exposes the catalog without requiring a WS upgrade.
        // Lets the WILMA client validate "the daemon advertises this event type"
        // before subscribing.
        ws.MapGet("/catalog", () => Results.Ok(new WsCatalogResponse(WsEventCatalog.All)))
          .Produces<WsCatalogResponse>(StatusCodes.Status200OK)
          .WithName("GetWebSocketCatalog");

        // §60.9 WebSocket upgrade.
        //   - WS request + X-Ara-WS-Version: 1 → accept and run the send/receive loop.
        //   - WS request + missing/wrong X-Ara-WS-Version → 426 (handshake rejection).
        //   - Plain HTTP GET → 426 with Upgrade/Connection headers per RFC 7231 §6.5.15.
        ws.MapGet("",
            async (HttpContext http, IWsEventChannel channel, ILoggerFactory loggerFactory) => {
                if (http.WebSockets.IsWebSocketRequest) {
                    // §60.9 requires X-Ara-WS-Version: 1. Per openapi.yaml line 674,
                    // a mismatched/missing version is rejected pre-upgrade with 426
                    // — close-code 4003 only applies if version negotiation fails
                    // *after* a successful upgrade, which can't happen with a
                    // pre-handshake header check.
                    var versionHeader = http.Request.Headers["X-Ara-WS-Version"].ToString();
                    if (!string.Equals(versionHeader, ProtocolVersion, StringComparison.Ordinal)) {
                        http.Response.Headers.Upgrade = "websocket";
                        http.Response.Headers.Connection = "Upgrade";
                        return Results.Problem(
                            type: "https://openastro.net/errors/ws-version-mismatch",
                            title: "Unsupported WebSocket protocol version",
                            statusCode: StatusCodes.Status426UpgradeRequired,
                            detail: $"X-Ara-WS-Version header is required and must equal \"{ProtocolVersion}\". Got: \"{versionHeader}\".");
                    }
                    var logger = loggerFactory.CreateLogger("OpenAstroAra.Server.Endpoints.WebSocket");
                    await HandleWebSocketAsync(http, channel, logger);
                    return Results.Empty;
                }
                // Per RFC 7231 §6.5.15, 426 responses must include hop-by-hop Upgrade
                // + Connection headers so the client knows which protocol to switch to.
                http.Response.Headers.Upgrade = "websocket";
                http.Response.Headers.Connection = "Upgrade";
                return Results.Problem(
                    type: "https://openastro.net/errors/upgrade-required",
                    title: "WebSocket upgrade required",
                    statusCode: StatusCodes.Status426UpgradeRequired,
                    detail: "This endpoint serves WebSocket connections only. See §60.9 for the connection protocol (X-Ara-WS-Version: 1, 30s ping / 60s pong, resume protocol with last-seen seq).");
            })
            .ProducesProblem(StatusCodes.Status426UpgradeRequired)
            .WithName("UpgradeToWebSocket");

        return app;
    }

    private static async Task HandleWebSocketAsync(
            HttpContext http,
            IWsEventChannel channel,
            ILogger logger) {
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        // Linked CTS lets the passive receive loop cancel the send loop
        // when the client sends a Close frame, while the server-side
        // RequestAborted token still drives shutdown.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        var ct = cts.Token;

        // Passive receive loop — sub-PR A discards all inbound frames except
        // Close. Sub-PR B handles pong responses; sub-PR C handles client
        // resume requests.
        var receiveTask = Task.Run(async () => {
            var buffer = new byte[1024];
            try {
                while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open) {
                    var result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) {
                        break;
                    }
                }
            } catch (OperationCanceledException) {
                // Expected on shutdown.
            } catch (WebSocketException) {
                // Expected on abrupt client disconnect.
            } catch (Exception ex) {
                logger.LogDebug(ex, "WS receive loop closed unexpectedly");
            }
            // Whatever caused the receive loop to exit (close frame, disconnect,
            // shutdown), tear down the send loop too.
            try { cts.Cancel(); } catch { /* CTS already disposed */ }
        }, ct);

        try {
            await foreach (var envelope in channel.ReadAllAsync(ct)) {
                if (socket.State != WebSocketState.Open) break;
                var json = JsonSerializer.SerializeToUtf8Bytes(
                    envelope, AraJsonSerializerContext.Default.WsEventEnvelopeDto);
                await socket.SendAsync(
                    json, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
        } catch (OperationCanceledException) {
            // Expected on shutdown or client close.
        } catch (WebSocketException ex) {
            logger.LogDebug(ex, "WS send loop ended on socket error");
        } catch (Exception ex) {
            logger.LogError(ex, "WS send loop failed");
        }

        // Best-effort close — if the socket is already gone the framework
        // throws, but there's nothing useful to do at that point.
        if (socket.State == WebSocketState.Open) {
            try {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "server closing", CancellationToken.None);
            } catch (Exception ex) {
                logger.LogDebug(ex, "WS close failed");
            }
        }

        try { cts.Cancel(); } catch { /* CTS already disposed */ }
        try { await receiveTask; } catch { /* swallowed; loop already logs */ }
    }
}
