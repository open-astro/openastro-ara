#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// §37/§30 multi-profile management — CRUD over the known-profiles set
/// (<see cref="IProfileRepository"/>). Distinct from the section-level
/// <c>/api/v1/profile</c> endpoints, which read/write the <em>active</em>
/// profile's settings.
/// </summary>
public static class ProfilesEndpoints {
    public static IEndpointRouteBuilder MapProfilesEndpoints(this IEndpointRouteBuilder app) {
        var profiles = app.MapGroup("/api/v1/profiles").WithTags("Profiles");

        profiles.MapGet("/", (IProfileRepository repo) => Results.Ok(repo.List()))
            .Produces<ProfileListDto>(StatusCodes.Status200OK)
            .WithName("ListProfiles")
            .WithSummary("List the known profiles and which one is active.");

        profiles.MapPost("/", (CreateProfileRequestDto body, bool? activate, IProfileRepository repo) => {
                // Creating a profile activates it by default (the wizard's Save lands you in the new
                // profile); pass ?activate=false to add one without switching (e.g. duplicate-current).
                var meta = repo.Create(body.Name, body.Settings, makeActive: activate ?? true);
                return Results.Created($"/api/v1/profiles/{meta.Id}", meta);
            })
            .Accepts<CreateProfileRequestDto>("application/json")
            .Produces<ProfileMetaDto>(StatusCodes.Status201Created)
            .WithName("CreateProfile")
            .WithSummary("Create a profile (clones the active profile's settings when none are supplied).");

        profiles.MapGet("/{id:guid}", (Guid id, IProfileRepository repo) => {
                var stored = repo.GetProfile(id);
                return stored is null ? Results.NotFound() : Results.Ok(stored);
            })
            .Produces<StoredProfileDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetProfile")
            .WithSummary("Get a saved profile's full settings.");

        profiles.MapPut("/{id:guid}", (Guid id, RenameProfileRequestDto body, IProfileRepository repo) =>
                repo.Rename(id, body.Name) ? Results.NoContent() : Results.NotFound())
            .Accepts<RenameProfileRequestDto>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("RenameProfile")
            .WithSummary("Rename a profile.");

        profiles.MapDelete("/{id:guid}", (Guid id, IProfileRepository repo) =>
                // The repository decides atomically under its lock, so a concurrent select
                // can't slip between an existence pre-check and the delete (TOCTOU).
                repo.Delete(id) switch {
                    ProfileDeleteResult.Deleted => Results.NoContent(),
                    ProfileDeleteResult.NotFound => Results.NotFound(),
                    ProfileDeleteResult.RefusedActive => Results.Problem(
                        detail: "Cannot delete the active profile — select another profile first.",
                        statusCode: StatusCodes.Status409Conflict),
                    ProfileDeleteResult.RefusedLastRemaining => Results.Problem(
                        detail: "Cannot delete the last remaining profile.",
                        statusCode: StatusCodes.Status409Conflict),
                    _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
                })
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("DeleteProfile")
            .WithSummary("Delete a profile (refused for the active or the last remaining profile).");

        profiles.MapPost("/{id:guid}/select", (Guid id, IProfileRepository repo) =>
                repo.SelectProfile(id) ? Results.Ok(repo.List()) : Results.NotFound())
            .Produces<ProfileListDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("SelectProfile")
            .WithSummary("Make a profile active (loads its settings into the live store).");

        return app;
    }
}
