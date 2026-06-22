import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_share_export.dart';

void main() {
  group('SequenceShareExport.fromJson', () {
    test('parses the daemon snake_case response with an inline manifest', () {
      final share = SequenceShareExport.fromJson(const {
        'sequence_id': 'abc',
        'sequence_name': 'M31 LRGB',
        'share_format': 'openastroara.v1',
        'manifest': {'schemaVersion': 'openastroara-sequence-v1', 'items': []},
        // download_url is null in v0.0.1 — the manifest is inline; ignored here.
        'download_url': null,
      });
      expect(share.sequenceName, 'M31 LRGB');
      expect(share.manifest['schemaVersion'], 'openastroara-sequence-v1');
    });

    test('defaults the name to empty when sequence_name is absent', () {
      final share = SequenceShareExport.fromJson(const {
        'manifest': {'schemaVersion': 'v1'},
      });
      expect(share.sequenceName, '');
    });

    test('throws on a missing manifest rather than writing an empty share', () {
      expect(() => SequenceShareExport.fromJson(const {'sequence_name': 'x'}),
          throwsA(isA<FormatException>()));
    });

    test('throws on an empty manifest', () {
      expect(
          () => SequenceShareExport.fromJson(const {'manifest': <String, dynamic>{}}),
          throwsA(isA<FormatException>()));
    });
  });
}
