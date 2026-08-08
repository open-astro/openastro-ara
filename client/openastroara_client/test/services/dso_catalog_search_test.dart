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
}
