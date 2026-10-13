import 'package:dio/dio.dart';

import '../models/server.dart';
import '../models/sky_model_calibration.dart';

/// #1140 P2 — `/api/v1/planning/sky-model`: the daemon's sky-model calibration
/// store. Read, refit from the library, clear.
class PlanningApi {
  final Dio _dio;

  PlanningApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              // A refit walks the catalog's recent rows (no pixels): seconds, not minutes.
              receiveTimeout: const Duration(seconds: 30),
            ),
          );

  /// The stored record, or null on a 404 (never fitted, cleared, or a daemon
  /// predating the route). Other failures throw `DioException`.
  Future<SkyModelCalibration?> read() async {
    try {
      final res = await _dio.get<dynamic>('/api/v1/planning/sky-model');
      return SkyModelCalibration.fromJson(res.data);
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  /// Refit from the library now and return the stored record.
  Future<SkyModelCalibration?> recalibrate() async {
    final res = await _dio.post<dynamic>('/api/v1/planning/sky-model/recalibrate');
    return SkyModelCalibration.fromJson(res.data);
  }

  /// Clear the stored record (204).
  Future<void> clear() async {
    await _dio.delete<void>('/api/v1/planning/sky-model');
  }

  void close() => _dio.close();
}
