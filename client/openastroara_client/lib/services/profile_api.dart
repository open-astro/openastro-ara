import 'package:dio/dio.dart';

import '../models/server.dart';
import '../state/imaging/exposure_state.dart' show FrameKind;
import '../state/settings/imaging_defaults_state.dart';
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
        filenameTemplate: (j['filename_template'] as String?) ??
            r'$$DATEMINUS12$$\$$IMAGETYPE$$\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s',
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
}
