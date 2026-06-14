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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// §55.1 multi-device WILMA settings sync: GET/PUT the profile's opaque UI-preferences blob so a user's other devices
/// pick it up on connect. Last-write-wins; the server doesn't interpret the blob.
/// </summary>
public static class ClientSettingsEndpoints {

    public static IEndpointRouteBuilder MapClientSettingsEndpoints(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/v1/client-settings").WithTags("ClientSettings");

        group.MapGet("",
                async (IClientSettingsService svc, CancellationToken ct) => Results.Ok(await svc.GetAsync(ct)))
            .Produces<ClientSettingsDto>(StatusCodes.Status200OK)
            .WithName("GetClientSettings");

        group.MapPut("",
                async (ClientSettingsUpdateDto body, IClientSettingsService svc, CancellationToken ct) => {
                    try {
                        return Results.Ok(await svc.ReplaceAsync(body.Settings, ct));
                    } catch (System.ArgumentException ex) {
                        // Non-object payload or over the size cap — a client mistake, surfaced as a 400 with the reason.
                        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
                    }
                })
            .Produces<ClientSettingsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            // Reject an oversized body at the transport layer (before deserialization) as defence-in-depth; the precise
            // 256 KiB-on-the-settings-object check still lives in the service.
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(ClientSettingsService.MaxRequestBytes))
            .WithName("ReplaceClientSettings");

        return app;
    }
}
