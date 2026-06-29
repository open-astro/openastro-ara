import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/sky_atlas/sky_atlas_state.dart';

void main() {
  group('SkyAtlasModeNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults to catalogView', () {
      expect(container.read(skyAtlasModeProvider), SkyAtlasMode.catalogView);
    });

    test('set switches mode', () {
      final n = container.read(skyAtlasModeProvider.notifier);
      n.set(SkyAtlasMode.tonightsSky);
      expect(container.read(skyAtlasModeProvider), SkyAtlasMode.tonightsSky);
      n.set(SkyAtlasMode.catalogView);
      expect(container.read(skyAtlasModeProvider), SkyAtlasMode.catalogView);
    });

    test('toggle flips between catalog and tonight', () {
      final n = container.read(skyAtlasModeProvider.notifier);
      n.toggle();
      expect(container.read(skyAtlasModeProvider), SkyAtlasMode.tonightsSky);
      n.toggle();
      expect(container.read(skyAtlasModeProvider), SkyAtlasMode.catalogView);
    });
  });

  group('SkyAtlasSearchNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults to empty', () {
      expect(container.read(skyAtlasSearchProvider), '');
    });

    test('set updates query', () {
      final n = container.read(skyAtlasSearchProvider.notifier);
      n.set('M42');
      expect(container.read(skyAtlasSearchProvider), 'M42');
    });
  });

  group('PlanetariumCommandNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('starts null', () {
      expect(container.read(planetariumCommandProvider), isNull);
    });

    test('send stores the command map', () {
      container
          .read(planetariumCommandProvider.notifier)
          .send({'type': 'goto', 'ra': 10.6847, 'dec': 41.269});
      expect(container.read(planetariumCommandProvider),
          {'type': 'goto', 'ra': 10.6847, 'dec': 41.269});
    });

    test('clear resets the bus to null after a command is consumed', () {
      final n = container.read(planetariumCommandProvider.notifier);
      n.send({'type': 'goto', 'ra': 1.0, 'dec': 2.0});
      n.clear();
      expect(container.read(planetariumCommandProvider), isNull);
    });

    test('always notifies so re-sending an identical command re-fires', () {
      var notifications = 0;
      container.listen<Map<String, Object?>?>(
          planetariumCommandProvider, (_, _) => notifications++);
      final n = container.read(planetariumCommandProvider.notifier);
      n.send({'type': 'goto', 'ra': 1.0, 'dec': 2.0});
      n.send({'type': 'goto', 'ra': 1.0, 'dec': 2.0}); // identical
      expect(notifications, 2);
    });
  });

  group('skyImageryAvailableProvider', () {
    test('Phase 12e.1 stub returns false', () {
      final container = ProviderContainer();
      addTearDown(container.dispose);
      // Demo state — no downloads tracked yet (12e.2 lifts this).
      expect(container.read(skyImageryAvailableProvider), isFalse);
    });
  });
}
