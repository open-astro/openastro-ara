import 'dart:math' as math;

import '../services/dso_catalog_service.dart';

/// #1267 item 4 — one object, one row. The bundled catalogs each list their
/// own designations, so one nebula sits in the list three times (NGC 7000,
/// Sh2-117 and C 20 of the North America Nebula). [mergeDuplicates] folds
/// those into one row keyed on the cross-identifications the catalogs carry
/// first (OpenNGC's Identifiers / NGC / IC / M columns), and on position as
/// the fallback: rows from DIFFERENT catalog families, of the same type
/// class, within [positionToleranceArcmin]. Rows of one family are never
/// merged by position: a galaxy pair, two WR stars in one cluster or the
/// halves of the Rosette are distinct objects with distinct ids.
///
/// The surviving row keeps its id, name and photometry; the others become
/// its [PlanningDso.aliases], so picking any alias selects the same target
/// and search still finds it. Pure; unit-tested.

/// Same-class rows from different families closer than this are one object.
/// 30″: catalog positions of one object differ by a few arcseconds to a few
/// tens; two real objects a full arcminute apart are not the same thing.
const double positionToleranceArcmin = 0.5;

/// The catalog a designation belongs to, by prefix. Rows of one family are
/// never merged with each other by position.
String catalogFamily(String id) {
  if (id.startsWith('NGC') || id.startsWith('IC')) return 'openngc';
  if (id.startsWith('Sh2-')) return 'sharpless';
  if (id.startsWith('LDN ')) return 'ldn';
  if (id.startsWith('vdB ')) return 'vdb';
  if (id.startsWith('Abell ')) return 'abell';
  if (id.startsWith('Arp ')) return 'arp';
  if (id.startsWith('WR ')) return 'wr';
  if (id.startsWith('REGION-')) return 'region';
  if (id.length > 1 && id[0] == 'B' && _isDigit(id[1])) return 'barnard';
  return 'other';
}

/// What kind of thing a row is, for the position fallback: two rows at the
/// same spot are only one object when they are the same kind.
String typeClass(String type) => switch (type) {
  'HII' || 'EmN' || 'RfN' || 'Neb' || 'Cl+N' || 'SNR' => 'nebula',
  'DrkN' => 'dark',
  'G' || 'GPair' || 'GTrpl' || 'GGroup' => 'galaxy',
  'OCl' || 'GCl' => 'cluster',
  'PN' => 'planetary',
  '*' || '**' || 'WR*' => 'star',
  _ => type,
};

bool _isDigit(String c) => c.codeUnitAt(0) >= 48 && c.codeUnitAt(0) <= 57;

/// Which row of a group survives: the one with photometry (a magnitude or a
/// surface brightness), then an NGC/IC designation, then the first listed.
int _rank(PlanningDso d) {
  var r = 0;
  if (d.magnitude != null) r += 4;
  if (d.surfaceBrightness != null) r += 2;
  if (catalogFamily(d.id) == 'openngc') r += 1;
  return r;
}

/// [links] are extra "these two ids are one object" pairs for twins the
/// catalogs do not cross-identify (OpenNGC's Sharpless twins carry no
/// identifier either way: NGC 7000 and Sh2-117 are the North America Nebula
/// 12′ apart as catalogued), the curated [sharplessAnchors] list.
List<PlanningDso> mergeDuplicates(
  List<PlanningDso> rows, {
  List<(String, String)> links = const [],
}) {
  if (rows.length < 2) return rows;
  final parent = List<int>.generate(rows.length, (i) => i);
  int find(int i) {
    while (parent[i] != i) {
      parent[i] = parent[parent[i]];
      i = parent[i];
    }
    return i;
  }

  // Per-root: whether the group holds an NGC/IC designation (by id, or by an
  // alias absorbed earlier: Arp 147 holds IC 298, so a second pass over
  // merged rows decides the same way as the first). Kept by union, O(1).
  final rootHasOpenNgc = [
    for (final r in rows)
      catalogFamily(r.id) == 'openngc' ||
          r.aliases.any(
            (a) => catalogFamily(a.replaceAll(' ', '')) == 'openngc',
          ),
  ];
  void union(int a, int b) {
    final ra = find(a), rb = find(b);
    if (ra != rb) {
      parent[rb] = ra;
      rootHasOpenNgc[ra] = rootHasOpenNgc[ra] || rootHasOpenNgc[rb];
    }
  }

  bool groupHasOpenNgc(int root) => rootHasOpenNgc[root];

  // Whether a group already holds an NGC/IC row: a position union must never
  // join two of those through a third-party row sitting between them (union
  // is transitive; review r5).
  // A star row and a non-star row are never one object: a nebula's row
  // linked to the star it surrounds (Sh2-108 ↔ IC 1318, gam Cyg) must stay a
  // nebula row, or the planning cull drops the nebula with the star
  // (review r1). Two star rows (a WR entry and its HD id) still merge.
  bool compatible(int i, int j) =>
      (typeClass(rows[i].type) == 'star') ==
      (typeClass(rows[j].type) == 'star');

  // 1. Cross-identifications: a row whose id is another row's alias (or id).
  // An id key two rows share (WR 2-1 / WR 21 once dashes go, review #1107)
  // is ambiguous and resolves nothing.
  final byKey = <String, int>{};
  final ambiguous = <String>{};
  final ownedById = <String>{};
  for (var i = 0; i < rows.length; i++) {
    final k = designationKey(rows[i].id);
    if (k.isEmpty) continue;
    if (byKey.containsKey(k)) {
      ambiguous.add(k);
    } else {
      byKey[k] = i;
      ownedById.add(k);
    }
  }
  // A row's own aliases also answer for it (a Dup designation such as
  // NGC 2244 names the Rosette cluster row NGC 2239). An id is
  // authoritative: an alias that names another row's id is a link to that
  // row, not a rival claim; two rows claiming one key by alias alone is
  // ambiguous and resolves nothing.
  for (var i = 0; i < rows.length; i++) {
    for (final a in rows[i].aliases) {
      final k = designationKey(a);
      if (k.isEmpty || ownedById.contains(k)) continue;
      final owner = byKey[k];
      if (owner == null) {
        byKey[k] = i;
      } else if (owner != i) {
        ambiguous.add(k);
      }
    }
  }
  // Dash-preserving first; a designation WITHOUT a dash may also meet a
  // dashed key ("Sh2 117" ↔ "Sh2-117"), a dashed one never meets a
  // dashless key ("M 2-9" is not "M 29").
  final byLooseKey = <String, int>{};
  final looseAmbiguous = <String>{};
  for (final entry in byKey.entries) {
    if (ambiguous.contains(entry.key)) continue;
    final lk = entry.key.replaceAll('-', '');
    if (byLooseKey.containsKey(lk) && byLooseKey[lk] != entry.value) {
      looseAmbiguous.add(lk);
    } else {
      byLooseKey[lk] = entry.value;
    }
  }
  int? lookup(String designation) {
    final k = designationKey(designation);
    if (k.isEmpty) return null;
    if (ambiguous.contains(k)) return null;
    final exact = byKey[k];
    if (exact != null || k.contains('-')) return exact;
    return looseAmbiguous.contains(k) ? null : byLooseKey[k];
  }

  // A row whose designations name SEVERAL other rows is a group (Arp 281 is
  // NGC 4627 + NGC 4631, the Antennae are Arp 244 = NGC 4038 + 4039): its
  // members stay their own rows and the group row stands on its own, out of
  // the position pass too (its centre sits on one member). Review r2.
  final groupRows = <int>{};
  for (var i = 0; i < rows.length; i++) {
    final targets = <int>{};
    for (final a in rows[i].aliases) {
      final j = lookup(a);
      if (j != null && j != i && compatible(i, j)) targets.add(j);
    }
    if (targets.length == 1) {
      union(i, targets.single);
    } else if (targets.length > 1) {
      groupRows.add(i);
    }
  }
  for (final (a, b) in links) {
    final i = lookup(a), j = lookup(b);
    if (i != null && j != null && i != j && compatible(i, j)) union(i, j);
  }

  // 2. Position: different families, same type class, within the tolerance.
  // 0.1° cells; the RA axis wraps at 360° so a twin on either side of 0h
  // still meets its neighbour.
  final cell = <(int, int), List<int>>{};
  (int, int) cellOf(PlanningDso d) =>
      (((d.raDeg % 360) * 10).floor() % 3600, (d.decDeg * 10).floor());
  for (var i = 0; i < rows.length; i++) {
    cell.putIfAbsent(cellOf(rows[i]), () => []).add(i);
  }
  for (var i = 0; i < rows.length; i++) {
    if (groupRows.contains(i)) continue;
    final d = rows[i];
    final fam = catalogFamily(d.id);
    final cls = typeClass(d.type);
    final (cx, cy) = cellOf(d);
    for (var dx = -1; dx <= 1; dx++) {
      for (var dy = -1; dy <= 1; dy++) {
        for (final j in cell[((cx + dx) % 3600, cy + dy)] ?? const <int>[]) {
          if (j <= i || groupRows.contains(j)) continue;
          final e = rows[j];
          if (catalogFamily(e.id) == fam || typeClass(e.type) != cls) continue;
          if (separationArcmin(d.raDeg, d.decDeg, e.raDeg, e.decDeg) <=
              positionToleranceArcmin) {
            final ri = find(i), rj = find(j);
            if (ri != rj && groupHasOpenNgc(ri) && groupHasOpenNgc(rj)) {
              continue;
            }
            union(i, j);
          }
        }
      }
    }
  }

  // 3. Fold each group into its best row, in the input order of that row.
  final groups = <int, List<int>>{};
  for (var i = 0; i < rows.length; i++) {
    groups.putIfAbsent(find(i), () => []).add(i);
  }
  final out = <PlanningDso>[];
  final emitted = <int>{};
  for (var i = 0; i < rows.length; i++) {
    final root = find(i);
    if (!emitted.add(root)) continue;
    final members = groups[root]!;
    if (members.length == 1) {
      out.add(rows[i]);
      continue;
    }
    var best = members.first;
    for (final m in members) {
      if (_rank(rows[m]) > _rank(rows[best])) best = m;
    }
    final primary = rows[best];
    final seen = {designationKey(primary.id), designationKey(primary.name)};
    final aliases = <String>[];
    void add(String s) {
      if (s.trim().isEmpty) return;
      if (seen.add(designationKey(s))) aliases.add(s.trim());
    }

    for (final a in primary.aliases) {
      add(a);
    }
    for (final m in members) {
      if (m == best) continue;
      add(rows[m].id);
      add(rows[m].name);
      for (final a in rows[m].aliases) {
        add(a);
      }
    }
    int? firstInt(int? Function(PlanningDso) f) {
      for (final m in members) {
        final v = f(rows[m]);
        if (v != null) return v;
      }
      return null;
    }

    PlanningDso? sized;
    for (final m in [best, ...members]) {
      if (rows[m].sizeMajArcmin != null) {
        sized = rows[m];
        break;
      }
    }

    out.add(
      PlanningDso(
        id: primary.id,
        name: primary.name,
        type: primary.type,
        magnitude: primary.magnitude,
        raDeg: primary.raDeg,
        decDeg: primary.decDeg,
        // The extent comes from ONE row (the best with a major axis), never
        // one row's major axis with another's minor axis and angle.
        sizeMajArcmin: sized?.sizeMajArcmin,
        sizeMinArcmin: sized?.sizeMinArcmin,
        posAngleDeg: sized?.posAngleDeg,
        surfaceBrightness: primary.surfaceBrightness,
        messierNum: firstInt((d) => d.messierNum),
        caldwellNum: firstInt((d) => d.caldwellNum),
        aliases: aliases,
      ),
    );
  }
  return out;
}

/// Angular separation in arcminutes (haversine).
double separationArcmin(double ra1, double dec1, double ra2, double dec2) {
  const r = math.pi / 180;
  final dRa = (ra2 - ra1) * r, dDec = (dec2 - dec1) * r;
  final a =
      math.pow(math.sin(dDec / 2), 2) +
      math.cos(dec1 * r) * math.cos(dec2 * r) * math.pow(math.sin(dRa / 2), 2);
  return 2 * math.asin(math.sqrt(a.clamp(0.0, 1.0))) / r * 60;
}
