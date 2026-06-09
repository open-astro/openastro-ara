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

        profile.MapGet("/storage", (IProfileStore store) =>
                Results.Ok(store.GetStorageSettings()))
            .Produces<StorageSettingsDto>(StatusCodes.Status200OK)
            .WithName("GetStorageSettings")
            .WithSummary("Get the active profile's storage settings.");

        profile.MapPut("/storage", (StorageSettingsDto body, IProfileStore store) => {
            store.PutStorageSettings(body);
            return Results.Ok(body);
        })
            .Accepts<StorageSettingsDto>("application/json")
            .Produces<StorageSettingsDto>(StatusCodes.Status200OK)
            .WithName("PutStorageSettings")
            .WithSummary("Replace the active profile's storage settings.");

        profile.MapGet("/notifications", (IProfileStore store) =>
                Results.Ok(store.GetNotificationsSettings()))
            .Produces<NotificationsSettingsDto>(StatusCodes.Status200OK)
            .WithName("GetNotificationsSettings")
            .WithSummary("Get the active profile's notifications settings.");

        profile.MapPut("/notifications", (NotificationsSettingsDto body, IProfileStore store) => {
            store.PutNotificationsSettings(body);
            return Results.Ok(body);
        })
            .Accepts<NotificationsSettingsDto>("application/json")
            .Produces<NotificationsSettingsDto>(StatusCodes.Status200OK)
            .WithName("PutNotificationsSettings")
            .WithSummary("Replace the active profile's notifications settings.");

        profile.MapGet("/site", (IProfileStore store) =>
                Results.Ok(store.GetSiteSettings()))
            .Produces<SiteSettingsDto>(StatusCodes.Status200OK)
            .WithName("GetSiteSettings")
            .WithSummary("Get the active profile's site preferences.");

        profile.MapPut("/site", (SiteSettingsDto body, IProfileStore store) => {
            store.PutSiteSettings(body);
            return Results.Ok(body);
        })
            .Accepts<SiteSettingsDto>("application/json")
            .Produces<SiteSettingsDto>(StatusCodes.Status200OK)
            .WithName("PutSiteSettings")
            .WithSummary("Replace the active profile's site preferences.");

        profile.MapGet("/filenames", (IProfileStore store) =>
                Results.Ok(store.GetFilenamesSettings()))
            .Produces<FilenamesSettingsDto>(StatusCodes.Status200OK)
            .WithName("GetFilenamesSettings")
            .WithSummary("Get the active profile's filenames settings.");

        profile.MapPut("/filenames", (FilenamesSettingsDto body, IProfileStore store) => {
            store.PutFilenamesSettings(body);
            return Results.Ok(body);
        })
            .Accepts<FilenamesSettingsDto>("application/json")
            .Produces<FilenamesSettingsDto>(StatusCodes.Status200OK)
            .WithName("PutFilenamesSettings")
            .WithSummary("Replace the active profile's filenames settings.");

        profile.MapGet("/safety", (IProfileStore store) =>
                Results.Ok(store.GetSafetyPolicies()))
            .Produces<SafetyPoliciesDto>(StatusCodes.Status200OK)
            .WithName("GetSafetyPolicies")
            .WithSummary("Get the active profile's safety policies.");

        profile.MapPut("/safety", (SafetyPoliciesDto body, IProfileStore store) => {
            store.PutSafetyPolicies(body);
            return Results.Ok(body);
        })
            .Accepts<SafetyPoliciesDto>("application/json")
            .Produces<SafetyPoliciesDto>(StatusCodes.Status200OK)
            .WithName("PutSafetyPolicies")
            .WithSummary("Replace the active profile's safety policies.");

        profile.MapGet("/autofocus", (IProfileStore store) =>
                Results.Ok(store.GetAutofocusSettings()))
            .Produces<AutofocusSettingsDto>(StatusCodes.Status200OK)
            .WithName("GetAutofocusSettings")
            .WithSummary("Get the active profile's autofocus settings.");

        profile.MapPut("/autofocus", (AutofocusSettingsDto body, IProfileStore store) => {
            store.PutAutofocusSettings(body);
            return Results.Ok(body);
        })
            .Accepts<AutofocusSettingsDto>("application/json")
            .Produces<AutofocusSettingsDto>(StatusCodes.Status200OK)
            .WithName("PutAutofocusSettings")
            .WithSummary("Replace the active profile's autofocus settings.");

        profile.MapGet("/plate-solve", (IProfileStore store) =>
                Results.Ok(store.GetPlateSolveSettings()))
            .Produces<PlateSolveSettingsDto>(StatusCodes.Status200OK)
            .WithName("GetPlateSolveSettings")
            .WithSummary("Get the active profile's plate-solve settings.");

        profile.MapPut("/plate-solve", (PlateSolveSettingsDto body, IProfileStore store) => {
            store.PutPlateSolveSettings(body);
            return Results.Ok(body);
        })
            .Accepts<PlateSolveSettingsDto>("application/json")
            .Produces<PlateSolveSettingsDto>(StatusCodes.Status200OK)
            .WithName("PutPlateSolveSettings")
            .WithSummary("Replace the active profile's plate-solve settings.");

        profile.MapGet("/diagnostics-mode", (IProfileStore store) =>
                Results.Ok(store.GetDiagnosticsMode()))
            .Produces<DiagnosticsModeDto>(StatusCodes.Status200OK)
            .WithName("GetDiagnosticsMode")
            .WithSummary("Get the active profile's diagnostics mode.");

        profile.MapPut("/diagnostics-mode", (DiagnosticsModeDto body, IProfileStore store) => {
            store.PutDiagnosticsMode(body);
            return Results.Ok(body);
        })
            .Accepts<DiagnosticsModeDto>("application/json")
            .Produces<DiagnosticsModeDto>(StatusCodes.Status200OK)
            .WithName("PutDiagnosticsMode")
            .WithSummary("Replace the active profile's diagnostics mode.");

        profile.MapGet("/phd2", (IProfileStore store) =>
                Results.Ok(store.GetPhd2Settings()))
            .Produces<Phd2SettingsDto>(StatusCodes.Status200OK)
            .WithName("GetPhd2Settings")
            .WithSummary("Get the active profile's PHD2 settings.");

        profile.MapPut("/phd2", (Phd2SettingsDto body, IProfileStore store) => {
            store.PutPhd2Settings(body);
            return Results.Ok(body);
        })
            .Accepts<Phd2SettingsDto>("application/json")
            .Produces<Phd2SettingsDto>(StatusCodes.Status200OK)
            .WithName("PutPhd2Settings")
            .WithSummary("Replace the active profile's PHD2 settings.");

        profile.MapGet("/equipment-connection", (IProfileStore store) =>
                Results.Ok(store.GetEquipmentConnection()))
            .Produces<EquipmentConnectionDto>(StatusCodes.Status200OK)
            .WithName("GetEquipmentConnection")
            .WithSummary("Get the active profile's equipment auto-connect bools.");

        profile.MapPut("/equipment-connection", (EquipmentConnectionDto body, IProfileStore store) => {
            store.PutEquipmentConnection(body);
            return Results.Ok(body);
        })
            .Accepts<EquipmentConnectionDto>("application/json")
            .Produces<EquipmentConnectionDto>(StatusCodes.Status200OK)
            .WithName("PutEquipmentConnection")
            .WithSummary("Replace the active profile's equipment auto-connect bools.");

        // §65.2 stretch defaults — controls the §40.5 frame-viewer's
        // default palette for Light frames + manual-slider seed values.
        // Calibration frames (Dark/Bias/Flat) always render linear and
        // ignore this setting per §65.2.
        profile.MapGet("/stretch-defaults", (IProfileStore store) =>
                Results.Ok(store.GetStretchDefaults()))
            .Produces<StretchDefaultsDto>(StatusCodes.Status200OK)
            .WithName("GetStretchDefaults")
            .WithSummary("Get the active profile's stretch defaults (§65.2).");

        profile.MapPut("/stretch-defaults", (StretchDefaultsDto body, IProfileStore store) => {
            store.PutStretchDefaults(body);
            return Results.Ok(body);
        })
            .Accepts<StretchDefaultsDto>("application/json")
            .Produces<StretchDefaultsDto>(StatusCodes.Status200OK)
            .WithName("PutStretchDefaults")
            .WithSummary("Replace the active profile's stretch defaults.");

        return app;
    }
}