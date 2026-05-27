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

  group('skyImageryAvailableProvider', () {
    test('Phase 12e.1 stub returns false', () {
      final container = ProviderContainer();
      addTearDown(container.dispose);
      // Demo state — no downloads tracked yet (12e.2 lifts this).
      expect(container.read(skyImageryAvailableProvider), isFalse);
    });
  });
}
