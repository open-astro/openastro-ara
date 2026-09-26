import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart' show rootBundle;

import 'dso_catalog_service.dart';

/// Offline-first sky catalogs, BUNDLED with the client.
///
/// Every deep-sky catalog the app knows (OpenNGC, Sharpless, LDN, Barnard,
/// vdB, Abell PN, Arp, Wolf-Rayet) ships as an asset in the OpenNGC column
/// layout from open-astro/sky-data, parsed here with the same rules the
/// daemon's SkyCatalogService applies. The daemon is no longer needed for
/// search, Tonight's Sky ranking, or the planetarium's Catalogs overlays —
/// at a dark site on the SBC's Wi-Fi they all work with no server at all.
/// (The daemon keeps its Data Manager packages for its own endpoints.)
const List<String> bundledCatalogAssets = [
  'assets/catalogs/NGC.csv',
  'assets/catalogs/sh2.csv',
  'assets/catalogs/ldn.csv',
  'assets/catalogs/barnard.csv',
  'assets/catalogs/vdb.csv',
  'assets/catalogs/abell-pn.csv',
  'assets/catalogs/arp.csv',
  'assets/catalogs/wr.csv',
];

/// Load and parse every bundled catalog once. Strings load on the UI isolate
/// (rootBundle needs it); the parse — ~16k rows — runs off it.
Future<List<PlanningDso>> loadBundledCatalogs() async {
  final texts = <String>[];
  for (final a in bundledCatalogAssets) {
    try {
      texts.add(await rootBundle.loadString(a));
    } catch (e) {
      debugPrint('[catalogs] bundled asset $a unreadable: $e');
    }
  }
  return compute(_parseAll, texts);
}

List<PlanningDso> _parseAll(List<String> texts) => [
      for (final t in texts) ...parseOpenNgcCsv(t),
    ];

/// Parse one OpenNGC-layout CSV (semicolon-separated). Port of the daemon's
/// ParseDsoCsvFile: "Dup" and "NonEx" rows are dropped, V-Mag then B-Mag is
/// the magnitude, the first common name is the display name, Messier /
/// Caldwell membership comes from the M column / "C NNN" identifiers.
List<PlanningDso> parseOpenNgcCsv(String text) {
  final lines = const LineSplitter().convert(text);
  if (lines.isEmpty) return const [];
  final cols = lines.first.split(';');
  int idx(String n) => cols.indexOf(n);
  final iName = idx('Name'), iType = idx('Type'), iRa = idx('RA'), iDec = idx('Dec');
  final iV = idx('V-Mag'), iB = idx('B-Mag'), iM = idx('M'), iId = idx('Identifiers');
  final iMaj = idx('MajAx'), iMin = idx('MinAx'), iPa = idx('PosAng'), iSb = idx('SurfBr');
  final iCommon = idx('Common names');
  if (iName < 0 || iRa < 0 || iDec < 0) return const [];
  final out = <PlanningDso>[];
  for (final line in lines.skip(1)) {
    if (line.isEmpty) continue;
    final f = line.split(';');
    if (f.length <= [iName, iRa, iDec].reduce((a, b) => a > b ? a : b)) continue;
    final ra = raToDeg(f[iRa]);
    final dec = decToDeg(f[iDec]);
    if (ra == null || dec == null) continue;
    final type = iType >= 0 && f.length > iType ? f[iType] : '';
    if (type == 'Dup' || type == 'NonEx') continue;
    double? num(int i) => i >= 0 && f.length > i ? double.tryParse(f[i].trim()) : null;
    final mag = num(iV) ?? num(iB);
    final messier = iM >= 0 && f.length > iM ? int.tryParse(f[iM].trim()) : null;
    final caldwell = iId >= 0 && f.length > iId ? caldwellNumOf(f[iId]) : null;
    String? common;
    if (iCommon >= 0 && f.length > iCommon) {
      final first = f[iCommon].split(',').first.trim();
      if (first.isNotEmpty) common = first;
    }
    final name = f[iName];
    out.add(PlanningDso(
      id: name,
      name: common ?? name,
      type: type,
      magnitude: mag,
      raDeg: ra,
      decDeg: dec,
      sizeMajArcmin: num(iMaj),
      sizeMinArcmin: num(iMin),
      posAngleDeg: num(iPa),
      surfaceBrightness: num(iSb),
      messierNum: messier,
      caldwellNum: caldwell,
    ));
  }
  return out;
}

/// "C 020" in the comma-separated identifiers → 20; null otherwise.
int? caldwellNumOf(String identifiers) {
  for (final raw in identifiers.split(',')) {
    final t = raw.trim();
    if (t.length > 2 && t[0] == 'C' && t[1] == ' ') {
      final rest = t.substring(2).trim();
      if (rest.isNotEmpty && RegExp(r'^\d+$').hasMatch(rest)) {
        return int.tryParse(rest);
      }
    }
  }
  return null;
}

/// "HH:MM:SS.s" (hours) → degrees; null when malformed (a bad seconds field
/// rejects the row rather than mis-placing it).
double? raToDeg(String s) {
  final p = s.trim().split(':');
  if (p.length < 2) return null;
  final h = double.tryParse(p[0]), m = double.tryParse(p[1]);
  if (h == null || m == null) return null;
  var sec = 0.0;
  if (p.length > 2) {
    final v = double.tryParse(p[2]);
    if (v == null) return null;
    sec = v;
  }
  return (h + m / 60 + sec / 3600) * 15.0;
}

/// "+DD:MM:SS.s" → degrees; exactly one leading sign char is honoured.
double? decToDeg(String raw) {
  final s = raw.trim();
  if (s.length < 2) return null;
  final sign = s[0] == '-' ? -1 : 1;
  final body = (s[0] == '+' || s[0] == '-') ? s.substring(1) : s;
  final p = body.split(':');
  if (p.length < 2) return null;
  final d = double.tryParse(p[0]), m = double.tryParse(p[1]);
  if (d == null || m == null) return null;
  var sec = 0.0;
  if (p.length > 2) {
    final v = double.tryParse(p[2]);
    if (v == null) return null;
    sec = v;
  }
  return sign * (d + m / 60 + sec / 3600);
}

/// The planning subset — the same cull the daemon's /dso-catalog applied:
/// mag ≤ [maxMag], plus magnitude-less nebula types (which legitimately have
/// no integrated magnitude), plus WR stars (kept for the search; they score
/// low in the ranker — no size, no surface brightness — and #1107 skips
/// star types outright). Magnitude-less stars / stubs stay out.
List<PlanningDso> planningCull(List<PlanningDso> all, {double maxMag = 12}) => [
      for (final d in all)
        if (d.type == 'WR*' ||
            (d.magnitude != null
                ? d.magnitude! <= maxMag
                : isMagnitudelessImagingType(d.type)))
          d,
    ];

bool isMagnitudelessImagingType(String type) => const {
      'HII', 'EmN', 'RfN', 'DrkN', 'Neb', 'Cl+N', 'SNR', 'PN',
    }.contains(type);

// ── Catalogs overlays (the planetarium's dots) ─────────────────────────────

typedef CatalogOverlay = ({String id, String name, String group, bool Function(PlanningDso) match});

/// Mirror of the daemon's CatalogDef table, so the Catalogs panel offers the
/// same sets online and off.
final List<CatalogOverlay> catalogOverlays = [
  (id: 'messier', name: 'Messier', group: 'Catalogs', match: (r) => r.messierNum != null),
  (id: 'caldwell', name: 'Caldwell', group: 'Catalogs', match: (r) => r.caldwellNum != null),
  (id: 'ngc', name: 'NGC', group: 'Catalogs', match: (r) => r.id.startsWith('NGC')),
  (id: 'ic', name: 'IC', group: 'Catalogs', match: (r) => r.id.startsWith('IC')),
  (id: 'sharpless', name: 'Sharpless (Sh2)', group: 'Catalogs', match: (r) => r.id.startsWith('Sh2-')),
  (id: 'barnard', name: 'Barnard dark nebulae', group: 'Catalogs',
      match: (r) => r.id.length > 1 && r.id[0] == 'B' && _isDigit(r.id[1])),
  (id: 'ldn', name: 'Lynds dark nebulae (LDN)', group: 'Catalogs', match: (r) => r.id.startsWith('LDN ')),
  (id: 'vdb', name: 'van den Bergh (vdB)', group: 'Catalogs', match: (r) => r.id.startsWith('vdB ')),
  (id: 'abell-pn', name: 'Abell planetary nebulae', group: 'Catalogs', match: (r) => r.id.startsWith('Abell ')),
  (id: 'arp', name: 'Arp peculiar galaxies', group: 'Catalogs', match: (r) => r.id.startsWith('Arp ')),
  (id: 'wolf-rayet', name: 'Wolf-Rayet stars (WR)', group: 'Catalogs', match: (r) => r.id.startsWith('WR ')),
  (id: 'galaxies', name: 'Galaxies', group: 'Types',
      match: (r) => const {'G', 'GPair', 'GTrpl', 'GGroup'}.contains(r.type)),
  (id: 'open-clusters', name: 'Open clusters', group: 'Types', match: (r) => r.type == 'OCl'),
  (id: 'globular-clusters', name: 'Globular clusters', group: 'Types', match: (r) => r.type == 'GCl'),
  (id: 'planetary-nebulae', name: 'Planetary nebulae', group: 'Types', match: (r) => r.type == 'PN'),
  (id: 'emission-nebulae', name: 'Emission nebulae', group: 'Types',
      match: (r) => r.type == 'HII' || r.type == 'EmN'),
  (id: 'reflection-nebulae', name: 'Reflection nebulae', group: 'Types', match: (r) => r.type == 'RfN'),
  (id: 'nebulae', name: 'Nebulae', group: 'Types', match: (r) => r.type == 'Neb' || r.type == 'Cl+N'),
  (id: 'supernova-remnants', name: 'Supernova remnants', group: 'Types', match: (r) => r.type == 'SNR'),
];

bool _isDigit(String c) => c.codeUnitAt(0) >= 48 && c.codeUnitAt(0) <= 57;

/// The `/api/v1/catalogs` list shape: [{id, name, group}].
List<Map<String, String>> catalogOverlayInfos() => [
      for (final o in catalogOverlays) {'id': o.id, 'name': o.name, 'group': o.group},
    ];

/// One overlay's objects in the `/api/v1/catalogs/{id}` shape
/// ({name, ra_deg, dec_deg, magnitude}), brightest first, capped at [limit].
/// Null for an unknown id.
List<Map<String, Object?>>? catalogOverlayObjects(
    String id, List<PlanningDso> all, {int limit = 500}) {
  CatalogOverlay? def;
  for (final o in catalogOverlays) {
    if (o.id == id) {
      def = o;
      break;
    }
  }
  if (def == null) return null;
  final rows = [for (final r in all) if (def.match(r)) r]
    ..sort((a, b) => (a.magnitude ?? double.infinity)
        .compareTo(b.magnitude ?? double.infinity));
  return [
    for (final r in rows.take(limit < 0 ? 0 : limit))
      {
        'name': overlayDisplayName(id, r),
        'ra_deg': r.raDeg,
        'dec_deg': r.decDeg,
        'magnitude': r.magnitude,
      },
  ];
}

String overlayDisplayName(String catalogId, PlanningDso r) => switch (catalogId) {
      'messier' => 'M ${r.messierNum}',
      'caldwell' => 'C ${r.caldwellNum}',
      _ => prettyDsoName(r.id),
    };

/// "NGC0224" → "NGC 224", "IC0080 NED01" → "IC 80 NED01"; others unchanged.
String prettyDsoName(String name) {
  final prefixLen = name.startsWith('NGC') ? 3 : name.startsWith('IC') ? 2 : 0;
  if (prefixLen == 0) return name;
  var i = prefixLen;
  while (i < name.length && name[i] == '0') {
    i++;
  }
  final rest = name.substring(i);
  if (rest.isEmpty || !_isDigit(rest[0])) return name;
  return '${name.substring(0, prefixLen)} $rest';
}
