import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/bundled_catalogs.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/util/dedupe_catalog.dart';
import 'package:openastroara/util/imaging_regions.dart';
import 'package:openastroara/widgets/sky_atlas/tonight_sky_panel.dart';

/// Parses the REAL bundled asset from disk (rootBundle needs a widget
/// binding; the parser is pure, so read the file directly).
List<PlanningDso> load(String file) =>
    parseOpenNgcCsv(File('assets/catalogs/$file').readAsStringSync());

void main() {
  test('coordinate parsers match the daemon', () {
    expect(raToDeg('00:42:44.30'), closeTo(10.6846, 1e-3));
    expect(decToDeg('+41:16:09.0'), closeTo(41.269, 1e-3));
    expect(decToDeg('-05:23:28'), closeTo(-5.391, 1e-3));
    expect(raToDeg('12:x'), isNull);
    // Exactly one sign char is stripped (the daemon's rule): "+-30" reads −30.
    expect(decToDeg('+-30:00:00'), -30.0);
  });

  test('OpenNGC parses: Dup/NonEx dropped, M31 by common name, Messier/Caldwell', () {
    final ngc = load('NGC.csv');
    expect(ngc.length, greaterThan(12000));
    expect(ngc.where((d) => d.type == 'Dup' || d.type == 'NonEx'), isEmpty);
    final m31 = ngc.firstWhere((d) => d.id == 'NGC0224');
    expect(m31.name, 'Andromeda Galaxy');
    expect(m31.messierNum, 31);
    expect(m31.magnitude, closeTo(3.44, 0.1));
    expect(m31.sizeMajArcmin, greaterThan(150));
    expect(ngc.where((d) => d.caldwellNum != null).length, greaterThan(90));
  });

  test('add-on catalogs parse with their ids intact', () {
    expect(load('sh2.csv').firstWhere((d) => d.id == 'Sh2-101').name, 'Tulip Nebula');
    expect(load('ldn.csv').length, 1787);
    expect(load('barnard.csv').firstWhere((d) => d.id == 'B33').raDeg, closeTo(85.24, 0.05));
    final wr = load('wr.csv');
    expect(wr.length, greaterThan(700), reason: 'Sheffield Galactic WR catalogue');
    final wr134 = wr.firstWhere((d) => d.id == 'WR 134');
    expect(wr134.type, 'WR*');
    expect(wr134.magnitude, closeTo(7.99, 0.01), reason: 'Johnson V, not B');
    expect(wr.firstWhere((d) => d.id == 'WR 136').name, 'NGC 6888',
        reason: 'the Nebula column rides in as the common name');
  });

  test('vdB rows drop the illuminating star\'s V-Mag (#1141)', () {
    final vdb = load('vdb.csv');
    expect(vdb.length, 158);
    expect(vdb.every((d) => d.magnitude == null), isTrue,
        reason: 'vdB "V-Mag" is the star (vdB 1 = 8.6 is BD+57 22), not the nebula');
    expect(vdb.every((d) => d.type == 'RfN'), isTrue);
    // Still planning-eligible: RfN is a magnitude-less imaging type.
    expect(planningCull(vdb).length, 158);
  });

  test('IC 1318 is a star row in OpenNGC, so Sh2-108 is the Sadr listing (#1141)', () {
    // The ('IC1318', 'Sh2-108') anchor relies on this: IC 1318 is typed `*`
    // and culled, so the anchor never fires and the Sharpless row stays. If a
    // catalog update retypes it as a nebula, the anchor will drop Sh2-108 in
    // favour of IC1318 — which is then the intended outcome, but this test
    // makes that change visible rather than silent (review on #1277).
    final ic1318 = load('NGC.csv').firstWhere((d) => d.id == 'IC1318');
    expect(isStarType(ic1318.type), isTrue);
    expect(planningCull([ic1318]), isEmpty);
  });

  test('no add-on row carries the separator inside a cell (review #1107)', () {
    // A ";" inside the Hubble cell ("WN5o+O4-6; WN5o+O7V") column-shifted 165
    // of the 717 WR rows: Identifiers landed in Common names, so WR 21 was
    // displayed as "HD 90657" and WR 30 lost "Anon (Marston)". The pinned
    // file now joins alternate spectral types with ", "; this pins the bytes.
    // (NGC.csv is exempt: OpenNGC's own stray ";" sits inside the quoted
    // "NED notes" column, right of every column the parser reads.)
    for (final file in ['abell-pn', 'arp', 'barnard', 'ldn', 'sh2', 'vdb', 'wr']) {
      final lines = File('assets/catalogs/$file.csv').readAsLinesSync();
      final width = lines.first.split(';').length;
      for (var i = 1; i < lines.length; i++) {
        if (lines[i].isEmpty) continue;
        expect(lines[i].split(';').length, width,
            reason: '$file.csv line ${i + 1} has a ";" inside a cell');
      }
    }
    final wr = load('wr.csv');
    expect(wr.firstWhere((d) => d.id == 'WR 21').name, 'WR 21',
        reason: 'no common name: the id, not the HD identifier');
    expect(wr.firstWhere((d) => d.id == 'WR 19').name, 'WR 19');
    // The two-classification row's Common names cell survives the shift and
    // still reaches the parser — but "Anon (Marston)" is the catalogue's
    // placeholder for "no common name", so the id is shown instead (#1198).
    expect(firstCommonName('Anon (Marston)'), isNull);
    expect(wr.firstWhere((d) => d.id == 'WR 30').name, 'WR 30');
    expect(wr.where((d) => d.name.startsWith('Anon')), isEmpty);
    expect(wr.firstWhere((d) => d.id == 'WR 136').name, 'NGC 6888');
    expect(firstCommonName(',Alt Name'), isNull,
        reason: 'leading comma = no common name, same as the daemon');
    expect(firstCommonName('Anon IR (Wachter)'), isNull);
  });

  test('planning cull keeps bright + magnitude-less nebulae, drops faint and stars', () {
    const rows = [
      PlanningDso(id: 'a', name: 'a', type: 'G', magnitude: 9, raDeg: 0, decDeg: 0),
      PlanningDso(id: 'b', name: 'b', type: 'G', magnitude: 14, raDeg: 0, decDeg: 0),
      PlanningDso(id: 'c', name: 'c', type: 'HII', magnitude: null, raDeg: 0, decDeg: 0),
      PlanningDso(id: 'd', name: 'd', type: '*', magnitude: null, raDeg: 0, decDeg: 0),
      PlanningDso(id: 'e', name: 'e', type: 'WR*', magnitude: 15, raDeg: 0, decDeg: 0),
      // Bright stars pass the magnitude bound; the type gate has to drop them
      // (review #1105: WR 134 at mag 8 outranked galaxies on a wide field).
      PlanningDso(id: 'f', name: 'f', type: 'WR*', magnitude: 8, raDeg: 0, decDeg: 0),
      PlanningDso(id: 'g', name: 'g', type: '**', magnitude: 7, raDeg: 0, decDeg: 0),
    ];
    expect(planningCull(rows).map((d) => d.id), ['a', 'c']);
  });

  test('overlays: same sets as the daemon, brightest first, display names', () {
    expect(catalogOverlayInfos().map((m) => m['id']),
        containsAll(['messier', 'caldwell', 'sharpless', 'wolf-rayet', 'galaxies']));
    final all = [...load('NGC.csv'), ...load('wr.csv')];
    final messier = catalogOverlayObjects('messier', all)!;
    expect(messier.length, greaterThan(100));
    expect(messier.first['name'], startsWith('M '));
    expect(messier.first['magnitude'], lessThan(messier.last['magnitude'] as num));
    final wr = catalogOverlayObjects('wolf-rayet', all, limit: 10)!;
    expect(wr, hasLength(10));
    expect(wr.first['name'], startsWith('WR '));
    expect(catalogOverlayObjects('nope', all), isNull);
    expect(prettyDsoName('NGC0224'), 'NGC 224');
    expect(prettyDsoName('IC0080 NED01'), 'IC 80 NED01');
    expect(prettyDsoName('Sh2-101'), 'Sh2-101');
  });

  test('one object, one row: the real bundle folds its twins (#1267 item 4)', () {
    final all = [for (final f in bundledCatalogAssets) ...load(f.split('/').last)];
    final merged = mergeDuplicates(all, links: catalogTwinLinks);
    expect(merged.length, lessThan(all.length));
    // The North America Nebula: NGC 7000 once, Sh2-117 and C 20 as aliases.
    final nam = merged.where((d) => d.id == 'NGC7000').single;
    expect(nam.aliases, containsAll(['Sh2-117', 'C 020']));
    expect(merged.where((d) => d.id == 'Sh2-117'), isEmpty);
    expect(findCatalogObject(merged, 'sh2 117')!.id, 'NGC7000');
    expect(findCatalogObject(merged, 'C 20')!.id, 'NGC7000');
    // The Sharpless overlay still shows the nebula, under its own name.
    final sh2 = catalogOverlayObjects('sharpless', merged, limit: 100000)!;
    expect(sh2.map((m) => m['name']), contains('Sh2-117'));
    // Arp 1 is NGC 2857 by its identifier; vdB 21 sits on NGC 1432 by position.
    expect(findCatalogObject(merged, 'Arp 1')!.id, 'NGC2857');
    expect(findCatalogObject(merged, 'vdB 21')!.id, 'NGC1432');
    // Sh2-108 (Sadr's nebula) is linked to IC 1318, which OpenNGC lists as
    // a STAR row (gam Cyg): the star must not swallow the nebula, or the
    // planning cull drops it (review r1).
    final planning = planningCull(merged);
    expect(planning.where((d) => d.id == 'Sh2-108'), hasLength(1));
    // The curated override keeps the merged aliases: the Lagoon's row still
    // answers to Sh2-25 after applyImagingRegions, as does the Rosette to
    // Sh2-275, and the Messier membership survives too.
    final curated = applyImagingRegions(merged);
    expect(findCatalogObject(curated, 'Sh2-25')!.id, 'NGC6523');
    expect(curated.where((d) => d.id == 'NGC6523').single.messierNum, 8);
    // NGC 2244 is a Dup designation of the Rosette cluster row NGC 2239: the
    // name still finds the row, the Sharpless twin folds into it, and the
    // curated 'NGC2244' override (dead before: no such row) renames it.
    final rosette = findCatalogObject(curated, 'NGC 2244')!;
    expect(rosette.id, 'NGC2239');
    expect(findCatalogObject(curated, 'Sh2-275')!.id, 'NGC2239');
    expect(rosette.name, 'Rosette Nebula (NGC 2244)');
    // An Arp row naming a PAIR (Arp 281 = NGC 4627 + NGC 4631) is a group,
    // not an identity: both galaxies survive, the Whale keeps its Caldwell
    // number, and the Arp row stands on its own (review r2).
    expect(merged.where((d) => d.id == 'NGC4631'), hasLength(1));
    expect(merged.where((d) => d.id == 'NGC4627'), hasLength(1));
    expect(merged.where((d) => d.id == 'NGC4631').single.caldwellNum, 32);
    expect(findCatalogObject(merged, 'NGC 4631')!.id, 'NGC4631');
    expect(merged.where((d) => d.id == 'Arp 281'), hasLength(1));
    expect(findCatalogObject(merged, 'NGC 4038')!.id, 'NGC4038', reason: 'the Antennae stay two');
    expect(findCatalogObject(merged, 'NGC 4039')!.id, 'NGC4039');
    // Family overlays never plot one designation twice: a group row (Arp 72
    // names NGC 5994 and NGC 5996) keeps those names as aliases, but they are
    // the member rows' own and never pull the group row into the NGC overlay.
    for (final overlay in ['ngc', 'ic', 'sharpless', 'arp', 'vdb']) {
      final names = catalogOverlayObjects(overlay, merged, limit: 1000000)!
          .map((m) => m['name'] as String)
          .toList();
      expect(names.toSet().length, names.length, reason: '$overlay has duplicates');
    }
    expect(merged.where((d) => d.id == 'Arp 72').single.aliases, contains('NGC 5994'));
    // The Tonight's Sky subtitle shows catalogue designations, best-known
    // first, never survey ids (review r4).
    expect(aliasSuffix(nam.aliases), ' · also Sh2-117, C 020, LBN 373');
    final m31 = merged.where((d) => d.id == 'NGC0224').single;
    expect(aliasSuffix(m31.aliases), ' · also M 31');
    // Merging the merged bundle again changes nothing (the provider does).
    final again = mergeDuplicates(merged);
    expect(again.map((d) => d.id), merged.map((d) => d.id));
    // Two NGC rows at one spot stay two objects.
    expect(merged.where((d) => d.id == 'NGC4567' || d.id == 'NGC4568'), hasLength(2));
    // No row lists another surviving row's id as an alias, except across the
    // star guard: Arp 65's identifiers name NGC 90 and NGC 91, and OpenNGC
    // types NGC 91 as a star, so that row rightly stays its own.
    // A group row (Arp 30 names NGC 6365 and its companions) also keeps its
    // members' names while they stay their own rows.
    final byId = {for (final d in merged) designationKey(d.id): d};
    for (final d in merged) {
      final named = [
        for (final a in d.aliases) ?byId[designationKey(a)],
      ];
      if (named.length > 1) continue; // a group designation
      for (final other in named) {
        expect(typeClass(other.type) == 'star', isNot(typeClass(d.type) == 'star'),
            reason: '${d.id} aliases ${other.id}');
      }
    }
  });

  test('aliases are parsed from identifiers, cross columns and further common names', () {
    final rows = parseOpenNgcCsv(
        'Name;Type;RA;Dec;V-Mag;B-Mag;M;NGC;IC;Identifiers;Common names\n'
        'NGC0224;G;00:42:44.33;+41:16:07.5;3.44;;031;;;C 017,UGC 00454;Andromeda Galaxy,Messier 31\n'
        'IC0434;HII;05:41:00.00;-02:27:00.0;;;;2023;;LBN 954;\n');
    expect(rows[0].aliases, ['Messier 31', 'M 31', 'C 017', 'UGC 00454']);
    expect(rows[1].aliases, ['NGC 2023', 'LBN 954']);
    final list = parseOpenNgcCsv(
        'Name;Type;RA;Dec;V-Mag;B-Mag;M;NGC;IC;Identifiers;Common names\n'
        'Arp 244;G;12:01:53.00;-18:52:10.0;;;;4038,4039;;NGC 4038,NGC 4039;Antennae\n');
    expect(list.single.aliases, ['NGC 4038', 'NGC 4039', 'NGC 4038', 'NGC 4039']);
    final dup = parseOpenNgcCsv(
        'Name;Type;RA;Dec;V-Mag;B-Mag;M;NGC;IC;Identifiers;Common names\n'
        'NGC2244;Dup;06:31:55.56;+04:56:34.6;;;;2239;;;\n'
        'NGC2239;Cl+N;06:31:55.56;+04:56:34.6;4.8;;;;;C 050;\n');
    expect(dup.map((d) => d.id), ['NGC2239']);
    expect(dup.single.aliases, ['C 050', 'NGC2244']);
  });
}
