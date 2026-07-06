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
/// Bridges the section-oriented <see cref="IProfileStore"/> and the whole-profile
/// <see cref="ProfileSnapshotDto"/>: <see cref="Capture"/> reads every section out,
/// <see cref="Apply"/> writes a full snapshot back (one Put per section). Used by the
/// multi-profile repository to clone the live profile and to load a saved profile into
/// the active working copy.
/// </summary>
public static class ProfileStoreSnapshot {
    public static ProfileSnapshotDto Capture(IProfileStore s) => new(
        ImagingDefaults: s.GetImagingDefaults(),
        Storage: s.GetStorageSettings(),
        Notifications: s.GetNotificationsSettings(),
        Site: s.GetSiteSettings(),
        Filenames: s.GetFilenamesSettings(),
        SafetyPolicies: s.GetSafetyPolicies(),
        Autofocus: s.GetAutofocusSettings(),
        PlateSolve: s.GetPlateSolveSettings(),
        DiagnosticsMode: s.GetDiagnosticsMode(),
        Phd2: s.GetPhd2Settings(),
        EquipmentConnection: s.GetEquipmentConnection(),
        StretchDefaults: s.GetStretchDefaults(),
        Optics: s.GetOpticsSettings(),
        CameraElectronics: s.GetCameraElectronics(),
        FilterSet: s.GetFilterSet(),
        FilterWheelLabels: s.GetFilterWheelLabels(),
        CustomHorizon: s.GetCustomHorizon());

    /// <summary>Push every section of <paramref name="snap"/> into the live store.
    /// Each Put raises <see cref="IProfileStore.Changed"/>, so callers that don't want
    /// 15 change notifications (e.g. profile-select) should suppress their own handler
    /// for the duration.</summary>
    public static void Apply(IProfileStore s, ProfileSnapshotDto snap) {
        s.PutImagingDefaults(snap.ImagingDefaults);
        s.PutStorageSettings(snap.Storage);
        s.PutNotificationsSettings(snap.Notifications);
        s.PutSiteSettings(snap.Site);
        s.PutFilenamesSettings(snap.Filenames);
        s.PutSafetyPolicies(snap.SafetyPolicies);
        s.PutAutofocusSettings(snap.Autofocus);
        s.PutPlateSolveSettings(snap.PlateSolve);
        s.PutDiagnosticsMode(snap.DiagnosticsMode);
        s.PutPhd2Settings(snap.Phd2);
        s.PutEquipmentConnection(snap.EquipmentConnection);
        s.PutStretchDefaults(snap.StretchDefaults);
        s.PutOpticsSettings(snap.Optics);
        s.PutCameraElectronics(snap.CameraElectronics);
        s.PutFilterSet(snap.FilterSet);
        s.PutFilterWheelLabels(snap.FilterWheelLabels);
        // Normalized snapshots always carry a non-null horizon; the fallback keeps
        // Apply safe for a hand-built DTO that skipped the optional param.
        s.PutCustomHorizon(snap.CustomHorizon ?? new CustomHorizonDto(Points: []));
    }
}
