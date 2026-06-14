import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/stats/stats_state.dart';

void main() {
  // Overview, Targets, and Achievements are now backed by the live
  // `/api/v1/stats/*` endpoints (see stats_overview_state_test.dart and
  // stats_targets_state_test.dart). Best Frames still derives from the library
  // demo data until its endpoint is wired.
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
