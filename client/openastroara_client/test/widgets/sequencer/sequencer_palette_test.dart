import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/sequencer/sequence_editor_state.dart';
import 'package:openastroara/widgets/sequencer/sequencer_palette.dart';

SequenceDetail emptySequence() => SequenceDetail(
      id: 's',
      name: 'M42',
      body: {
        r'$type':
            'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
        'Name': 'root',
        'Items': {r'$type': itemsWrapperType, r'$values': <Map<String, dynamic>>[]},
      },
    );

Future<ProviderContainer> _pump(WidgetTester tester, {SequenceDetail? detail}) async {
  // Tall viewport so the lazy palette list builds all instruction tiles — the smoke checks below
  // assert on tiles (e.g. "Dither") that would otherwise scroll below the default 600px fold as the
  // catalog grows.
  tester.view.physicalSize = const Size(1000, 4000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);

  final container = ProviderContainer();
  addTearDown(container.dispose);
  if (detail != null) {
    container.read(sequenceEditorProvider.notifier).load(detail);
  }
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SequencerPalette())),
    ),
  );
  return container;
}

void main() {
  testWidgets('renders a tile for every catalogued instruction', (tester) async {
    await _pump(tester, detail: emptySequence());
    expect(find.text('Take Exposure'), findsOneWidget);
    expect(find.text('Switch Filter'), findsOneWidget);
    expect(find.text('Dither'), findsOneWidget);
    // category headers (uppercased)
    expect(find.text('CAMERA'), findsOneWidget);
    expect(find.text('GUIDER'), findsOneWidget);
  });

  testWidgets('tapping a tile adds that instruction to the sequence',
      (tester) async {
    final container = await _pump(tester, detail: emptySequence());
    expect(childrenOf(container.read(sequenceEditorProvider)!.body), isEmpty);

    await tester.tap(find.text('Take Exposure'));
    await tester.pump();

    final body = container.read(sequenceEditorProvider)!.body;
    expect(childrenOf(body), hasLength(1));
    expect(childrenOf(body).single[r'$type'], contains('TakeExposure'));
  });

  testWidgets('tiles are disabled (no add) when no sequence is loaded',
      (tester) async {
    final container = await _pump(tester); // nothing loaded
    await tester.tap(find.text('Take Exposure'));
    await tester.pump();
    // still nothing loaded → no crash, no state
    expect(container.read(sequenceEditorProvider), isNull);
  });

  testWidgets('S8: search filters tiles and descriptions render', (tester) async {
    await _pump(tester, detail: emptySequence());
    expect(find.text('Capture one frame with the current settings.'),
        findsOneWidget);
    await tester.enterText(find.byType(TextField), 'dither');
    await tester.pump();
    expect(find.text('Dither'), findsOneWidget);
    expect(find.text('Take Exposure'), findsNothing);
  });
}
