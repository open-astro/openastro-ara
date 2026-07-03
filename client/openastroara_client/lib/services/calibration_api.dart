import 'package:dio/dio.dart';

import '../models/calibration/calibration_models.dart';
import '../models/server.dart';

/// §39 calibration client (`/api/v1/calibration/*`). Interface first so tests
/// and future transports can fake it, mirroring `SequenceClient`.
abstract interface class CalibrationClient {
  Future<List<CalibrationSession>> listSessions({int limit = 50});

  /// Generate matching flats for a session. With [generateOnly] false (the
  /// default) the server persists a runnable §38 sequence and the result
  /// carries its id.
  Future<GeneratedFlatSequence> generateMatchingFlats(
    String sessionId, {
    int? frameCount,
    int? targetAdu,
    bool generateOnly = false,
  });

  Future<DarkLibraryState> darkLibraryStatus();

  /// Request a dark-library build; the server generates a runnable dark-matrix
  /// sequence surfaced via [darkLibraryStatus].
  Future<void> buildDarkLibrary(DarkLibraryBuildRequest request);

  void close();
}

/// Dio wrapper over `/api/v1/calibration/*`.
class CalibrationApi implements CalibrationClient {
  final Dio _dio;

  CalibrationApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              sendTimeout: const Duration(seconds: 5),
              receiveTimeout: const Duration(seconds: 12),
            ));

  @override
  Future<List<CalibrationSession>> listSessions({int limit = 50}) async {
    final res = await _dio.get<dynamic>(
      '/api/v1/calibration/sessions',
      queryParameters: <String, dynamic>{'limit': limit},
    );
    final data = res.data;
    // CursorPage envelope { items, next_cursor, has_more }; a 2xx with another
    // shape means the wire contract changed — throw so the notifier surfaces it.
    if (data is! Map<String, dynamic> || data['items'] is! List) {
      throw FormatException(
          'calibration sessions returned an unexpected body (${data.runtimeType})');
    }
    return (data['items'] as List)
        .whereType<Map<String, dynamic>>()
        .map(CalibrationSession.fromJson)
        .where((s) => s.id.isNotEmpty)
        .toList(growable: false);
  }

  @override
  Future<GeneratedFlatSequence> generateMatchingFlats(
    String sessionId, {
    int? frameCount,
    int? targetAdu,
    bool generateOnly = false,
  }) async {
    final res = await _dio.post<dynamic>(
      '/api/v1/calibration/sessions/$sessionId/matching-flats',
      data: <String, dynamic>{
        'override_frame_count': frameCount,
        'override_target_adu': targetAdu,
        'generate_only': generateOnly,
      },
    );
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException(
          'matching-flats returned an unexpected body (${data.runtimeType})');
    }
    return GeneratedFlatSequence.fromJson(data);
  }

  @override
  Future<DarkLibraryState> darkLibraryStatus() async {
    final res = await _dio.get<dynamic>('/api/v1/calibration/dark-library/status');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException(
          'dark-library status returned an unexpected body (${data.runtimeType})');
    }
    return DarkLibraryState.fromJson(data);
  }

  @override
  Future<void> buildDarkLibrary(DarkLibraryBuildRequest request) async {
    await _dio.post<dynamic>(
      '/api/v1/calibration/dark-library/build',
      data: request.toJson(),
    );
  }

  @override
  void close() => _dio.close();
}
