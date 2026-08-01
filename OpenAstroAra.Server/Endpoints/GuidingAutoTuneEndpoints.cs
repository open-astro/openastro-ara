#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services.Guiding;
using System;
using System.Threading;

namespace OpenAstroAra.Server.Endpoints;

public static class GuidingAutoTuneEndpoints {
    public static IEndpointRouteBuilder MapGuidingAutoTuneEndpoints(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/v1/guiding/autotune").WithTags("Guiding Auto-Tune");
        group.MapGet("/capabilities", (IGuidingAutoTuneService service) => Results.Ok(service.GetCapabilities()))
            .Produces<GuidingAutoTuneCapabilitiesDto>(StatusCodes.Status200OK)
            .WithName("GetGuidingAutoTuneCapabilities");
        group.MapGet("/sessions/latest", (IGuidingAutoTuneService service) => Results.Ok(service.GetStatus()))
            .Produces<GuidingAutoTuneStatusDto>(StatusCodes.Status200OK)
            .WithName("GetLatestGuidingAutoTuneSession");
        group.MapGet("/sessions/latest/report", (IGuidingAutoTuneService service) => Results.Ok(service.GetReport()))
            .Produces<GuidingAutoTuneReportDto>(StatusCodes.Status200OK)
            .WithName("GetGuidingAutoTuneReport");
        group.MapGet("/sessions/{sessionId:guid}", (Guid sessionId, IGuidingAutoTuneService service) => {
                try { return Results.Ok(service.GetStatus(sessionId)); }
                catch (InvalidOperationException error) { return Results.NotFound(new { error = error.Message }); }
            })
            .Produces<GuidingAutoTuneStatusDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("GetGuidingAutoTuneSession");
        group.MapGet("/sessions/{sessionId:guid}/report", (Guid sessionId, IGuidingAutoTuneService service) => {
                try { return Results.Ok(service.GetReport(sessionId)); }
                catch (InvalidOperationException error) { return Results.NotFound(new { error = error.Message }); }
            })
            .Produces<GuidingAutoTuneReportDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("GetGuidingAutoTuneSessionReport");
        group.MapPost("/sessions", async (GuidingAutoTuneStartRequestDto request, IGuidingAutoTuneService service, CancellationToken ct) => {
                try { return Results.Ok(await service.StartAsync(request, ct)); }
                catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
                catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
            })
            .Produces<GuidingAutoTuneStatusDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("StartGuidingAutoTune");
        group.MapPost("/sessions/latest/cancel", async (IGuidingAutoTuneService service, CancellationToken ct) => {
                try { return Results.Ok(await service.CancelAsync(ct)); }
                catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
            })
            .Produces<GuidingAutoTuneStatusDto>(StatusCodes.Status200OK)
            .WithName("CancelGuidingAutoTune");
        group.MapPost("/sessions/latest/apply", async (IGuidingAutoTuneService service, CancellationToken ct) => {
                try { return Results.Ok(await service.ApplyAsync(ct)); }
                catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
            })
            .Produces<GuidingAutoTuneStatusDto>(StatusCodes.Status200OK)
            .WithName("ApplyGuidingAutoTune");
        group.MapPost("/sessions/latest/rollback", async (IGuidingAutoTuneService service, CancellationToken ct) => {
                try { return Results.Ok(await service.RollbackAsync(ct)); }
                catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
            })
            .Produces<GuidingAutoTuneStatusDto>(StatusCodes.Status200OK)
            .WithName("RollbackGuidingAutoTune");
        group.MapPost("/sessions/{sessionId:guid}/cancel", async (Guid sessionId, IGuidingAutoTuneService service, CancellationToken ct) => {
                try { return Results.Ok(await service.CancelAsync(sessionId, ct)); }
                catch (InvalidOperationException error) when (error.Message.Contains("not current", StringComparison.OrdinalIgnoreCase))
                    { return Results.NotFound(new { error = error.Message }); }
                catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
            })
            .Produces<GuidingAutoTuneStatusDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("CancelGuidingAutoTuneSession");
        group.MapPost("/sessions/{sessionId:guid}/apply", async (Guid sessionId, IGuidingAutoTuneService service, CancellationToken ct) => {
                try { return Results.Ok(await service.ApplyAsync(sessionId, ct)); }
                catch (InvalidOperationException error) when (error.Message.Contains("not current", StringComparison.OrdinalIgnoreCase))
                    { return Results.NotFound(new { error = error.Message }); }
                catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
            })
            .Produces<GuidingAutoTuneStatusDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("ApplyGuidingAutoTuneSession");
        group.MapPost("/sessions/{sessionId:guid}/rollback", async (Guid sessionId, IGuidingAutoTuneService service, CancellationToken ct) => {
                try { return Results.Ok(await service.RollbackAsync(sessionId, ct)); }
                catch (InvalidOperationException error) when (error.Message.Contains("not current", StringComparison.OrdinalIgnoreCase))
                    { return Results.NotFound(new { error = error.Message }); }
                catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
            })
            .Produces<GuidingAutoTuneStatusDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("RollbackGuidingAutoTuneSession");
        return app;
    }
}
