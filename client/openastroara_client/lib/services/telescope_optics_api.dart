import 'package:dio/dio.dart';

import '../models/server.dart';

/// §37/§25.5.5 — the connected mount's optics (focal length + aperture), read
/// from `GET /api/v1/equipment/telescope` for the wizard's "Refresh from
/// connected mount" affordance. The daemon reports these in mm (it converts the
/// ASCOM metres at the read, per §578); both are null when the driver doesn't
/// report them (most mounts NotImplement `FocalLength`/`ApertureDiameter`).
class TelescopeOptics {
  final double? focalLengthMm;
  final double? apertureMm;

  const TelescopeOptics({this.focalLengthMm, this.apertureMm});

  bool get hasAny => focalLengthMm != null || apertureMm != null;

  /// Parse a `GET /api/v1/equipment/telescope` body, or null when no mount is
  /// connected. A connected mount that reports neither value yields a
  /// `TelescopeOptics` with both null (`hasAny == false`) so the caller can say
  /// "this mount doesn't report its optics" rather than silently doing nothing.
  static TelescopeOptics? fromTelescopeJson(Map<String, dynamic> json) {
    if (json['state'] != 'connected') return null;
    final caps = json['capabilities'];
    if (caps is! Map<String, dynamic>) return null;
    // Tolerant casts: a wrong-typed / non-positive value → null for that field
    // (the daemon already zero-guards, but be defensive against a junk wire value).
    double? mm(Object? v) {
      final d = v is num ? v.toDouble() : null;
      return (d != null && d > 0) ? d : null;
    }

    return TelescopeOptics(
      focalLengthMm: mm(caps['focal_length_mm']),
      apertureMm: mm(caps['aperture_diameter_mm']),
    );
  }
}

class TelescopeOpticsApi {
  final Dio _dio;

  TelescopeOpticsApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 5),
        ));

  /// The connected mount's optics, or null when none is connected. Throws
  /// `DioException` on transport failure.
  Future<TelescopeOptics?> read() async {
    try {
      final res =
          await _dio.get<Map<String, dynamic>>('/api/v1/equipment/telescope');
      final data = res.data;
      return data is Map<String, dynamic>
          ? TelescopeOptics.fromTelescopeJson(data)
          : null;
    } finally {
      // One-shot client: close the Dio so its connection pool isn't leaked per press.
      _dio.close();
    }
  }
}
