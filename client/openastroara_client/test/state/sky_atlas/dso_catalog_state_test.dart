import 'dart:convert';
import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/state/sky_atlas/dso_catalog_state.dart';

/// #1198 (review on #1277): the server-mirror rows bypass planningCull, so
/// dsoCatalogProvider repeats the star gate on them. Nothing read the
/// provider before, so removing that gate left the suite green.
void main() {
  late Directory dir;
  setUp(() => dir = Directory.systemTemp.createTempSync('dso-mirror-'));
  tearDown(() => dir.deleteSync(recursive: true));

  // Distinct positions: two rows at one spot of one class are one object
  // (#1267 item 4) and would fold together, which is not what these test.
  PlanningDso row(String id, String type, {double? mag}) => PlanningDso(
    id: id,
    name: id,
    type: type,
    magnitude: mag,
    // A position of its own per id, so no two rows fold together.
    raDeg: (id.hashCode % 3000) / 10,
    decDeg: 20,
  );

  test(
    'mirror rows the bundle lacks are appended, star rows are culled',
    () async {
      // The service reads dso_catalog.json from its support dir: write the
      // mirror the daemon would have left there.
      File('${dir.path}/dso_catalog.json').writeAsStringSync(
        jsonEncode([
          row('WR 134', 'WR*', mag: 8.1).toJson(),
          row('HD 1', '*', mag: 6.0).toJson(),
          row('PGC 1', 'G', mag: 11.0).toJson(),
          row(
            'NGC0224',
            'G',
            mag: 3.4,
          ).toJson(), // also in the bundle: not doubled
          // The bundle folded Sh2-117 into NGC 7000: the mirror's own
          // Sh2-117 row rejoins it through the alias (#1267 item 4).
          row('Sh2-117', 'HII').toJson(),
        ]),
      );
      final container = ProviderContainer(
        overrides: [
          bundledCatalogProvider.overrideWith(
            (ref) async => [
              row('NGC0224', 'G', mag: 3.4),
              const PlanningDso(
                id: 'NGC7000',
                name: 'North America Nebula',
                type: 'Neb',
                magnitude: null,
                raDeg: 314.8,
                decDeg: 44.5,
                aliases: ['Sh2-117'],
              ),
            ],
          ),
          dsoCatalogServiceProvider.overrideWithValue(
            DsoCatalogService(supportDir: () async => dir),
          ),
        ],
      );
      addTearDown(container.dispose);

      final ids = (await container.read(dsoCatalogProvider.future))
          .map((d) => d.id);
      expect(
        ids,
        ['NGC0224', 'NGC7000', 'PGC 1'],
        reason:
            'a star has no field to frame; size-unknown would score it '
            'neutral ahead of real galaxies (review #1105)',
      );
    },
  );
}
