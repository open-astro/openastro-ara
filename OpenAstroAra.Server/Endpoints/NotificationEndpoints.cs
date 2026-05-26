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

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// Phase 9 notification endpoints per PORT_PLAYBOOK.md §10.9 + §46.
/// </summary>
public static class NotificationEndpoints {

    private static IResult NotImplementedStub(string endpoint, string section) =>
        Results.Problem(
            type: "https://openastro.net/errors/not-implemented",
            title: "Endpoint not yet implemented",
            statusCode: StatusCodes.Status501NotImplemented,
            detail: $"{endpoint} is part of Phase 9's incremental implementation ({section}). Stub registered so the OpenAPI surface is stable; service wiring lands per area.");

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app) {
        var notifications = app.MapGroup("/api/v1/notifications").WithTags("Notifications");

        notifications.MapGet("", () => NotImplementedStub("GET /api/v1/notifications", "§46"));
        notifications.MapPost("/{id:guid}/dismiss", (Guid id) => NotImplementedStub("POST /api/v1/notifications/{id}/dismiss", "§46"));
        notifications.MapPost("/{id:guid}/mark-read", (Guid id) => NotImplementedStub("POST /api/v1/notifications/{id}/mark-read", "§46"));

        notifications.MapGet("/preferences", () => NotImplementedStub("GET /api/v1/notifications/preferences", "§46.4"));
        notifications.MapPut("/preferences", () => NotImplementedStub("PUT /api/v1/notifications/preferences", "§46.4"));

        return app;
    }
}
