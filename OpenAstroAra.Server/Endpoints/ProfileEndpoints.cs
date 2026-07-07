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
            // §29 — reject an invalid disk-space threshold pair at write so the stored profile always matches
            // what the monitor enforces (no silent fallback to defaults while the UI shows the bad numbers).
            if (body.MinFreeDiskCriticalGb < 1 || body.MinFreeDiskWarnGb <= body.MinFreeDiskCriticalGb) {
                return Results.Problem(
                    detail: "min_free_disk_critical_gb must be >= 1 and strictly below min_free_disk_warn_gb.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            // §43-2b retention — 0 means keep everything; negative is meaningless, reject at write so the
            // stored profile always matches what the pruner enforces.
            if (body.BackupRetentionCount < 0) {
                return Results.Problem(
                    detail: "backup_retention_count must be >= 0 (0 keeps every snapshot).",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            store.PutStorageSettings(body);
            return Results.Ok(body);
        })
            .Accepts<StorageSettingsDto>("application/json")
            .Produces<StorageSettingsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
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

        profile.MapGet("/custom-horizon", (IProfileStore store) =>
                Results.Ok(store.GetCustomHorizon()))
            .Produces<CustomHorizonDto>(StatusCodes.Status200OK)
            .WithName("GetCustomHorizon")
            .WithSummary("Get the active profile's custom terrain horizon (empty points = none entered).");

        profile.MapPut("/custom-horizon", (CustomHorizonDto body, IProfileStore store) => {
            var (normalized, error) = CustomHorizonValidator.Normalize(body);
            if (error is not null) {
                return Results.Problem(
                    type: "https://openastro.net/errors/validation",
                    title: "Invalid custom horizon",
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    detail: error);
            }
            store.PutCustomHorizon(normalized!);
            return Results.Ok(normalized);
        })
            .Accepts<CustomHorizonDto>("application/json")
            .Produces<CustomHorizonDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("PutCustomHorizon")
            .WithSummary("Replace the active profile's custom terrain horizon (points are sorted by azimuth; azimuth 0-360, altitude -10..90, max 361 points).");

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
            // §58.8 — FirstFlipConfirmed is DAEMON-owned, not client-editable: the flip executor
            // sets it out-of-band whenever a first flip actually runs, so a client Save PUTting
            // back a stale hydrate must never clobber it (an overnight flip's confirmation would
            // be silently cleared and the announce would repeat — the exact behaviour §58.8
            // exists to prevent). The stored value always wins; re-arming goes through the
            // dedicated endpoint below.
            // Atomic read-merge-write under the store lock: a Get→Put pair from here could
            // lose a concurrent executor confirmation / re-arm to a stale snapshot — the exact
            // lost-update class this merge exists to close.
            var merged = store.UpdateSafetyPolicies(current => PreserveDaemonOwnedSafetyFields(body, current));
            return Results.Ok(merged);
        })
            .Accepts<SafetyPoliciesDto>("application/json")
            .Produces<SafetyPoliciesDto>(StatusCodes.Status200OK)
            .WithName("PutSafetyPolicies")
            .WithSummary("Replace the active profile's safety policies. first_flip_confirmed is " +
                "daemon-owned and ignored on PUT (the stored value is preserved and echoed) — " +
                "use POST /safety/first-flip/rearm to re-arm the §58.8 announce.");

        // §58.8 — the one-way re-arm: clears first_flip_confirmed so the NEXT flip announces
        // again (after re-balancing or a rig change the optics-based auto-reset can't see).
        // Deliberately no inverse endpoint: only the flip executor ever confirms a flip.
        profile.MapPost("/safety/first-flip/rearm", (IProfileStore store) =>
            Results.Ok(store.UpdateSafetyPolicies(current => current with { FirstFlipConfirmed = false })))
            .Produces<SafetyPoliciesDto>(StatusCodes.Status200OK)
            .WithName("RearmFirstFlipAnnounce")
            .WithSummary("Re-arm the §58.8 one-time first-flip announce (clears first_flip_confirmed). " +
                "Returns the updated safety policies.");

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

        profile.MapGet("/optics", (IProfileStore store) =>
                Results.Ok(store.GetOpticsSettings()))
            .Produces<OpticsSettingsDto>(StatusCodes.Status200OK)
            .WithName("GetOpticsSettings")
            .WithSummary("Get the active profile's optics settings.");

        profile.MapPut("/optics", (OpticsSettingsDto body, IProfileStore store) => {
            // reducer_factor multiplies focal length in the pixel-scale formula, so
            // 0 (or negative) would divide-by-zero / invert the FOV. It must stay
            // strictly positive (1.0 = no reducer/barlow).
            if (body.ReducerFactor <= 0) {
                return Results.Problem(
                    detail: "reducer_factor must be > 0 (1.0 = no reducer/barlow).",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            // The remaining geometry fields use 0 = "unset"; negatives are
            // meaningless (and produce garbage FOV math) — reject rather than store.
            if (body.FocalLengthMm < 0 || body.PixelSizeUm < 0 ||
                body.SensorWidthPx < 0 || body.SensorHeightPx < 0 || body.ApertureMm < 0) {
                return Results.Problem(
                    detail: "focal_length_mm, pixel_size_um, aperture_mm and sensor dimensions must be >= 0 (0 = unset).",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            store.PutOpticsSettings(body);
            return Results.Ok(body);
        })
            .Accepts<OpticsSettingsDto>("application/json")
            .Produces<OpticsSettingsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("PutOpticsSettings")
            .WithSummary("Replace the active profile's optics settings.");

        // NEXTGEN §4 camera electronics — exposure-planning inputs (read noise,
        // full well, e⁻/ADU, gain, QE peak). ASCOM-sourced fields auto-capture on
        // camera connect; this pair is the manual read/override path.
        profile.MapGet("/camera-electronics", (IProfileStore store) =>
                Results.Ok(store.GetCameraElectronics()))
            .Produces<CameraElectronicsDto>(StatusCodes.Status200OK)
            .WithName("GetCameraElectronics")
            .WithSummary("Get the active profile's camera electronics (exposure-planning inputs).");

        profile.MapPut("/camera-electronics", (CameraElectronicsDto body, IProfileStore store) => {
            // 0 = unset for the physical values; negatives are meaningless. Gain uses
            // -1 = unset (0 is a real gain on many cameras). QE peak is a fraction.
            if (!(double.IsFinite(body.ReadNoiseE) && body.ReadNoiseE >= 0) ||
                !(double.IsFinite(body.FullWellE) && body.FullWellE >= 0) ||
                !(double.IsFinite(body.ElectronsPerAdu) && body.ElectronsPerAdu >= 0)) {
                return Results.Problem(
                    detail: "read_noise_e, full_well_e and electrons_per_adu must be finite and >= 0 (0 = unset).",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            if (body.Gain < -1) {
                return Results.Problem(
                    detail: "gain must be >= -1 (-1 = unset).",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            if (!(double.IsFinite(body.QuantumEfficiencyPeak) &&
                  body.QuantumEfficiencyPeak is >= 0 and <= 1)) {
                return Results.Problem(
                    detail: "quantum_efficiency_peak must be in [0, 1] (0 = unset).",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            store.PutCameraElectronics(body);
            return Results.Ok(body);
        })
            .Accepts<CameraElectronicsDto>("application/json")
            .Produces<CameraElectronicsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("PutCameraElectronics")
            .WithSummary("Replace the active profile's camera electronics.");

        // NEXTGEN §4 planning filter set — the user's declared filters (kind +
        // effective bandwidth), matched to sequences by name. Separate from the
        // equipment FilterInfo, which must round-trip NINA imports untouched.
        profile.MapGet("/filter-set", (IProfileStore store) =>
                Results.Ok(store.GetFilterSet()))
            .Produces<FilterSetDto>(StatusCodes.Status200OK)
            .WithName("GetFilterSet")
            .WithSummary("Get the active profile's planning filter set.");

        profile.MapPut("/filter-set", (FilterSetDto body, IProfileStore store) => {
            if ((System.Collections.Generic.IReadOnlyList<PlanningFilterDto>?)body.Filters is null) {
                return Results.Problem(
                    detail: "filters must be a list (may be empty).",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var f in body.Filters) {
                if (string.IsNullOrWhiteSpace(f.Name)) {
                    return Results.Problem(
                        detail: "every filter needs a non-empty name (it's the key sequences match on).",
                        statusCode: StatusCodes.Status400BadRequest);
                }
                if (!seen.Add(f.Name.Trim())) {
                    return Results.Problem(
                        detail: $"duplicate filter name '{f.Name.Trim()}' (names are case-insensitive keys).",
                        statusCode: StatusCodes.Status400BadRequest);
                }
                if (!(double.IsFinite(f.BandwidthNm) && f.BandwidthNm >= 0)) {
                    return Results.Problem(
                        detail: "bandwidth_nm must be finite and >= 0 (0 = use the kind's default).",
                        statusCode: StatusCodes.Status400BadRequest);
                }
            }
            store.PutFilterSet(body);
            return Results.Ok(body);
        })
            .Accepts<FilterSetDto>("application/json")
            .Produces<FilterSetDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("PutFilterSet")
            .WithSummary("Replace the active profile's planning filter set.");

        // §37.4/§46.2 filter-wheel slot labels — the offline-authoring names the §38
        // editor's filter picker sources (12h.2b round-trip). Distinct from the
        // connected wheel's driver names AND the planning filter set above.
        profile.MapGet("/filter-wheel/labels", (IProfileStore store) =>
                Results.Ok(store.GetFilterWheelLabels()))
            .Produces<FilterWheelLabelsDto>(StatusCodes.Status200OK)
            .WithName("GetFilterWheelLabels")
            .WithSummary("Get the active profile's filter-wheel slot labels.");

        profile.MapPut("/filter-wheel/labels", (FilterWheelLabelsDto body, IProfileStore store) => {
            if ((System.Collections.Generic.IReadOnlyList<string>?)body.Labels is null) {
                return Results.Problem(
                    detail: "labels must be a list of slot names (empty string = unused slot).",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            if (body.Labels.Count is < 1 or > 32) {
                return Results.Problem(
                    detail: "labels must carry between 1 and 32 slots.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            var trimmed = new List<string>(body.Labels.Count);
            foreach (var label in body.Labels) {
                var t = (label ?? string.Empty).Trim();
                if (t.Length > 64) {
                    return Results.Problem(
                        detail: "a slot label can be at most 64 characters.",
                        statusCode: StatusCodes.Status400BadRequest);
                }
                trimmed.Add(t);
            }
            var normalized = new FilterWheelLabelsDto(trimmed);
            store.PutFilterWheelLabels(normalized);
            return Results.Ok(normalized);
        })
            .Accepts<FilterWheelLabelsDto>("application/json")
            .Produces<FilterWheelLabelsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("PutFilterWheelLabels")
            .WithSummary("Replace the active profile's filter-wheel slot labels.");

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

    /// <summary>§58.8 — the safety-policies PUT merge: daemon-owned fields keep their STORED
    /// value regardless of what the client sent. Today that is only <c>FirstFlipConfirmed</c>
    /// (set out-of-band by the flip executor when a first flip actually runs; a stale panel
    /// hydrate PUT back later must not clobber an overnight confirmation). Extracted so the
    /// ownership rule is unit-testable without a web host.</summary>
    internal static SafetyPoliciesDto PreserveDaemonOwnedSafetyFields(
        SafetyPoliciesDto incoming, SafetyPoliciesDto stored) =>
        incoming with { FirstFlipConfirmed = stored.FirstFlipConfirmed };
}