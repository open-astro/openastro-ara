import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/draft_sequence.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/draft_sequence_service.dart';
import 'package:openastroara/state/sequencer/draft_sequences_state.dart';
import 'package:openastroara/state/sequencer/sequence_editor_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/widgets/sequencer/sequence_load_dialog.dart';

/// In-memory draft store so the dialog's per-row delete can be observed
/// without disk.
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

Future<(ProviderContainer, _MemDrafts)> _pump(WidgetTester tester) async {
  final drafts = _MemDrafts()
    ..store[_id] = DraftSequence(
        id: _id, name: 'M 31 night', updatedUtc: DateTime.utc(2026), body: const {});
  final container = ProviderContainer(overrides: [
    sequenceApiProvider.overrideWithValue(null), // offline: drafts only
    draftSequenceServiceProvider.overrideWithValue(drafts),
  ]);
  addTearDown(container.dispose);
  await container.read(draftSequencesProvider.future);
  // The doomed draft is the one open in the Run tab.
  container.read(selectedSequenceIdProvider.notifier).select(_id);
  container.read(sequenceEditorProvider.notifier).load(
      SequenceDetail(id: _id, name: 'M 31 night', body: const {}));
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(home: Scaffold(body: SequenceLoadDialog())),
  ));
  await tester.pumpAndSettle();
  return (container, drafts);
}

void main() {
  testWidgets(
      'per-row draft delete clears the open selection + editor (#1142)',
      (tester) async {
    final (container, drafts) = await _pump(tester);

    await tester.tap(find.byTooltip('Delete draft'));
    await tester.pumpAndSettle();
    expect(find.text('Delete draft?'), findsOneWidget);
    await tester.tap(find.widgetWithText(TextButton, 'Delete'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 500));

    expect(drafts.deleted, [_id]);
    expect(find.text('Deleted "M 31 night".'), findsOneWidget);
    expect(container.read(selectedSequenceIdProvider), isNull);
    expect(container.read(sequenceEditorProvider), isNull,
        reason: 'an editor left on the deleted draft would resurrect it via '
            'saveBody on the next Save');
    await tester.pumpAndSettle();
  });

  testWidgets('cancelling the confirm keeps the draft and the editor',
      (tester) async {
    final (container, drafts) = await _pump(tester);
    await tester.tap(find.byTooltip('Delete draft'));
    await tester.pumpAndSettle();
    // The Load dialog has its own Cancel; the confirm's is the topmost.
    await tester.tap(find.text('Cancel').last);
    await tester.pumpAndSettle();
    expect(drafts.deleted, isEmpty);
    expect(container.read(selectedSequenceIdProvider), _id);
    expect(container.read(sequenceEditorProvider)?.id, _id);
  });
}
