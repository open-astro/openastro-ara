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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// §27.3 single-client connection endpoints (no auth per §67 — v0.0.1 is
/// LAN-only). Thin HTTP mapping over <see cref="ClientSessionService"/>,
/// which owns the slot, the takeover dance, and the liveness rules.
/// </summary>
public static class ConnectionEndpoints {

    /// <summary>Display cap for the client-supplied hostname — it only ever feeds the
    /// holder's takeover modal, 409 details, and the 4004 close reason (which RFC 6455
    /// caps at 123 bytes anyway).</summary>
    private const int MaxHostnameLength = 64;

    public static IEndpointRouteBuilder MapConnectionEndpoints(this IEndpointRouteBuilder app) {
        var server = app.MapGroup("/api/v1/server").WithTags("Connection");

        server.MapPost("/connect",
                async ([FromBody] ClientConnectRequestDto body, ClientSessionService sessions, CancellationToken ct) => {
                    var hostname = body?.Hostname?.Trim();
                    if (string.IsNullOrEmpty(hostname)) {
                        return Results.Problem(
                            type: "https://openastro.net/errors/validation",
                            title: "hostname is required",
                            statusCode: StatusCodes.Status422UnprocessableEntity,
                            detail: "Body must be { \"hostname\": \"<display name of the connecting client>\" }.");
                    }
                    if (hostname.Length > MaxHostnameLength) {
                        hostname = hostname[..MaxHostnameLength];
                    }
                    var outcome = await sessions.ConnectAsync(hostname, ct);
                    return outcome.Kind switch {
                        ConnectOutcomeKind.Granted => Results.Ok(
                            new ClientConnectResponseDto(outcome.SessionId, hostname, outcome.ConnectedAt)),
                        ConnectOutcomeKind.Rejected => Results.Problem(
                            type: "https://openastro.net/errors/connection-rejected",
                            title: "Connection rejected",
                            statusCode: StatusCodes.Status409Conflict,
                            detail: $"Server in use by {outcome.CurrentHostname}."),
                        ConnectOutcomeKind.Unresponsive => Results.Problem(
                            type: "https://openastro.net/errors/connection-unresponsive",
                            title: "Current client unresponsive",
                            statusCode: StatusCodes.Status409Conflict,
                            detail: "The current client did not answer the takeover request. Try again in 60 s — an unresponsive client is marked dead after 60 s of silence and the slot frees up."),
                        _ => Results.Problem(
                            type: "https://openastro.net/errors/connection-busy",
                            title: "Another connection attempt is in progress",
                            statusCode: StatusCodes.Status409Conflict,
                            detail: "The current client is already being asked about a takeover. Try again shortly."),
                    };
                })
              .Accepts<ClientConnectRequestDto>("application/json")
              .Produces<ClientConnectResponseDto>(StatusCodes.Status200OK)
              .ProducesProblem(StatusCodes.Status409Conflict)
              .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
              .WithName("ConnectClient");

        server.MapPost("/disconnect",
                ([FromBody] ClientDisconnectRequestDto body, ClientSessionService sessions) =>
                    sessions.Disconnect(body.SessionId)
                        ? Results.NoContent()
                        : Results.Problem(
                            type: "https://openastro.net/errors/session-not-found",
                            title: "Session not found",
                            statusCode: StatusCodes.Status404NotFound,
                            detail: "No current session matches that session id — it may have been taken over or already released."))
              .Accepts<ClientDisconnectRequestDto>("application/json")
              .Produces(StatusCodes.Status204NoContent)
              .ProducesProblem(StatusCodes.Status404NotFound)
              .WithName("DisconnectClient");

        server.MapGet("/session",
                (ClientSessionService sessions) => Results.Ok(sessions.GetSession()))
              .Produces<ClientSessionInfoDto>(StatusCodes.Status200OK)
              .WithName("GetClientSession");

        return app;
    }
}
