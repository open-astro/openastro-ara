import 'package:dio/dio.dart';

import '../models/server.dart';
import '../state/imaging/exposure_state.dart' show FrameKind;
import '../state/settings/autofocus_settings_state.dart';
import '../state/settings/diagnostics_mode_state.dart';
import '../state/settings/equipment_connection_state.dart';
import '../state/settings/filenames_settings_state.dart';
import '../state/settings/imaging_defaults_state.dart';
import '../state/settings/notifications_settings_state.dart';
import '../state/settings/optics_settings_state.dart';
import '../state/settings/phd2_settings_state.dart';
import '../state/settings/plate_solve_settings_state.dart';
import '../state/settings/safety_policies_state.dart';
import '../state/settings/site_settings_state.dart';
import '../state/settings/storage_settings_state.dart';

/// Client-side wrapper around §37 profile endpoints. Phase 12h.6a landed the
/// daemon side (`GET`/`PUT /api/v1/profile/imaging-defaults` backed by an
/// in-memory store); this class is the WILMA-side counterpart that the
/// settings panels call to round-trip their state.
///
/// Each profile section is one method pair (`get…` / `put…`). 12h.6b ships
/// imaging-defaults only; storage/notifications/site/etc follow as 12h.6c-N.
class ProfileApi {
  final Dio _dio;

  ProfileApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 5),
        ));

  /// GET the active profile's imaging-defaults section.
  Future<ImagingDefaults> getImagingDefaults() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/imaging-defaults',
    );
    return _imagingDefaultsFromJson(res.data ?? const {});
  }

  /// PUT the active profile's imaging-defaults section. Returns the daemon's
  /// echo so the caller can confirm what was persisted.
  Future<ImagingDefaults> putImagingDefaults(ImagingDefaults value) async {
    final res = await _dio.put<Map<String, dynamic>>(
      '/api/v1/profile/imaging-defaults',
      data: _imagingDefaultsToJson(value),
    );
    return _imagingDefaultsFromJson(res.data ?? const {});
  }

  /// GET the active profile's optics section (§36 — FOV geometry inputs).
  Future<OpticsSettings> getOptics() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/optics',
    );
    return _opticsFromJson(res.data ?? const {});
  }

  /// PUT the active profile's optics section. Returns the daemon's echo (it may
  /// normalize fields — e.g. it rejects reducer_factor ≤ 0).
  Future<OpticsSettings> putOptics(OpticsSettings value) async {
    final res = await _dio.put<Map<String, dynamic>>(
      '/api/v1/profile/optics',
      data: _opticsToJson(value),
    );
    return _opticsFromJson(res.data ?? const {});
  }

  /// GET the active profile's storage-settings section.
  Future<StorageSettings> getStorageSettings() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/storage',
    );
    return _storageSettingsFromJson(res.data ?? const {});
  }

  /// PUT the active profile's storage-settings section.
  Future<StorageSettings> putStorageSettings(StorageSettings value) async {
    final res = await _dio.put<Map<String, dynamic>>(
      '/api/v1/profile/storage',
      data: _storageSettingsToJson(value),
    );
    return _storageSettingsFromJson(res.data ?? const {});
  }

  /// GET the active profile's notifications-settings section.
  Future<NotificationsSettings> getNotificationsSettings() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/notifications',
    );
    return _notificationsSettingsFromJson(res.data ?? const {});
  }

  /// PUT the active profile's notifications-settings section.
  Future<NotificationsSettings> putNotificationsSettings(
      NotificationsSettings value) async {
    final res = await _dio.put<Map<String, dynamic>>(
      '/api/v1/profile/notifications',
      data: _notificationsSettingsToJson(value),
    );
    return _notificationsSettingsFromJson(res.data ?? const {});
  }

  /// GET the active profile's site-settings section.
  Future<SiteSettings> getSiteSettings() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/site',
    );
    return _siteSettingsFromJson(res.data ?? const {});
  }

  /// PUT the active profile's site-settings section.
  Future<SiteSettings> putSiteSettings(SiteSettings value) async {
    final res = await _dio.put<Map<String, dynamic>>(
      '/api/v1/profile/site',
      data: _siteSettingsToJson(value),
    );
    return _siteSettingsFromJson(res.data ?? const {});
  }

  /// GET the active profile's filenames-settings section.
  Future<FilenamesSettings> getFilenamesSettings() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/filenames',
    );
    return _filenamesSettingsFromJson(res.data ?? const {});
  }

  /// PUT the active profile's filenames-settings section.
  Future<FilenamesSettings> putFilenamesSettings(FilenamesSettings value) async {
    final res = await _dio.put<Map<String, dynamic>>(
      '/api/v1/profile/filenames',
      data: _filenamesSettingsToJson(value),
    );
    return _filenamesSettingsFromJson(res.data ?? const {});
  }

  /// GET the active profile's safety policies.
  Future<SafetyPolicies> getSafetyPolicies() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/safety',
    );
    return _safetyPoliciesFromJson(res.data ?? const {});
  }

  /// PUT the active profile's safety policies.
  Future<SafetyPolicies> putSafetyPolicies(SafetyPolicies value) async {
    final res = await _dio.put<Map<String, dynamic>>(
      '/api/v1/profile/safety',
      data: _safetyPoliciesToJson(value),
    );
    return _safetyPoliciesFromJson(res.data ?? const {});
  }

  /// GET the active profile's autofocus settings.
  Future<AutofocusSettings> getAutofocusSettings() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/autofocus',
    );
    return _autofocusSettingsFromJson(res.data ?? const {});
  }

  /// PUT the active profile's autofocus settings.
  Future<AutofocusSettings> putAutofocusSettings(AutofocusSettings value) async {
    final res = await _dio.put<Map<String, dynamic>>(
      '/api/v1/profile/autofocus',
      data: _autofocusSettingsToJson(value),
    );
    return _autofocusSettingsFromJson(res.data ?? const {});
  }

  /// GET the active profile's plate-solve settings.
  Future<PlateSolveSettings> getPlateSolveSettings() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/plate-solve',
    );
    return _plateSolveSettingsFromJson(res.data ?? const {});
  }

  /// PUT the active profile's plate-solve settings.
  Future<PlateSolveSettings> putPlateSolveSettings(
      PlateSolveSettings value) async {
    final res = await _dio.put<Map<String, dynamic>>(
      '/api/v1/profile/plate-solve',
      data: _plateSolveSettingsToJson(value),
    );
    return _plateSolveSettingsFromJson(res.data ?? const {});
  }

  /// GET the active profile's diagnostics mode.
  Future<DiagnosticsMode> getDiagnosticsMode() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/diagnostics-mode',
    );
    return _diagnosticsModeFromString((res.data ?? const {})['mode'] as String?);
  }

  /// PUT the active profile's diagnostics mode.
  Future<DiagnosticsMode> putDiagnosticsMode(DiagnosticsMode value) async {
    final res = await _dio.put<Map<String, dynamic>>(
      '/api/v1/profile/diagnostics-mode',
      data: {'mode': _diagnosticsModeToString(value)},
    );
    return _diagnosticsModeFromString((res.data ?? const {})['mode'] as String?);
  }

  /// GET the active profile's PHD2 settings.
  Future<Phd2Settings> getPhd2Settings() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/phd2',
    );
    return _phd2SettingsFromJson(res.data ?? const {});
  }

  /// PUT the active profile's PHD2 settings.
  Future<Phd2Settings> putPhd2Settings(Phd2Settings value) async {
    final res = await _dio.put<Map<String, dynamic>>(
      '/api/v1/profile/phd2',
      data: _phd2SettingsToJson(value),
    );
    return _phd2SettingsFromJson(res.data ?? const {});
  }

  /// GET the active profile's equipment auto-connect bools.
  Future<EquipmentConnectionSettings> getEquipmentConnection() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/equipment-connection',
    );
    return _equipmentConnectionFromJson(res.data ?? const {});
  }

  /// PUT the active profile's equipment auto-connect bools.
  Future<EquipmentConnectionSettings> putEquipmentConnection(
      EquipmentConnectionSettings value) async {
    final res = await _dio.put<Map<String, dynamic>>(
      '/api/v1/profile/equipment-connection',
      data: _equipmentConnectionToJson(value),
    );
    return _equipmentConnectionFromJson(res.data ?? const {});
  }

  // ── JSON mapping ────────────────────────────────────────────────────────
  // The server's `ConfigureHttpJsonOptions` uses snake_case, so the wire
  // shape is `exposure_seconds` etc. (not `defaultExposure`).

  static ImagingDefaults _imagingDefaultsFromJson(Map<String, dynamic> j) =>
      ImagingDefaults(
        defaultExposure: Duration(seconds: (j['exposure_seconds'] as num?)?.toInt() ?? 5),
        defaultGain: (j['gain'] as num?)?.toInt() ?? 100,
        defaultOffset: (j['offset'] as num?)?.toInt() ?? 50,
        defaultBin: (j['bin'] as num?)?.toInt() ?? 1,
        defaultFrameKind: _frameKindFromString(j['frame_kind'] as String?),
        coolerTargetC: (j['cooler_target_c'] as num?)?.toDouble() ?? -10.0,
        coolerRampRatePerMin:
            (j['cooler_ramp_c_per_min'] as num?)?.toDouble() ?? 1.0,
        warmupAtSessionEnd: (j['warmup_at_session_end'] as bool?) ?? false,
      );

  static Map<String, dynamic> _imagingDefaultsToJson(ImagingDefaults v) => {
        'exposure_seconds': v.defaultExposure.inSeconds,
        'gain': v.defaultGain,
        'offset': v.defaultOffset,
        'bin': v.defaultBin,
        'frame_kind': v.defaultFrameKind.name,
        'cooler_target_c': v.coolerTargetC,
        'cooler_ramp_c_per_min': v.coolerRampRatePerMin,
        'warmup_at_session_end': v.warmupAtSessionEnd,
      };

  static FrameKind _frameKindFromString(String? s) {
    switch (s) {
      case 'dark':
        return FrameKind.dark;
      case 'bias':
        return FrameKind.bias;
      case 'flat':
        return FrameKind.flat;
      case 'light':
      default:
        return FrameKind.light;
    }
  }

  // ── Storage settings JSON mapping ──────────────────────────────────────

  static OpticsSettings _opticsFromJson(Map<String, dynamic> j) => OpticsSettings(
        focalLengthMm: (j['focal_length_mm'] as num?)?.toDouble() ?? 0,
        reducerFactor: (j['reducer_factor'] as num?)?.toDouble() ?? 1.0,
        sensorWidthPx: (j['sensor_width_px'] as num?)?.toInt() ?? 0,
        sensorHeightPx: (j['sensor_height_px'] as num?)?.toInt() ?? 0,
        pixelSizeUm: (j['pixel_size_um'] as num?)?.toDouble() ?? 0,
      );

  static Map<String, dynamic> _opticsToJson(OpticsSettings v) => {
        'focal_length_mm': v.focalLengthMm,
        'reducer_factor': v.reducerFactor,
        'sensor_width_px': v.sensorWidthPx,
        'sensor_height_px': v.sensorHeightPx,
        'pixel_size_um': v.pixelSizeUm,
      };

  static StorageSettings _storageSettingsFromJson(Map<String, dynamic> j) =>
      StorageSettings(
        saveDirectory: (j['save_directory'] as String?) ?? '/media/openastroara',
        fileFormat: _fileFormatFromString(j['file_format'] as String?),
        compression: _compressionFromString(j['compression'] as String?),
        // Fallback matches the StorageSettings() constructor default
        // (raw-string literal with `\\` double-backslash separators).
        filenameTemplate: (j['filename_template'] as String?) ??
            r'$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s',
        minFreeDiskWarnGb: (j['min_free_disk_warn_gb'] as num?)?.toInt() ?? 10,
        minFreeDiskCriticalGb: (j['min_free_disk_critical_gb'] as num?)?.toInt() ?? 2,
      );

  static Map<String, dynamic> _storageSettingsToJson(StorageSettings v) => {
        'save_directory': v.saveDirectory,
        'file_format': _fileFormatToString(v.fileFormat),
        'compression': v.compression.name,
        'filename_template': v.filenameTemplate,
        'min_free_disk_warn_gb': v.minFreeDiskWarnGb,
        'min_free_disk_critical_gb': v.minFreeDiskCriticalGb,
      };

  static StorageFileFormat _fileFormatFromString(String? s) {
    switch (s) {
      case 'xisf':
        return StorageFileFormat.xisf;
      case 'fits_rice':
        return StorageFileFormat.fitsRice;
      case 'fits_gzip':
        return StorageFileFormat.fitsGzip;
      case 'fits':
      default:
        return StorageFileFormat.fits;
    }
  }

  static String _fileFormatToString(StorageFileFormat f) {
    switch (f) {
      case StorageFileFormat.fits:
        return 'fits';
      case StorageFileFormat.xisf:
        return 'xisf';
      case StorageFileFormat.fitsRice:
        return 'fits_rice';
      case StorageFileFormat.fitsGzip:
        return 'fits_gzip';
    }
  }

  static StorageCompression _compressionFromString(String? s) {
    switch (s) {
      case 'off':
        return StorageCompression.off;
      case 'gzip':
        return StorageCompression.gzip;
      case 'rice':
      default:
        return StorageCompression.rice;
    }
  }

  // ── Equipment-connection JSON mapping ──────────────────────────────────

  static EquipmentConnectionSettings _equipmentConnectionFromJson(
      Map<String, dynamic> j) {
    bool read(String key, bool dflt) => (j[key] as bool?) ?? dflt;
    return EquipmentConnectionSettings(autoConnectOnBoot: {
      EquipmentDeviceType.camera: read('camera', true),
      EquipmentDeviceType.mount: read('mount', true),
      EquipmentDeviceType.focuser: read('focuser', true),
      EquipmentDeviceType.filterWheel: read('filter_wheel', true),
      EquipmentDeviceType.rotator: read('rotator', true),
      EquipmentDeviceType.guider: read('guider', false),
      EquipmentDeviceType.flatPanel: read('flat_panel', true),
      EquipmentDeviceType.dome: read('dome', false),
      EquipmentDeviceType.weather: read('weather', false),
      EquipmentDeviceType.safetyMonitor: read('safety_monitor', true),
    });
  }

  static Map<String, dynamic> _equipmentConnectionToJson(
          EquipmentConnectionSettings v) =>
      {
        'camera': v.autoConnect(EquipmentDeviceType.camera),
        'mount': v.autoConnect(EquipmentDeviceType.mount),
        'focuser': v.autoConnect(EquipmentDeviceType.focuser),
        'filter_wheel': v.autoConnect(EquipmentDeviceType.filterWheel),
        'rotator': v.autoConnect(EquipmentDeviceType.rotator),
        'guider': v.autoConnect(EquipmentDeviceType.guider),
        'flat_panel': v.autoConnect(EquipmentDeviceType.flatPanel),
        'dome': v.autoConnect(EquipmentDeviceType.dome),
        'weather': v.autoConnect(EquipmentDeviceType.weather),
        'safety_monitor': v.autoConnect(EquipmentDeviceType.safetyMonitor),
      };

  // ── PHD2 settings JSON mapping ─────────────────────────────────────────

  static Phd2Settings _phd2SettingsFromJson(Map<String, dynamic> j) =>
      Phd2Settings(
        host: (j['host'] as String?) ?? 'localhost',
        port: (j['port'] as num?)?.toInt() ?? 4400,
        phd2Profile: (j['phd2_profile'] as String?) ?? 'Default',
        ditherEnabled: (j['dither_enabled'] as bool?) ?? true,
        ditherEveryNFrames:
            (j['dither_every_n_frames'] as num?)?.toInt() ?? 1,
        ditherPixels: (j['dither_pixels'] as num?)?.toDouble() ?? 5.0,
        settlePixels: (j['settle_pixels'] as num?)?.toDouble() ?? 1.5,
        settleTimeSec: (j['settle_time_sec'] as num?)?.toInt() ?? 10,
        settleTimeoutSec: (j['settle_timeout_sec'] as num?)?.toInt() ?? 60,
        forceCalibrationEachSession:
            (j['force_calibration_each_session'] as bool?) ?? false,
        // §63.5 guider-engine config (defaults match the server's optional-field defaults).
        guideFocalLength: (j['guide_focal_length'] as num?)?.toInt() ?? 0,
        guidePixelSize: (j['guide_pixel_size'] as num?)?.toDouble() ?? 0,
        raAggressiveness: (j['ra_aggressiveness'] as num?)?.toDouble() ?? 0.7,
        decAggressiveness: (j['dec_aggressiveness'] as num?)?.toDouble() ?? 0.7,
        minimumMove: (j['minimum_move'] as num?)?.toDouble() ?? 0.15,
        decGuideMode: (j['dec_guide_mode'] as String?) ?? 'auto',
      );

  static Map<String, dynamic> _phd2SettingsToJson(Phd2Settings v) => {
        'host': v.host,
        'port': v.port,
        'phd2_profile': v.phd2Profile,
        'dither_enabled': v.ditherEnabled,
        'dither_every_n_frames': v.ditherEveryNFrames,
        'dither_pixels': v.ditherPixels,
        'settle_pixels': v.settlePixels,
        'settle_time_sec': v.settleTimeSec,
        'settle_timeout_sec': v.settleTimeoutSec,
        'force_calibration_each_session': v.forceCalibrationEachSession,
        'guide_focal_length': v.guideFocalLength,
        'guide_pixel_size': v.guidePixelSize,
        'ra_aggressiveness': v.raAggressiveness,
        'dec_aggressiveness': v.decAggressiveness,
        'minimum_move': v.minimumMove,
        'dec_guide_mode': v.decGuideMode,
      };

  // ── Diagnostics mode JSON mapping ──────────────────────────────────────

  static DiagnosticsMode _diagnosticsModeFromString(String? s) {
    switch (s) {
      case 'pause_on_critical':
        return DiagnosticsMode.pauseOnCritical;
      case 'abort_on_critical':
        return DiagnosticsMode.abortOnCritical;
      case 'notify_only':
      default:
        return DiagnosticsMode.notifyOnly;
    }
  }

  static String _diagnosticsModeToString(DiagnosticsMode m) {
    switch (m) {
      case DiagnosticsMode.notifyOnly:
        return 'notify_only';
      case DiagnosticsMode.pauseOnCritical:
        return 'pause_on_critical';
      case DiagnosticsMode.abortOnCritical:
        return 'abort_on_critical';
    }
  }

  // ── Plate-solve settings JSON mapping ──────────────────────────────────

  static PlateSolveSettings _plateSolveSettingsFromJson(Map<String, dynamic> j) =>
      PlateSolveSettings(
        engine: _plateSolveEngineFromString(j['engine'] as String?),
        pathOrEndpoint: (j['path_or_endpoint'] as String?) ?? '/usr/bin/astap',
        indexDownloadPath:
            (j['index_download_path'] as String?) ?? '/var/lib/astap',
        searchRadiusDeg: (j['search_radius_deg'] as num?)?.toDouble() ?? 30,
        downsampleFactor: (j['downsample_factor'] as num?)?.toInt() ?? 2,
        timeoutSeconds: (j['timeout_seconds'] as num?)?.toInt() ?? 60,
        useBlindFallback: (j['use_blind_fallback'] as bool?) ?? true,
        centerAfterSlew: (j['center_after_slew'] as bool?) ?? true,
        syncToCoordinates: (j['sync_to_coordinates'] as bool?) ?? true,
        maxIterations: (j['max_iterations'] as num?)?.toInt() ?? 5,
        convergenceToleranceArcsec:
            (j['convergence_tolerance_arcsec'] as num?)?.toDouble() ?? 60.0,
      );

  static Map<String, dynamic> _plateSolveSettingsToJson(PlateSolveSettings v) =>
      {
        'engine': _plateSolveEngineToString(v.engine),
        'path_or_endpoint': v.pathOrEndpoint,
        'index_download_path': v.indexDownloadPath,
        'search_radius_deg': v.searchRadiusDeg,
        'downsample_factor': v.downsampleFactor,
        'timeout_seconds': v.timeoutSeconds,
        'use_blind_fallback': v.useBlindFallback,
        'center_after_slew': v.centerAfterSlew,
        'sync_to_coordinates': v.syncToCoordinates,
        'max_iterations': v.maxIterations,
        'convergence_tolerance_arcsec': v.convergenceToleranceArcsec,
      };

  static PlateSolveEngine _plateSolveEngineFromString(String? s) {
    switch (s) {
      case 'astrometry_net':
        return PlateSolveEngine.astrometryNet;
      case 'platesolve2':
        return PlateSolveEngine.platesolve2;
      case 'astap':
      default:
        return PlateSolveEngine.astap;
    }
  }

  static String _plateSolveEngineToString(PlateSolveEngine e) {
    switch (e) {
      case PlateSolveEngine.astap:
        return 'astap';
      case PlateSolveEngine.astrometryNet:
        return 'astrometry_net';
      case PlateSolveEngine.platesolve2:
        return 'platesolve2';
    }
  }

  // ── Autofocus settings JSON mapping ────────────────────────────────────

  static AutofocusSettings _autofocusSettingsFromJson(Map<String, dynamic> j) =>
      AutofocusSettings(
        method: _autofocusMethodFromString(j['method'] as String?),
        steps: (j['steps'] as num?)?.toInt() ?? 7,
        stepSize: (j['step_size'] as num?)?.toInt() ?? 50,
        exposureSeconds: (j['exposure_seconds'] as num?)?.toInt() ?? 5,
        binning: (j['binning'] as num?)?.toInt() ?? 1,
        afFilter: (j['af_filter'] as String?) ?? 'L',
        runAfterFilterChange: (j['run_after_filter_change'] as bool?) ?? true,
        triggerTempDeltaC:
            (j['trigger_temp_delta_c'] as num?)?.toDouble() ?? 2.0,
        triggerHfrDriftPct:
            (j['trigger_hfr_drift_pct'] as num?)?.toDouble() ?? 15.0,
        everyNHours: (j['every_n_hours'] as num?)?.toInt() ?? 2,
        abortSequenceOnAfFailure:
            (j['abort_sequence_on_af_failure'] as bool?) ?? true,
        restorePositionOnFailure:
            (j['restore_position_on_failure'] as bool?) ?? true,
      );

  static Map<String, dynamic> _autofocusSettingsToJson(AutofocusSettings v) => {
        'method': _autofocusMethodToString(v.method),
        'steps': v.steps,
        'step_size': v.stepSize,
        'exposure_seconds': v.exposureSeconds,
        'binning': v.binning,
        'af_filter': v.afFilter,
        'run_after_filter_change': v.runAfterFilterChange,
        'trigger_temp_delta_c': v.triggerTempDeltaC,
        'trigger_hfr_drift_pct': v.triggerHfrDriftPct,
        'every_n_hours': v.everyNHours,
        'abort_sequence_on_af_failure': v.abortSequenceOnAfFailure,
        'restore_position_on_failure': v.restorePositionOnFailure,
      };

  static AutofocusMethod _autofocusMethodFromString(String? s) {
    switch (s) {
      case 'brightest_star_hfr':
        return AutofocusMethod.brightestStarHfr;
      case 'fwhm':
        return AutofocusMethod.fwhm;
      case 'hfr_v_curve':
      default:
        return AutofocusMethod.hfrVCurve;
    }
  }

  static String _autofocusMethodToString(AutofocusMethod m) {
    switch (m) {
      case AutofocusMethod.hfrVCurve:
        return 'hfr_v_curve';
      case AutofocusMethod.brightestStarHfr:
        return 'brightest_star_hfr';
      case AutofocusMethod.fwhm:
        return 'fwhm';
    }
  }

  // ── Safety policies JSON mapping ───────────────────────────────────────

  static SafetyPolicies _safetyPoliciesFromJson(Map<String, dynamic> j) =>
      SafetyPolicies(
        onUnsafe: _unsafeActionFromString(j['on_unsafe'] as String?),
        autoResumeWhenSafe: (j['auto_resume_when_safe'] as bool?) ?? true,
        resumeDelayMin: (j['resume_delay_min'] as num?)?.toInt() ?? 10,
        meridianFlipAuto: (j['meridian_flip_auto'] as bool?) ?? true,
        meridianPauseMin: (j['meridian_pause_min'] as num?)?.toInt() ?? 5,
        meridianRecenter: (j['meridian_recenter'] as bool?) ?? true,
        meridianRecalGuider: (j['meridian_recal_guider'] as bool?) ?? true,
        onAltitudeLimit:
            _altitudeLimitActionFromString(j['on_altitude_limit'] as String?),
        parkIfNoMoreTargets: (j['park_if_no_more_targets'] as bool?) ?? true,
        onGuiderLost: _guiderLostActionFromString(j['on_guider_lost'] as String?),
        guiderRetryTimeoutSec:
            (j['guider_retry_timeout_sec'] as num?)?.toInt() ?? 60,
        skipTargetIfRecoveryFails:
            (j['skip_target_if_recovery_fails'] as bool?) ?? true,
        onDiskSpaceCritical: (j['on_disk_space_critical'] as String?) == 'abort'
            ? DiskSpaceCriticalAction.abort
            : DiskSpaceCriticalAction.warn,
      );

  static Map<String, dynamic> _safetyPoliciesToJson(SafetyPolicies v) => {
        'on_unsafe': _unsafeActionToString(v.onUnsafe),
        'auto_resume_when_safe': v.autoResumeWhenSafe,
        'resume_delay_min': v.resumeDelayMin,
        'meridian_flip_auto': v.meridianFlipAuto,
        'meridian_pause_min': v.meridianPauseMin,
        'meridian_recenter': v.meridianRecenter,
        'meridian_recal_guider': v.meridianRecalGuider,
        'on_altitude_limit': _altitudeLimitActionToString(v.onAltitudeLimit),
        'park_if_no_more_targets': v.parkIfNoMoreTargets,
        'on_guider_lost': _guiderLostActionToString(v.onGuiderLost),
        'guider_retry_timeout_sec': v.guiderRetryTimeoutSec,
        'skip_target_if_recovery_fails': v.skipTargetIfRecoveryFails,
        // enum.name is 'warn'/'abort' — matches the server's OnDiskSpaceCritical values verbatim.
        'on_disk_space_critical': v.onDiskSpaceCritical.name,
      };

  static UnsafeAction _unsafeActionFromString(String? s) {
    switch (s) {
      case 'park_only':
        return UnsafeAction.parkOnly;
      case 'abort_and_park':
        return UnsafeAction.abortAndPark;
      case 'ignore':
        return UnsafeAction.ignore;
      case 'pause_and_park':
      default:
        return UnsafeAction.pauseAndPark;
    }
  }

  static String _unsafeActionToString(UnsafeAction a) {
    switch (a) {
      case UnsafeAction.pauseAndPark:
        return 'pause_and_park';
      case UnsafeAction.parkOnly:
        return 'park_only';
      case UnsafeAction.abortAndPark:
        return 'abort_and_park';
      case UnsafeAction.ignore:
        return 'ignore';
    }
  }

  static AltitudeLimitAction _altitudeLimitActionFromString(String? s) {
    switch (s) {
      case 'pause_sequence':
        return AltitudeLimitAction.pauseSequence;
      case 'abort_sequence':
        return AltitudeLimitAction.abortSequence;
      case 'skip_target':
      default:
        return AltitudeLimitAction.skipTarget;
    }
  }

  static String _altitudeLimitActionToString(AltitudeLimitAction a) {
    switch (a) {
      case AltitudeLimitAction.skipTarget:
        return 'skip_target';
      case AltitudeLimitAction.pauseSequence:
        return 'pause_sequence';
      case AltitudeLimitAction.abortSequence:
        return 'abort_sequence';
    }
  }

  static GuiderLostAction _guiderLostActionFromString(String? s) {
    switch (s) {
      case 'skip_target':
        return GuiderLostAction.skipTarget;
      case 'abort_sequence':
        return GuiderLostAction.abortSequence;
      case 'pause_and_retry':
      default:
        return GuiderLostAction.pauseAndRetry;
    }
  }

  static String _guiderLostActionToString(GuiderLostAction a) {
    switch (a) {
      case GuiderLostAction.pauseAndRetry:
        return 'pause_and_retry';
      case GuiderLostAction.skipTarget:
        return 'skip_target';
      case GuiderLostAction.abortSequence:
        return 'abort_sequence';
    }
  }

  // ── Filenames settings JSON mapping ────────────────────────────────────

  static FilenamesSettings _filenamesSettingsFromJson(Map<String, dynamic> j) =>
      FilenamesSettings(
        dateSeparator: _dateSeparatorFromString(j['date_separator'] as String?),
        compressDarksAndBias: (j['compress_darks_and_bias'] as bool?) ?? true,
      );

  static Map<String, dynamic> _filenamesSettingsToJson(FilenamesSettings v) => {
        'date_separator': _dateSeparatorToString(v.dateSeparator),
        'compress_darks_and_bias': v.compressDarksAndBias,
      };

  static DateSeparator _dateSeparatorFromString(String? s) {
    switch (s) {
      case 'underscore':
        return DateSeparator.underscore;
      case 'dash':
        return DateSeparator.dash;
      case 'forward_slash':
      default:
        return DateSeparator.forwardSlash;
    }
  }

  static String _dateSeparatorToString(DateSeparator d) {
    switch (d) {
      case DateSeparator.forwardSlash:
        return 'forward_slash';
      case DateSeparator.underscore:
        return 'underscore';
      case DateSeparator.dash:
        return 'dash';
    }
  }

  // ── Site settings JSON mapping ─────────────────────────────────────────

  static SiteSettings _siteSettingsFromJson(Map<String, dynamic> j) =>
      SiteSettings(
        siteName: (j['site_name'] as String?) ?? 'Backyard',
        latitudeDeg: (j['latitude_deg'] as num?)?.toDouble() ?? 0,
        longitudeDeg: (j['longitude_deg'] as num?)?.toDouble() ?? 0,
        elevationM: (j['elevation_m'] as num?)?.toDouble() ?? 0,
        timeZone: (j['time_zone'] as String?) ?? 'UTC',
        useCustomHorizon: (j['use_custom_horizon'] as bool?) ?? false,
        defaultHorizonAltitudeDeg:
            (j['default_horizon_altitude_deg'] as num?)?.toDouble() ?? 20,
        bortleClass: (j['bortle_class'] as num?)?.toInt() ?? 6,
        typicalSeeingArcsec:
            (j['typical_seeing_arcsec'] as num?)?.toDouble() ?? 2.5,
        twilightDefinition:
            _twilightFromString(j['twilight_definition'] as String?),
      );

  static Map<String, dynamic> _siteSettingsToJson(SiteSettings v) => {
        'site_name': v.siteName,
        'latitude_deg': v.latitudeDeg,
        'longitude_deg': v.longitudeDeg,
        'elevation_m': v.elevationM,
        'time_zone': v.timeZone,
        'use_custom_horizon': v.useCustomHorizon,
        'default_horizon_altitude_deg': v.defaultHorizonAltitudeDeg,
        'bortle_class': v.bortleClass,
        'typical_seeing_arcsec': v.typicalSeeingArcsec,
        'twilight_definition': v.twilightDefinition.name,
      };

  static TwilightDefinition _twilightFromString(String? s) {
    switch (s) {
      case 'civil':
        return TwilightDefinition.civil;
      case 'nautical':
        return TwilightDefinition.nautical;
      case 'astronomical':
      default:
        return TwilightDefinition.astronomical;
    }
  }

  // ── Notifications settings JSON mapping ────────────────────────────────

  static NotificationsSettings _notificationsSettingsFromJson(
          Map<String, dynamic> j) =>
      NotificationsSettings(
        inAppBanner: (j['in_app_banner'] as bool?) ?? true,
        osDesktop: (j['os_desktop'] as bool?) ?? true,
        soundAlert: (j['sound_alert'] as bool?) ?? true,
        pushoverToken: (j['pushover_token'] as String?) ?? '',
        telegramBotToken: (j['telegram_bot_token'] as String?) ?? '',
        onSequenceComplete: (j['on_sequence_complete'] as bool?) ?? true,
        onSequencePaused: (j['on_sequence_paused'] as bool?) ?? true,
        onCriticalDiagnostic: (j['on_critical_diagnostic'] as bool?) ?? true,
        onSafetyEvent: (j['on_safety_event'] as bool?) ?? true,
        onAutofocusFailed: (j['on_autofocus_failed'] as bool?) ?? true,
        onPlateSolveFailed: (j['on_plate_solve_failed'] as bool?) ?? true,
        onDiskSpaceLow: (j['on_disk_space_low'] as bool?) ?? true,
      );

  static Map<String, dynamic> _notificationsSettingsToJson(
          NotificationsSettings v) =>
      {
        'in_app_banner': v.inAppBanner,
        'os_desktop': v.osDesktop,
        'sound_alert': v.soundAlert,
        'pushover_token': v.pushoverToken,
        'telegram_bot_token': v.telegramBotToken,
        'on_sequence_complete': v.onSequenceComplete,
        'on_sequence_paused': v.onSequencePaused,
        'on_critical_diagnostic': v.onCriticalDiagnostic,
        'on_safety_event': v.onSafetyEvent,
        'on_autofocus_failed': v.onAutofocusFailed,
        'on_plate_solve_failed': v.onPlateSolveFailed,
        'on_disk_space_low': v.onDiskSpaceLow,
      };
}
