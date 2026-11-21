import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/nina_import.dart';

/// `metadata.importedFrom` (docs/specs/sequence-v2.md, "NINA JSON import" step
/// 7): `{ "format": "nina", "fileName": "<name>", "importedUtc": "<ISO 8601>" }`.
/// `translateNinaSequence` takes the file name and the clock as optional named
/// parameters `fileName` and `now`; they are called through [Function.apply]
/// so this file compiles before they exist and fails because the function does
/// not accept them.
const _nina = {
  r'$type': 'NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer',
  'Name': 'M42 wide',
  'Items': {
    r'$values': [
      {
        r'$type':
            'NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer',
        'ExposureTime': 60.0,
      },
    ],
  },
};

NinaImportTranslation _translate(
  Map<String, dynamic> nina, {
  String? fileName,
  DateTime? now,
}) => Function.apply(
  translateNinaSequence,
  [nina],
  {#fileName: ?fileName, #now: ?now},
) as NinaImportTranslation;

void main() {
  test(
    'the document records where it came from, with an ISO 8601 UTC stamp',
    () {
      final t = _translate(
        _nina,
        fileName: 'M42 wide.json',
        now: DateTime.utc(2026, 11, 19, 12, 30, 5),
      );
      final metadata = t.body['metadata'] as Map;
      expect(metadata.keys, ['importedFrom']);
      final from = metadata['importedFrom'] as Map;
      expect(from.keys, ['format', 'fileName', 'importedUtc']);
      expect(from['format'], 'nina');
      expect(from['fileName'], 'M42 wide.json');
      final stamp = from['importedUtc'] as String;
      expect(stamp, endsWith('Z'));
      expect(DateTime.parse(stamp), DateTime.utc(2026, 11, 19, 12, 30, 5));
    },
  );

  test('a local clock is stamped in UTC', () {
    final local = DateTime(2026, 11, 19, 12, 30, 5);
    final t = _translate(_nina, fileName: 'a.json', now: local);
    final stamp =
        ((t.body['metadata'] as Map)['importedFrom'] as Map)['importedUtc']
            as String;
    expect(stamp, endsWith('Z'));
    expect(DateTime.parse(stamp), local.toUtc());
  });
}
