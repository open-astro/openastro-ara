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
    /// <para>Implementations MUST raise this <b>synchronously</b> on the thread that performed the
    /// Put — do not marshal it to a thread pool / <c>Task.Run</c>. <see cref="FileProfileRepository"/>
    /// relies on synchronous dispatch to suppress its active-file mirror while it applies a
    /// multi-section snapshot during profile-select.</para>
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

    /// <summary>
    /// Atomically read-modify-write the safety-policies section — same contract as
    /// <see cref="UpdateOpticsSettings"/> (null from <paramref name="update"/> = no write, no
    /// change event; the transform runs under the store lock so keep it pure and fast). Used by
    /// the §58.8 daemon-owned-field merge on PUT, the re-arm endpoint, and the flip executor's
    /// confirmation write, so none of them can lose a concurrent update to a stale snapshot.
    /// </summary>
    SafetyPoliciesDto UpdateSafetyPolicies(Func<SafetyPoliciesDto, SafetyPoliciesDto?> update);

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
    /// <para><paramref name="update"/> runs while the store's internal lock is held — keep it a pure,
    /// fast transform: do not call back into the store, acquire other locks, or do IO, or it may deadlock.</para>
    /// </summary>
    OpticsSettingsDto UpdateOpticsSettings(Func<OpticsSettingsDto, OpticsSettingsDto?> update);

    EquipmentConnectionDto GetEquipmentConnection();
    void PutEquipmentConnection(EquipmentConnectionDto value);

    StretchDefaultsDto GetStretchDefaults();
    void PutStretchDefaults(StretchDefaultsDto value);

    CameraElectronicsDto GetCameraElectronics();
    void PutCameraElectronics(CameraElectronicsDto value);

    /// <summary>
    /// Atomically read-modify-write the camera-electronics section — same contract as
    /// <see cref="UpdateOpticsSettings"/> (null from <paramref name="update"/> = no write, no
    /// change event; the transform runs under the store lock so keep it pure and fast). Used by
    /// the camera-connect auto-capture so a concurrent user edit can't be lost.
    /// </summary>
    CameraElectronicsDto UpdateCameraElectronics(Func<CameraElectronicsDto, CameraElectronicsDto?> update);

    FilterSetDto GetFilterSet();
    void PutFilterSet(FilterSetDto value);

    FilterWheelLabelsDto GetFilterWheelLabels();
    void PutFilterWheelLabels(FilterWheelLabelsDto value);

    /// <summary>§36 custom terrain horizon (sorted az/alt skyline; empty = none entered).</summary>
    CustomHorizonDto GetCustomHorizon();
    void PutCustomHorizon(CustomHorizonDto value);
}