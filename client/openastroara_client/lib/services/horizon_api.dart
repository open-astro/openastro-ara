import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import '../models/server.dart';

/// §36 — one point on the Planning horizon overlay from `GET /api/v1/planning/horizon`:
/// an equatorial (J2000) coordinate the local horizon curve passes through.
@immutable
class HorizonPoint {
  final double raDeg;
  final double decDeg;

  const HorizonPoint(this.raDeg, this.decDeg);

  /// Parse one `{ra_deg, dec_deg}` point, or null when either coord is
  /// missing/wrong-typed (a bad value would be a real-but-wrong (0,0) sky spot).
  static HorizonPoint? fromJson(Map<String, dynamic> json) {
    final ra = json['ra_deg'];
    final dec = json['dec_deg'];
    if (ra is! num || dec is! num) return null;
    return HorizonPoint(ra.toDouble(), dec.toDouble());
  }
}

/// §36 — a labelled compass point (N/E/S/W) sitting on the horizon, for orienting
/// the overlay.
@immutable
class HorizonCardinal {
  final String label;
  final double raDeg;
  final double decDeg;

  const HorizonCardinal(this.label, this.raDeg, this.decDeg);

  static HorizonCardinal? fromJson(Map<String, dynamic> json) {
    final label = json['label'];
    final ra = json['ra_deg'];
    final dec = json['dec_deg'];
    if (label is! String || ra is! num || dec is! num) return null;
    return HorizonCardinal(label, ra.toDouble(), dec.toDouble());
  }
}

/// §36 — the active profile's local horizon projected onto the equatorial (RA/Dec)
/// sky for the Aladin overlay. Aladin is an equatorial atlas, not an alt/az
/// planetarium, so the horizon is a curve in RA/Dec that depends on the site
/// latitude and the local sidereal time. [points] is that closed curve;
/// [zenith] is the point straight up; [cardinals] marks N/E/S/W;
/// [customHorizonIgnored] is true when the profile asked for a custom terrain
/// horizon this flat-horizon slice doesn't yet render.
@immutable
class Horizon {
  final double horizonAltitudeDeg;
  final HorizonPoint zenith;
  final List<HorizonPoint> points;
  final List<HorizonCardinal> cardinals;
  final bool customHorizonIgnored;

  const Horizon({
    required this.horizonAltitudeDeg,
    required this.zenith,
    required this.points,
    required this.cardinals,
    required this.customHorizonIgnored,
  });

  /// Parse the horizon response, or null when the required geometry (the zenith
  /// and a non-empty curve) is missing/malformed — so a bad payload draws no
  /// overlay rather than a misleading partial one. Individual malformed points
  /// are skipped.
  static Horizon? fromJson(Map<String, dynamic> json) {
    final rawZenith = json['zenith'];
    if (rawZenith is! Map<String, dynamic>) return null;
    final zenith = HorizonPoint.fromJson(rawZenith);
    if (zenith == null) return null;

    final rawPoints = json['points'];
    if (rawPoints is! List) return null;
    final points = <HorizonPoint>[];
    for (final e in rawPoints) {
      if (e is Map<String, dynamic>) {
        final p = HorizonPoint.fromJson(e);
        if (p != null) points.add(p);
      }
    }
    if (points.isEmpty) return null;

    final cardinals = <HorizonCardinal>[];
    final rawCardinals = json['cardinals'];
    if (rawCardinals is List) {
      for (final e in rawCardinals) {
        if (e is Map<String, dynamic>) {
          final c = HorizonCardinal.fromJson(e);
          if (c != null) cardinals.add(c);
        }
      }
    }

    final alt = json['horizon_altitude_deg'];
    return Horizon(
      horizonAltitudeDeg: alt is num ? alt.toDouble() : 0.0,
      zenith: zenith,
      points: points,
      cardinals: cardinals,
      customHorizonIgnored: json['custom_horizon_ignored'] == true,
    );
  }
}

class HorizonApi {
  final Dio _dio;

  /// [dio] is injectable so tests can stub the transport with a fake adapter;
  /// production callers pass only the server and get a configured one-shot Dio.
  HorizonApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ));

  /// The local horizon for the active profile's site at [at] (default: the
  /// server's "now"). Throws `DioException` on transport failure; returns null on
  /// a 200 whose body isn't a horizon object (an error/HTML body), so the caller
  /// draws nothing rather than throwing. The owning provider holds the lifecycle —
  /// call [close] when done with it.
  Future<Horizon?> fetch({DateTime? at}) async {
    final res = await _dio.get<dynamic>(
      '/api/v1/planning/horizon',
      queryParameters: <String, dynamic>{
        if (at != null) 'at': at.toUtc().toIso8601String(),
      },
    );
    final raw = res.data;
    if (raw is! Map<String, dynamic>) {
      debugPrint('HorizonApi: unexpected 200 body (${raw.runtimeType}); '
          'expected a horizon object — drawing no horizon.');
      return null;
    }
    return Horizon.fromJson(raw);
  }

  /// Release the underlying connection. `force: true` cancels any in-flight
  /// request so the provider can drop a pending fetch from `onDispose`.
  void close() => _dio.close(force: true);
}
