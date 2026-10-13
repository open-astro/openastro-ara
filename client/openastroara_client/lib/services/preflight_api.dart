import 'package:dio/dio.dart';

import '../models/preflight_checklist.dart';
import '../models/server.dart';

/// §58.14 — `GET /api/v1/preflight/today`, the daemon's pre-sleep checklist.
class PreflightApi {
  final Dio _dio;

  PreflightApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              // The daemon reads every device for this; give a slow driver room.
              receiveTimeout: const Duration(seconds: 20),
            ),
          );

  /// Tonight's checklist, or null on a 404 (a daemon predating the route).
  /// Other failures throw `DioException`.
  Future<PreflightChecklist?> fetchToday() async {
    try {
      final res = await _dio.get<dynamic>('/api/v1/preflight/today');
      return PreflightChecklist.fromJson(res.data);
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  void close() => _dio.close();
}
