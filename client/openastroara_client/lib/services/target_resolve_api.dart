import 'package:dio/dio.dart';

import '../models/server.dart';

/// One answer from `GET /api/v1/targets/resolve` (#1267): a name resolved to
/// a J2000 position, or why it could not be.
class TargetResolution {
  /// `resolved`, `not_found` or `offline` (not in the offline index and the
  /// daemon could not reach Simbad: needs internet).
  final String status;

  /// `index` (the daemon's bundled HYG / OpenNGC cross-ids, no internet),
  /// `cache` (a Simbad answer kept on the daemon) or `simbad`; empty on a miss.
  final String source;
  final String? mainId;
  final List<String> aliases;
  final double? raDeg;
  final double? decDeg;
  final String? objectType;
  final double? magnitude;
  final String? message;

  const TargetResolution({
    required this.status,
    this.source = '',
    this.mainId,
    this.aliases = const [],
    this.raDeg,
    this.decDeg,
    this.objectType,
    this.magnitude,
    this.message,
  });

  bool get resolved =>
      status == 'resolved' && raDeg != null && decDeg != null && mainId != null;
  bool get offline => status == 'offline';

  /// The answer that stands in for an unreachable daemon.
  static const unreachable = TargetResolution(
    status: 'offline',
    message: 'The rig could not be reached to look the name up.',
  );

  static TargetResolution? fromJson(dynamic json) {
    if (json is! Map<String, dynamic>) return null;
    final status = json['status'];
    if (status is! String) return null;
    final ra = json['ra_deg'];
    final dec = json['dec_deg'];
    final mag = json['magnitude'];
    final aliases = json['aliases'];
    return TargetResolution(
      status: status,
      source: json['source'] is String ? json['source'] as String : '',
      mainId: json['main_id'] is String ? json['main_id'] as String : null,
      aliases: aliases is List
          ? aliases.whereType<String>().toList(growable: false)
          : const [],
      raDeg: ra is num ? ra.toDouble() : null,
      decDeg: dec is num ? dec.toDouble() : null,
      objectType: json['object_type'] is String
          ? json['object_type'] as String
          : null,
      magnitude: mag is num ? mag.toDouble() : null,
      message: json['message'] is String ? json['message'] as String : null,
    );
  }

  /// A one-line provenance for the dialog: where the answer came from, the
  /// object's type and magnitude, and the other names it answers to.
  String get provenance {
    final parts = <String>[
      switch (source) {
        'index' => 'Offline index',
        'cache' => 'Simbad (saved on the rig)',
        'simbad' => 'Simbad',
        _ => 'Resolved',
      },
    ];
    final t = objectType;
    if (t != null && t.isNotEmpty) parts.add(t);
    final m = magnitude;
    if (m != null) parts.add('mag ${m.toStringAsFixed(1)}');
    if (aliases.isNotEmpty) {
      final shown = aliases.take(4).join(', ');
      parts.add('also ${aliases.length > 4 ? '$shown, …' : shown}');
    }
    return parts.join(' · ');
  }
}

abstract interface class TargetResolveClient {
  /// Resolve a name through the daemon. Never throws: an unreachable daemon
  /// answers [TargetResolution.unreachable].
  Future<TargetResolution> resolve(String name);
  void close();
}

class TargetResolveApi implements TargetResolveClient {
  final Dio _dio;

  /// [dio] is injectable so tests can supply a stub adapter.
  TargetResolveApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              // The daemon bounds its own Simbad call at 5 s, and the first
              // lookup after a catalog install builds the offline index on a
              // Pi (seconds): this covers both back to back.
              receiveTimeout: const Duration(seconds: 20),
            ),
          );

  @override
  Future<TargetResolution> resolve(String name) async {
    try {
      final res = await _dio.get<dynamic>(
        '/api/v1/targets/resolve',
        queryParameters: {'name': name},
      );
      return TargetResolution.fromJson(res.data) ??
          TargetResolution.unreachable;
    } on DioException {
      return TargetResolution.unreachable;
    }
  }

  @override
  void close() => _dio.close();
}
