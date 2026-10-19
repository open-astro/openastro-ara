import 'dart:convert';
import 'dart:io';

import 'package:dio/dio.dart';
import 'package:path_provider/path_provider.dart';

import '../models/server.dart';

/// One planning-shaped deep-sky object from the daemon's
/// `GET /api/v1/data-manager/dso-catalog` (the installed DSO catalogs after
/// the daemon's cull: mag ≤ 12, plus magnitude-less nebula types and every
/// `WR*` star so the offline search resolves any WR number; the ranker skips
/// stars itself): everything the client-side Tonight's Sky ranker scores on.
/// [magnitude] is null for objects that carry none (dark nebulae, most HII
/// regions) — the ranker scores those on size/surface brightness instead.
class PlanningDso {
  final String id;
  final String name;
  final String type;
  final double? magnitude;
  final double raDeg;
  final double decDeg;
  final double? sizeMajArcmin;
  final double? sizeMinArcmin;
  final double? posAngleDeg;
  final double? surfaceBrightness;

  /// Overlay membership (bundled catalogs only; null on wire rows).
  final int? messierNum;
  final int? caldwellNum;

  /// The object's other designations and names (#1267 item 4): the
  /// catalog's cross-identifications, further common names, and the ids of
  /// rows folded into this one by [mergeDuplicates]. Any of them finds the
  /// object in search; Tonight's Sky lists them under the row.
  final List<String> aliases;

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
    this.messierNum,
    this.caldwellNum,
    this.aliases = const [],
  });

  /// Whether [id] or any alias starts with [prefix] (an overlay's family).
  bool hasDesignationPrefix(String prefix) {
    if (id.startsWith(prefix)) return true;
    for (final a in aliases) {
      if (a.startsWith(prefix)) return true;
    }
    return false;
  }

  /// A Barnard id is "B" + digits ("B33"), which no other family shares.
  bool get hasBarnardDesignation => barnardDesignation != null;
  String? get barnardDesignation {
    bool isB(String s) =>
        s.length > 1 && s[0] == 'B' && s.codeUnitAt(1) >= 48 && s.codeUnitAt(1) <= 57;
    if (isB(id)) return id;
    for (final a in aliases) {
      if (isB(a)) return a;
    }
    return null;
  }

  /// The designation starting with [prefix]: the id when it matches, else
  /// the first such alias, else null.
  String? designationWithPrefix(String prefix) {
    if (id.startsWith(prefix)) return id;
    for (final a in aliases) {
      if (a.startsWith(prefix)) return a;
    }
    return null;
  }

  /// Wire shape is the daemon's snake_case DsoEntryDto. Null on a malformed
  /// row (skipped, never a crash).
  static PlanningDso? fromJson(Object? json) {
    if (json is! Map) return null;
    final name = json['name'];
    final ra = json['ra_deg'];
    final dec = json['dec_deg'];
    final mag = json['magnitude'];
    if (name is! String || ra is! num || dec is! num) {
      return null;
    }
    double? opt(String key) =>
        json[key] is num ? (json[key] as num).toDouble() : null;
    final common = json['common_name'];
    final aliases = json['aliases'];
    return PlanningDso(
      id: name,
      name: common is String && common.isNotEmpty ? common : name,
      type: json['type']?.toString() ?? '',
      magnitude: mag is num ? mag.toDouble() : null,
      raDeg: ra.toDouble(),
      decDeg: dec.toDouble(),
      sizeMajArcmin: opt('maj_ax_arcmin'),
      sizeMinArcmin: opt('min_ax_arcmin'),
      posAngleDeg: opt('pos_angle_deg'),
      surfaceBrightness: opt('surface_brightness'),
      aliases: aliases is List
          ? aliases.whereType<String>().toList(growable: false)
          : const [],
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
    if (aliases.isNotEmpty) 'aliases': aliases,
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
      return [for (final row in decoded) ?PlanningDso.fromJson(row)];
    } catch (_) {
      return const [];
    }
  }

  /// Fetch from [server] and overwrite the local mirror. Returns the fetched
  /// list; a 404 (catalog not installed on the daemon) or transport failure
  /// returns null and leaves the existing mirror untouched.
  Future<List<PlanningDso>?> refreshFrom(AraServer server, {Dio? dio}) async {
    final client =
        dio ??
        Dio(
          BaseOptions(
            baseUrl: server.baseUrl,
            connectTimeout: const Duration(seconds: 3),
            receiveTimeout: const Duration(seconds: 30),
          ),
        );
    try {
      final res = await client.get<List<dynamic>>(
        '/api/v1/data-manager/dso-catalog',
      );
      final rows = res.data;
      if (rows == null) return null;
      final list = [for (final row in rows) ?PlanningDso.fromJson(row)];
      if (list.isEmpty) return null; // don't clobber a good mirror with junk
      final f = await _file();
      await f.writeAsString(
        jsonEncode([for (final d in list) d.toJson()]),
        flush: true,
      );
      return list;
    } catch (_) {
      // DioException (404 / unreachable) AND local-IO failures (support dir,
      // disk full): this class is best-effort by contract — the caller fires
      // it unawaited, so nothing may escape (review #847). Mirror unchanged.
      return null;
    } finally {
      if (dio == null) client.close();
    }
  }
}

/// The planetarium command a typed search becomes. A planning-catalog hit is a
/// `goto` by J2000 coordinates that ALWAYS carries the object's name: the page
/// keeps one framing target (position + name), and a goto without a name used
/// to leave the previously selected object's name on the box — select the
/// Moon, type "NGC 7000", Create Run, and the run was called "Moon" at NGC
/// 7000's coordinates (night of 2026-10-02). No hit → the page's own engine
/// search, which selects the object and sets both. Pure — unit-tested.
Map<String, Object?> planetariumSearchCommand(
    List<PlanningDso> catalog, String query) {
  final q = query.trim();
  final hit = findCatalogObject(catalog, q);
  if (hit != null) {
    return {
      'type': 'goto',
      'ra': hit.raDeg,
      'dec': hit.decDeg,
      'name': hit.name.trim().isEmpty ? hit.id : hit.name,
    };
  }
  return {'type': 'search', 'q': q};
}

/// A designation's lookup key: upper-case, no spaces, no zero padding
/// ("NGC 0224", "ngc224", "NGC0224" and "C 020" / "C 20" are one key).
/// Dashes STAY: "M 2-9" (Minkowski's Twin Jet) is not "M 29", and "WR 2-1"
/// is not "WR 21" (review #1107, r3). Shared by the search and the
/// one-row-per-object merge (#1267).
String designationKey(String s) {
  final up = s.trim().toUpperCase().replaceAll(_spaces, '');
  return up.replaceAllMapped(_zeroPad, (m) => '${m[1]}${m[2]}');
}

// Built once: the search keys every id and alias of the bundle on a miss.
final RegExp _spaces = RegExp(r'[\s_]');
final RegExp _zeroPad = RegExp(r'([A-Z])0+(\d)');

/// The key without dashes, for a query typed WITHOUT one ("sh2 117" finds
/// Sh2-117). A query that carries a dash is never matched loosely.
String looseDesignationKey(String s) =>
    designationKey(s).replaceAll('-', '');

/// Whether [query] matches [designation] by key: dash-preserving first; the
/// dashless form only when the query itself has no dash.
bool designationMatches(String query, String designation) {
  final qk = designationKey(query);
  if (qk.isEmpty) return false;
  if (qk == designationKey(designation)) return true;
  return !qk.contains('-') && qk == looseDesignationKey(designation);
}

/// A one-pass lookup index over a catalog (#1267 item 4): designation key
/// → row for every id, display name and alias, with the dashless form as a
/// fallback for a query without a dash. Built once per catalog list, so a
/// Simbad answer's whole alias list resolves in O(1) each instead of a full
/// scan per name (review r5).
class CatalogNameIndex {
  final Map<String, PlanningDso> _exact = {};
  final Map<String, PlanningDso> _loose = {};
  // A dashless key two different rows share answers nothing, as in the
  // merge ("WR 2-1" and "WR 21" are two stars; review r8).
  final Set<String> _looseAmbiguous = {};

  CatalogNameIndex(List<PlanningDso> catalog) {
    void add(String designation, PlanningDso o) {
      final k = designationKey(designation);
      if (k.isEmpty) return;
      _exact.putIfAbsent(k, () => o);
      final lk = k.replaceAll('-', '');
      final owner = _loose[lk];
      if (owner == null) {
        _loose[lk] = o;
      } else if (!identical(owner, o)) {
        _looseAmbiguous.add(lk);
      }
    }
    // Ids first, then names, then aliases: an id claim always wins.
    for (final o in catalog) {
      add(o.id, o);
    }
    for (final o in catalog) {
      add(o.name, o);
    }
    for (final o in catalog) {
      for (final a in o.aliases) {
        add(a, o);
      }
    }
  }

  /// The row [query] names exactly (dash-preserving; the dashless form only
  /// for a query without a dash), or null.
  PlanningDso? lookup(String query) {
    final k = designationKey(query);
    if (k.isEmpty) return null;
    final hit = _exact[k];
    if (hit != null || k.contains('-')) return hit;
    return _looseAmbiguous.contains(k) ? null : _loose[k];
  }
}


/// Case/space/dash-insensitive lookup over the planning mirror: "sh2 129",
/// "SH2-129", "b 33", "ldn1235" and common names ("horsehead") all resolve.
/// Exact normalized id/name match wins; otherwise the first object whose
/// common name contains the query. Null when nothing matches.
PlanningDso? findCatalogObject(List<PlanningDso> catalog, String query) {
  // Two normalisations: a dash-PRESERVING one first, because designations
  // with a suffix collide with plain numbers once dashes go — "WR 2-1" and
  // "WR 21" both strip to "wr21" (review #1107); then the loose form so
  // "sh2 129" / "SH2129" / "b 33" still resolve.
  String tight(String s) => s.toLowerCase().replaceAll(RegExp(r'[\s_]'), '');
  String loose(String s) => s.toLowerCase().replaceAll(RegExp(r'[\s\-_]'), '');
  final qt = tight(query);
  final q = loose(query);
  if (q.isEmpty) return null;
  for (final o in catalog) {
    if (tight(o.id) == qt || tight(o.name) == qt) return o;
  }
  for (final o in catalog) {
    if (loose(o.id) == q || loose(o.name) == q) return o;
  }
  // Zero padding is not part of a designation ("NGC 224" is NGC0224), and
  // any other designation the object answers to selects it (#1267 item 4):
  // "Sh2-117", "C 20" and "LBN 373" all select the North America Nebula's
  // one row.
  if (designationKey(query).isNotEmpty) {
    for (final o in catalog) {
      if (designationMatches(query, o.id)) return o;
    }
    for (final o in catalog) {
      for (final a in o.aliases) {
        if (designationMatches(query, a)) return o;
      }
    }
  }
  for (final o in catalog) {
    if (loose(o.name).contains(q)) return o;
  }
  return null;
}
