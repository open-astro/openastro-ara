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
/// Every section starts at its default from the section table
/// (<see cref="ProfileSections"/>), the one place a default is stated, so a fresh
/// daemon, a fresh <see cref="FileProfileStore"/> and the normalizer agree.
/// </summary>
public sealed class InMemoryProfileStore : IProfileStore {
    private readonly object _lock = new();

    public event EventHandler? Changed;

    // Raised OUTSIDE _lock (after the section swap) so a subscriber that reads back through the
    // Get* methods can't deadlock against the store.
    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private ImagingDefaultsDto _imagingDefaults = ProfileSections.ImagingDefaults.Default!;

    private StorageSettingsDto _storage = ProfileSections.Storage.Default!;

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
        // #1148 — same token normalization FileProfileStore gets from ProfileSnapshotNormalizer.
        value = StorageFormatTokens.Normalize(value);
        lock (_lock) { _storage = value; }
        RaiseChanged();
    }

    private NotificationsSettingsDto _notifications = ProfileSections.Notifications.Default!;

    public NotificationsSettingsDto GetNotificationsSettings() {
        lock (_lock) { return _notifications; }
    }

    public void PutNotificationsSettings(NotificationsSettingsDto value) {
        lock (_lock) { _notifications = value; }
        RaiseChanged();
    }

    private SiteSettingsDto _site = ProfileSections.Site.Default!;

    public SiteSettingsDto GetSiteSettings() {
        lock (_lock) { return _site; }
    }

    public void PutSiteSettings(SiteSettingsDto value) {
        lock (_lock) { _site = value; }
        RaiseChanged();
    }

    private FilenamesSettingsDto _filenames = ProfileSections.Filenames.Default!;

    public FilenamesSettingsDto GetFilenamesSettings() {
        lock (_lock) { return _filenames; }
    }

    public void PutFilenamesSettings(FilenamesSettingsDto value) {
        lock (_lock) { _filenames = value; }
        RaiseChanged();
    }

    private SafetyPoliciesDto _safety = ProfileSections.SafetyPolicies.Default!;

    public SafetyPoliciesDto GetSafetyPolicies() {
        lock (_lock) { return _safety; }
    }

    public void PutSafetyPolicies(SafetyPoliciesDto value) {
        lock (_lock) { _safety = value; }
        RaiseChanged();
    }

    public SafetyPoliciesDto UpdateSafetyPolicies(Func<SafetyPoliciesDto, SafetyPoliciesDto?> update) {
        SafetyPoliciesDto next;
        lock (_lock) {
            var current = _safety;
            var candidate = update(current);
            if (candidate is null || candidate == current) {
                return current; // no change — no event
            }
            next = candidate;
            _safety = next;
        }
        RaiseChanged();
        return next;
    }

    private AutofocusSettingsDto _autofocus = ProfileSections.Autofocus.Default!;

    public AutofocusSettingsDto GetAutofocusSettings() {
        lock (_lock) { return _autofocus; }
    }

    public void PutAutofocusSettings(AutofocusSettingsDto value) {
        lock (_lock) { _autofocus = value; }
        RaiseChanged();
    }

    private PolarAlignSettingsDto _polarAlign = ProfileSections.PolarAlign.Default!;

    public PolarAlignSettingsDto GetPolarAlignSettings() {
        lock (_lock) { return _polarAlign; }
    }

    public void PutPolarAlignSettings(PolarAlignSettingsDto value) {
        lock (_lock) { _polarAlign = value; }
        RaiseChanged();
    }

    private UnguidedSettingsDto _unguided = ProfileSections.Unguided.Default!;

    public UnguidedSettingsDto GetUnguidedSettings() {
        lock (_lock) { return _unguided; }
    }

    public void PutUnguidedSettings(UnguidedSettingsDto value) {
        lock (_lock) { _unguided = value; }
        RaiseChanged();
    }

    private GuiderAdvancedSettingsDto _guiderAdvanced = ProfileSections.GuiderAdvanced.Default!;

    public GuiderAdvancedSettingsDto GetGuiderAdvancedSettings() {
        lock (_lock) { return _guiderAdvanced; }
    }

    public void PutGuiderAdvancedSettings(GuiderAdvancedSettingsDto value) {
        lock (_lock) { _guiderAdvanced = value; }
        RaiseChanged();
    }

    private PlateSolveSettingsDto _plateSolve = ProfileSections.PlateSolve.Default!;

    public PlateSolveSettingsDto GetPlateSolveSettings() {
        lock (_lock) { return _plateSolve; }
    }

    public void PutPlateSolveSettings(PlateSolveSettingsDto value) {
        lock (_lock) { _plateSolve = value; }
        RaiseChanged();
    }

    private DiagnosticsModeDto _diagnosticsMode = ProfileSections.DiagnosticsMode.Default!;

    public DiagnosticsModeDto GetDiagnosticsMode() {
        lock (_lock) { return _diagnosticsMode; }
    }

    public void PutDiagnosticsMode(DiagnosticsModeDto value) {
        lock (_lock) { _diagnosticsMode = value; }
        RaiseChanged();
    }

    private Phd2SettingsDto _phd2 = ProfileSections.Phd2.Default!;

    public Phd2SettingsDto GetPhd2Settings() {
        lock (_lock) { return _phd2; }
    }

    public void PutPhd2Settings(Phd2SettingsDto value) {
        lock (_lock) { _phd2 = value; }
        RaiseChanged();
    }

    public Phd2SettingsDto UpdatePhd2Settings(Func<Phd2SettingsDto, Phd2SettingsDto?> update) {
        Phd2SettingsDto next;
        lock (_lock) {
            var current = _phd2;
            var candidate = update(current);
            if (candidate is null || candidate == current) {
                return current; // no change — no event
            }
            next = candidate;
            _phd2 = next;
        }
        RaiseChanged();
        return next;
    }

    private OpticsSettingsDto _optics = ProfileSections.Optics.Default!;

    public OpticsSettingsDto GetOpticsSettings() {
        lock (_lock) { return _optics; }
    }

    public void PutOpticsSettings(OpticsSettingsDto value) {
        lock (_lock) {
            SetOpticsLocked(value);
        }
        RaiseChanged();
    }

    public OpticsSettingsDto UpdateOpticsSettings(Func<OpticsSettingsDto, OpticsSettingsDto?> update) {
        OpticsSettingsDto next;
        lock (_lock) {
            var current = _optics;
            var candidate = update(current);
            if (candidate is null || candidate == current) {
                return current; // no change — no event
            }
            next = candidate;
            SetOpticsLocked(next);
        }
        RaiseChanged();
        return next;
    }

    // §58.8 — mirror FileProfileStore.WithOptics: an optics-train change invalidates the
    // first-flip confirmation; an identical re-save keeps it. Shared by the Put AND Update
    // paths — the camera-connect auto-populate (CameraService.MaybeAutoPopulateOptics) writes
    // through Update, and a swapped camera is exactly the rig change the safety net re-arms
    // on. Caller holds _lock.
    private void SetOpticsLocked(OpticsSettingsDto value) {
        if (value != _optics && _safety.FirstFlipConfirmed) {
            _safety = _safety with { FirstFlipConfirmed = false };
        }
        _optics = value;
    }

    private EquipmentConnectionDto _equipmentConnection = ProfileSections.EquipmentConnection.Default!;

    public EquipmentConnectionDto GetEquipmentConnection() {
        lock (_lock) { return _equipmentConnection; }
    }

    public void PutEquipmentConnection(EquipmentConnectionDto value) {
        lock (_lock) { _equipmentConnection = value; }
        RaiseChanged();
    }

    private CameraElectronicsDto _cameraElectronics = ProfileSections.CameraElectronics.Default!;

    public CameraElectronicsDto GetCameraElectronics() {
        lock (_lock) { return _cameraElectronics; }
    }

    public void PutCameraElectronics(CameraElectronicsDto value) {
        lock (_lock) { _cameraElectronics = value; }
        RaiseChanged();
    }

    public CameraElectronicsDto UpdateCameraElectronics(Func<CameraElectronicsDto, CameraElectronicsDto?> update) {
        CameraElectronicsDto next;
        lock (_lock) {
            var current = _cameraElectronics;
            var candidate = update(current);
            if (candidate is null || candidate == current) {
                return current; // no change — no event
            }
            next = candidate;
            _cameraElectronics = next;
        }
        RaiseChanged();
        return next;
    }

    private FilterSetDto _filterSet = ProfileSections.FilterSet.Default!;

    public FilterSetDto GetFilterSet() {
        lock (_lock) { return _filterSet; }
    }

    public void PutFilterSet(FilterSetDto value) {
        lock (_lock) { _filterSet = value; }
        RaiseChanged();
    }

    private FilterWheelLabelsDto _filterWheelLabels = ProfileSections.FilterWheelLabels.Default!;

    public FilterWheelLabelsDto GetFilterWheelLabels() {
        lock (_lock) { return _filterWheelLabels; }
    }

    public void PutFilterWheelLabels(FilterWheelLabelsDto value) {
        lock (_lock) { _filterWheelLabels = value; }
        RaiseChanged();
    }

    private FilterWheelPolicyDto _filterWheelPolicy = ProfileSections.FilterWheelPolicy.Default!;

    public FilterWheelPolicyDto GetFilterWheelPolicy() {
        lock (_lock) { return _filterWheelPolicy; }
    }

    public void PutFilterWheelPolicy(FilterWheelPolicyDto value) {
        lock (_lock) { _filterWheelPolicy = value; }
        RaiseChanged();
    }

    private CoolingFanDto _coolingFan = ProfileSections.CoolingFan.Default!;

    public CoolingFanDto GetCoolingFan() {
        lock (_lock) { return _coolingFan; }
    }

    public void PutCoolingFan(CoolingFanDto value) {
        lock (_lock) { _coolingFan = value; }
        RaiseChanged();
    }

    private FilterFocusOffsetsDto _filterFocusOffsets = ProfileSections.FilterFocusOffsets.Default!;

    public FilterFocusOffsetsDto GetFilterFocusOffsets() {
        lock (_lock) { return _filterFocusOffsets; }
    }

    public void PutFilterFocusOffsets(FilterFocusOffsetsDto value) {
        lock (_lock) { _filterFocusOffsets = value; }
        RaiseChanged();
    }

    private DomeSettingsDto _dome = ProfileSections.Dome.Default!;

    public DomeSettingsDto GetDomeSettings() {
        lock (_lock) { return _dome; }
    }

    public void PutDomeSettings(DomeSettingsDto value) {
        lock (_lock) { _dome = value; }
        RaiseChanged();
    }

    private CustomHorizonDto _customHorizon = ProfileSections.CustomHorizon.Default!;

    public CustomHorizonDto GetCustomHorizon() {
        lock (_lock) { return _customHorizon; }
    }

    public void PutCustomHorizon(CustomHorizonDto value) {
        lock (_lock) { _customHorizon = value; }
        RaiseChanged();
    }

    private FocusCalibrationDto? _focusCalibration = ProfileSections.FocusCalibration.Default;

    public FocusCalibrationDto? GetFocusCalibration() {
        lock (_lock) { return _focusCalibration; }
    }

    public void PutFocusCalibration(FocusCalibrationDto? value) {
        lock (_lock) { _focusCalibration = value; }
        RaiseChanged();
    }

    private TrackingCalibrationDto? _trackingCalibration = ProfileSections.TrackingCalibration.Default;

    public TrackingCalibrationDto? GetTrackingCalibration() {
        lock (_lock) { return _trackingCalibration; }
    }

    public void PutTrackingCalibration(TrackingCalibrationDto? value) {
        lock (_lock) { _trackingCalibration = value; }
        RaiseChanged();
    }

    private SkyModelCalibrationDto? _skyModelCalibration = ProfileSections.SkyModelCalibration.Default;

    public SkyModelCalibrationDto? GetSkyModelCalibration() {
        lock (_lock) { return _skyModelCalibration; }
    }

    public void PutSkyModelCalibration(SkyModelCalibrationDto? value) {
        lock (_lock) { _skyModelCalibration = value; }
        RaiseChanged();
    }

    private CalibrationStateDto _calibrationState = ProfileSections.CalibrationState.Default!;

    public CalibrationStateDto GetCalibrationState() {
        lock (_lock) { return _calibrationState; }
    }

    public void PutCalibrationState(CalibrationStateDto value) {
        lock (_lock) { _calibrationState = value; }
        RaiseChanged();
    }

    private EquipmentIdentityDto _equipmentIdentity = ProfileSections.EquipmentIdentity.Default!;

    public EquipmentIdentityDto GetEquipmentIdentity() {
        lock (_lock) { return _equipmentIdentity; }
    }

    public void PutEquipmentIdentity(EquipmentIdentityDto value) {
        lock (_lock) { _equipmentIdentity = value ?? ProfileSections.EquipmentIdentity.Default!; }
        RaiseChanged();
    }

    private SavedTargetsDto _savedTargets = ProfileSections.SavedTargets.Default!;

    public SavedTargetsDto GetSavedTargets() {
        lock (_lock) { return _savedTargets; }
    }

    public void PutSavedTargets(SavedTargetsDto value) {
        lock (_lock) { _savedTargets = value ?? ProfileSections.SavedTargets.Default!; }
        RaiseChanged();
    }

    private StretchDefaultsDto _stretchDefaults = ProfileSections.StretchDefaults.Default!;

    public StretchDefaultsDto GetStretchDefaults() {
        lock (_lock) { return _stretchDefaults; }
    }

    public void PutStretchDefaults(StretchDefaultsDto value) {
        lock (_lock) { _stretchDefaults = value; }
        RaiseChanged();
    }
}