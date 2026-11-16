import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/custom_horizon_state.dart';

import '../../support/fake_section_rig.dart';
import '../../support/section_load.dart';

void main() {
  group('CustomHorizonNotifier', () {
    late ProviderContainer container;

    setUp(() async {
      container = ProviderContainer();
      addTearDown(container.dispose);
      await loadSection(container, customHorizonProvider, 'custom-horizon');
    });

    List<(double, double)> shape(List<CustomHorizonPoint> pts) => [
      for (final p in pts) (p.azimuthDeg, p.altitudeDeg),
    ];

    test('add/update keep row indices STABLE; remove drops the row', () {
      final n = container.read(customHorizonProvider.notifier);

      n.addPoint(180, 40);
      n.addPoint(10, 15);
      // NOT sorted while editing: the panel's editable rows bind to their
      // index, so reordering mid-edit would rebind a just-edited field to a
      // different vertex. Sorting happens on hydrate + the Save echo.
      expect(shape(container.read(customHorizonProvider).value!), [
        (180.0, 40.0),
        (10.0, 15.0),
      ]);

      // Editing an azimuth keeps the row where the user is editing it.
      n.updateAt(0, azimuthDeg: 270);
      expect(shape(container.read(customHorizonProvider).value!), [
        (270.0, 40.0),
        (10.0, 15.0),
      ]);

      n.removeAt(1);
      expect(shape(container.read(customHorizonProvider).value!), [
        (270.0, 40.0),
      ]);

      // Out-of-range indices are ignored, not thrown.
      n.removeAt(5);
      n.updateAt(-1, altitudeDeg: 1);
      expect(container.read(customHorizonProvider).value!, hasLength(1));

      // Out-of-range VALUES are rejected like the sibling site setters —
      // the daemon's 422 range check is unreachable from this UI.
      n.addPoint(400, 10);
      n.addPoint(10, 95);
      n.updateAt(0, azimuthDeg: -5);
      n.updateAt(0, altitudeDeg: -20);
      expect(container.read(customHorizonProvider).value!, hasLength(1));
      expect(
        container.read(customHorizonProvider).value!.single.azimuthDeg,
        270,
      );
    });

    test('hydrate sorts the served skyline; persist sends staged and adopts the echo', () async {
      final rig = FakeSectionRig({
        'custom-horizon': ProfileApi.customHorizonToJson(const [
          CustomHorizonPoint(azimuthDeg: 350, altitudeDeg: 5),
          CustomHorizonPoint(azimuthDeg: 20, altitudeDeg: 30),
        ]),
      });
      final api = rig.api();
      final n = container.read(customHorizonProvider.notifier);

      await n.hydrateFromServer(api);
      expect(shape(container.read(customHorizonProvider).value!), [
        (20.0, 30.0),
        (350.0, 5.0),
      ]);

      n.addPoint(100, 12);
      await n.save(api);

      expect(
        (rig.puts.single.body as Map)['points'],
        hasLength(3),
        reason: 'staged skyline was sent',
      );
      expect(shape(container.read(customHorizonProvider).value!), [
        (20.0, 30.0),
        (100.0, 12.0),
        (350.0, 5.0),
      ], reason: 'client adopts the echo, sorted');
    });
  });
}
