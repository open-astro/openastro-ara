import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';

// A template-shaped body: a root container wrapping two children in the
// ObservableCollection `$values` wrapper the daemon uses.
Map<String, dynamic> sampleBody() => {
      'schemaVersion': 'openastroara-sequence-v1',
      r'$type': 'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
      'Name': 'root',
      'Items': {
        r'$type': itemsWrapperType,
        r'$values': [
          {r'$type': 'X.SwitchFilter', 'Filter': 'L'},
          {
            r'$type': 'X.SequentialContainer',
            'Items': {
              r'$type': itemsWrapperType,
              r'$values': [
                {r'$type': 'X.TakeExposure', 'ExposureTime': 60.0},
              ],
            },
          },
        ],
      },
    };

void main() {
  group('childrenOf', () {
    test(r'unwraps the ObservableCollection $values', () {
      final kids = childrenOf(sampleBody());
      expect(kids, hasLength(2));
      expect(kids[0]['Filter'], 'L');
    });

    test('a leaf (no Items) → empty', () {
      expect(childrenOf({r'$type': 'X.TakeExposure'}), isEmpty);
    });

    test('tolerates a plain-array Items', () {
      final kids = childrenOf({
        'Items': [
          {r'$type': 'X.A'}
        ]
      });
      expect(kids, hasLength(1));
    });
  });

  group('withChildren', () {
    test('preserves an imported body\'s existing wrapper \$type', () {
      const ninaWrapper =
          'System.Collections.ObjectModel.ObservableCollection`1[[NINA.Sequencer.SequenceItem.ISequenceItem, NINA.Sequencer]], System.ObjectModel';
      final node = <String, dynamic>{
        r'$type': 'NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer',
        'Items': {r'$type': ninaWrapper, r'$values': []},
      };
      final out = withChildren(node, [
        {r'$type': 'X.Wait'}
      ]);
      expect((out['Items'] as Map)[r'$type'], ninaWrapper);
      expect(childrenOf(out).single[r'$type'], 'X.Wait');
    });

    test('falls back to itemsWrapperType when the node has no wrapper', () {
      final out = withChildren({r'$type': 'X.SequentialContainer'}, [
        {r'$type': 'X.Wait'}
      ]);
      expect((out['Items'] as Map)[r'$type'], itemsWrapperType);
    });
  });

  group('nodeAt', () {
    test('addresses by child-index path', () {
      final b = sampleBody();
      expect(nodeAt(b, [])![r'$type'], contains('SequentialContainer')); // root
      expect(nodeAt(b, [0])!['Filter'], 'L');
      expect(nodeAt(b, [1, 0])!['ExposureTime'], 60.0); // nested
    });

    test('out-of-range → null', () {
      expect(nodeAt(sampleBody(), [5]), isNull);
      expect(nodeAt(sampleBody(), [0, 0]), isNull); // leaf has no children
    });
  });

  group('setField', () {
    test('edits a nested field without touching the source', () {
      final b = sampleBody();
      final out = setField(b, [1, 0], 'ExposureTime', 120.0);
      expect(nodeAt(out, [1, 0])!['ExposureTime'], 120.0);
      // source unchanged (it's deeply unmodifiable in production; copy here).
      expect(nodeAt(b, [1, 0])!['ExposureTime'], 60.0);
      // untouched sibling preserved.
      expect(nodeAt(out, [0])!['Filter'], 'L');
      // wrapper shape preserved.
      expect((out['Items'] as Map)[r'$type'], itemsWrapperType);
    });
  });

  group('insertChild / removeAt / reorderChild', () {
    test('insertChild adds at the index (clamped)', () {
      final out = insertChild(sampleBody(), [], 1, {r'$type': 'X.Dither'});
      final kids = childrenOf(out);
      expect(kids.map((n) => n[r'$type']),
          ['X.SwitchFilter', 'X.Dither', 'X.SequentialContainer']);
    });

    test('insertChild into a nested container', () {
      final out = insertChild(sampleBody(), [1], 0, {r'$type': 'X.Wait'});
      expect(childrenOf(nodeAt(out, [1])!).first[r'$type'], 'X.Wait');
    });

    test('removeAt drops the addressed node', () {
      final out = removeAt(sampleBody(), [0]);
      final kids = childrenOf(out);
      expect(kids, hasLength(1));
      expect(kids.first[r'$type'], contains('SequentialContainer'));
    });

    test('removeAt rejects the root', () {
      expect(() => removeAt(sampleBody(), []), throwsArgumentError);
    });

    test('removeAt with an out-of-range terminal index throws RangeError', () {
      expect(() => removeAt(sampleBody(), [9]), throwsRangeError);
    });

    test('reorderChild with an out-of-range oldIndex throws RangeError', () {
      expect(() => reorderChild(sampleBody(), [], 9, 0), throwsRangeError);
    });

    test('reorderChild moves a sibling', () {
      final out = reorderChild(sampleBody(), [], 0, 1);
      expect(childrenOf(out).map((n) => n[r'$type']),
          ['X.SequentialContainer', 'X.SwitchFilter']);
    });

    test('a bad path throws RangeError', () {
      expect(() => setField(sampleBody(), [9], 'x', 1), throwsRangeError);
    });
  });
}
