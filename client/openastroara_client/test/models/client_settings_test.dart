import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/client_settings.dart';

void main() {
  group('ClientSettings.fromJson', () {
    test('parses a settings object + timestamp', () {
      final cs = ClientSettings.fromJson(const {
        'settings': {'theme': 'dark', 'zoom': 1.5},
        'updated_utc': '2026-06-14T12:00:00Z',
      });
      expect(cs.settings['theme'], 'dark');
      expect(cs.settings['zoom'], 1.5);
      expect(cs.updatedUtc, DateTime.utc(2026, 6, 14, 12));
      expect(cs.updatedUtc!.isUtc, isTrue);
      expect(cs.isEmpty, isFalse);
    });

    test('an empty store → empty map + null timestamp', () {
      final cs = ClientSettings.fromJson(const {'settings': {}, 'updated_utc': null});
      expect(cs.isEmpty, isTrue);
      expect(cs.updatedUtc, isNull);
    });

    test('degrades a missing / wrong-typed settings field to an empty map', () {
      expect(ClientSettings.fromJson(const {'settings': 'nope'}).isEmpty, isTrue);
      expect(ClientSettings.fromJson(const {}).isEmpty, isTrue);
    });

    test('the settings map is unmodifiable', () {
      final cs = ClientSettings.fromJson(const {'settings': {'a': 1}});
      expect(() => cs.settings['b'] = 2, throwsUnsupportedError);
    });
  });
}
