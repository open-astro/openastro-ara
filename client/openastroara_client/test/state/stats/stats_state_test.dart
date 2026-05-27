import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/stats/stats_state.dart';

void main() {
  group('statsOverviewProvider', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('demo data → 7 sessions, 7 targets, 7 nights', () {
      // Library demo seeds 7 distinct sessions for distinct targets+dates
      // (see _demoSessions in library_state.dart).
      final overview = container.read(statsOverviewProvider);
      expect(overview.totalSessions, 7);
      expect(overview.totalTargets, 7);
      expect(overview.totalNights, 7);
    });

    test('totalFrames matches sum across all sessions', () {
      final overview = container.read(statsOverviewProvider);
      // 6 + 9 + 12 + 12 + 10 + 6 + 5 = 60 demo frames.
      expect(overview.totalFrames, 60);
    });

    test('totalIntegration is non-zero (lights only)', () {
      final overview = container.read(statsOverviewProvider);
      expect(overview.totalIntegration.inMinutes, greaterThan(0));
    });

    test('averageHfr is in a reasonable astrophotography range', () {
      final overview = container.read(statsOverviewProvider);
      expect(overview.averageHfr, greaterThan(1.0));
      expect(overview.averageHfr, lessThan(3.0));
    });
  });

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
