import 'package:dio/dio.dart';

import '../models/calibration_status.dart';
import '../models/server.dart';

/// The §63.6 guider calibration operations the state layer depends on. An
/// interface so tests can supply a pure fake (no Dio); [GuiderCalibrationApi] is
/// the Dio-backed production implementation.
abstract interface class GuiderCalibrationClient {
  Future<CalibrationStatusResponse> getStatus();
  Future<void> buildDarkLibrary({
    int frameCount,
    int? minExposureMs,
    int? maxExposureMs,
    bool clearExisting,
    String? notes,
    bool loadAfter,
  });
  Future<void> buildDefectMap({
    int exposureMs,
    int frameCount,
    String? notes,
    bool loadAfter,
  });
  Future<void> setDarkLibraryEnabled(bool enabled);
  Future<void> setDefectMapEnabled(bool enabled);
  void close();
}

/// Dio wrapper over the §63.6 guider calibration surface
/// (`/api/v1/equipment/guider/{darklibrary,defectmap}/…`). The build calls are
/// 202-Accepted (the daemon runs the capture in the background and reports
/// progress over the `guider.dark_library.*` / `guider.defect_map.*` WS stream),
/// so they return when the request is accepted, not when the build completes.
class GuiderCalibrationApi implements GuiderCalibrationClient {
  final Dio _dio;

  GuiderCalibrationApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          sendTimeout: const Duration(seconds: 5),
          receiveTimeout: const Duration(seconds: 5),
        ));

  @override
  Future<CalibrationStatusResponse> getStatus() async {
    final res = await _dio.get<dynamic>('/api/v1/equipment/guider/darklibrary/status');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      return const CalibrationStatusResponse(connected: false);
    }
    return CalibrationStatusResponse.fromJson(data);
  }

  @override
  Future<void> buildDarkLibrary({
    int frameCount = 5,
    int? minExposureMs,
    int? maxExposureMs,
    bool clearExisting = false,
    String? notes,
    bool loadAfter = true,
  }) async {
    await _dio.post<void>(
      '/api/v1/equipment/guider/darklibrary/build',
      data: <String, dynamic>{
        'frame_count': frameCount,
        'min_exposure_ms': ?minExposureMs,
        'max_exposure_ms': ?maxExposureMs,
        'clear_existing': clearExisting,
        'notes': ?notes,
        'load_after': loadAfter,
      },
    );
  }

  @override
  Future<void> buildDefectMap({
    int exposureMs = 3000,
    int frameCount = 10,
    String? notes,
    bool loadAfter = true,
  }) async {
    await _dio.post<void>(
      '/api/v1/equipment/guider/defectmap/build',
      data: <String, dynamic>{
        'exposure_ms': exposureMs,
        'frame_count': frameCount,
        'notes': ?notes,
        'load_after': loadAfter,
      },
    );
  }

  @override
  Future<void> setDarkLibraryEnabled(bool enabled) async {
    await _dio.post<void>(
      '/api/v1/equipment/guider/darklibrary/enabled',
      data: <String, dynamic>{'enabled': enabled},
    );
  }

  @override
  Future<void> setDefectMapEnabled(bool enabled) async {
    await _dio.post<void>(
      '/api/v1/equipment/guider/defectmap/enabled',
      data: <String, dynamic>{'enabled': enabled},
    );
  }

  @override
  void close() => _dio.close(force: true);
}
