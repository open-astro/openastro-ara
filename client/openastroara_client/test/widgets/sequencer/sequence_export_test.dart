import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/sequencer/sequence_export.dart';

void main() {
  group('prepareNinaExportJson', () {
    test('strips the ARA schemaVersion backfill, keeps everything else', () {
      final json = prepareNinaExportJson({
        'schemaVersion': 1,
        r'$type': 'NINA...Root',
        'Name': 'M42',
        'Items': {
          r'$values': [
            {'Exposure': 60}
          ]
        },
      });
      final decoded = jsonDecode(json) as Map<String, dynamic>;
      expect(decoded.containsKey('schemaVersion'), isFalse); // stripped
      expect(decoded['Name'], 'M42'); // preserved
      expect((decoded['Items'] as Map)[r'$values'], hasLength(1)); // nested kept
    });

    test('is pretty-printed (2-space indent)', () {
      final json = prepareNinaExportJson({'Name': 'M42'});
      expect(json, contains('\n  "Name"'));
    });

    test('does not mutate the source body', () {
      final body = {'schemaVersion': 1, 'Name': 'M42'};
      prepareNinaExportJson(body);
      expect(body.containsKey('schemaVersion'), isTrue); // shallow-copied, intact
    });
  });

  group('sequenceExportFileName', () {
    test('appends .json and sanitizes unsafe characters', () {
      expect(sequenceExportFileName('M42 LRGB'), 'M42 LRGB.json');
      expect(sequenceExportFileName('M42/LRGB:v2'), 'M42_LRGB_v2.json');
    });

    test('falls back to "sequence" for a blank name', () {
      expect(sequenceExportFileName('   '), 'sequence.json');
    });

    test('does not double the .json suffix (case-insensitive)', () {
      expect(sequenceExportFileName('M42.json'), 'M42.json');
      expect(sequenceExportFileName('M42.JSON'), 'M42.json');
      expect(sequenceExportFileName('.json'), 'sequence.json'); // only suffix
    });
  });
}
