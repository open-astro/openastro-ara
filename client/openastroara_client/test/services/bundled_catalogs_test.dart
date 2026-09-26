import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/bundled_catalogs.dart';
import 'package:openastroara/services/dso_catalog_service.dart';

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

  test('planning cull keeps bright + magnitude-less nebulae + WR, drops faint', () {
    const rows = [
      PlanningDso(id: 'a', name: 'a', type: 'G', magnitude: 9, raDeg: 0, decDeg: 0),
      PlanningDso(id: 'b', name: 'b', type: 'G', magnitude: 14, raDeg: 0, decDeg: 0),
      PlanningDso(id: 'c', name: 'c', type: 'HII', magnitude: null, raDeg: 0, decDeg: 0),
      PlanningDso(id: 'd', name: 'd', type: '*', magnitude: null, raDeg: 0, decDeg: 0),
      PlanningDso(id: 'e', name: 'e', type: 'WR*', magnitude: 15, raDeg: 0, decDeg: 0),
    ];
    expect(planningCull(rows).map((d) => d.id), ['a', 'c', 'e']);
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
}
