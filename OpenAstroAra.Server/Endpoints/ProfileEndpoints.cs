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
using OpenAstroAra.Profile;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System.Linq;

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

        profile.MapPut("/storage", PutStorageSettings)
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

        // §30.7.4 (e-4b-2) — read-only: the calibration builds stamp this server-side (daemon-owned
        // derived data, like focus-calibration), so there is deliberately no PUT.
        profile.MapGet("/calibration-state", (IProfileStore store) =>
                Results.Ok(store.GetCalibrationState()))
            .Produces<CalibrationStateDto>(StatusCodes.Status200OK)
            .WithName("GetCalibrationState")
            .WithSummary("Get the active profile's calibration-state block (guider dark library / defect map validity + last-built timestamps).");

        // #1267 item 5 — the recent and saved targets: client-authored, replaced as a whole list.
        profile.MapGet("/saved-targets", (IProfileStore store) =>
                Results.Ok(store.GetSavedTargets()))
            .Produces<SavedTargetsDto>(StatusCodes.Status200OK)
            .WithName("GetSavedTargets")
            .WithSummary("Get the active profile's recent and saved targets (name, aliases, J2000 position, source), newest first.");

        profile.MapPut("/saved-targets", (SavedTargetsDto body, IProfileStore store) => {
            var (normalized, error) = SavedTargetsValidator.Normalize(body);
            if (error is not null) {
                return Results.Problem(
                    type: "https://openastro.net/errors/validation",
                    title: "Invalid saved targets",
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    detail: error);
            }
            store.PutSavedTargets(normalized!);
            return Results.Ok(normalized);
        })
            .Accepts<SavedTargetsDto>("application/json")
            .Produces<SavedTargetsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("PutSavedTargets")
            .WithSummary("Replace the active profile's recent and saved targets (newest first; one entry per name; at most 200; finite J2000 positions).");

        // #1362 — read-only: the connect-time reconcile records each gear slot's last-seen Alpaca identity.
        profile.MapGet("/equipment-identity", (IProfileStore store) =>
                Results.Ok(store.GetEquipmentIdentity()))
            .Produces<EquipmentIdentityDto>(StatusCodes.Status200OK)
            .WithName("GetEquipmentIdentity")
            .WithSummary("Get the active profile's last-seen Alpaca identity (UniqueID, name, seen time) per gear slot.");

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

        profile.MapGet("/polar-align", (IProfileStore store) =>
                Results.Ok(store.GetPolarAlignSettings()))
            .Produces<PolarAlignSettingsDto>(StatusCodes.Status200OK)
            .WithName("GetPolarAlignSettings")
            .WithSummary("Get the active profile's §45 polar-alignment settings.");

        profile.MapPut("/polar-align", PutPolarAlignSettings)
            .Accepts<PolarAlignSettingsDto>("application/json")
            .Produces<PolarAlignSettingsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("PutPolarAlignSettings")
            .WithSummary("Replace the active profile's §45 polar-alignment settings.");

        // #1264 — Imaging → Unguided: the probe / guard / re-centre knobs and the prior's tolerance basis.
        profile.MapGet("/unguided", (IProfileStore store) =>
                Results.Ok(store.GetUnguidedSettings()))
            .Produces<UnguidedSettingsDto>(StatusCodes.Status200OK)
            .WithName("GetUnguidedSettings")
            .WithSummary("Get the active profile's unguided-imaging settings.");

        profile.MapPut("/unguided", PutUnguidedSettings)
            .Accepts<UnguidedSettingsDto>("application/json")
            .Produces<UnguidedSettingsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("PutUnguidedSettings")
            .WithSummary("Replace the active profile's unguided-imaging settings.");

        // #1364 — the guider's settings beyond Tune Guiding. Apply = PUT here, then
        // POST /equipment/guider/profile/push?scope=tuning (multi-star lands on the next connect).
        profile.MapGet("/guider-advanced", (IProfileStore store) =>
                Results.Ok(store.GetGuiderAdvancedSettings()))
            .Produces<GuiderAdvancedSettingsDto>(StatusCodes.Status200OK)
            .WithName("GetGuiderAdvancedSettings")
            .WithSummary("Get the active profile's guider-advanced settings (null fields keep the guider's own).");

        profile.MapPut("/guider-advanced", (GuiderAdvancedSettingsDto body, IProfileStore store) => {
            if (GuiderAdvancedSettingsMapping.Validate(body) is { } problem) {
                return Results.Problem(detail: problem, statusCode: StatusCodes.Status400BadRequest);
            }
            var normalized = body with {
                RaAlgorithm = body.RaAlgorithm?.Trim(),
                DecAlgorithm = body.DecAlgorithm?.Trim(),
            };
            store.PutGuiderAdvancedSettings(normalized);
            return Results.Ok(normalized);
        })
            .Accepts<GuiderAdvancedSettingsDto>("application/json")
            .Produces<GuiderAdvancedSettingsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("PutGuiderAdvancedSettings")
            .WithSummary("Replace the active profile's guider-advanced settings.");

        profile.MapGet("/plate-solve", (IProfileStore store) =>
                Results.Ok(store.GetPlateSolveSettings()))
            .Produces<PlateSolveSettingsDto>(StatusCodes.Status200OK)
            .WithName("GetPlateSolveSettings")
            .WithSummary("Get the active profile's plate-solve settings.");

        profile.MapPut("/plate-solve", PutPlateSolveSettings)
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

        // §63.19 — the read side applies the same guide-train rule as the write side, so a guide-scope
        // profile.json written before the rule never shows a rotator the daemon no longer pushes.
        profile.MapGet("/phd2", (IProfileStore store) =>
                Results.Ok(NormalizeGuideTrain(store.GetPhd2Settings())))
            .Produces<Phd2SettingsDto>(StatusCodes.Status200OK)
            .WithName("GetPhd2Settings")
            .WithSummary("Get the active profile's PHD2 settings.");

        profile.MapPut("/phd2", (Phd2SettingsDto body, IProfileStore store) => {
            // §63.19 — normalize the setup type at the write boundary so an out-of-band PUT can't persist an
            // arbitrary token (#885 review): unknown → the guide_scope default, matching the client's read-side
            // coercion. Other fields keep the store's existing store-verbatim posture.
            var normalized = NormalizeGuideTrain(body);
            var rangeError = ValidateGuideExposureRange(normalized) ?? ValidateGuideCameraGain(normalized);
            if (rangeError is not null) {
                return Results.Problem(detail: rangeError, statusCode: StatusCodes.Status400BadRequest);
            }
            store.PutPhd2Settings(normalized);
            return Results.Ok(normalized);
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
        // editor's filter picker sources (12h.2b round-trip). Mirrored from the
        // connected wheel's Alpaca names on connect (FilterWheelService.MirrorSlotLabels);
        // distinct from the planning filter set above.
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

        // #1075 — filter-wheel policy: whether the daemon parks the wheel on slot 0 the first
        // time it connects after the daemon starts (#1066). Whole-section PUT like the others.
        profile.MapGet("/filter-wheel/policy", (IProfileStore store) =>
                Results.Ok(store.GetFilterWheelPolicy()))
            .Produces<FilterWheelPolicyDto>(StatusCodes.Status200OK)
            .WithName("GetFilterWheelPolicy")
            .WithSummary("Get the active profile's filter-wheel policy (home on first connect).");

        profile.MapPut("/filter-wheel/policy", (FilterWheelPolicyDto body, IProfileStore store) => {
            store.PutFilterWheelPolicy(body);
            return Results.Ok(body);
        })
            .Accepts<FilterWheelPolicyDto>("application/json")
            .Produces<FilterWheelPolicyDto>(StatusCodes.Status200OK)
            .WithName("PutFilterWheelPolicy")
            .WithSummary("Replace the active profile's filter-wheel policy.");

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

    /// <summary>§63.19 — write-boundary normalization for the guide setup type: only the openapi enum
    /// tokens pass through; anything else (case/whitespace variants included, matching the client's
    /// read-side coercion) becomes the <c>guide_scope</c> default. Extracted for unit tests.</summary>
    internal static string NormalizeGuiderSetupType(string? setupType) =>
        GuiderSettings.NormalizeSetupType(setupType);

    /// <summary>The guide-train rule at the write boundary: the setup type is coerced to its enum and a
    /// rotator selection only survives on an off-axis guider. PHD2 turns its calibration by every move of
    /// the rotator it is given, which is right for an OAG (the guide camera turns with it) and wrong for a
    /// separate guide scope (it doesn't) — so a guide-scope profile never stores one, and the push tells the
    /// guider "None". Extracted for unit tests.</summary>
    internal static Phd2SettingsDto NormalizeGuideTrain(Phd2SettingsDto body) {
        var setupType = GuiderSettings.NormalizeSetupType(body.GuiderSetupType);
        return body with {
            GuiderSetupType = setupType,
            GuiderRotator = GuiderSettings.UsesGuideCameraRotator(setupType) ? body.GuiderRotator : string.Empty,
        };
    }

    /// <summary>#1215 — <c>PUT /profile/plate-solve</c> echoes what the store HOLDS, not the body as
    /// typed: the normalizer's <c>/usr/bin/astap</c> → <c>/usr/bin/astap_cli</c> migration runs on the
    /// write path, and a client that adopts the echo (the settings panels do) kept showing the typed
    /// path. Extracted so the echo rule is unit-testable with a real store.</summary>
    internal static IResult PutPlateSolveSettings(PlateSolveSettingsDto body, IProfileStore store) {
        store.PutPlateSolveSettings(body);
        return Results.Ok(store.GetPlateSolveSettings());
    }

    /// <summary>§29 / §43-2b / §42.5 — <c>PUT /profile/storage</c> write-boundary validation: an invalid
    /// disk-space pair or a negative retention value is a 400 and never reaches the store. Extracted so
    /// the 400s are unit-testable without a host (#1145).</summary>
    internal static IResult PutStorageSettings(StorageSettingsDto body, IProfileStore store) {
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
        // §42.5 fault-log retention — same contract: 0 keeps everything, negative is rejected (#1145).
        if (body.FaultLogRetentionDays < 0) {
            return Results.Problem(
                detail: "fault_log_retention_days must be >= 0 (0 keeps every fault row).",
                statusCode: StatusCodes.Status400BadRequest);
        }
        store.PutStorageSettings(body);
        return Results.Ok(body);
    }

    /// <summary>#1264 — write-boundary validation: the guard's two eccentricity bars must be ordered and
    /// in (0, 1], its counts positive, the ladder ascending and positive, the re-centre figures positive,
    /// and the tolerance basis one of the three names — a bad body would hand the sequencer a guard that
    /// never steps or a probe that never passes.</summary>
    internal static IResult PutUnguidedSettings(UnguidedSettingsDto body, IProfileStore store) {
        static IResult Bad(string detail) => Results.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest);
        if (!(double.IsFinite(body.EccentricityTolerance) && body.EccentricityTolerance > 0 && body.EccentricityTolerance <= 1)) {
            return Bad("eccentricity_tolerance must be in (0, 1]");
        }
        if (!(double.IsFinite(body.GoodEccentricity) && body.GoodEccentricity > 0 && body.GoodEccentricity < body.EccentricityTolerance)) {
            return Bad("good_eccentricity must be in (0, eccentricity_tolerance)");
        }
        if (body.StepDownAfter < 1 || body.StepUpAfter < 1) {
            return Bad("step_down_after and step_up_after must be at least 1");
        }
        if (body.ProbeRungsSeconds is { Count: > 0 } rungs) {
            if (rungs.Count > 10 || rungs.Any(r => !double.IsFinite(r) || r <= 0)) {
                return Bad("probe_rungs_seconds must hold 1–10 positive exposures");
            }
            for (var i = 1; i < rungs.Count; i++) {
                if (rungs[i] <= rungs[i - 1]) {
                    return Bad("probe_rungs_seconds must be strictly ascending");
                }
            }
        }
        if (!(double.IsFinite(body.RecentreDriftArcmin) && body.RecentreDriftArcmin > 0) || body.RecentreSolveEveryN < 1) {
            return Bad("recentre_drift_arcmin must be positive and recentre_solve_every_n at least 1");
        }
        if (!UnguidedPriorToleranceBases.IsValid(body.PriorToleranceBasis)) {
            return Bad("prior_tolerance_basis must be \"max\", \"pixel\" or \"seeing\"");
        }
        store.PutUnguidedSettings(body);
        return Results.Ok(body);
    }

    /// <summary>§45.12 — <c>PUT /profile/polar-align</c>. The live loop re-reads these settings before
    /// every frame, so a bad exposure or an unknown loop mode must be refused at the write boundary
    /// (400) rather than reaching the camera. Extracted so the 400s are unit-testable without a host.</summary>
    internal static IResult PutPolarAlignSettings(PolarAlignSettingsDto body, IProfileStore store) {
        if (!(double.IsFinite(body.ExposureSeconds) && body.ExposureSeconds > 0 && body.ExposureSeconds <= 60)) {
            return Results.Problem(detail: "exposure_seconds must be greater than 0 and at most 60",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (body.LoopMode is not (PolarAlignLoopModes.Loop or PolarAlignLoopModes.SingleFrame)) {
            return Results.Problem(detail: "loop_mode must be \"loop\" or \"single\"",
                statusCode: StatusCodes.Status400BadRequest);
        }
        store.PutPolarAlignSettings(body);
        return Results.Ok(body);
    }

    /// <summary>§76.2 — write-boundary validation of the guide exposure range. The range feeds BOTH
    /// the dark-library build and the guiding bounds, so an inverted or non-positive range would
    /// silently corrupt both consumers; the wizard gates it client-side, but out-of-band PUTs must
    /// not slip by. Returns the 400 detail, or null when valid. Extracted for unit tests.</summary>
    internal static string? ValidateGuideExposureRange(Phd2SettingsDto body) =>
        body.GuideExposureMinMs <= 0 || body.GuideExposureMaxMs <= 0 ||
        body.GuideExposureMinMs > body.GuideExposureMaxMs
            ? "guide_exposure_min_ms/guide_exposure_max_ms must be > 0 with min <= max."
            : body.GuideExposureMs < 0
                ? "guide_exposure_ms must be >= 0 (0 = leave the guider's own exposure)."
                : null;

    /// <summary>The guider refuses a gain outside 0..100 (percent of the camera's range).</summary>
    internal static string? ValidateGuideCameraGain(Phd2SettingsDto body) =>
        body.GuideCameraGain is < 0 or > 100 ? "guide_camera_gain must be 0..100 (percent of the camera's gain range)." : null;
}