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
using Microsoft.Extensions.DependencyInjection;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// Phase 9 notification endpoints per PORT_PLAYBOOK.md §10.9 + §46.
/// Phase 13.4 wires every route to INotificationService (placeholder
/// today; §46.5 SQLite-backed impl lands alongside the §28 frame
/// catalog DB).
/// </summary>
public static class NotificationEndpoints {

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app) {
        var notifications = app.MapGroup("/api/v1/notifications").WithTags("Notifications");

        notifications.MapGet("",
                async (int? limit, string? cursor, bool? unreadOnly, INotificationService svc, CancellationToken ct) =>
                    Results.Ok(await svc.ListAsync(limit ?? 50, cursor, unreadOnly, ct)))
            .Produces<CursorPage<NotificationDto>>(StatusCodes.Status200OK)
            .WithName("ListNotifications");

        notifications.MapPost("/{id:guid}/dismiss",
                async (Guid id, [FromBody] NotificationActionRequestDto request, INotificationService svc,
                        HttpContext http, CancellationToken ct) => {
                    // §58.12 — dismissing a notification is the playbook's "tap
                    // [Acknowledge]": the user is back, cancel any shutdown countdown.
                    http.RequestServices.GetService<UnattendedShutdownService>()
                        ?.NotifyUserActivity("notification.dismiss");
                    var updated = await svc.DismissAsync(id, request, ct);
                    return updated is null ? Results.NotFound() : Results.Ok(updated);
                })
            .Accepts<NotificationActionRequestDto>("application/json")
            .Produces<NotificationDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("DismissNotification");

        notifications.MapPost("/{id:guid}/mark-read",
                async (Guid id, INotificationService svc, HttpContext http, CancellationToken ct) => {
                    // §58.12 — reading the failure counts as attention too.
                    http.RequestServices.GetService<UnattendedShutdownService>()
                        ?.NotifyUserActivity("notification.mark-read");
                    var updated = await svc.MarkReadAsync(id, ct);
                    return updated is null ? Results.NotFound() : Results.Ok(updated);
                })
            .Produces<NotificationDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("MarkNotificationRead");

        notifications.MapGet("/preferences",
                async (INotificationService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetPreferencesAsync(ct)))
            .Produces<NotificationPreferenceDto>(StatusCodes.Status200OK)
            .WithName("GetNotificationPreferences");

        notifications.MapPut("/preferences",
                async ([FromBody] NotificationPreferenceDto preferences, INotificationService svc, CancellationToken ct) =>
                    Results.Ok(await svc.SetPreferencesAsync(preferences, ct)))
            .Accepts<NotificationPreferenceDto>("application/json")
            .Produces<NotificationPreferenceDto>(StatusCodes.Status200OK)
            // The placeholder accepts any prefs body, but the §46.5 real
            // implementation will validate (e.g. reject empty category
            // lists, malformed quiet-hours times). Keep the annotation so
            // the contract is visible for that future validation.
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("SetNotificationPreferences");

        return app;
    }
}