import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/draft_sequence.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/draft_sequence_service.dart';
import 'package:openastroara/state/sequencer/draft_sequences_state.dart';
import 'package:openastroara/state/sequencer/sequence_editor_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/widgets/sequencer/sequencer_toolbar.dart';

import 'toolbar_surface.dart';

/// In-memory draft store so the toolbar's delete can be observed without disk.
class _MemDrafts extends DraftSequenceService {
  final store = <String, DraftSequence>{};
  final deleted = <String>[];
  @override
  Future<List<DraftSequence>> loadAll() async => store.values.toList();
  @override
  Future<void> save(DraftSequence draft) async => store[draft.id] = draft;
  @override
  Future<void> delete(String id) async {
    deleted.add(id);
    store.remove(id);
  }
}

const _id = '${draftIdPrefix}abc';

TextButton _deleteButton(WidgetTester tester) => tester.widget<TextButton>(
      find.ancestor(of: find.text('Delete'), matching: find.byType(TextButton)),
    );

void main() {
  testWidgets('an open offline draft can be deleted from the toolbar',
      (tester) async {
    await wideSurface(tester);
    final drafts = _MemDrafts()
      ..store[_id] = DraftSequence(
          id: _id, name: 'M 31 night', updatedUtc: DateTime.utc(2026), body: const {});
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWithValue(null), // no server at all
      draftSequenceServiceProvider.overrideWithValue(drafts),
    ]);
    addTearDown(container.dispose);
    await container.read(draftSequencesProvider.future);
    container.read(selectedSequenceIdProvider.notifier).select(_id);
    container.read(sequenceEditorProvider.notifier).load(
        SequenceDetail(id: _id, name: 'M 31 night', body: const {}));
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SequencerToolbar())),
    ));
    await tester.pump();

    // Before #1106 this was `: null` for any draft.
    expect(_deleteButton(tester).onPressed, isNotNull);

    await tester.tap(find.text('Delete'));
    await tester.pumpAndSettle();
    expect(find.text('Delete draft?'), findsOneWidget);
    await tester.tap(find.widgetWithText(TextButton, 'Delete').last);
    // Pump frame by frame, not pumpAndSettle: settling can run the SnackBar's
    // 4 s display timer out before the assertion looks (flaked in the full
    // suite, passed alone).
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 500));

    expect(drafts.deleted, [_id]);
    // The toolbar names a draft "<name> (offline draft)"; the SnackBar echoes it.
    expect(find.text('Deleted "M 31 night (offline draft)".'), findsOneWidget);
    expect(container.read(selectedSequenceIdProvider), isNull);
    expect(container.read(sequenceEditorProvider), isNull,
        reason: 'the Run tab must not keep editing a ghost');
    await tester.pumpAndSettle();
  });

  testWidgets('cancelling the confirm keeps the draft', (tester) async {
    await wideSurface(tester);
    final drafts = _MemDrafts()
      ..store[_id] = DraftSequence(
          id: _id, name: 'M 31 night', updatedUtc: DateTime.utc(2026), body: const {});
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWithValue(null),
      draftSequenceServiceProvider.overrideWithValue(drafts),
    ]);
    addTearDown(container.dispose);
    await container.read(draftSequencesProvider.future);
    container.read(selectedSequenceIdProvider.notifier).select(_id);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SequencerToolbar())),
    ));
    await tester.pump();
    await tester.tap(find.text('Delete'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();
    expect(drafts.deleted, isEmpty);
    expect(container.read(selectedSequenceIdProvider), _id);
  });
}
