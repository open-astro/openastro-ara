#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §37 active-profile store. v0.0.1 ships an in-memory implementation
/// (<see cref="InMemoryProfileStore"/>); Phase 13+ adds file-based
/// persistence so settings survive daemon restarts. Multi-profile + the
/// §42 import/export flow is v0.1.0 per the §55.1 roadmap.
/// </summary>
public interface IProfileStore {
    /// <summary>
    /// Raised after any section is replaced via a Put. Subscribers (the §14e store-backed
    /// <c>IProfileService</c>) re-read through the Get methods; implementations raise outside their
    /// internal lock so a read-back can't deadlock.
    /// </summary>
    event System.EventHandler? Changed;

    ImagingDefaultsDto GetImagingDefaults();
    void PutImagingDefaults(ImagingDefaultsDto value);

    StorageSettingsDto GetStorageSettings();
    void PutStorageSettings(StorageSettingsDto value);

    NotificationsSettingsDto GetNotificationsSettings();
    void PutNotificationsSettings(NotificationsSettingsDto value);

    SiteSettingsDto GetSiteSettings();
    void PutSiteSettings(SiteSettingsDto value);

    FilenamesSettingsDto GetFilenamesSettings();
    void PutFilenamesSettings(FilenamesSettingsDto value);

    SafetyPoliciesDto GetSafetyPolicies();
    void PutSafetyPolicies(SafetyPoliciesDto value);

    AutofocusSettingsDto GetAutofocusSettings();
    void PutAutofocusSettings(AutofocusSettingsDto value);

    PlateSolveSettingsDto GetPlateSolveSettings();
    void PutPlateSolveSettings(PlateSolveSettingsDto value);

    DiagnosticsModeDto GetDiagnosticsMode();
    void PutDiagnosticsMode(DiagnosticsModeDto value);

    Phd2SettingsDto GetPhd2Settings();
    void PutPhd2Settings(Phd2SettingsDto value);

    OpticsSettingsDto GetOpticsSettings();
    void PutOpticsSettings(OpticsSettingsDto value);

    /// <summary>
    /// Atomically read-modify-write the optics section under the store lock, so a concurrent
    /// <see cref="PutOpticsSettings"/> (e.g. the user editing focal length while a camera connect
    /// auto-populates the sensor geometry) can't be lost to a stale-snapshot overwrite.
    /// <paramref name="update"/> receives the current optics and returns the value to persist, or
    /// <c>null</c> to leave it unchanged (no write, no change event). Returns the resulting optics.
    /// </summary>
    OpticsSettingsDto UpdateOpticsSettings(Func<OpticsSettingsDto, OpticsSettingsDto?> update);

    EquipmentConnectionDto GetEquipmentConnection();
    void PutEquipmentConnection(EquipmentConnectionDto value);

    StretchDefaultsDto GetStretchDefaults();
    void PutStretchDefaults(StretchDefaultsDto value);
}