import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/catalog_object.dart';

void main() {
  group('CatalogObject.tryFromJson', () {
    test('parses a full row', () {
      final o = CatalogObject.tryFromJson(const {
        'name': 'Andromeda Galaxy',
        'ra_deg': 10.6847,
        'dec_deg': 41.2693,
        'magnitude': 3.4,
      });
      expect(o, isNotNull);
      expect(o!.name, 'Andromeda Galaxy');
      expect(o.raDeg, closeTo(10.6847, 1e-9));
      expect(o.decDeg, closeTo(41.2693, 1e-9));
      expect(o.magnitude, 3.4);
    });

    test('a missing magnitude is null (not a parse failure)', () {
      final o = CatalogObject.tryFromJson(const {'name': 'X', 'ra_deg': 1, 'dec_deg': 2});
      expect(o, isNotNull);
      expect(o!.magnitude, isNull);
    });

    test('an integer coordinate is accepted (wire may send 0 not 0.0)', () {
      final o = CatalogObject.tryFromJson(const {'name': 'Sol', 'ra_deg': 0, 'dec_deg': 0});
      expect(o, isNotNull);
      expect(o!.raDeg, 0.0);
    });

    test('a row without a finite position is dropped (null)', () {
      expect(CatalogObject.tryFromJson(const {'name': 'NoPos', 'magnitude': 5}), isNull);
      expect(CatalogObject.tryFromJson(const {'name': 'BadRa', 'ra_deg': 'x', 'dec_deg': 2}), isNull);
      expect(CatalogObject.tryFromJson(const {'name': 'NaN', 'ra_deg': double.nan, 'dec_deg': 2}), isNull);
    });

    test('a missing name degrades to empty (still placeable)', () {
      final o = CatalogObject.tryFromJson(const {'ra_deg': 1, 'dec_deg': 2});
      expect(o, isNotNull);
      expect(o!.name, '');
    });
  });
}
