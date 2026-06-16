import 'package:dio/dio.dart';

import '../models/server.dart';

/// §36/§25.5 — the connected camera's sensor geometry, read from
/// `GET /api/v1/equipment/camera` for the Optics panel's "Refresh from
/// connected camera" affordance. (The daemon already auto-caches this into the
/// optics section on connect; this lets the user re-pull it on demand.)
class CameraGeometry {
  final int sensorWidthPx;
  final int sensorHeightPx;
  final double pixelSizeUm;

  const CameraGeometry({
    required this.sensorWidthPx,
    required this.sensorHeightPx,
    required this.pixelSizeUm,
  });

  /// Parse a `GET /api/v1/equipment/camera` body, or null when no camera is
  /// connected or it doesn't report a usable sensor (zeros) — so the caller can
  /// prompt "connect a camera that reports its sensor" instead of writing junk.
  static CameraGeometry? fromCameraJson(Map<String, dynamic> json) {
    if (json['state'] != 'connected') return null;
    final caps = json['capabilities'];
    if (caps is! Map<String, dynamic>) return null;
    // Tolerant casts: a wrong-typed field (e.g. a stringified number) yields 0 →
    // fails the positivity guard below → null, matching this parser's "null on
    // anything unexpected" contract rather than throwing a TypeError.
    final ws = caps['sensor_width'];
    final hs = caps['sensor_height'];
    final ps = caps['pixel_size_um'];
    final w = ws is num ? ws.toInt() : 0;
    final h = hs is num ? hs.toInt() : 0;
    final px = ps is num ? ps.toDouble() : 0.0;
    if (w <= 0 || h <= 0 || px <= 0) return null;
    return CameraGeometry(sensorWidthPx: w, sensorHeightPx: h, pixelSizeUm: px);
  }
}

class CameraGeometryApi {
  final Dio _dio;

  CameraGeometryApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 5),
        ));

  /// The connected camera's sensor geometry, or null when none is connected /
  /// it reports no usable sensor. Throws `DioException` on transport failure.
  Future<CameraGeometry?> read() async {
    try {
      final res = await _dio.get<Map<String, dynamic>>('/api/v1/equipment/camera');
      final data = res.data;
      return data is Map<String, dynamic> ? CameraGeometry.fromCameraJson(data) : null;
    } finally {
      // One-shot client: close the Dio so its HttpClient connection pool isn't leaked per button press.
      _dio.close();
    }
  }
}
