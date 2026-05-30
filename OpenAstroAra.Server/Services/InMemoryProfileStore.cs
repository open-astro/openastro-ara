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
/// v0.0.1 profile store. Pure in-memory — values reset to defaults on
/// every daemon restart. File-based persistence lands in Phase 13.
///
/// Defaults match the WILMA client's <c>ImagingDefaults</c> constructor
/// defaults exactly so a fresh daemon + fresh client agree on initial
/// state without a round-trip.
/// </summary>
public sealed class InMemoryProfileStore : IProfileStore {
    private readonly object _lock = new();

    private ImagingDefaultsDto _imagingDefaults = new(
        ExposureSeconds: 5,
        Gain: 100,
        Offset: 50,
        Bin: 1,
        FrameKind: "light",
        CoolerTargetC: -10.0,
        CoolerRampCPerMin: 1.0,
        WarmupAtSessionEnd: false);

    // Defaults match the WILMA client's StorageSettings() constructor
    // (lib/state/settings/storage_settings_state.dart) field-for-field,
    // including the `\\` double-backslash separators in the filename
    // template — the raw-string literal on the client uses literal
    // double-backslashes, so the verbatim string here matches with `\\`.
    // The save directory matches the §13 systemd unit's WorkingDirectory.
    private StorageSettingsDto _storage = new(
        SaveDirectory: "/media/openastroara",
        FileFormat: "fits",
        Compression: "rice",
        FilenameTemplate: @"$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s");

    public ImagingDefaultsDto GetImagingDefaults() {
        lock (_lock) { return _imagingDefaults; }
    }

    public void PutImagingDefaults(ImagingDefaultsDto value) {
        lock (_lock) { _imagingDefaults = value; }
    }

    public StorageSettingsDto GetStorageSettings() {
        lock (_lock) { return _storage; }
    }

    public void PutStorageSettings(StorageSettingsDto value) {
        lock (_lock) { _storage = value; }
    }

    // Defaults match NotificationsSettings() — every trigger + channel on,
    // tokens empty (the user fills them after wiring their pushover/telegram
    // accounts).
    private NotificationsSettingsDto _notifications = new(
        InAppBanner: true,
        OsDesktop: true,
        SoundAlert: true,
        PushoverToken: "",
        TelegramBotToken: "",
        OnSequenceComplete: true,
        OnSequencePaused: true,
        OnCriticalDiagnostic: true,
        OnSafetyEvent: true,
        OnAutofocusFailed: true,
        OnPlateSolveFailed: true,
        OnDiskSpaceLow: true);

    public NotificationsSettingsDto GetNotificationsSettings() {
        lock (_lock) { return _notifications; }
    }

    public void PutNotificationsSettings(NotificationsSettingsDto value) {
        lock (_lock) { _notifications = value; }
    }

    // Defaults match SiteSettings() constructor. Lat/lon default to 0,0
    // (Gulf of Guinea) — astrometry math works there, the user just sees
    // the wizard prompt to set their real location.
    private SiteSettingsDto _site = new(
        SiteName: "Backyard",
        LatitudeDeg: 0.0,
        LongitudeDeg: 0.0,
        ElevationM: 0.0,
        TimeZone: "UTC",
        UseCustomHorizon: false,
        DefaultHorizonAltitudeDeg: 20.0,
        BortleClass: 6,
        TypicalSeeingArcsec: 2.5,
        TwilightDefinition: "astronomical");

    public SiteSettingsDto GetSiteSettings() {
        lock (_lock) { return _site; }
    }

    public void PutSiteSettings(SiteSettingsDto value) {
        lock (_lock) { _site = value; }
    }
}
