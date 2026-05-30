import 'package:dio/dio.dart';

import '../models/server.dart';
import '../state/imaging/exposure_state.dart' show FrameKind;
import '../state/settings/filenames_settings_state.dart';
import '../state/settings/imaging_defaults_state.dart';
import '../state/settings/notifications_settings_state.dart';
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

  static StorageSettings _storageSettingsFromJson(Map<String, dynamic> j) =>
      StorageSettings(
        saveDirectory: (j['save_directory'] as String?) ?? '/media/openastroara',
        fileFormat: _fileFormatFromString(j['file_format'] as String?),
        compression: _compressionFromString(j['compression'] as String?),
        // Fallback matches the StorageSettings() constructor default
        // (raw-string literal with `\\` double-backslash separators).
        filenameTemplate: (j['filename_template'] as String?) ??
            r'$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s',
      );

  static Map<String, dynamic> _storageSettingsToJson(StorageSettings v) => {
        'save_directory': v.saveDirectory,
        'file_format': _fileFormatToString(v.fileFormat),
        'compression': v.compression.name,
        'filename_template': v.filenameTemplate,
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
