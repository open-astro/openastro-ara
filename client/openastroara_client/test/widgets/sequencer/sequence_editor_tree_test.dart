import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
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

    test('uses a container\'s Name when not in the catalog', () {
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

  testWidgets('tapping a row selects that node by path', (tester) async {
    final container = await _pump(tester, detail: sampleDetail());
    await tester.tap(find.text('Take Exposure'));
    await tester.pump();
    expect(container.read(sequenceEditorProvider)!.selectedPath, [0]);

    await tester.tap(find.text('Switch Filter'));
    await tester.pump();
    expect(container.read(sequenceEditorProvider)!.selectedPath, [1]);
  });
}
