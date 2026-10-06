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

  PlanningDso row(String id, String type, {double? mag}) => PlanningDso(
    id: id,
    name: id,
    type: type,
    magnitude: mag,
    raDeg: 10,
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
        ]),
      );
      final container = ProviderContainer(
        overrides: [
          bundledCatalogProvider.overrideWith(
            (ref) async => [row('NGC0224', 'G', mag: 3.4)],
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
        ['NGC0224', 'PGC 1'],
        reason:
            'a star has no field to frame; size-unknown would score it '
            'neutral ahead of real galaxies (review #1105)',
      );
    },
  );
}
