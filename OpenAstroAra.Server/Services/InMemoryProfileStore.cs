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
using System;

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

    public event EventHandler? Changed;

    // Raised OUTSIDE _lock (after the section swap) so a subscriber that reads back through the
    // Get* methods can't deadlock against the store.
    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

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
        RaiseChanged();
    }

    public StorageSettingsDto GetStorageSettings() {
        lock (_lock) { return _storage; }
    }

    public void PutStorageSettings(StorageSettingsDto value) {
        lock (_lock) { _storage = value; }
        RaiseChanged();
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
        RaiseChanged();
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
        RaiseChanged();
    }

    // Defaults match FilenamesSettings() — slash separator + RICE-compress
    // darks/bias on (highly compressible + lossless).
    private FilenamesSettingsDto _filenames = new(
        DateSeparator: "forward_slash",
        CompressDarksAndBias: true);

    public FilenamesSettingsDto GetFilenamesSettings() {
        lock (_lock) { return _filenames; }
    }

    public void PutFilenamesSettings(FilenamesSettingsDto value) {
        lock (_lock) { _filenames = value; }
        RaiseChanged();
    }

    // Defaults match SafetyPolicies() constructor.
    private SafetyPoliciesDto _safety = new(
        OnUnsafe: "pause_and_park",
        AutoResumeWhenSafe: true,
        ResumeDelayMin: 10,
        MeridianFlipAuto: true,
        MeridianPauseMin: 5,
        MeridianRecenter: true,
        MeridianRecalGuider: true,
        OnAltitudeLimit: "skip_target",
        ParkIfNoMoreTargets: true,
        OnGuiderLost: "pause_and_retry",
        GuiderRetryTimeoutSec: 60,
        SkipTargetIfRecoveryFails: true);

    public SafetyPoliciesDto GetSafetyPolicies() {
        lock (_lock) { return _safety; }
    }

    public void PutSafetyPolicies(SafetyPoliciesDto value) {
        lock (_lock) { _safety = value; }
        RaiseChanged();
    }

    // Defaults match AutofocusSettings() constructor.
    private AutofocusSettingsDto _autofocus = new(
        Method: "hfr_v_curve",
        Steps: 7,
        StepSize: 50,
        ExposureSeconds: 5,
        Binning: 1,
        AfFilter: "L",
        RunAfterFilterChange: true,
        TriggerTempDeltaC: 2.0,
        TriggerHfrDriftPct: 15.0,
        EveryNHours: 2,
        AbortSequenceOnAfFailure: true,
        RestorePositionOnFailure: true);

    public AutofocusSettingsDto GetAutofocusSettings() {
        lock (_lock) { return _autofocus; }
    }

    public void PutAutofocusSettings(AutofocusSettingsDto value) {
        lock (_lock) { _autofocus = value; }
        RaiseChanged();
    }

    // Defaults match PlateSolveSettings() constructor.
    private PlateSolveSettingsDto _plateSolve = new(
        Engine: "astap",
        PathOrEndpoint: "/usr/bin/astap",
        IndexDownloadPath: "/var/lib/astap",
        SearchRadiusDeg: 30.0,
        DownsampleFactor: 2,
        TimeoutSeconds: 60,
        UseBlindFallback: true,
        CenterAfterSlew: true,
        SyncToCoordinates: true,
        MaxIterations: 5,
        ConvergenceToleranceArcsec: 60.0);

    public PlateSolveSettingsDto GetPlateSolveSettings() {
        lock (_lock) { return _plateSolve; }
    }

    public void PutPlateSolveSettings(PlateSolveSettingsDto value) {
        lock (_lock) { _plateSolve = value; }
        RaiseChanged();
    }

    // Default matches DiagnosticsModeNotifier's build() — notify_only is
    // the safe ship-it default per §51 (never auto-pauses a sequence).
    private DiagnosticsModeDto _diagnosticsMode = new(Mode: "notify_only");

    public DiagnosticsModeDto GetDiagnosticsMode() {
        lock (_lock) { return _diagnosticsMode; }
    }

    public void PutDiagnosticsMode(DiagnosticsModeDto value) {
        lock (_lock) { _diagnosticsMode = value; }
        RaiseChanged();
    }

    // Defaults match Phd2Settings() constructor.
    private Phd2SettingsDto _phd2 = new(
        Host: "localhost",
        Port: 4400,
        Phd2Profile: "Default",
        DitherEnabled: true,
        DitherEveryNFrames: 1,
        DitherPixels: 5.0,
        SettlePixels: 1.5,
        SettleTimeSec: 10,
        SettleTimeoutSec: 60,
        ForceCalibrationEachSession: false,
        GuideFocalLength: 0,
        GuidePixelSize: 0,
        RaAggressiveness: 0.7,
        DecAggressiveness: 0.7,
        MinimumMove: 0.15,
        DecGuideMode: "auto");

    public Phd2SettingsDto GetPhd2Settings() {
        lock (_lock) { return _phd2; }
    }

    public void PutPhd2Settings(Phd2SettingsDto value) {
        lock (_lock) { _phd2 = value; }
        RaiseChanged();
    }

    // Defaults match EquipmentConnectionSettings() constructor: camera +
    // mount + focuser + filter wheel + rotator + flat + safety on by
    // default; dome + weather + guider off (manual connect — dome
    // involves shutter actuation, guider starts PHD2 client).
    private EquipmentConnectionDto _equipmentConnection = new(
        Camera: true,
        Mount: true,
        Focuser: true,
        FilterWheel: true,
        Rotator: true,
        Guider: false,
        FlatPanel: true,
        Dome: false,
        Weather: false,
        SafetyMonitor: true);

    public EquipmentConnectionDto GetEquipmentConnection() {
        lock (_lock) { return _equipmentConnection; }
    }

    public void PutEquipmentConnection(EquipmentConnectionDto value) {
        lock (_lock) { _equipmentConnection = value; }
        RaiseChanged();
    }

    // §65.2 stretch defaults — auto_stf for Lights, calibration frames
    // always render linear via the frame-type auto-override.
    private StretchDefaultsDto _stretchDefaults = new(
        LightDefault: "auto_stf",
        ManualDefaultParams: new(Blackpoint: 0.02, Midpoint: 0.5, Whitepoint: 0.98),
        AsinhDefaultBeta: 3.0,
        LinearClipPercentilesLow: 0.005,
        LinearClipPercentilesHigh: 0.995);

    public StretchDefaultsDto GetStretchDefaults() {
        lock (_lock) { return _stretchDefaults; }
    }

    public void PutStretchDefaults(StretchDefaultsDto value) {
        lock (_lock) { _stretchDefaults = value; }
        RaiseChanged();
    }
}