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

        notifications.MapGet("", () => NotImplementedStub("GET /api/v1/notifications", "§46"))
                     .Produces<CursorPage<NotificationDto>>(StatusCodes.Status200OK)
                     .ProducesProblem(StatusCodes.Status501NotImplemented)
                     .WithName("ListNotifications");

        notifications.MapPost("/{id:guid}/dismiss",
                (Guid id, [FromBody] NotificationActionRequestDto request) =>
                    NotImplementedStub("POST /api/v1/notifications/{id}/dismiss", "§46"))
            .Accepts<NotificationActionRequestDto>("application/json")
            .Produces<NotificationDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("DismissNotification");

        notifications.MapPost("/{id:guid}/mark-read", (Guid id) =>
                NotImplementedStub("POST /api/v1/notifications/{id}/mark-read", "§46"))
            .Produces<NotificationDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("MarkNotificationRead");

        notifications.MapGet("/preferences", () =>
                NotImplementedStub("GET /api/v1/notifications/preferences", "§46.4"))
            .Produces<NotificationPreferenceDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GetNotificationPreferences");

        notifications.MapPut("/preferences", ([FromBody] NotificationPreferenceDto preferences) =>
                NotImplementedStub("PUT /api/v1/notifications/preferences", "§46.4"))
            .Accepts<NotificationPreferenceDto>("application/json")
            .Produces<NotificationPreferenceDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("SetNotificationPreferences");

        return app;
    }
}
