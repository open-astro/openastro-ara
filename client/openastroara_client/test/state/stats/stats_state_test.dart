import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/stats/stats_state.dart';

void main() {
  // The Overview section is now backed by the live `/api/v1/stats/overview`
  // endpoint (see stats_overview_state_test.dart); the demo `statsOverviewProvider`
  // was removed. Targets + Best Frames still derive from the library demo data.
  group('targetRollupsProvider', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('returns one rollup per distinct target', () {
      final rollups = container.read(targetRollupsProvider);
      expect(rollups.length, 7);
      final names = rollups.map((r) => r.targetName).toSet();
      expect(names.length, 7);
    });

    test('sorted by integration descending', () {
      final rollups = container.read(targetRollupsProvider);
      for (var i = 1; i < rollups.length; i++) {
        expect(
          rollups[i - 1].integration >= rollups[i].integration,
          isTrue,
          reason: 'order violated at idx $i: '
              '${rollups[i - 1].integration} vs ${rollups[i].integration}',
        );
      }
    });

    test('per-rollup frameCount + integration are non-zero', () {
      for (final r in container.read(targetRollupsProvider)) {
        expect(r.frameCount, greaterThan(0), reason: r.targetName);
        expect(r.integration.inMinutes, greaterThan(0), reason: r.targetName);
      }
    });
  });

  group('bestFramesProvider', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('returns at most 10 lights ranked by HFR ascending', () {
      final best = container.read(bestFramesProvider);
      expect(best.length, lessThanOrEqualTo(10));
      for (var i = 1; i < best.length; i++) {
        expect(best[i - 1].hfr <= best[i].hfr, isTrue);
      }
    });

    test('all entries are Light frames', () {
      final best = container.read(bestFramesProvider);
      for (final f in best) {
        expect(f.frameType.toLowerCase(), 'light');
      }
    });
  });
}
