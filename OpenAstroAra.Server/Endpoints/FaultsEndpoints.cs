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
using System.Threading;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// §42.5 fault-history read API over <see cref="IFaultLogService"/>. Write-side
/// has no endpoint — faults are detected and persisted daemon-side (the
/// EquipmentFaultHub choke point), never posted by a client.
/// </summary>
public static class FaultsEndpoints {

    public static IEndpointRouteBuilder MapFaultsEndpoints(this IEndpointRouteBuilder app) {
        var faults = app.MapGroup("/api/v1/faults").WithTags("Faults");

        faults.MapGet("",
                async (int? limit, string? cursor, string? equipmentType, Guid? sessionId,
                        bool? unresolvedOnly, string? faultType, IFaultLogService svc, CancellationToken ct) =>
                    Results.Ok(await svc.ListAsync(limit ?? 50, cursor, equipmentType, sessionId, unresolvedOnly, faultType, ct)))
            .Produces<CursorPage<FaultDto>>(StatusCodes.Status200OK)
            .WithName("ListFaults");

        faults.MapGet("/{id:guid}",
                async (Guid id, IFaultLogService svc, CancellationToken ct) =>
                    await svc.GetAsync(id, ct) is { } fault ? Results.Ok(fault) : Results.NotFound())
            .Produces<FaultDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetFault");

        return app;
    }
}
