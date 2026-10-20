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
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// §37/§30 multi-profile management — CRUD over the known-profiles set
/// (<see cref="IProfileRepository"/>). Distinct from the section-level
/// <c>/api/v1/profile</c> endpoints, which read/write the <em>active</em>
/// profile's settings.
/// </summary>
public static partial class ProfilesEndpoints {
    public static IEndpointRouteBuilder MapProfilesEndpoints(this IEndpointRouteBuilder app) {
        var profiles = app.MapGroup("/api/v1/profiles").WithTags("Profiles");

        profiles.MapGet("/", (IProfileRepository repo) => Results.Ok(repo.List()))
            .Produces<ProfileListDto>(StatusCodes.Status200OK)
            .WithName("ListProfiles")
            .WithSummary("List the known profiles and which one is active.");

        profiles.MapPost("/", (CreateProfileRequestDto body, bool? activate, IProfileRepository repo, IGuiderService guider) => {
                // Creating a profile activates it by default (the wizard's Save lands you in the new
                // profile); pass ?activate=false to add one without switching (e.g. duplicate-current).
                // With no settings in the body this is "duplicate the active profile" — snapshot the
                // source name BEFORE the create, which may switch the active profile.
                var source = body.Settings is null ? ActiveProfileName(repo) : null;
                var meta = repo.Create(body.Name, body.Settings, makeActive: activate ?? true);
                if (source is not null) {
                    // #1156 lifecycle hook — the duplicate's guider twin is cloned from the source's
                    // twin so calibration survives. Fire-and-forget, never throws (best-effort by
                    // contract); the 201 must not wait on a guider RPC.
                    _ = guider.TryCloneAraGuiderProfileAsync(source, meta.Name, CancellationToken.None);
                }
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

        profiles.MapPut("/{id:guid}", (Guid id, RenameProfileRequestDto body, IProfileRepository repo, IGuiderService guider) => {
                // Snapshot before the rename: the twin's name derives from the OLD name, and whether
                // another ARA profile still uses it (identically-named profiles share a twin, so the
                // twin must stay put for the survivor). Read-only; the repository owns atomicity.
                var before = repo.List().Profiles;
                var oldName = before.FirstOrDefault(p => p.Id == id)?.Name;
                // Case-insensitive + trimmed, the way the twin names are matched (AraGuiderDisplayProfileName
                // trims; the daemon's name check ignores case).
                var shared = oldName is not null && before.Count(p => string.Equals(p.Name?.Trim(), oldName.Trim(), StringComparison.OrdinalIgnoreCase)) > 1;
                if (!repo.Rename(id, body.Name)) {
                    return Results.NotFound();
                }
                if (oldName is not null && !shared) {
                    // #1156 lifecycle hook — carry the twin (and its calibration) over to the new
                    // name AS STORED (the repository normalizes: blank → "Untitled profile",
                    // over-long → truncated), so the twin's name is the one the next connect looks
                    // for. Fire-and-forget, never throws.
                    var storedName = repo.List().Profiles.FirstOrDefault(p => p.Id == id)?.Name ?? body.Name;
                    _ = guider.TryRenameAraGuiderProfileAsync(oldName, id, storedName, CancellationToken.None);
                }
                return Results.NoContent();
            })
            .Accepts<RenameProfileRequestDto>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("RenameProfile")
            .WithSummary("Rename a profile.");

        profiles.MapGet("/{id:guid}/guider-twin", async (Guid id, IProfileRepository repo, IGuiderService guider, CancellationToken ct) => {
                // §63.4 — what the delete dialog needs before it asks about the guider twin.
                var all = repo.List().Profiles;
                var me = all.FirstOrDefault(p => p.Id == id);
                if (me is null) {
                    return Results.NotFound();
                }
                var sharedWith = all.FirstOrDefault(p => p.Id != id && SameTwinName(p.Name, me.Name))?.Name;
                var status = await guider.DescribeAraGuiderTwinAsync(me.Name, id, ct).ConfigureAwait(false);
                return Results.Ok(new GuiderTwinPreviewDto(
                    TwinNames: new[] { PHD2Guider.AraGuiderDisplayProfileName(me.Name), PHD2Guider.AraGuiderProfileName(me.Name, id) },
                    Exists: status.Exists,
                    Shared: sharedWith is not null,
                    SharedWith: sharedWith,
                    SelectedOnDaemon: status.SelectedOnDaemon,
                    CanDeleteNow: status.CanDeleteNow,
                    Reason: status.Reason));
            })
            .Produces<GuiderTwinPreviewDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetProfileGuiderTwin")
            .WithSummary("Describe the profile's OpenAstro Guider profile (its twin) for the delete dialog: " +
                "whether it exists on the daemon, whether another profile shares it, and whether a delete " +
                "could run now or would be queued.");

        profiles.MapDelete("/{id:guid}", async (Guid id,
                [FromQuery(Name = "guider_profile")] string? guiderProfile,
                [FromQuery(Name = "dark_library")] string? darkLibrary,
                IProfileRepository repo, IGuiderService guider) => {
                // §63.4 — the guider twin goes only on an explicit ask (default keep): an older client
                // that sends no parameter never deletes guider data without the user being asked.
                bool deleteTwin;
                switch (guiderProfile) {
                    case null or "" or "keep": deleteTwin = false; break;
                    case "delete": deleteTwin = true; break;
                    default: return Results.Problem("guider_profile must be 'keep' or 'delete'", statusCode: StatusCodes.Status400BadRequest);
                }
                bool deleteDarks;
                switch (darkLibrary) {
                    case null or "" or "delete": deleteDarks = true; break;
                    case "keep": deleteDarks = false; break;
                    default: return Results.Problem("dark_library must be 'keep' or 'delete'", statusCode: StatusCodes.Status400BadRequest);
                }
                // Snapshot the name BEFORE the delete — the §63.4 twin's name derives from it, and the
                // record is gone once Delete succeeds. Read-only, so the repository's atomic
                // decide-under-lock (below) still owns TOCTOU.
                var name = repo.GetProfile(id)?.Meta.Name;
                // The repository decides atomically under its lock, so a concurrent select can't
                // slip between an existence pre-check and the delete (TOCTOU).
                var result = repo.Delete(id);
                if (result == ProfileDeleteResult.NotFound) {
                    return Results.NotFound();
                }
                if (result != ProfileDeleteResult.Deleted) {
                    return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
                }
                if (!deleteTwin) {
                    return Results.Ok(new ProfileDeleteResultDto("kept", null));
                }
                // The response reports what happened to the twin, so the delete is awaited — bounded,
                // and NOT by the request's own abort: a client that drops mid-way must not leave the
                // daemon's equipment released. Past the bound the service queues the delete itself.
                using var bound = new CancellationTokenSource(GuiderTwinDeleteBound);
                var outcome = await guider.DeleteAraGuiderProfileAsync(name, id, deleteDarks, bound.Token).ConfigureAwait(false);
                return Results.Ok(new ProfileDeleteResultDto(outcome.Status switch {
                    GuiderTwinDeleteStatus.Deleted => "deleted",
                    GuiderTwinDeleteStatus.Queued => "queued",
                    GuiderTwinDeleteStatus.NotFound => "not_found",
                    _ => "failed",
                }, outcome.Reason));
            })
            .Produces<ProfileDeleteResultDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("DeleteProfile")
            .WithSummary("Delete a profile. Deleting the active profile activates the newest " +
                "remaining one; deleting the last profile returns to the zero-profile state. " +
                "?guider_profile=delete also deletes its OpenAstro Guider profile (now, or queued " +
                "until the guider is connected and idle); ?dark_library=keep spares the dark library. " +
                "The body says what happened to the guider profile.");

        profiles.MapPost("/{id:guid}/select", (Guid id, IProfileRepository repo, IGuiderService guider,
                Microsoft.Extensions.Logging.ILoggerFactory loggerFactory) => {
                if (!repo.SelectProfile(id)) {
                    return Results.NotFound();
                }
                // §63.4 lifecycle hook — a CONNECTED guider follows the profile switch: the
                // push re-runs the twin-profile ensure first (select-or-create-or-migrate the
                // NEW profile's twin on the daemon, dark library and all), then lands the new
                // profile's guider settings. Fire-and-forget with a best-effort swallow: a
                // disconnected guider (InvalidOperationException) just means there is nothing
                // to flip — the next guider connect maps the twin anyway — and the select
                // response must not wait on daemon RPCs.
                _ = PushGuiderAfterProfileSwitchAsync(guider, loggerFactory.CreateLogger("ProfilesEndpoints"));
                return Results.Ok(repo.List());
            })
            .Produces<ProfileListDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("SelectProfile")
            .WithSummary("Make a profile active (loads its settings into the live store). A " +
                "connected guider follows: its twin profile is selected/created to match.");

        return app;
    }

    /// <summary>How long the delete response waits on the guider before the twin delete is queued instead.</summary>
    internal static readonly TimeSpan GuiderTwinDeleteBound = TimeSpan.FromSeconds(15);

    /// <summary>Two ARA profile names map to one guider twin when they match trimmed and case-insensitively
    /// (AraGuiderDisplayProfileName trims; the daemon's name check ignores case).</summary>
    private static bool SameTwinName(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? ActiveProfileName(IProfileRepository repo) {
        var list = repo.List();
        return list.ActiveId is Guid active ? list.Profiles.FirstOrDefault(p => p.Id == active)?.Name : null;
    }

    /// <summary>The §63.4 profile-switch follow: push the (new) active profile to a connected guider,
    /// best-effort. Every failure is LOGGED (review r1 — a silent unobserved-task swallow hid "the twin
    /// didn't follow" from operators): disconnected guider at Debug (expected — the next connect maps
    /// the twin), everything else at Warning.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fire-and-forget boundary: any escape would be an unobserved task exception with no log — the catch-all exists precisely to record unexpected faults.")]
    private static async Task PushGuiderAfterProfileSwitchAsync(IGuiderService guider, ILogger logger) {
        try {
            // refuseUnservedSelection: false — a pre-#1306 profile with a bare Alpaca selection still gets
            // its twin and tuning on a switch; only the explicit Apply refuses it.
            await guider.PushGuiderProfileAsync(idempotencyKey: null, CancellationToken.None, refuseUnservedSelection: false)
                .ConfigureAwait(false);
        } catch (InvalidOperationException) {
            // Guider not connected — nothing to flip; the §63.4 connect-path ensure maps the twin
            // on the next connect.
            LogProfileSwitchPushSkipped(logger);
        } catch (Exception ex) {
            // Daemon rejection, transport fault, shutdown race — best-effort by design, but the
            // operator must be able to see the twin did NOT follow the switch.
            LogProfileSwitchPushFailed(logger, ex);
        }
    }

    [LoggerMessage(EventId = 3710, Level = LogLevel.Debug,
        Message = "§63.4 profile-switch guider push skipped: guider not connected (twin maps on next connect).")]
    private static partial void LogProfileSwitchPushSkipped(ILogger logger);

    [LoggerMessage(EventId = 3711, Level = LogLevel.Warning,
        Message = "§63.4 profile-switch guider push failed — the daemon twin did not follow the ARA profile switch.")]
    private static partial void LogProfileSwitchPushFailed(ILogger logger, Exception ex);
}
