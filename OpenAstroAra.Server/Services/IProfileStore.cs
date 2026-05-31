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

    EquipmentConnectionDto GetEquipmentConnection();
    void PutEquipmentConnection(EquipmentConnectionDto value);

    StretchDefaultsDto GetStretchDefaults();
    void PutStretchDefaults(StretchDefaultsDto value);
}
