import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/sky_atlas/sky_atlas_state.dart';

void main() {
  group('skyAtlasSearchProvider', () {
    test('re-emits on resubmitting the same target (so a repeat search recenters)', () {
      final c = ProviderContainer();
      addTearDown(c.dispose);

      final fired = <String>[];
      c.listen(skyAtlasSearchProvider, (_, next) => fired.add(next), fireImmediately: false);

      final notifier = c.read(skyAtlasSearchProvider.notifier);
      notifier.set('M31');
      notifier.set('M31'); // same target again — must still notify
      notifier.set('M42');

      expect(fired, ['M31', 'M31', 'M42']);
    });
  });
}
