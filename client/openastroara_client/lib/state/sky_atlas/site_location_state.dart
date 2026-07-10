import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../saved_server_state.dart';

/// §36 — the active profile's observing site (from `GET /api/v1/profile/site`),
/// used to set the planetarium observer's location. Degrees / metres.
@immutable
class SiteLocation {
  final double latitudeDeg;
  final double longitudeDeg;
  final double elevationM;

  const SiteLocation({
    required this.latitudeDeg,
    required this.longitudeDeg,
    required this.elevationM,
  });

  static SiteLocation? fromJson(Map<String, dynamic> json) {
    final lat = json['latitude_deg'];
    final lon = json['longitude_deg'];
    if (lat is! num || lon is! num) return null;
    final elev = json['elevation_m'];
    return SiteLocation(
      latitudeDeg: lat.toDouble(),
      longitudeDeg: lon.toDouble(),
      elevationM: elev is num ? elev.toDouble() : 0.0,
    );
  }

  @override
  bool operator ==(Object other) =>
      other is SiteLocation &&
      other.latitudeDeg == latitudeDeg &&
      other.longitudeDeg == longitudeDeg &&
      other.elevationM == elevationM;

  @override
  int get hashCode => Object.hash(latitudeDeg, longitudeDeg, elevationM);
}

/// The active server's site location, or null when no server is connected / the
/// site can't be read. The planetarium listens and re-points the observer.
final siteLocationProvider =
    FutureProvider.autoDispose<SiteLocation?>((ref) async {
  final server = await ref.watch(activeServerFutureProvider.future);
  if (server == null) return null;
  final dio = Dio(BaseOptions(
    baseUrl: server.baseUrl,
    connectTimeout: const Duration(seconds: 3),
    receiveTimeout: const Duration(seconds: 5),
  ));
  ref.onDispose(() => dio.close(force: true));
  try {
    final res = await dio.get<dynamic>('/api/v1/profile/site');
    final raw = res.data;
    return raw is Map<String, dynamic> ? SiteLocation.fromJson(raw) : null;
  } on DioException catch (e) {
    debugPrint('siteLocationProvider: $e');
    return null;
  }
});
