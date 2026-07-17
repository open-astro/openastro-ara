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

/// §37 — the connected mount's wizard-relevant properties for the Mount step:
/// the driver's device name and its fastest MoveAxis slew rate (deg/sec, the
/// max of `move_axis_rates_deg_per_sec`; null when the mount reports no axis
/// rates — many NotImplement MoveAxis).
class MountProps {
  final String? name;
  final double? maxSlewRateDegPerSec;

  const MountProps({this.name, this.maxSlewRateDegPerSec});

  bool get hasAny => name != null || maxSlewRateDegPerSec != null;

  /// Parse a `GET /api/v1/equipment/telescope` body, or null when no mount is
  /// connected (same contract as [TelescopeOptics.fromTelescopeJson]).
  static MountProps? fromTelescopeJson(Map<String, dynamic> json) {
    if (json['state'] != 'connected') return null;
    final rawName = json['name'];
    final name = rawName is String && rawName.trim().isNotEmpty
        ? rawName.trim()
        : null;
    double? maxRate;
    final caps = json['capabilities'];
    if (caps is Map<String, dynamic>) {
      final rates = caps['move_axis_rates_deg_per_sec'];
      if (rates is List) {
        for (final r in rates) {
          if (r is num && r > 0 && (maxRate == null || r > maxRate)) {
            maxRate = r.toDouble();
          }
        }
      }
    }
    return MountProps(name: name, maxSlewRateDegPerSec: maxRate);
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

  /// The connected mount's name + fastest slew rate, or null when none is
  /// connected. Throws `DioException` on transport failure.
  Future<MountProps?> readProps() async {
    try {
      final res =
          await _dio.get<Map<String, dynamic>>('/api/v1/equipment/telescope');
      final data = res.data;
      return data is Map<String, dynamic>
          ? MountProps.fromTelescopeJson(data)
          : null;
    } finally {
      _dio.close();
    }
  }
}
