import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/dso_catalog_service.dart';

PlanningDso _dso(String id, {String? common}) => PlanningDso(
      id: id,
      name: common ?? id,
      type: 'HII',
      magnitude: null,
      raDeg: 100,
      decDeg: 10,
    );

void main() {
  final catalog = [
    _dso('Sh2-129'),
    _dso('B33', common: 'Horsehead Nebula'),
    _dso('LDN 1235'),
    _dso('NGC0224', common: 'Andromeda Galaxy'),
  ];

  test('designations resolve case/space/dash-insensitively', () {
    expect(findCatalogObject(catalog, 'Sh2-129')!.id, 'Sh2-129');
    expect(findCatalogObject(catalog, 'sh2 129')!.id, 'Sh2-129');
    expect(findCatalogObject(catalog, 'SH2129')!.id, 'Sh2-129');
    expect(findCatalogObject(catalog, 'b 33')!.id, 'B33');
    expect(findCatalogObject(catalog, 'ldn1235')!.id, 'LDN 1235');
  });

  test('common names resolve by substring', () {
    expect(findCatalogObject(catalog, 'horsehead')!.id, 'B33');
    expect(findCatalogObject(catalog, 'andromeda')!.id, 'NGC0224');
  });

  test('no match returns null so the Stellarium fallback fires', () {
    expect(findCatalogObject(catalog, 'jupiter'), isNull);
    expect(findCatalogObject(catalog, ''), isNull);
  });

  test('a suffixed WR designation never answers for the plain number', () {
    // "WR 2-1" and "WR 21" both strip to wr21 once dashes go (review #1107):
    // the dash-preserving pass has to win before the loose one.
    const wr21 = PlanningDso(
        id: 'WR 2-1', name: 'WR 2-1', type: 'WR*', magnitude: 14.0,
        raDeg: 18.0, decDeg: 64.8);
    const wr21plain = PlanningDso(
        id: 'WR 21', name: 'WR 21', type: 'WR*', magnitude: 9.8,
        raDeg: 155.7, decDeg: -58.1);
    for (final order in [[wr21, wr21plain], [wr21plain, wr21]]) {
      expect(findCatalogObject(order, 'WR 21')!.id, 'WR 21');
      expect(findCatalogObject(order, 'wr21')!.id, 'WR 21');
      expect(findCatalogObject(order, 'WR 2-1')!.id, 'WR 2-1');
      expect(findCatalogObject(order, 'wr 2-1')!.id, 'WR 2-1');
    }
    // Loose matching still serves the un-dashed spellings people type.
    const sh2 = PlanningDso(
        id: 'Sh2-129', name: 'Sh2-129', type: 'HII', magnitude: null,
        raDeg: 318.0, decDeg: 60.0);
    expect(findCatalogObject(const [sh2, wr21], 'SH2129')!.id, 'Sh2-129');
    expect(findCatalogObject(const [sh2, wr21], 'sh2 129')!.id, 'Sh2-129');
  });
}
