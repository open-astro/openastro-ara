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

    test('asserts against an inline \$ref handle (debug builds)', () {
      expect(
        () => childrenOf({
          'Items': {
            r'$type': itemsWrapperType,
            r'$values': [
              {r'$ref': '5'}
            ],
          },
        }),
        throwsA(isA<AssertionError>()),
      );
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

    test('preserves non-\$type wrapper metadata (e.g. \$id)', () {
      final node = <String, dynamic>{
        'Items': {r'$type': itemsWrapperType, r'$id': '7', r'$values': []},
      };
      final out = withChildren(node, [
        {r'$type': 'X.Wait'}
      ]);
      expect((out['Items'] as Map)[r'$id'], '7'); // reference handle survives
      expect((out['Items'] as Map)[r'$type'], itemsWrapperType);
    });
  });

  group('isContainer', () {
    test('true for a node with an Items collection', () {
      expect(isContainer(sampleBody()), isTrue);
      expect(isContainer(nodeAt(sampleBody(), [1])!), isTrue); // nested container
    });

    test('true for a .Container. \$type even without Items', () {
      expect(
          isContainer({
            r'$type':
                'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer'
          }),
          isTrue);
    });

    test('false for a leaf instruction', () {
      expect(isContainer(nodeAt(sampleBody(), [0])!), isFalse); // SwitchFilter leaf
      expect(isContainer({r'$type': 'X.TakeExposure', 'ExposureTime': 1.0}), isFalse);
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

    test('insertChild clamps an out-of-range index instead of throwing', () {
      final big = insertChild(sampleBody(), [], 99, {r'$type': 'X.End'});
      expect(childrenOf(big).last[r'$type'], 'X.End'); // clamped to the end
      final neg = insertChild(sampleBody(), [], -5, {r'$type': 'X.Start'});
      expect(childrenOf(neg).first[r'$type'], 'X.Start'); // clamped to the front
    });

    test('editing a plain-array Items node promotes it to ObservableCollection',
        () {
      // childrenOf tolerates a plain-array Items; once edited, withChildren
      // emits the canonical wrapper shape the daemon's templates use.
      final body = {
        'Items': [
          {r'$type': 'X.A', 'v': 1},
        ],
      };
      final out = setField(body, [0], 'v', 2);
      expect(nodeAt(out, [0])!['v'], 2); // edit landed
      final items = out['Items'];
      expect(items, isA<Map>()); // promoted from List → wrapper Map
      expect((items as Map)[r'$type'], itemsWrapperType);
      expect(items[r'$values'], isA<List>());
    });

    test('removeAt drops the addressed node', () {
      final out = removeAt(sampleBody(), [0]);
      final kids = childrenOf(out);
      expect(kids, hasLength(1));
      expect(kids.first[r'$type'], contains('SequentialContainer'));
    });

    test('removeAt drops a deeply nested node', () {
      // sampleBody's [1,0] is the TakeExposure inside the nested container.
      final out = removeAt(sampleBody(), [1, 0]);
      expect(childrenOf(nodeAt(out, [1])!), isEmpty); // leaf gone
      expect(childrenOf(out), hasLength(2)); // siblings intact
      expect(nodeAt(sampleBody(), [1, 0])!['ExposureTime'], 60.0); // source untouched
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

    test('reorderChild moves a sibling (Flutter onReorder convention)', () {
      // Drag child 0 to the end of a 2-item list: Flutter delivers newIndex=2.
      final out = reorderChild(sampleBody(), [], 0, 2);
      expect(childrenOf(out).map((n) => n[r'$type']),
          ['X.SequentialContainer', 'X.SwitchFilter']);
    });

    test('reorderChild with oldIndex == newIndex is a no-op', () {
      final out = reorderChild(sampleBody(), [], 1, 1);
      expect(childrenOf(out).map((n) => n[r'$type']),
          ['X.SwitchFilter', 'X.SequentialContainer']);
    });

    test('reorderChild absorbs Flutter\'s pre-removal newIndex', () {
      // [A, B, C] → drag A to sit between B and C. Flutter onReorder delivers
      // (0, 2) pre-removal; reorderChild normalises it internally → [B, A, C].
      final body = {
        'Items': {
          r'$type': itemsWrapperType,
          r'$values': [
            {r'$type': 'A'},
            {r'$type': 'B'},
            {r'$type': 'C'},
          ],
        },
      };
      final out = reorderChild(body, [], 0, 2);
      expect(childrenOf(out).map((n) => n[r'$type']), ['B', 'A', 'C']);
      // Dragging backward (no shift): C (index 2) → front, newIndex=0.
      final back = reorderChild(body, [], 2, 0);
      expect(childrenOf(back).map((n) => n[r'$type']), ['C', 'A', 'B']);
    });

    test('_rebuild handles deep nesting after the sublist→index refactor', () {
      // Build a 4-deep chain and edit the leaf to exercise multi-level recursion.
      Map<String, dynamic> wrap(Map<String, dynamic> child) => {
            'Items': {
              r'$type': itemsWrapperType,
              r'$values': [child],
            },
          };
      final deep = wrap(wrap(wrap({r'$type': 'Leaf', 'v': 1})));
      final out = setField(deep, [0, 0, 0], 'v', 2);
      expect(nodeAt(out, [0, 0, 0])!['v'], 2);
      expect(nodeAt(deep, [0, 0, 0])!['v'], 1); // source untouched
    });

    test('a bad path throws RangeError', () {
      expect(() => setField(sampleBody(), [9], 'x', 1), throwsRangeError);
    });
  });

  group('conditions (conditionsOf / withConditions / add / remove / setField)', () {
    Map<String, dynamic> loop(int iterations) => {
          r'$type': 'OpenAstroAra.Sequencer.Conditions.LoopCondition, OpenAstroAra.Sequencer',
          'Iterations': iterations,
          'Parent': null,
        };

    test('conditionsOf reads the wrapped \$values, empty when absent', () {
      expect(conditionsOf(sampleBody()), isEmpty); // no Conditions on the sample
      final withOne = <String, dynamic>{
        'Conditions': {
          r'$type': conditionsWrapperType,
          r'$values': [loop(5)],
        },
      };
      expect(conditionsOf(withOne), hasLength(1));
      expect(conditionsOf(withOne).single['Iterations'], 5);
    });

    test('addCondition appends in the wrapper shape without mutating the source', () {
      final root = sampleBody();
      final out = addCondition(root, const [], loop(3));
      expect(conditionsOf(out), hasLength(1));
      final wrapper = out['Conditions'] as Map<String, dynamic>;
      expect(wrapper[r'$type'], conditionsWrapperType);
      expect(conditionsOf(root), isEmpty); // source untouched
      // Items are untouched by a Conditions edit.
      expect(childrenOf(out), hasLength(childrenOf(root).length));
    });

    test('addCondition targets a nested container by path', () {
      final root = sampleBody();
      final out = addCondition(root, const [1], loop(7)); // the nested container
      expect(conditionsOf(nodeAt(out, [1])!).single['Iterations'], 7);
      expect(conditionsOf(nodeAt(out, [])!), isEmpty); // root unaffected
    });

    test('removeConditionAt drops one and bounds-checks', () {
      var root = addCondition(sampleBody(), const [], loop(1));
      root = addCondition(root, const [], loop(2));
      final out = removeConditionAt(root, const [], 0);
      expect(conditionsOf(out).single['Iterations'], 2);
      expect(() => removeConditionAt(out, const [], 5), throwsRangeError);
    });

    test('setConditionField edits one condition in place', () {
      final root = addCondition(sampleBody(), const [], loop(2));
      final out = setConditionField(root, const [], 0, 'Iterations', 20);
      expect(conditionsOf(out).single['Iterations'], 20);
      expect(conditionsOf(root).single['Iterations'], 2); // source untouched
      expect(() => setConditionField(out, const [], 9, 'Iterations', 1), throwsRangeError);
    });

    test('withConditions preserves an existing wrapper \$type / \$id', () {
      final node = <String, dynamic>{
        'Conditions': {r'$type': 'Custom.Wrapper', r'$id': '5', r'$values': []},
      };
      final out = withConditions(node, [loop(1)]);
      final wrapper = out['Conditions'] as Map<String, dynamic>;
      expect(wrapper[r'$type'], 'Custom.Wrapper'); // kept, not overwritten
      expect(wrapper[r'$id'], '5');
      expect((wrapper[r'$values'] as List), hasLength(1));
    });
  });

  group('triggers (triggersOf / withTriggers / add / remove / setField)', () {
    Map<String, dynamic> flip() => {
          r'$type':
              'OpenAstroAra.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger, OpenAstroAra.Sequencer',
          'Parent': null,
        };

    test('triggersOf reads the wrapped \$values, empty when absent', () {
      expect(triggersOf(sampleBody()), isEmpty);
      final withOne = <String, dynamic>{
        'Triggers': {
          r'$type': triggersWrapperType,
          r'$values': [flip()],
        },
      };
      expect(triggersOf(withOne), hasLength(1));
      expect(triggersOf(withOne).single[r'$type'], contains('MeridianFlipTrigger'));
    });

    test('addTrigger appends in the wrapper shape without mutating the source', () {
      final root = sampleBody();
      final out = addTrigger(root, const [], flip());
      expect(triggersOf(out), hasLength(1));
      expect((out['Triggers'] as Map<String, dynamic>)[r'$type'], triggersWrapperType);
      expect(triggersOf(root), isEmpty); // source untouched
      expect(childrenOf(out), hasLength(childrenOf(root).length)); // Items untouched
    });

    test('addTrigger targets a nested container by path', () {
      final out = addTrigger(sampleBody(), const [1], flip());
      expect(triggersOf(nodeAt(out, [1])!), hasLength(1));
      expect(triggersOf(nodeAt(out, [])!), isEmpty); // root unaffected
    });

    test('removeTriggerAt drops one and bounds-checks', () {
      var root = addTrigger(sampleBody(), const [], flip());
      root = addTrigger(root, const [], flip());
      final out = removeTriggerAt(root, const [], 0);
      expect(triggersOf(out), hasLength(1));
      expect(() => removeTriggerAt(out, const [], 5), throwsRangeError);
    });

    test('setTriggerField edits one trigger in place', () {
      final root = addTrigger(sampleBody(), const [], flip());
      final out = setTriggerField(root, const [], 0, 'Parent', 'x');
      expect(triggersOf(out).single['Parent'], 'x');
      expect(triggersOf(root).single['Parent'], isNull); // source untouched
      expect(() => setTriggerField(out, const [], 9, 'Parent', 1), throwsRangeError);
    });

    test('withTriggers preserves an existing wrapper \$type / \$id', () {
      final node = <String, dynamic>{
        'Triggers': {r'$type': 'Custom.Wrapper', r'$id': '7', r'$values': []},
      };
      final out = withTriggers(node, [flip()]);
      final wrapper = out['Triggers'] as Map<String, dynamic>;
      expect(wrapper[r'$type'], 'Custom.Wrapper');
      expect(wrapper[r'$id'], '7');
      expect((wrapper[r'$values'] as List), hasLength(1));
    });
  });
}
