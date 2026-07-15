import 'dart:convert';
import 'dart:io';

import 'package:dio/dio.dart';
import 'package:path_provider/path_provider.dart';

import '../models/server.dart';

/// One planning-shaped deep-sky object from the daemon's
/// `GET /api/v1/data-manager/dso-catalog` (the installed openngc-dso culled
/// to mag ≤ 12): everything the client-side Tonight's Sky ranker scores on.
class PlanningDso {
  final String id;
  final String name;
  final String type;
  final double magnitude;
  final double raDeg;
  final double decDeg;
  final double? sizeMajArcmin;
  final double? sizeMinArcmin;
  final double? posAngleDeg;
  final double? surfaceBrightness;

  const PlanningDso({
    required this.id,
    required this.name,
    required this.type,
    required this.magnitude,
    required this.raDeg,
    required this.decDeg,
    this.sizeMajArcmin,
    this.sizeMinArcmin,
    this.posAngleDeg,
    this.surfaceBrightness,
  });

  /// Wire shape is the daemon's snake_case DsoEntryDto. Null on a malformed
  /// row (skipped, never a crash).
  static PlanningDso? fromJson(Object? json) {
    if (json is! Map) return null;
    final name = json['name'];
    final ra = json['ra_deg'];
    final dec = json['dec_deg'];
    final mag = json['magnitude'];
    if (name is! String || ra is! num || dec is! num || mag is! num) {
      return null;
    }
    double? opt(String key) =>
        json[key] is num ? (json[key] as num).toDouble() : null;
    final common = json['common_name'];
    return PlanningDso(
      id: name,
      name: common is String && common.isNotEmpty ? common : name,
      type: json['type']?.toString() ?? '',
      magnitude: mag.toDouble(),
      raDeg: ra.toDouble(),
      decDeg: dec.toDouble(),
      sizeMajArcmin: opt('maj_ax_arcmin'),
      sizeMinArcmin: opt('min_ax_arcmin'),
      posAngleDeg: opt('pos_angle_deg'),
      surfaceBrightness: opt('surface_brightness'),
    );
  }

  Map<String, dynamic> toJson() => {
        'name': id,
        'common_name': name,
        'type': type,
        'magnitude': magnitude,
        'ra_deg': raDeg,
        'dec_deg': decDeg,
        'maj_ax_arcmin': sizeMajArcmin,
        'min_ax_arcmin': sizeMinArcmin,
        'pos_angle_deg': posAngleDeg,
        'surface_brightness': surfaceBrightness,
      };
}

/// §2 offline planning — fetches the daemon-hosted DSO catalog and caches it
/// as an app-support JSON file, so the client-side Tonight's Sky ranker scores
/// the real catalog online or off. Data hosting stays on the daemon
/// (PORT_DECISIONS 2026-07-15); this is a mirror, refreshed best-effort on
/// connect and never a hard dependency — reads degrade to "no catalog"
/// (starter-list fallback), writes are flushed.
class DsoCatalogService {
  DsoCatalogService({Future<Directory> Function()? supportDir})
      : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;
  static const _fileName = 'dso_catalog.json';

  Future<File> _file() async {
    final dir = await _supportDir();
    return File('${dir.path}/$_fileName');
  }

  /// The locally-cached catalog, or empty when never fetched / unreadable.
  Future<List<PlanningDso>> loadCached() async {
    try {
      final f = await _file();
      if (!await f.exists()) return const [];
      final decoded = jsonDecode(await f.readAsString());
      if (decoded is! List) return const [];
      return [
        for (final row in decoded) ?PlanningDso.fromJson(row),
      ];
    } catch (_) {
      return const [];
    }
  }

  /// Fetch from [server] and overwrite the local mirror. Returns the fetched
  /// list; a 404 (catalog not installed on the daemon) or transport failure
  /// returns null and leaves the existing mirror untouched.
  Future<List<PlanningDso>?> refreshFrom(AraServer server, {Dio? dio}) async {
    final client = dio ??
        Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 30),
        ));
    try {
      final res =
          await client.get<List<dynamic>>('/api/v1/data-manager/dso-catalog');
      final rows = res.data;
      if (rows == null) return null;
      final list = [
        for (final row in rows) ?PlanningDso.fromJson(row),
      ];
      if (list.isEmpty) return null; // don't clobber a good mirror with junk
      final f = await _file();
      await f.writeAsString(
          jsonEncode([for (final d in list) d.toJson()]),
          flush: true);
      return list;
    } on DioException {
      return null; // 404 (not installed) or unreachable — mirror unchanged
    } finally {
      if (dio == null) client.close();
    }
  }
}
