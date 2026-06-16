import 'package:dio/dio.dart';

import '../models/server.dart';

/// §36/§25.5 — one Tonight's Sky entry from `GET /api/v1/planning/tonight`:
/// a curated deep-sky object ranked by its current altitude above the site
/// horizon. [altitudeDeg] is the ranking key; [maxAltitudeDeg] is its highest
/// possible (transit) altitude from this latitude; [raDeg]/[decDeg] (J2000) let
/// the atlas recentre on it.
class TonightSkyObject {
  final String id;
  final String name;
  final String type;
  final double magnitude;
  final double raDeg;
  final double decDeg;
  final double altitudeDeg;
  final double maxAltitudeDeg;

  const TonightSkyObject({
    required this.id,
    required this.name,
    required this.type,
    required this.magnitude,
    required this.raDeg,
    required this.decDeg,
    required this.altitudeDeg,
    required this.maxAltitudeDeg,
  });

  /// Parse one wire object, or null when the required string fields are missing
  /// or wrong-typed (so a malformed row is skipped, not crashed on). Numeric
  /// fields tolerate a wrong type by falling back to 0.
  static TonightSkyObject? fromJson(Map<String, dynamic> json) {
    final id = json['id'];
    final name = json['name'];
    final type = json['type'];
    if (id is! String || name is! String || type is! String) return null;
    // The position is the object's identity — a missing/wrong-typed ra/dec would
    // otherwise default to (0,0), a real-but-wrong sky spot. Skip such a row.
    final ra = json['ra_deg'];
    final dec = json['dec_deg'];
    if (ra is! num || dec is! num) return null;
    // The display-only numerics tolerate a wrong type (fall back to 0) rather than dropping the row.
    double num_(Object? v) => v is num ? v.toDouble() : 0.0;
    return TonightSkyObject(
      id: id,
      name: name,
      type: type,
      magnitude: num_(json['magnitude']),
      raDeg: ra.toDouble(),
      decDeg: dec.toDouble(),
      altitudeDeg: num_(json['altitude_deg']),
      maxAltitudeDeg: num_(json['max_altitude_deg']),
    );
  }
}

class TonightSkyApi {
  final Dio _dio;

  TonightSkyApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 5),
        ));

  /// The curated objects above the active profile's site horizon right now,
  /// highest first (server-ranked). Throws `DioException` on transport failure.
  Future<List<TonightSkyObject>> fetch({int limit = 12}) async {
    try {
      final res = await _dio.get<List<dynamic>>(
        '/api/v1/planning/tonight',
        queryParameters: <String, dynamic>{'limit': limit},
      );
      final data = res.data ?? const <dynamic>[];
      final out = <TonightSkyObject>[];
      for (final e in data) {
        if (e is Map<String, dynamic>) {
          final o = TonightSkyObject.fromJson(e);
          if (o != null) out.add(o);
        }
      }
      return out;
    } finally {
      // One-shot client — close the Dio so its connection pool isn't leaked per refresh. Guard the
      // close so a (rare) close failure can't replace the real transport error on the way out.
      try {
        _dio.close();
      } catch (_) {
        // ignore — closing is best-effort cleanup.
      }
    }
  }
}
