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

  /// Visual magnitude, or null when the server omits/mangles it — kept nullable
  /// so a bad value can't masquerade as a genuine `mag 0.0` (≈ Vega).
  final double? magnitude;
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

  /// Parse one wire object, or null when a required field is missing/wrong-typed
  /// (so a malformed row is skipped, not crashed on). The string identity (id /
  /// name / type) and the position (ra/dec — a bad value would be a real-but-wrong
  /// (0,0) sky spot) are required; the display-only altitudes fall back to 0, and
  /// `magnitude` is left null rather than defaulting to the real value 0.
  static TonightSkyObject? fromJson(Map<String, dynamic> json) {
    final id = json['id'];
    final name = json['name'];
    final type = json['type'];
    if (id is! String || name is! String || type is! String) return null;
    final ra = json['ra_deg'];
    final dec = json['dec_deg'];
    if (ra is! num || dec is! num) return null;
    final mag = json['magnitude'];
    final alt = json['altitude_deg'];
    final maxAlt = json['max_altitude_deg'];
    return TonightSkyObject(
      id: id,
      name: name,
      type: type,
      magnitude: mag is num ? mag.toDouble() : null,
      raDeg: ra.toDouble(),
      decDeg: dec.toDouble(),
      altitudeDeg: alt is num ? alt.toDouble() : 0.0,
      maxAltitudeDeg: maxAlt is num ? maxAlt.toDouble() : 0.0,
    );
  }
}

class TonightSkyApi {
  final Dio _dio;

  TonightSkyApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 10),
        ));

  /// The curated objects above the active profile's site horizon right now,
  /// highest first (server-ranked). Throws `DioException` on transport failure.
  /// The owning provider holds the lifecycle — call [close] when done with it.
  Future<List<TonightSkyObject>> fetch({int limit = 12}) async {
    final res = await _dio.get<dynamic>(
      '/api/v1/planning/tonight',
      queryParameters: <String, dynamic>{'limit': limit},
    );
    // A well-behaved server returns a JSON array; anything else (an error object,
    // an HTML body, null) is not iterable — treat it as "nothing to show" rather
    // than throwing a TypeError out of the provider.
    final raw = res.data;
    if (raw is! List) return const <TonightSkyObject>[];
    final out = <TonightSkyObject>[];
    for (final e in raw) {
      if (e is Map<String, dynamic>) {
        final o = TonightSkyObject.fromJson(e);
        if (o != null) out.add(o);
      }
    }
    return out;
  }

  /// Release the underlying connection. `force: true` cancels any in-flight
  /// request, so the provider can call this from `onDispose` to drop a pending
  /// fetch when the panel closes rather than waiting out the receive timeout.
  void close() => _dio.close(force: true);
}
