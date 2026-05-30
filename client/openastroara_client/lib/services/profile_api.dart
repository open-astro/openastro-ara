import 'package:dio/dio.dart';

import '../models/server.dart';
import '../state/imaging/exposure_state.dart' show FrameKind;
import '../state/settings/imaging_defaults_state.dart';

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
}
