import 'package:dio/dio.dart';

import '../models/server.dart';
import '../models/tracking_calibration.dart';

/// #1264 — the unguided tracking ceiling: `/api/v1/tracking/*`. The daemon's
/// Tracking Probe writes the record; the client reads it (readiness fact, sub
/// advisor, Imaging → Unguided) and may clear it so the next run measures afresh.
class TrackingApi {
  final Dio _dio;

  TrackingApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 5),
            ),
          );

  /// The stored calibration, or null on a 404 (never measured, cleared, or a
  /// daemon predating #1264). Other failures throw `DioException`.
  Future<TrackingCalibration?> read() async {
    try {
      final res = await _dio.get<dynamic>('/api/v1/tracking/calibration');
      return TrackingCalibration.fromJson(res.data);
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  /// Clear the stored calibration (204). The next unguided run's probe starts
  /// from the ladder again instead of last night's ceiling.
  Future<void> recalibrate() async {
    await _dio.post<void>('/api/v1/tracking/recalibrate');
  }

  void close() => _dio.close();
}
