import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/util/imaging_regions.dart';

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

  test('curated imaging-region names resolve on top of the mirror', () {
    // The planetarium search runs over applyImagingRegions(mirror): the WR
    // shells / famous complexes have imaging names (and some have no mirror
    // row at all), so "WR 134" or "thor" must hit even with an empty mirror.
    final layered = applyImagingRegions(const []);
    expect(findCatalogObject(layered, 'WR 134')!.id, 'REGION-WR134');
    expect(findCatalogObject(layered, 'wr134')!.id, 'REGION-WR134');
    expect(findCatalogObject(layered, 'dolphin')!.id, 'REGION-SH2-308');
    expect(findCatalogObject(layered, 'tulip')!.id, 'REGION-SH2-101');
    // An override renames a mirror row: the imaging name resolves to it.
    const crescent = PlanningDso(
        id: 'NGC6888', name: 'NGC6888', type: 'EmN', magnitude: 7.4,
        raDeg: 303.05, decDeg: 38.35, sizeMajArcmin: 18);
    final withRow = applyImagingRegions(const [crescent]);
    expect(findCatalogObject(withRow, 'crescent')!.id, 'NGC6888');
    expect(findCatalogObject(withRow, "thor's helmet"), isNull,
        reason: 'no NGC 2359 row in this mirror — nothing to rename');
  });
}
