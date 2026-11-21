import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/run_index_map.dart';

Map<String, dynamic> container(String name, List<Map<String, dynamic>> kids) => {
      r'$type': 'X.SequentialContainer',
      'Name': name,
      'Items': {r'$type': itemsWrapperType, r'$values': kids},
    };

Map<String, dynamic> leaf(String type, [Map<String, dynamic>? extra]) =>
    {r'$type': 'OpenAstroAra.Sequencer.SequenceItem.$type, X', ...?extra};

void main() {
  // root → [slew, inner[exposure, exposure], dither]
  Map<String, dynamic> body() => container('root', [
        leaf('SlewScopeToRaDec'),
        container('inner', [
          leaf('TakeExposure', {'ExposureTime': 60.0}),
          leaf('TakeExposure', {'ExposureTime': 60.0}),
        ]),
        leaf('Dither'),
      ]);

  test('executableLeafPaths walks depth-first, skipping containers', () {
    final paths = executableLeafPaths(body());
    expect(paths, [
      [0],
      [1, 0],
      [1, 1],
      [2],
    ]);
  });

  test('verified ordinal mapping when totals agree', () {
    final s = resolveSpotlight(body(), index: 2, total: 4);
    expect(s.verified, isTrue);
    expect(s.currentPath, [1, 1]);
    expect(s.completedPaths, [
      [0],
      [1, 0],
    ]);
  });

  test('count mismatch → description fallback, no completed tinting', () {
    final s = resolveSpotlight(body(),
        index: 7, total: 9, description: 'Dither');
    expect(s.verified, isFalse);
    expect(s.currentPath, [2]);
    expect(s.completedPaths, isEmpty);
  });

  test('ambiguous description advances monotonically', () {
    final first = resolveSpotlight(body(),
        index: 1, total: 99, description: 'TakeExposure');
    expect(first.currentLeaf, 1); // first TakeExposure
    final second = resolveSpotlight(body(),
        index: 2,
        total: 99,
        description: 'TakeExposure',
        lastMatchedLeaf: 2);
    expect(second.currentLeaf, 2); // never behind lastMatchedLeaf
  });

  test('out-of-range / unmatched → null spotlight, never a wrong node', () {
    final s = resolveSpotlight(body(),
        index: 40, total: 99, description: 'No Such Instruction');
    expect(s.currentPath, isNull);
    expect(s.completedPaths, isEmpty);
    final none = resolveSpotlight(body(), index: null, total: 4);
    expect(none.currentPath, isNull);
  });
}
