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
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System.Threading;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>§33 client-pushed update (#1122): upload a <c>.deb</c>, apply it through the root
/// helper, read the outcome after the restart. See <see cref="ServerUpdateService"/>.</summary>
public static class ServerUpdateEndpoints {

    public static IEndpointRouteBuilder MapServerUpdateEndpoints(this IEndpointRouteBuilder app) {
        var update = app.MapGroup("/api/v1/server/update").WithTags("ServerUpdate");

        // Raw body, not multipart: one file, no form fields, and the client streams it. The
        // Content-Length pre-check refuses an oversized upload before a byte is buffered; the
        // service re-checks while copying for chunked bodies. Kestrel's default 30 MB body cap
        // is lifted for this one route only.
        update.MapPost("",
                async (HttpContext http, IServerUpdateService svc, CancellationToken ct) => {
                    if (http.Request.ContentLength is long len && len > ServerUpdateService.MaxUploadBytes) {
                        return Results.Problem(detail: "Upload exceeds the size limit.",
                            statusCode: StatusCodes.Status413PayloadTooLarge);
                    }
                    http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = ServerUpdateService.MaxUploadBytes;
                    try {
                        string? sha256 = http.Request.Headers["X-Update-Sha256"];
                        return Results.Accepted(value: await svc.StageAsync(http.Request.Body, http.Request.ContentLength, sha256, ct));
                    } catch (ServerUpdateRejectedException ex) {
                        return Rejected(ex);
                    }
                })
            .Accepts<byte[]>("application/vnd.debian.binary-package", "application/octet-stream")
            .Produces<ServerUpdateStagedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("StageServerUpdate")
            .WithSummary("Upload an openastroara-server .deb to stage it for an in-place update (#1122). Optional X-Update-Sha256 header is checked against the received bytes.");

        update.MapPost("/{id}/apply",
                async (string id, IServerUpdateService svc, CancellationToken ct) => {
                    try {
                        return Results.Accepted(value: await svc.ApplyAsync(id, ct));
                    } catch (ServerUpdateRejectedException ex) {
                        return Rejected(ex);
                    }
                })
            .Produces<ServerUpdateStatusDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ApplyServerUpdate")
            .WithSummary("Install a staged update through the root helper; the daemon emits server.restart_imminent and restarts.");

        update.MapGet("/{id}",
                async (string id, IServerUpdateService svc, CancellationToken ct) => {
                    var status = await svc.GetStatusAsync(id, ct);
                    return status is null ? Results.NotFound() : Results.Ok(status);
                })
            .Produces<ServerUpdateStatusDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("GetServerUpdateStatus")
            .WithSummary("Outcome of an applied update (pending / applied / rolled_back / failed), readable after the restart.");

        return app;
    }

    private static IResult Rejected(ServerUpdateRejectedException ex) {
        var status = ex.Reason switch {
            "unknown_id" => StatusCodes.Status404NotFound,
            "too_large" => StatusCodes.Status413PayloadTooLarge,
            "not_packaged" or "helper_unavailable" or "update_in_progress" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status422UnprocessableEntity,
        };
        return Results.Problem(title: ex.Reason, detail: ex.Message, statusCode: status);
    }
}
