#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts;

/// <summary>
/// On-disk representation of the active profile — all §37 sections in
/// one document. Phase 12h.7 introduces this for the
/// <see cref="Services.FileProfileStore"/> file-backed implementation.
///
/// Wire shape (after snake_case serialization):
/// <code>
/// {
///   "imaging_defaults":      { ... },
///   "storage":               { ... },
///   "notifications":         { ... },
///   "site":                  { ... },
///   "filenames":             { ... },
///   "safety_policies":       { ... },
///   "autofocus":             { ... },
///   "plate_solve":           { ... },
///   "diagnostics_mode":      { ... },
///   "phd2":                  { ... },
///   "equipment_connection":  { ... },
///   "optics":                { ... },
///   "camera_electronics":    { ... },
///   "filter_set":            { ... },
///   "filter_wheel_labels":   { ... }
/// }
/// </code>
///
/// Not exposed on the wire today — kept here next to the section DTOs
/// because it's purely a composition of them. v0.1.0 §42 import/export
/// will reuse this shape.
/// </summary>
public sealed record ProfileSnapshotDto(
    ImagingDefaultsDto ImagingDefaults,
    StorageSettingsDto Storage,
    NotificationsSettingsDto Notifications,
    SiteSettingsDto Site,
    FilenamesSettingsDto Filenames,
    SafetyPoliciesDto SafetyPolicies,
    AutofocusSettingsDto Autofocus,
    PlateSolveSettingsDto PlateSolve,
    DiagnosticsModeDto DiagnosticsMode,
    Phd2SettingsDto Phd2,
    EquipmentConnectionDto EquipmentConnection,
    StretchDefaultsDto StretchDefaults,
    OpticsSettingsDto Optics,
    CameraElectronicsDto CameraElectronics,
    FilterSetDto FilterSet,
    FilterWheelLabelsDto FilterWheelLabels,
    // §36 custom terrain horizon. Appended as an OPTIONAL param (like #704's
    // notifications fields) so existing positional constructions and older
    // profile.json files keep working; ProfileSnapshotNormalizer back-fills
    // null to the empty horizon.
    CustomHorizonDto? CustomHorizon = null);