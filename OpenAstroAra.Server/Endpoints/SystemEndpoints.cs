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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// Phase 9 bug-report, data manager, backup, share endpoints per PORT_PLAYBOOK.md §10.9.
/// </summary>
public static class SystemEndpoints {

    private static IResult NotImplementedStub(string endpoint, string section) =>
        Results.Problem(
            type: "https://openastro.net/errors/not-implemented",
            title: "Endpoint not yet implemented",
            statusCode: StatusCodes.Status501NotImplemented,
            detail: $"{endpoint} is part of Phase 9's incremental implementation ({section}). Stub registered so the OpenAPI surface is stable; service wiring lands per area.");

    /// <summary>Parses the optional <c>?at</c> ISO-8601 query parameter shared by the planning
    /// endpoints. A null/absent value resolves to "now"; a present-but-unparseable value returns a 400
    /// <see cref="IResult"/> rather than silently falling back to now, so the caller can't confuse a bad
    /// timestamp with a real one. Returns null (with <paramref name="atUtc"/> set) on success.</summary>
    private static IResult? TryParseAt(string? at, out System.DateTimeOffset atUtc) {
        if (at is null) {
            atUtc = System.DateTimeOffset.UtcNow;
            return null;
        }
        if (System.DateTimeOffset.TryParse(at,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed)) {
            atUtc = parsed;
            return null;
        }
        atUtc = default;
        return Results.Problem("Query parameter 'at' must be an ISO-8601 date-time.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app) {
        // ─── Bug report (§54) ───
        var bug = app.MapGroup("/api/v1/bugreport").WithTags("BugReport");

        // Phase 13.9 — wired to IBugReportService (placeholder).
        bug.MapPost("/prepare",
                async ([FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IBugReportService svc, CancellationToken ct) =>
                    Results.Accepted(value: await svc.PrepareAsync(idempotencyKey, ct)))
           .Produces<BugReportPreparationDto>(StatusCodes.Status202Accepted)
           .WithName("PrepareBugReport");

        bug.MapGet("/download",
                async (Guid preparationId, string? acknowledge, IBugReportService svc, CancellationToken ct) => {
                    // §54 PII backstop: the bundle carries the daemon logs, the full profile.json
                    // (possible equipment credentials / API + notification tokens / precise
                    // observatory coordinates / network endpoints), and a system-info.json with the
                    // local filesystem path. The server refuses to serve it unless the caller passes
                    // ?acknowledge=pii — so a client that hasn't shown the user a disclosure literally
                    // can't download it (enforces the §54 disclosure rather than leaving it to honour).
                    if (!string.Equals(acknowledge, "pii", StringComparison.Ordinal)) {
                        return Results.Problem(
                            type: "https://openastro.net/errors/bugreport-pii-ack-required",
                            title: "Bug-report download requires PII acknowledgement",
                            statusCode: StatusCodes.Status403Forbidden,
                            detail: "The bug-report bundle contains the daemon logs, the full profile.json " +
                                "(which may include equipment credentials, API/notification tokens, precise " +
                                "observatory coordinates, and network endpoints), and a system-info.json with " +
                                "the local filesystem path. Show the user this disclosure, then re-request " +
                                "with ?acknowledge=pii to download.");
                    }
                    var result = await svc.OpenDownloadAsync(preparationId, ct);
                    if (result is null) return Results.NotFound();
                    return Results.Stream(result.Value.Stream, "application/zip", result.Value.FileName);
                })
            .Produces<byte[]>(StatusCodes.Status200OK, "application/zip")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("DownloadBugReport");

        // ─── Data Manager (§36.2) — Phase 13.10 wired to IDataManagerService ───
        var data = app.MapGroup("/api/v1/data-manager").WithTags("DataManager");

        data.MapGet("/packages",
                async (IDataManagerService svc, CancellationToken ct) =>
                    Results.Ok(await svc.ListPackagesAsync(ct)))
            .Produces<IReadOnlyList<DataPackageDto>>(StatusCodes.Status200OK)
            .WithName("ListDataPackages");

        data.MapPost("/download",
                async ([FromBody] DownloadRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IDataManagerService svc, CancellationToken ct) => {
                    try {
                        return Results.Accepted(value: await svc.DownloadAsync(request, idempotencyKey, ct));
                    } catch (PackageNotFoundException ex) {
                        // §36: PackageId isn't in the curated catalog. A dedicated type (not a bare
                        // KeyNotFoundException) means an unrelated dictionary miss inside the §36-2 engine
                        // can't be silently turned into a 404 here.
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
                    } catch (PackageAlreadyInstalledException ex) {
                        // §36: a non-force request for an already-installed package — it did NOT re-download.
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
                    }
                })
            .Accepts<DownloadRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("StartDataPackageDownload");

        data.MapPost("/cancel/{downloadId:guid}",
                async (Guid downloadId, IDataManagerService svc, CancellationToken ct) => {
                    try {
                        return Results.Accepted(value: await svc.CancelAsync(downloadId, ct));
                    } catch (DownloadNotFoundException ex) {
                        // §36-2: the download id isn't an in-flight job (unknown or already finished).
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
                    }
                })
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("CancelDataPackageDownload");

        data.MapDelete("/{packageId}",
                async (string packageId, IDataManagerService svc, CancellationToken ct) =>
                    await svc.DeleteAsync(packageId, ct) switch {
                        PackageDeleteResult.Deleted => Results.NoContent(),
                        // Locked files / permission denied: 409 tells the client "still there, retry
                        // later" — distinctly from 404's "already gone" (treating a locked dir as
                        // clear would strand the disk space with no way to notice).
                        PackageDeleteResult.Blocked => Results.Problem(
                            "The package's files are in use or protected — close anything using them and try again.",
                            statusCode: StatusCodes.Status409Conflict),
                        PackageDeleteResult.NotInstalled => Results.NotFound(),
                        // No catch-all 404: a new enum member added without updating this map should
                        // fail loudly, not silently read as "already gone" (Debayer.CellOffsets precedent).
                        var other => throw new System.Diagnostics.UnreachableException(
                            $"Unhandled PackageDeleteResult: {other}"),
                    })
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("DeleteDataPackage");

        data.MapGet("/state",
                async (IDataManagerService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetStateAsync(ct)))
            .Produces<DataManagerStateDto>(StatusCodes.Status200OK)
            .WithName("GetDataManagerState");

        // §36 — serve an installed catalog's objects (normalized {name, ra°, dec°, mag}) for the Sky Atlas overlay.
        // 404 when the package isn't a known catalog, isn't installed, or has no catalog.csv. Optional ?max_mag= drops
        // fainter objects (e.g. naked-eye stars only) and ?limit= caps the count, keeping the overlay payload sane.
        // NOTE: ?max_mag= also drops objects with NO recorded magnitude (e.g. OpenNGC DSOs carrying neither V- nor
        // B-Mag), since they can't be compared to the threshold — omit the filter to include those.
        data.MapGet("/{packageId}/catalog",
                async (string packageId, [FromQuery(Name = "max_mag")] double? maxMag, [FromQuery] int? limit,
                        IDataManagerService svc, CancellationToken ct) => {
                    if (limit is < 0) {
                        // limit=0 is intentionally valid — it's a well-defined "max 0 rows" request that returns an
                        // empty list (like a paging limit); only a negative count is a malformed request.
                        return Results.Problem(detail: "limit must be >= 0", statusCode: StatusCodes.Status400BadRequest);
                    }
                    if (maxMag is { } mm && !double.IsFinite(mm)) {
                        // NaN would make `mag > maxMag` always false (every magnitude row passes); ±∞ is meaningless here.
                        return Results.Problem(detail: "max_mag must be a finite number", statusCode: StatusCodes.Status400BadRequest);
                    }
                    var objects = await svc.ReadCatalogAsync(packageId, maxMag, limit, ct);
                    return objects is null ? Results.NotFound() : Results.Ok(objects);
                })
            .Produces<IReadOnlyList<CatalogObjectDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetDataManagerCatalog");

        // ─── Catalogs (§36) — toggleable overlay catalogs/type-filters derived from OpenNGC ───
        var catalogs = app.MapGroup("/api/v1/catalogs").WithTags("Catalogs");
        // List the catalogs that have data (empty until openngc-dso is installed).
        catalogs.MapGet("",
                (ISkyCatalogService svc) => Results.Ok(svc.List()))
            .Produces<IReadOnlyList<CatalogInfoDto>>(StatusCodes.Status200OK)
            .WithName("ListCatalogs");
        // A single catalog's objects ({name, ra°, dec°, mag}) for the planetarium overlay. 404 when the id is
        // unknown or its source catalog isn't installed; ?limit= caps the overlay payload. With no ?limit= we
        // apply a brightest-first default cap so a large type-filter (e.g. ~8k galaxies, ~500 KB JSON) can't
        // spike the server for a caller that forgets one; explicit callers still get exactly what they ask for.
        const int defaultCatalogLimit = 500;
        catalogs.MapGet("/{catalogId}",
                (string catalogId, [FromQuery] int? limit, ISkyCatalogService svc, CancellationToken ct) => {
                    // limit == 0 is intentionally valid — it returns an empty 200, matching the
                    // /data-manager/{id}/catalog route's documented contract. Only a negative limit is a 400.
                    if (limit is < 0) {
                        return Results.Problem("limit must be >= 0", statusCode: StatusCodes.Status400BadRequest);
                    }
                    var objects = svc.GetObjects(catalogId, limit ?? defaultCatalogLimit, ct);
                    return objects is null ? Results.NotFound() : Results.Ok(objects);
                })
            .Produces<IReadOnlyList<CatalogObjectDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetCatalog");

        // ─── Planning (§36/§25.5) — Tonight's Sky ───
        var planning = app.MapGroup("/api/v1/planning").WithTags("Planning");
        planning.MapGet("/tonight",
                (ITonightSkyService svc, IProfileStore profiles, [FromQuery] int? limit,
                        [FromQuery(Name = "at")] string? at,
                        [FromQuery] double? focalLengthMm, [FromQuery] double? reducer,
                        [FromQuery] int? sensorW, [FromQuery] int? sensorH, [FromQuery] double? pixelUm,
                        [FromQuery] int? mosaicX, [FromQuery] int? mosaicY) => {
                    if (TryParseAt(at, out var atUtc) is { } atError) {
                        return atError;
                    }
                    // The service treats limit <= 0 as "return all" internally; don't expose that through the
                    // endpoint (the spec says limit >= 1) — a client passing 0 is a request error.
                    if (limit is < 1) {
                        return Results.Problem("Query parameter 'limit' must be >= 1.",
                            statusCode: StatusCodes.Status400BadRequest);
                    }

                    // Mosaic tiles cap at 20 per axis — the FOV (and so the per-object framing scan cost)
                    // scales with the tile count, so an unbounded value would be a cheap way to blow it up.
                    if (mosaicX is < 1 or > 20) {
                        return Results.Problem("Query parameter 'mosaicX' must be in [1, 20].",
                            statusCode: StatusCodes.Status400BadRequest);
                    }
                    if (mosaicY is < 1 or > 20) {
                        return Results.Problem("Query parameter 'mosaicY' must be in [1, 20].",
                            statusCode: StatusCodes.Status400BadRequest);
                    }

                    // Build an optics override only when AT LEAST ONE optic field is supplied; the
                    // un-supplied fields are sourced per-field from the active profile so a caller can tweak
                    // just (say) the reducer. When NO optic field is supplied we pass null and the service
                    // uses the profile directly — no profile read on the common path. TonightSkyOverrides.Build
                    // validates every supplied field (finite, positive, reducer ≤ cap) AND the assembled
                    // train (so a field merged onto an un/partly-configured profile is a 400, not a silent
                    // 200); the profile read is lazy (skipped when all five fields are supplied).
                    OpticsSettingsDto? opticsOverride = null;
                    if (focalLengthMm is not null || reducer is not null || sensorW is not null
                            || sensorH is not null || pixelUm is not null) {
                        var (built, error) = TonightSkyOverrides.Build(
                            profiles.GetOpticsSettings,
                            focalLengthMm, reducer, sensorW, sensorH, pixelUm);
                        if (error is not null) {
                            return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);
                        }
                        opticsOverride = built;
                    }

                    return Results.Ok(svc.GetTonight(atUtc, limit ?? 10, opticsOverride,
                        mosaicX ?? 1, mosaicY ?? 1));
                })
            .Produces<IReadOnlyList<TonightSkyObjectDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("GetTonightSky")
            .WithDescription("Optional 'at' is an ISO-8601 instant; include a 'Z' or offset — a " +
                "timezone-naive value is interpreted as UTC. Absent → now. Optional optical-train overrides " +
                "(focalLengthMm, reducer, sensorW, sensorH, pixelUm) each replace one field of the active " +
                "profile's optics for this request (each supplied value must be finite and > 0; reducer ≤ " +
                "10; unsupplied fields fall back to the profile, and the assembled train must be complete — " +
                "supplying one field on an unconfigured profile is rejected); mosaicX/mosaicY (default 1, " +
                "range [1, 20]) enlarge the framing FOV by that tile count per axis. Absent overrides → the " +
                "profile's optics at 1×1.");

        // NEXTGEN §2/§3 — the Optimal-Sub calculator: the Glover read-noise floor (t = C(ε)·R²/P,
        // criterion popularised by Dr. Robin Glover of SharpCap, used with permission) intersected
        // with the sky-background saturation ceiling. Every parameter is optional; unsupplied
        // fields merge from the active profile (camera-electronics / optics / site Bortle /
        // filter-set) and finally from Tier-0 generic-CMOS defaults, with every defaulted field
        // named in the response's assumed_defaults for transparency.
        planning.MapGet("/optimal-sub",
                (IProfileStore profiles,
                        [FromQuery] string? filter, [FromQuery] double? bandwidthNm,
                        [FromQuery] double? readNoise, [FromQuery] double? fullWell,
                        [FromQuery] double? ePerAdu, [FromQuery] int? gain, [FromQuery] double? qe,
                        [FromQuery] double? apertureMm, [FromQuery] double? focalLengthMm,
                        [FromQuery] double? reducer, [FromQuery] double? pixelUm,
                        [FromQuery] double? skyMag, [FromQuery] int? bortle,
                        [FromQuery] double? noiseTolerancePct, [FromQuery] double? headroom) => {
                    var (input, assumedDefaults, error) = OptimalSubOverrides.Build(
                        profiles.GetOpticsSettings, profiles.GetCameraElectronics,
                        profiles.GetSiteSettings, profiles.GetFilterSet,
                        filter, bandwidthNm,
                        readNoise, fullWell, ePerAdu, gain, qe,
                        apertureMm, focalLengthMm, reducer, pixelUm,
                        skyMag, bortle, noiseTolerancePct, headroom);
                    if (error is not null) {
                        return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);
                    }
                    var result = OptimalSubCalculator.Compute(input!);
                    return Results.Ok(result with {
                        AssumedDefaults = assumedDefaults.Count > 0 ? assumedDefaults : null,
                    });
                })
            .Produces<OptimalSubResultDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("GetOptimalSub")
            .WithDescription("Optimal sub-exposure window: the read-noise floor (criterion popularised " +
                "by Dr. Robin Glover, SharpCap — 'noiseTolerancePct' is the knob, default 5% ≈ the " +
                "popular t = 10·R²/P) intersected with the sky-background saturation ceiling " +
                "('headroom' × effective full well, default 0.8). Filter passband: 'filter' (a planning " +
                "filter-set name) XOR 'bandwidthNm'; absent → the 100 nm effective-broadband default. " +
                "Sky: 'skyMag' (mag/arcsec²) XOR 'bortle' (1..9); absent → the profile site's Bortle. " +
                "Electronics ('readNoise', 'fullWell', 'ePerAdu', 'gain', 'qe') fall back to the " +
                "profile's camera-electronics section, then to generic-CMOS defaults; geometry " +
                "('apertureMm', 'focalLengthMm', 'reducer', 'pixelUm') falls back to the profile's " +
                "optics and has no further default — an incomplete train is a 400. Fields that landed " +
                "on a generic default are named in the response's assumed_defaults.");

        planning.MapGet("/horizon",
                (IHorizonService svc, [FromQuery(Name = "at")] string? at) => {
                    if (TryParseAt(at, out var atUtc) is { } atError) {
                        return atError;
                    }
                    return Results.Ok(svc.GetHorizon(atUtc));
                })
            .Produces<HorizonDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("GetPlanningHorizon")
            .WithDescription("Optional 'at' is an ISO-8601 instant; include a 'Z' or offset — a " +
                "timezone-naive value is interpreted as UTC. Absent → now.");

        // ─── Backup (§43) — Phase 13.11 wired to IBackupService ───
        var backup = app.MapGroup("/api/v1/backup").WithTags("Backup");

        backup.MapPost("/create-zip",
                async ([FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IBackupService svc, CancellationToken ct) => {
                    try {
                        return Results.Accepted(value: await svc.CreateZipAsync(idempotencyKey, ct));
                    } catch (BackupNothingToArchiveException ex) {
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
                    }
                })
              .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
              .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
              .WithName("CreateBackupZip");

        backup.MapPost("/restore-zip",
                async ([FromBody] RestoreRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IBackupService svc, CancellationToken ct) => {
                    try {
                        return Results.Accepted(value: await svc.RestoreZipAsync(request, idempotencyKey, ct));
                    } catch (BackupSnapshotNotFoundException ex) {
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
                    } catch (BackupRestoreSourceUnsupportedException ex) {
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
                    } catch (BackupRestoreNoAreaSelectedException ex) {
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
                    } catch (BackupCorruptException ex) {
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
                    } catch (BackupRestoreInProgressException ex) {
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
                    }
                })
            .Accepts<RestoreRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("RestoreBackupZip");

        backup.MapGet("/snapshots",
                async (IBackupService svc, CancellationToken ct) =>
                    Results.Ok(await svc.ListSnapshotsAsync(ct)))
              .Produces<IReadOnlyList<BackupZipDto>>(StatusCodes.Status200OK)
              .WithName("ListBackupSnapshots");

        backup.MapGet("/clone-status",
                async (IBackupService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetCloneStatusAsync(ct)))
              .Produces<System.Text.Json.JsonElement>(StatusCodes.Status200OK)
              .WithName("GetBackupCloneStatus");

        backup.MapGet("/snapshot/{id:guid}/download",
                async (Guid id, IBackupService svc, CancellationToken ct) => {
                    var snapshot = await svc.OpenSnapshotAsync(id, ct);
                    return snapshot is null
                        ? Results.NotFound()
                        : Results.File(snapshot.Value.Stream, "application/zip", snapshot.Value.FileName);
                })
              .Produces(StatusCodes.Status200OK, contentType: "application/zip")
              .ProducesProblem(StatusCodes.Status404NotFound)
              .WithName("DownloadBackupSnapshot");

        // ─── Profile sharing (§70) — Phase 13.10 wired to IProfileShareService ───
        var profiles = app.MapGroup("/api/v1/profiles").WithTags("ProfileShare");

        profiles.MapPost("/{id:guid}/share-export",
                async (Guid id, IProfileShareService svc, CancellationToken ct) => {
                    var share = await svc.ExportAsync(id, ct);
                    return share is null ? Results.NotFound() : Results.Ok(share);
                })
            .Produces<ProfileShareDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ExportProfileShare");

        // §70.4 import — preview parses + validates the uploaded profile-share-v1
        // file and returns a short-lived token; commit creates the profile from it.
        profiles.MapPost("/share-import",
                async ([FromBody] System.Text.Json.JsonElement manifest, IProfileShareService svc, HttpResponse response, CancellationToken ct) => {
                    try {
                        return Results.Ok(await svc.ImportPreviewAsync(manifest, ct));
                    } catch (InvalidProfileShareException) {
                        // Not a recognized share file (bad JSON / wrong schema / no settings).
                        // Fixed wire detail — the exception message stays internal so it
                        // isn't pinned into the public API contract.
                        return Results.Problem(
                            detail: "The uploaded file is not a recognized profile share (expected a profile-share-v1 file).",
                            statusCode: StatusCodes.Status422UnprocessableEntity);
                    } catch (ProfileShareImportThrottledException) {
                        // Pending-import cap hit — ask the caller to back off. RFC 6585: a
                        // slot is guaranteed free within the 15-min preview TTL, so advertise
                        // that as the Retry-After upper bound (seconds).
                        response.Headers.RetryAfter = "900";
                        return Results.Problem(
                            detail: "Too many pending profile-share imports — commit or wait for one to expire, then retry.",
                            statusCode: StatusCodes.Status429TooManyRequests);
                    }
                })
            // Document the body as the typed manifest so the OpenAPI spec surfaces the
            // expected profile-share-v1 shape, even though the handler binds a raw
            // JsonElement to keep validation tolerant (garbled input → 422, not 400).
            .Accepts<ProfileShareManifest>("application/json")
            .Produces<ProfileShareImportPreviewDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithName("PreviewProfileShareImport");

        // Token travels in the request body (not the query string) so it never lands
        // in web-server / proxy access logs — it authorizes profile creation in-TTL.
        profiles.MapPost("/share-import/commit",
                async ([FromBody] ProfileShareImportCommitRequest req, IProfileShareService svc, CancellationToken ct) => {
                    try {
                        var newProfileId = await svc.ImportCommitAsync(req.ImportToken, ct);
                        return Results.Created($"/api/v1/profiles/{newProfileId}", value: newProfileId);
                    } catch (ProfileShareImportTokenException) {
                        // Unknown / expired / already-committed token. Fixed wire detail
                        // (exception message stays internal, off the public API contract).
                        return Results.Problem(
                            detail: "Import token is unknown or expired — preview the share file again.",
                            statusCode: StatusCodes.Status404NotFound);
                    }
                })
            .Accepts<ProfileShareImportCommitRequest>("application/json")
            .Produces<Guid>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("CommitProfileShareImport");

        // Phase 13.16 — sky-data-recommendations wired to IDataManagerService.
        // Recommends the not-installed packages from the catalog; real impl
        // ranks them by relevance to the profile's site latitude + target
        // catalog. Placeholder returns the not-installed entries directly.
        profiles.MapGet("/{id:guid}/sky-data-recommendations",
                async (Guid id, IDataManagerService svc, CancellationToken ct) => {
                    var packages = await svc.ListPackagesAsync(ct);
                    var recommended = packages.Where(p => !p.IsInstalled).ToList();
                    return Results.Ok((IReadOnlyList<DataPackageDto>)recommended);
                })
            .Produces<IReadOnlyList<DataPackageDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetProfileSkyDataRecommendations");

        return app;
    }
}