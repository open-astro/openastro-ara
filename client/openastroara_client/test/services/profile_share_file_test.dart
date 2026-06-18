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
}
