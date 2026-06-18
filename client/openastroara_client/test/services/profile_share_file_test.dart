import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/profile_share_file.dart';

void main() {
  group('parseShareManifest', () {
    test('decodes a JSON object into the manifest map', () {
      final bytes = utf8.encode(
          '{"schema_version":"profile-share-v1","settings":{"a":1}}');
      final manifest = parseShareManifest(bytes);
      expect(manifest['schema_version'], 'profile-share-v1');
      expect(manifest['settings'], isA<Map<String, dynamic>>());
    });

    test('throws a friendly FormatException on non-JSON bytes', () {
      final bytes = utf8.encode('not json at all');
      expect(
        () => parseShareManifest(bytes),
        throwsA(isA<FormatException>().having(
            (e) => e.message, 'message', contains("isn't a profile share"))),
      );
    });

    test('throws when the JSON is not an object (e.g. an array)', () {
      final bytes = utf8.encode('[1,2,3]');
      expect(
        () => parseShareManifest(bytes),
        throwsA(isA<FormatException>().having(
            (e) => e.message, 'message', contains('expected a JSON object'))),
      );
    });
  });

  group('shareExpiryNote', () {
    final now = DateTime.utc(2026, 6, 18, 4, 0, 0);

    test('returns null when there is no expiry', () {
      expect(shareExpiryNote(null, now: now), isNull);
    });

    test('shows minutes remaining for a future expiry', () {
      final note =
          shareExpiryNote(now.add(const Duration(minutes: 14)), now: now);
      expect(note, contains('about 14 minutes'));
    });

    test('says expired once the deadline has passed', () {
      final note =
          shareExpiryNote(now.subtract(const Duration(seconds: 1)), now: now);
      expect(note, contains('expired'));
    });

    test('normalizes a local-time expiry to UTC before comparing', () {
      // A non-UTC DateTime must still compare correctly (both sides → UTC).
      final localExpiry = now.add(const Duration(minutes: 10)).toLocal();
      expect(shareExpiryNote(localExpiry, now: now), contains('about 10'));
    });
  });
}
