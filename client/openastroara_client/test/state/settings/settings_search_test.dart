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

    test('§68.4 help entries are indexed as informational hits', () {
      final helpHits = index.where((e) => e.helpKey != null).toList();
      expect(helpHits, isNotEmpty, reason: 'the §69 help registry is indexed');
      expect(helpHits.every((e) => e.groupLabel == 'Help'), isTrue);
      expect(helpHits.every((e) => e.panelId == null && e.settingId == null),
          isTrue, reason: 'help hits open the sheet, never navigate');
    });

    test('§68.4 "equipment hub down" surfaces the AlpacaBridge troubleshoot '
        'help entry', () {
      final results = searchSettings(index, 'equipment hub down');
      expect(results, isNotEmpty);
      expect(results.first.helpKey, 'equipment.alpacabridge.troubleshoot');
    });

    test('§68.4 a help TITLE search finds the entry too', () {
      final results = searchSettings(index, 'alpacabridge not detected');
      expect(results.map((e) => e.helpKey),
          contains('equipment.alpacabridge.troubleshoot'));
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

  group('§61.10 navigation entries', () {
    test('"go to run" surfaces the Run tab navigation hit first', () {
      final index = buildSearchIndex();
      final results = searchSettings(index, 'go to run');
      expect(results, isNotEmpty);
      expect(results.first.label, 'Go to Run');
      expect(results.first.tabIndex, 1);
    });

    test('each main tab is reachable by an intent word', () {
      final index = buildSearchIndex();
      expect(searchSettings(index, 'planning').any((e) => e.tabIndex == 0), isTrue);
      expect(searchSettings(index, 'sequencer').any((e) => e.tabIndex == 1), isTrue);
      expect(searchSettings(index, 'live view').any((e) => e.tabIndex == 2), isTrue);
      expect(searchSettings(index, 'preferences').any((e) => e.tabIndex == 3), isTrue);
    });

    test('navigation hits carry a stable synthetic id', () {
      final index = buildSearchIndex();
      final nav = index.where((e) => e.tabIndex != null).toList();
      expect(nav, hasLength(4));
      expect(nav.map((e) => e.id), everyElement(startsWith('nav.')));
    });
  });
}
