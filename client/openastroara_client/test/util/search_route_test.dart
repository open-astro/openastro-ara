import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/services/target_resolve_api.dart';
import 'package:openastroara/util/search_route.dart';

void main() {
  final catalog = [
    const PlanningDso(
      id: 'NGC7000',
      name: 'North America Nebula',
      type: 'Neb',
      magnitude: null,
      raDeg: 314.82,
      decDeg: 44.53,
    ),
  ];

  test('a pasted position opens the coordinate entry', () {
    expect(
      routeSearch(catalog, '05 35 17 -05 23 28').kind,
      SearchRouteKind.coordinates,
    );
    expect(
      routeSearch(catalog, '83.82 -5.39').kind,
      SearchRouteKind.coordinates,
    );
  });

  test('a catalog object is a goto with its name', () {
    final r = routeSearch(catalog, 'ngc 7000');
    expect(r.kind, SearchRouteKind.catalog);
    expect(r.command!['type'], 'goto');
    expect(r.command!['name'], 'North America Nebula');
  });

  test('solar-system names go to the engine at once, never the daemon', () {
    for (final q in [
      'Moon',
      ' jupiter ',
      'MARS',
      'Sun',
      'Sol',
      'Titan',
      'Miranda',
      'Eris',
    ]) {
      final r = routeSearch(catalog, q);
      expect(r.kind, SearchRouteKind.engine, reason: q);
      expect(r.command!['type'], 'search');
    }
  });

  test('anything else asks the daemon and carries the engine fall-through', () {
    final r = routeSearch(catalog, 'AB Cas');
    expect(r.kind, SearchRouteKind.resolve);
    expect(r.command, {'type': 'search', 'q': 'AB Cas'});
  });

  test('the fall-through message explains an offline miss and stays quiet when the engine may find it', () {
    expect(
      searchFallbackMessage(
        const TargetResolution(
          status: 'resolved',
          mainId: 'x',
          raDeg: 1,
          decDeg: 2,
        ),
      ),
      isNull,
    );
    expect(
      searchFallbackMessage(const TargetResolution(status: 'not_found')),
      isNull,
    );
    expect(
      searchFallbackMessage(
        const TargetResolution(
          status: 'not_found',
          message: 'Simbad knows no object by that name.',
        ),
      ),
      'Simbad knows no object by that name.',
    );
    expect(
      searchFallbackMessage(
        const TargetResolution(status: 'offline', message: 'Needs internet.'),
      ),
      'Needs internet. If the sky map does not find it either, try again with internet.',
    );
  });

  test(
    'a resolved object the bundle lists goes to that row, by main id or alias',
    () {
      const r = TargetResolution(
        status: 'resolved',
        source: 'simbad',
        mainId: 'NAME North America Nebula',
        aliases: ['NGC 7000', 'LBN 373'],
        raDeg: 314.8,
        decDeg: 44.5,
      );
      final goto = routeResolved(catalog, r);
      expect(goto, isNotNull);
      expect(goto!['type'], 'goto');
      expect(goto['name'], 'North America Nebula');
      expect(
        routeResolved(
          catalog,
          const TargetResolution(
            status: 'resolved',
            mainId: 'V* AB Cas',
            raDeg: 1,
            decDeg: 2,
          ),
        ),
        isNull,
      );
      expect(
        routeResolved(catalog, const TargetResolution(status: 'offline')),
        isNull,
      );
      // Simbad's "PN M 2-9" (the Twin Jet) is not M 29.
      final m29 = [
        const PlanningDso(
          id: 'NGC6913',
          name: 'NGC6913',
          type: 'OCl',
          magnitude: 6.6,
          raDeg: 305.98,
          decDeg: 38.5,
          aliases: ['M 29'],
        ),
      ];
      expect(
        routeResolved(
          m29,
          const TargetResolution(
            status: 'resolved',
            mainId: 'PN M 2-9',
            raDeg: 1,
            decDeg: 2,
          ),
        ),
        isNull,
      );
      // Exact only: a Simbad alias that merely contains a bundled name's
      // text does not land on that row.
      expect(
        routeResolved(
          catalog,
          const TargetResolution(
            status: 'resolved',
            mainId: 'NAME North',
            raDeg: 1,
            decDeg: 2,
          ),
        ),
        isNull,
      );
    },
  );
}
