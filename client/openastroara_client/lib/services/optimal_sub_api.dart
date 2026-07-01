import 'package:dio/dio.dart';

import '../models/server.dart';

/// NEXTGEN §2/§5 — the daemon's Optimal-Sub calculation for one filter: the
/// Glover read-noise floor intersected with the sky-background saturation
/// ceiling (`GET /api/v1/planning/optimal-sub`, slice 2).
class OptimalSubResult {
  /// The modelled sky-background flux P (e⁻/s/px).
  final double skyFluxEPerSecPerPx;

  /// The Glover read-noise floor (seconds) — the recommendation when viable.
  final double floorSec;

  /// The sky-background saturation ceiling (seconds).
  final double ceilingSec;

  /// floor ≤ ceiling. False = the ceiling collapsed below the floor
  /// (saturation-limited before read noise is swamped).
  final bool viable;

  /// Which bound decides: `readnoisefloor` or `saturationceiling` (wire tokens).
  final String limitingBound;

  /// min(floor, ceiling) — per Glover, past the floor buys no further
  /// read-noise gain, so the floor IS the recommendation when reachable.
  final double recommendedSec;

  /// Snake_case names of every input the daemon had to default (Tier-0
  /// generic-CMOS fallbacks) — the transparency contract. Null/empty = the
  /// figure is built entirely from configured values.
  final List<String>? assumedDefaults;

  const OptimalSubResult({
    required this.skyFluxEPerSecPerPx,
    required this.floorSec,
    required this.ceilingSec,
    required this.viable,
    required this.limitingBound,
    required this.recommendedSec,
    this.assumedDefaults,
  });

  static OptimalSubResult? fromJson(Map<String, dynamic> json) {
    final floor = json['floor_sec'];
    final ceiling = json['ceiling_sec'];
    final recommended = json['recommended_sec'];
    if (floor is! num || ceiling is! num || recommended is! num) return null;
    return OptimalSubResult(
      skyFluxEPerSecPerPx:
          (json['sky_flux_e_per_sec_per_px'] as num?)?.toDouble() ?? 0,
      floorSec: floor.toDouble(),
      ceilingSec: ceiling.toDouble(),
      viable: json['viable'] is bool ? json['viable'] as bool : true,
      limitingBound: json['limiting_bound'] is String
          ? json['limiting_bound'] as String
          : 'readnoisefloor',
      recommendedSec: recommended.toDouble(),
      assumedDefaults: switch (json['assumed_defaults']) {
        final List<dynamic> l => l.whereType<String>().toList(),
        _ => null,
      },
    );
  }
}

/// The daemon rejected the request as un-computable (a 400 — e.g. the imaging
/// train has no aperture yet). [message] is the daemon's human-readable detail,
/// good enough to show verbatim.
class OptimalSubUnavailable implements Exception {
  final String message;
  const OptimalSubUnavailable(this.message);
  @override
  String toString() => message;
}

/// Client for `GET /api/v1/planning/optimal-sub`. Mirrors [TonightSkyApi]'s
/// shape (own Dio bound to one server; [close] when done).
class OptimalSubApi {
  final Dio _dio;

  OptimalSubApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 5),
              receiveTimeout: const Duration(seconds: 10),
            ));

  /// The Optimal-Sub window for [filter] (a planning filter-set name) or an
  /// explicit [bandwidthNm]; both absent → the daemon's effective-broadband
  /// default (flagged in `assumed_defaults`).
  ///
  /// Returns null on a 404 — an older daemon without the endpoint, so the
  /// advisor simply doesn't render. Throws [OptimalSubUnavailable] with the
  /// daemon's message on a 400 (e.g. optics not yet configured); rethrows
  /// transport errors.
  Future<OptimalSubResult?> get({String? filter, double? bandwidthNm}) async {
    try {
      final res = await _dio.get<Map<String, dynamic>>(
        '/api/v1/planning/optimal-sub',
        queryParameters: {
          if (filter != null && filter.trim().isNotEmpty) 'filter': filter.trim(),
          if (bandwidthNm != null) 'bandwidthNm': bandwidthNm,
        },
      );
      return OptimalSubResult.fromJson(res.data ?? const {});
    } on DioException catch (e) {
      final status = e.response?.statusCode;
      if (status == 404) return null; // pre-slice-2 daemon → hide the advisor
      if (status == 400) {
        final data = e.response?.data;
        final detail = data is Map<String, dynamic> ? data['detail'] : null;
        throw OptimalSubUnavailable(detail is String
            ? detail
            : 'The daemon could not compute an optimal sub for this setup.');
      }
      rethrow;
    }
  }

  void close() => _dio.close(force: true);
}
