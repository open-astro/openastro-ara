import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/node_display.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/sequencer/sequence_editor_state.dart';
import 'package:openastroara/widgets/sequencer/sequence_editor_tree.dart';

InstructionDef _def(String type) => instructionForType(type)!;
const _takeExposure =
    'OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure, OpenAstroAra.Sequencer';
const _switchFilter =
    'OpenAstroAra.Sequencer.SequenceItem.FilterWheel.SwitchFilter, OpenAstroAra.Sequencer';

SequenceDetail sampleDetail() => SequenceDetail(
      id: 's',
      name: 'M42',
      body: {
        r'$type':
            'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
        'Name': 'My Sequence',
        'Items': {
          r'$type': itemsWrapperType,
          r'$values': [
            _def(_takeExposure).build(),
            _def(_switchFilter).build(),
          ],
        },
      },
    );

Future<ProviderContainer> _pump(WidgetTester tester, {SequenceDetail? detail}) async {
  final container = ProviderContainer();
  addTearDown(container.dispose);
  if (detail != null) {
    container.read(sequenceEditorProvider.notifier).load(detail);
  }
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SequenceEditorTree())),
    ),
  );
  return container;
}

void main() {
  group('nodeLabel', () {
    test('uses the catalog label for a known instruction', () {
      expect(nodeLabel({r'$type': _takeExposure}), 'Take Exposure');
    });

    test('a container\'s own Name wins over its catalog label', () {
      // SequentialContainer is catalogued ("Sequential Instruction Set"), but a
      // user-named container shows its Name, not the generic catalog label.
      expect(
        nodeLabel({
          r'$type':
              'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
          'Name': 'My Sequence',
          'Items': {r'$type': itemsWrapperType, r'$values': []},
        }),
        'My Sequence',
      );
    });

    test('an unnamed catalogued container falls back to its catalog label', () {
      expect(
        nodeLabel({
          r'$type':
              'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
          'Items': {r'$type': itemsWrapperType, r'$values': []},
        }),
        'Sequential Instruction Set',
      );
    });

    test('falls back to the short \$type for an unknown leaf', () {
      expect(nodeLabel({r'$type': 'Foo.Bar.SomethingNew, Foo'}), 'SomethingNew');
    });

    test('Unknown when there is no \$type', () {
      expect(nodeLabel({'x': 1}), 'Unknown');
    });

    test('Unknown for a degenerate trailing-dot or empty \$type', () {
      expect(nodeLabel({r'$type': 'A., Asm'}), 'Unknown'); // trailing dot → ''
      expect(nodeLabel({r'$type': ', Asm'}), 'Unknown'); // empty before comma
    });
  });

  group('nodeIcon', () {
    test('catalog icon for a known instruction', () {
      expect(nodeIcon({r'$type': _takeExposure}), _def(_takeExposure).icon);
    });

    test('folder for an unknown container, glyph for an unknown leaf', () {
      expect(
        nodeIcon({
          r'$type': 'X.Y.Container.Weird, X',
        }),
        Icons.account_tree_outlined,
      );
      expect(nodeIcon({r'$type': 'X.Leaf, X'}), Icons.help_outline);
    });
  });

  testWidgets('empty state when nothing is loaded', (tester) async {
    await _pump(tester);
    expect(find.text('No sequence loaded'), findsOneWidget);
  });

  testWidgets('renders a row per node with catalog labels', (tester) async {
    await _pump(tester, detail: sampleDetail());
    expect(find.text('My Sequence'), findsOneWidget); // root container Name
    expect(find.text('Take Exposure'), findsOneWidget);
    expect(find.text('Switch Filter'), findsOneWidget);
  });

  testWidgets('a deeply nested body renders without a stack overflow',
      (tester) async {
    // 120 nested containers — past _flatten's depth cap (and a real safety net,
    // though SequenceDetail also rejects bodies deeper than its own data-layer
    // limit at construction).
    Map<String, dynamic> nest(int depth) {
      var node = <String, dynamic>{
        r'$type':
            'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
        'Name': 'leaf',
        'Items': {r'$type': itemsWrapperType, r'$values': <Map<String, dynamic>>[]},
      };
      for (var i = 0; i < depth; i++) {
        node = {
          r'$type':
              'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
          'Name': 'c$i',
          'Items': {
            r'$type': itemsWrapperType,
            r'$values': [node],
          },
        };
      }
      return node;
    }

    await _pump(tester, detail: SequenceDetail(id: 'deep', body: nest(120)));
    expect(tester.takeException(), isNull);
  });

  testWidgets('selected non-root row shows a delete button that removes it',
      (tester) async {
    final c = await _pump(tester, detail: sampleDetail());
    expect(find.byIcon(Icons.delete_outline), findsNothing); // none until selected

    await tester.tap(find.text('Take Exposure'));
    await tester.pump();
    expect(find.byIcon(Icons.delete_outline), findsOneWidget);

    await tester.tap(find.byIcon(Icons.delete_outline));
    await tester.pump();
    expect(find.text('Take Exposure'), findsNothing); // removed
    expect(childrenOf(c.read(sequenceEditorProvider)!.body), hasLength(1));
    // removeNode clears selection → no delete button lingering.
    expect(find.byIcon(Icons.delete_outline), findsNothing);
  });

  testWidgets('the root row has no delete button (can\'t delete the sequence)',
      (tester) async {
    await _pump(tester, detail: sampleDetail());
    await tester.tap(find.text('My Sequence')); // root row
    await tester.pump();
    expect(find.byIcon(Icons.delete_outline), findsNothing);
    expect(find.byIcon(Icons.arrow_upward), findsNothing);
  });

  testWidgets('move-down reorders the selected row and follows it', (tester) async {
    final c = await _pump(tester, detail: sampleDetail());
    await tester.tap(find.text('Take Exposure')); // index 0
    await tester.pump();
    await tester.tap(find.byIcon(Icons.arrow_downward));
    await tester.pump();
    // Now second child; selection followed it (delete still shown on its row).
    final kids = childrenOf(c.read(sequenceEditorProvider)!.body);
    expect(kids[1][r'$type'], contains('TakeExposure'));
    expect(c.read(sequenceEditorProvider)!.selectedPath, [1]);
  });

  testWidgets('move-up is disabled on the first child', (tester) async {
    await _pump(tester, detail: sampleDetail());
    await tester.tap(find.text('Take Exposure')); // index 0 (first)
    await tester.pump();
    final up = tester.widget<IconButton>(
      find.ancestor(of: find.byIcon(Icons.arrow_upward), matching: find.byType(IconButton)),
    );
    expect(up.onPressed, isNull); // can't move the first child up
  });

  testWidgets('tapping a row selects that node by path', (tester) async {
    final container = await _pump(tester, detail: sampleDetail());
    await tester.tap(find.text('Take Exposure'));
    await tester.pump();
    expect(container.read(sequenceEditorProvider)!.selectedPath, [0]);

    await tester.tap(find.text('Switch Filter'));
    await tester.pump();
    expect(container.read(sequenceEditorProvider)!.selectedPath, [1]);
  });

  // root container 'My Sequence' with: [0] Take Exposure (leaf), [1] 'Inner'
  // container holding [1,0] 'Innermost' container holding [1,0,0] Switch Filter.
  SequenceDetail nestedDetail() {
    Map<String, dynamic> container(String name, List<Map<String, dynamic>> items) => {
          r'$type':
              'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
          'Name': name,
          'Items': {r'$type': itemsWrapperType, r'$values': items},
        };
    return SequenceDetail(
      id: 'n',
      body: container('My Sequence', [
        _def(_takeExposure).build(),
        container('Inner', [
          container('Innermost', [_def(_switchFilter).build()]),
        ]),
      ]),
    );
  }

  group('canReparentInto (drag-drop policy)', () {
    final body = nestedDetail().body;
    test('accepts a leaf dropped into a sibling container', () {
      expect(canReparentInto(body, const [0], const [1]), isTrue);
    });
    test('rejects a drop onto a leaf (not a container)', () {
      expect(canReparentInto(body, const [1], const [0]), isFalse);
    });
    test('rejects a drop onto the node itself', () {
      expect(canReparentInto(body, const [1], const [1]), isFalse);
    });
    test('rejects a container dropped into its own descendant', () {
      expect(canReparentInto(body, const [1], const [1, 0]), isFalse);
    });
    test('rejects the no-op: already the last child of the target', () {
      expect(canReparentInto(body, const [1, 0, 0], const [1, 0]), isFalse);
    });
    test('accepts a non-last child dropped back into its parent (reorder to end)', () {
      expect(canReparentInto(body, const [0], const []), isTrue);
    });
    test('rejects an empty dragged path (the root) without throwing', () {
      expect(canReparentInto(body, const [], const [1]), isFalse);
    });
  });

  testWidgets('long-press dragging a leaf onto a container moves it inside',
      (tester) async {
    final c = await _pump(tester, detail: nestedDetail());
    final from = tester.getCenter(find.text('Take Exposure'));
    final to = tester.getCenter(find.text('Inner'));
    final gesture = await tester.startGesture(from);
    await tester.pump(const Duration(milliseconds: 600)); // past long-press
    await gesture.moveTo(to);
    await tester.pump();
    await gesture.up();
    await tester.pumpAndSettle();

    final body = c.read(sequenceEditorProvider)!.body;
    expect(childrenOf(body), hasLength(1)); // root now holds just the Inner container
    final inner = childrenOf(body).single;
    expect(childrenOf(inner), hasLength(2)); // Innermost + the moved Take Exposure
    expect(childrenOf(inner).last[r'$type'], contains('TakeExposure'));
  });

  // A flat root container with three leaves [a, b, c] for the index-shift cases.
  SequenceDetail flat3() => SequenceDetail(
        id: 'f3',
        body: {
          r'$type':
              'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
          'Name': 'root',
          'Items': {
            r'$type': itemsWrapperType,
            r'$values': [
              {r'$type': 'X.A'},
              {r'$type': 'X.B'},
              {r'$type': 'X.C'},
            ],
          },
        },
      );

  group('resolveDropBefore (gap drop policy)', () {
    final nested = nestedDetail().body;
    final flat = flat3().body;

    test('reorder up: drop a later sibling before an earlier one', () {
      final r = resolveDropBefore(nested, const [1], const [0]); // Inner before TakeExposure
      expect(r, isNotNull);
      expect(r!.parent, isEmpty);
      expect(r.index, 0);
    });

    test('no-op: drop before self or before the immediately-following row', () {
      expect(resolveDropBefore(flat, const [1], const [1]), isNull); // before self
      expect(resolveDropBefore(flat, const [0], const [1]), isNull); // [0] already before [1]
    });

    test('reorder down applies the post-removal index shift', () {
      // Move A([0]) to just before C([2]) → after removing A, insert at index 1.
      final r = resolveDropBefore(flat, const [0], const [2]);
      expect(r!.index, 1);
      expect(r.parent, isEmpty);
    });

    test('cross-parent insert before a deep row keeps the pre-removal index', () {
      // TakeExposure([0]) before SwitchFilter([1,0,0]) → into Innermost at 0.
      final r = resolveDropBefore(nested, const [0], const [1, 0, 0]);
      expect(r!.parent, [1, 0]);
      expect(r.index, 0);
    });

    test('rejects the root, the gap-above-root, and dropping before a descendant', () {
      expect(resolveDropBefore(nested, const [], const [0]), isNull); // root not movable
      expect(resolveDropBefore(nested, const [0], const []), isNull); // no gap above root
      expect(resolveDropBefore(nested, const [1], const [1, 0]), isNull); // before own child
    });
  });

  group('resolveDropInto (append-gap drop policy)', () {
    final nested = nestedDetail().body;
    final flat = flat3().body;

    test('appends a non-last sibling to the end of its own parent', () {
      // A([0]) appended to root → post-removal [B, C], insert at 2.
      final r = resolveDropInto(flat, const [0], const []);
      expect(r, isNotNull);
      expect(r!.parent, isEmpty);
      expect(r.index, 2);
    });

    test('cross-parent append uses the pre-removal child count', () {
      // TakeExposure([0]) appended into Innermost([1,0], 1 child) → index 1.
      final r = resolveDropInto(nested, const [0], const [1, 0]);
      expect(r!.parent, [1, 0]);
      expect(r.index, 1);
    });

    test('rejects the already-last-child no-op and non-containers', () {
      expect(resolveDropInto(nested, const [1, 0, 0], const [1, 0]), isNull); // already last
      expect(resolveDropInto(nested, const [1], const [0]), isNull); // [0] is a leaf
    });
  });

  group('appendGapContainers (trailing-gap layout)', () {
    final nested = nestedDetail().body;

    test('the last leaf closes every enclosing container, deepest first', () {
      // Below SwitchFilter([1,0,0]) — the last row — Innermost then Inner close.
      expect(appendGapContainers(nested, const [1, 0, 0], null), [
        [1, 0],
        [1],
      ]);
    });

    test('a leaf followed by a sibling closes nothing', () {
      expect(appendGapContainers(nested, const [0], const [1]), isEmpty);
    });

    test('a container whose subtree continues to the next row closes nothing', () {
      expect(appendGapContainers(nested, const [1], const [1, 0]), isEmpty);
    });

    test('the root is never returned (append-to-root is the root row drop)', () {
      expect(appendGapContainers(nested, const [], const [0]), isEmpty);
    });

    test('an empty container contributes a gap at its own row', () {
      final body = {
        r'$type':
            'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
        'Name': 'root',
        'Items': {
          r'$type': itemsWrapperType,
          r'$values': [
            {
              r'$type':
                  'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
              'Name': 'empty',
              'Items': {r'$type': itemsWrapperType, r'$values': <Map<String, dynamic>>[]},
            },
            {r'$type': 'X.Leaf'},
          ],
        },
      };
      expect(appendGapContainers(body, const [0], const [1]), [
        [0],
      ]);
    });
  });

  testWidgets('dragging a row onto a trailing append gap moves it to the end',
      (tester) async {
    final c = await _pump(tester, detail: nestedDetail());
    // Drop TakeExposure([0]) onto the "append into Inner" gap below the last row
    // → root holds just Inner; Inner = [Innermost, Take Exposure].
    final teRow = tester.getCenter(find.text('Take Exposure'));
    final appendInner =
        tester.getCenter(find.byKey(ValueKey(gapAppendKey(const [1]))));
    final g = await tester.startGesture(teRow);
    await tester.pump(const Duration(milliseconds: 600)); // past long-press
    await g.moveTo(appendInner);
    await tester.pump();
    await g.up();
    await tester.pumpAndSettle();

    final body = c.read(sequenceEditorProvider)!.body;
    expect(childrenOf(body), hasLength(1)); // root now holds just Inner
    final inner = childrenOf(body).single;
    expect(childrenOf(inner), hasLength(2));
    expect(childrenOf(inner).last[r'$type'], contains('TakeExposure'));
  });

  testWidgets('dragging a row onto a gap reorders it', (tester) async {
    final c = await _pump(tester, detail: flat3());
    // Long-press the C row (unknown type → labelled "C") and drop on the gap
    // above A (the first child) → [C, A, B].
    final cRow = tester.getCenter(find.text('C'));
    final gapBeforeA = tester.getCenter(find.byKey(ValueKey(gapBeforeKey(const [0]))));
    final g = await tester.startGesture(cRow);
    await tester.pump(const Duration(milliseconds: 600)); // past long-press
    await g.moveTo(gapBeforeA);
    await tester.pump();
    await g.up();
    await tester.pumpAndSettle();
    expect(
      childrenOf(c.read(sequenceEditorProvider)!.body).map((n) => n[r'$type']),
      ['X.C', 'X.A', 'X.B'],
    );
  });
}
