import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/settings_search.dart';

void main() {
  group('searchSettings', () {
    late List<SettingsSearchEntry> index;

    setUp(() => index = buildSearchIndex());

    test('empty query returns empty', () {
      expect(searchSettings(index, ''), isEmpty);
      expect(searchSettings(index, '   '), isEmpty);
    });

    test('limit <= 0 short-circuits to empty', () {
      expect(searchSettings(index, 'camera', limit: 0), isEmpty);
      expect(searchSettings(index, 'camera', limit: -5), isEmpty);
    });

    test('exact label match ranks highest', () {
      final results = searchSettings(index, 'camera');
      expect(results.first.label, 'Camera');
    });

    test('keyword "dither" surfaces Guider panel', () {
      final results = searchSettings(index, 'dither');
      expect(results.any((e) => e.panelId == 'eq.guider'), isTrue);
    });

    test('keyword "park" surfaces both Mount and Safety Policies', () {
      final results = searchSettings(index, 'park').map((e) => e.panelId).toSet();
      expect(results, containsAll(['eq.mount', 'safety.policies']));
    });

    test('group label "equipment" surfaces equipment panels', () {
      final results = searchSettings(index, 'equipment');
      expect(results.any((e) => e.groupLabel == 'Equipment'), isTrue);
    });

    test('no-match query returns empty', () {
      expect(searchSettings(index, 'xyzzyplugh-no-such-word'), isEmpty);
    });

    test('limit caps result count', () {
      final all = searchSettings(index, 'a');
      final capped = searchSettings(index, 'a', limit: 2);
      expect(capped.length, lessThanOrEqualTo(2));
      expect(all.length, greaterThanOrEqualTo(capped.length));
    });
  });

  group('buildSearchIndex', () {
    test('returns one entry per panel in the settings tree', () {
      final entries = buildSearchIndex();
      expect(entries.length, greaterThan(20)); // 22 panels in Phase 12h.2
      // Spot-check a few known panels exist.
      final ids = entries.map((e) => e.panelId).toSet();
      expect(ids, containsAll([
        'eq.camera',
        'eq.mount',
        'img.defaults',
        'session.storage',
        'safety.diagnostics',
        'profile.active',
      ]));
    });
  });
}
