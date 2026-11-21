import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/sky_atlas/dso_catalog_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';

class _Site extends SiteSettingsNotifier {
  @override
  LoadedSection<SiteSettings> build() => LoadedSection(
    value: const SiteSettings(latitudeDeg: 51.477833, longitudeDeg: -0.0015),
    source: SectionSource.rig,
  );
}

void main() {
  test(
    "Tonight's Best ranks again on the sky clock tick, from the clock's now",
    () async {
      var now = DateTime.utc(2026, 1, 15, 20);
      final tick = StreamController<int>();
      final c = ProviderContainer(
        overrides: [
          activeServerFutureProvider.overrideWith((ref) async => null),
          siteSettingsProvider.overrideWith(_Site.new),
          dsoCatalogProvider.overrideWith(
            (ref) async => const [
              PlanningDso(
                id: 'HIGH',
                name: 'High',
                type: 'G',
                magnitude: 9,
                raDeg: 135.0,
                decDeg: 41.3,
              ),
            ],
          ),
          clockProvider.overrideWithValue(() => now),
          skyClockProvider.overrideWith((ref) => tick.stream),
        ],
      );
      addTearDown(() {
        c.dispose();
        tick.close();
      });
      c.listen(tonightsBestProvider, (_, _) {});
      double hours(List<dynamic> l) =>
          l.firstWhere((o) => o.id == 'HIGH').hoursLeft as double;
      final first = hours(await c.read(tonightsBestProvider.future));
      now = DateTime.utc(2026, 1, 15, 7);
      tick.add(1);
      await Future<void>.delayed(const Duration(milliseconds: 50));
      final later = hours(await c.read(tonightsBestProvider.future));
      expect(later, lessThan(first));
    },
  );
}
