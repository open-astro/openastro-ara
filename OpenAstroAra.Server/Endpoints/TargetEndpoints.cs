#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Mvc;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>#1267 — target search: one name to one J2000 position, offline index first, then Simbad through
/// the daemon (the client has no internet dependency; the answer is cached for the dark site).</summary>
public static class TargetEndpoints {

    /// <summary>A name that is not a name: empty, longer than Sesame's sensible bound, or carrying control
    /// characters (it goes into a URL and a log line).</summary>
    internal static string? ValidateName(string? name) {
        var q = (name ?? string.Empty).Trim();
        if (q.Length == 0) {
            return "name is required";
        }
        if (q.Length > SesameClient.MaxNameLength) {
            return $"name must be at most {SesameClient.MaxNameLength} characters";
        }
        foreach (var c in q) {
            if (char.IsControl(c)) {
                return "name must not contain control characters";
            }
        }
        return null;
    }

    public static IEndpointRouteBuilder MapTargetEndpoints(this IEndpointRouteBuilder app) {
        var targets = app.MapGroup("/api/v1/targets").WithTags("Targets");
        targets.MapGet("/resolve",
                async ([FromQuery] string? name, ITargetResolver resolver, CancellationToken ct) => {
                    if (ValidateName(name) is { } problem) {
                        return Results.Problem(detail: problem, statusCode: StatusCodes.Status400BadRequest);
                    }
                    return Results.Ok(await resolver.ResolveAsync(name!, ct));
                })
            .Produces<TargetResolveResponseDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("ResolveTarget")
            .WithSummary("Resolve a target name (any identifier: AB Cas, HIP 12235, M 31, Wolf 359) to a J2000 position: the offline index first, then the cached or live Simbad answer; status says resolved / not_found / offline.");
        return app;
    }
}
