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
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// §55.1 multi-device WILMA settings sync: GET/PUT/PATCH the profile's opaque UI-preferences blob so a user's other devices
/// pick it up on connect. Last-write-wins; the server doesn't interpret the blob.
/// </summary>
public static class ClientSettingsEndpoints {

    private static readonly string[] PatchMethod = { "PATCH" };

    public static IEndpointRouteBuilder MapClientSettingsEndpoints(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/v1/client-settings").WithTags("ClientSettings");

        group.MapGet("",
                async (IClientSettingsService svc, CancellationToken ct) => Results.Ok(await svc.GetAsync(ct)))
            .Produces<ClientSettingsDto>(StatusCodes.Status200OK)
            .WithName("GetClientSettings");

        // Body is read manually from HttpContext (not a bound parameter) so an oversized request can be rejected by
        // its declared Content-Length BEFORE the body is buffered/deserialized — minimal APIs don't honour MVC's
        // RequestSizeLimit attribute, so this is the reliable pre-allocation guard. The precise 256 KiB-on-the-object
        // check still lives in the service for chunked bodies that omit Content-Length.
        group.MapPut("", (HttpContext http, IClientSettingsService svc, CancellationToken ct) =>
                WriteAsync(http, (settings, token) => svc.ReplaceAsync(settings, token), ct))
            .Produces<ClientSettingsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .WithName("ReplaceClientSettings");

        // #1644 — top-level-key merge: same body, same checks, but only the given keys change (null removes one), so
        // two devices saving different keys don't overwrite each other.
        group.MapMethods("", PatchMethod,
                (HttpContext http, IClientSettingsService svc, CancellationToken ct) =>
                    WriteAsync(http, (settings, token) => svc.MergeAsync(settings, token), ct))
            .Produces<ClientSettingsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .WithName("MergeClientSettings");

        return app;
    }

    // Body is read manually from HttpContext (not a bound parameter) so an oversized request can be rejected by
    // its declared Content-Length BEFORE the body is buffered/deserialized — minimal APIs don't honour MVC's
    // RequestSizeLimit attribute, so this is the reliable pre-allocation guard. The precise 256 KiB-on-the-object
    // check still lives in the service for chunked bodies that omit Content-Length.
    private static async Task<IResult> WriteAsync(HttpContext http,
        Func<JsonElement, CancellationToken, Task<ClientSettingsDto>> write, CancellationToken ct) {
        if (!http.Request.HasJsonContentType()) {
            return Results.Problem(detail: "Expected a JSON request body.",
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }
        if (http.Request.ContentLength is long len && len > ClientSettingsService.MaxRequestBytes) {
            return Results.Problem(detail: "Request body exceeds the size limit.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        ClientSettingsUpdateDto? body;
        try {
            body = await http.Request.ReadFromJsonAsync(
                AraJsonSerializerContext.Default.ClientSettingsUpdateDto, ct);
        } catch (JsonException) {
            return Results.Problem(detail: "Request body is not valid JSON.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (body is null) {
            return Results.Problem(detail: "A request body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        try {
            return Results.Ok(await write(body.Settings, ct));
        } catch (ArgumentException ex) {
            // Non-object payload or over the size cap — a client mistake, surfaced as a 400 with the reason.
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}