import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/sequencer/sequence_editor_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/widgets/sequencer/sequence_editor_tree.dart';
import 'package:openastroara/widgets/sequencer/sequence_field_editor.dart';
import 'package:openastroara/widgets/sequencer/sequencer_toolbar.dart';

import 'toolbar_surface.dart';

/// The planner's "Narrowband · Ha / OIII" run for one target.
Map<String, dynamic> _run() => buildImagingRunBody(
  raDeg: 10.1,
  decDeg: 41.7,
  targetName: 'NGC0205',
  exposureSeconds: 310,
  frameCount: 56,
  autofocusEveryNExposures: 23,
  startGuiding: true,
  ditherEveryNExposures: 1,
  filterPlan: const [
    FilterPlanStep(filterName: 'Ha', exposureSeconds: 310, frameCount: 56),
    FilterPlanStep(filterName: 'OIII', exposureSeconds: 310, frameCount: 56),
  ],
);

NodePath _pathTo(
  Map<String, dynamic> root,
  String name, [
  NodePath at = const [],
]) {
  final kids = childrenOf(nodeAt(root, at)!);
  for (var i = 0; i < kids.length; i++) {
    final p = [...at, i];
    if (kids[i]['Name'] == name) return p;
    if (isContainer(kids[i])) {
      final hit = _pathTo(root, name, p);
      if (hit.isNotEmpty) return hit;
    }
  }
  return const [];
}

Future<ProviderContainer> _pump(
  WidgetTester tester,
  Widget child, {
  NodePath? select,
}) async {
  await wideSurface(tester);
  final container = ProviderContainer(
    overrides: [sequenceApiProvider.overrideWithValue(null)],
  );
  addTearDown(container.dispose);
  final editor = container.read(sequenceEditorProvider.notifier)
    ..load(SequenceDetail(id: 's', name: 'NGC0205', body: _run()));
  if (select != null) editor.select(select);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(home: Scaffold(body: child)),
    ),
  );
  await tester.pump();
  return container;
}

void main() {
  testWidgets('a frame loop row says × 56 and what fires, not a bare "⟳ 1"', (
    tester,
  ) async {
    await _pump(tester, const SequenceEditorTree());
    expect(
      find.text('× 56 · 5.2 min · ≈ 4.8 h'),
      findsNWidgets(2),
      reason: 'one per filter loop',
    );
    expect(find.text('AF every 23 · dither every 1'), findsNWidgets(2));
    expect(
      find.text('while above horizon'),
      findsOneWidget,
      reason: 'the target block runs once and ends when NGC0205 sets',
    );
    // On a desktop-width editor the chips show their whole text; the first
    // layout cut them to "× 34 · 5.2 mi…".
    for (final t in [
      '× 56 · 5.2 min · ≈ 4.8 h',
      'AF every 23 · dither every 1',
    ]) {
      final p = tester.renderObject<RenderParagraph>(find.text(t).first);
      expect(p.didExceedMaxLines, isFalse, reason: '"$t" is ellipsized');
    }
  });

  testWidgets('a Take Exposure inspector says how many frames it will take', (
    tester,
  ) async {
    final body = _run();
    final ha = _pathTo(body, 'Ha Imaging');
    await _pump(tester, const SequenceFieldEditor(), select: [...ha, 0]);
    expect(find.text('Take Exposure'), findsOneWidget);
    expect(
      find.text(
        'Repeats 56× in Ha Imaging (≈ 4.8 h) · AF every 23 · dither every 1',
      ),
      findsOneWidget,
    );
  });

  testWidgets(
    'Delete removes only the highlighted item, and Undo brings it back',
    (tester) async {
      final body = _run();
      final oiii = _pathTo(body, 'OIII Imaging');
      final container = await _pump(
        tester,
        const SequencerToolbar(),
        select: oiii,
      );
      final blockPath = oiii.sublist(0, oiii.length - 1);
      int blockChildren() => childrenOf(
        nodeAt(container.read(sequenceEditorProvider)!.body, blockPath)!,
      ).length;
      final before = blockChildren();

      expect(
        find.text('Delete sequence'),
        findsNothing,
        reason: 'with an item highlighted, Delete is about that item',
      );
      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle(); // the snackbar slides in

      expect(blockChildren(), before - 1);
      expect(
        _pathTo(container.read(sequenceEditorProvider)!.body, 'OIII Imaging'),
        isEmpty,
      );
      expect(
        _pathTo(container.read(sequenceEditorProvider)!.body, 'Ha Imaging'),
        isNotEmpty,
        reason: 'the rest of the run is untouched',
      );
      expect(find.text('Deleted OIII Imaging'), findsOneWidget);

      await tester.tap(find.text('Undo'));
      await tester.pumpAndSettle();
      expect(blockChildren(), before);
    },
  );

  testWidgets('with nothing highlighted Delete offers the whole sequence', (
    tester,
  ) async {
    await _pump(tester, const SequencerToolbar());
    expect(find.text('Delete sequence'), findsOneWidget);
  });
}
