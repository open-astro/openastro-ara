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
/// §37 profile endpoints. Phase 12h.6a implements imaging-defaults
/// end-to-end (GET + PUT). Other sections (storage, notifications, site,
/// safety policies, autofocus, plate solve, etc) follow in 12h.6b-N — each
/// adds a section-specific DTO + endpoint pair on top of the same
/// <see cref="IProfileStore"/>.
///
/// The DTOs here mirror the WILMA client's settings notifiers field-for-
/// field, so a single PUT sends the entire section state in one round
/// trip — simpler than PATCH for v0.0.1 where every panel "Save" button
/// already holds the full state client-side.
/// </summary>
public static class ProfileEndpoints {
    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app) {
        var profile = app.MapGroup("/api/v1/profile").WithTags("Profile");

        profile.MapGet("/imaging-defaults", (IProfileStore store) =>
                Results.Ok(store.GetImagingDefaults()))
            .Produces<ImagingDefaultsDto>(StatusCodes.Status200OK)
            .WithName("GetImagingDefaults")
            .WithSummary("Get the active profile's imaging defaults.");

        profile.MapPut("/imaging-defaults", (ImagingDefaultsDto body, IProfileStore store) => {
                store.PutImagingDefaults(body);
                return Results.Ok(body);
            })
            .Accepts<ImagingDefaultsDto>("application/json")
            .Produces<ImagingDefaultsDto>(StatusCodes.Status200OK)
            .WithName("PutImagingDefaults")
            .WithSummary("Replace the active profile's imaging defaults.");

        return app;
    }
}
