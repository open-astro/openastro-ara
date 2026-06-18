import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/screens/tabs/sequencer_tab.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/sequencer/sequence_state.dart';

/// getSequence returns a root tagged with the requested id so the test can prove
/// the tab loaded the right body into the controller.
class _FakeClient implements SequenceClient {
  @override
  Future<SequenceNode> getSequence(String id) async => SequenceNode(
        id: 'root',
        kind: SequenceNodeKind.root,
        displayName: 'Loaded $id',
      );
  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => null;
  @override
  Future<SequencePage> list({int limit = 50}) async => const SequencePage(items: []);
  @override
  Future<String> start(String id) async => 'op';
  @override
  Future<String> pause(String id) async => 'op';
  @override
  Future<String> resume(String id) async => 'op';
  @override
  Future<String> abort(String id) async => 'op';
  @override
  Future<String> stop(String id) async => 'op';
  @override
  void close() {}
}

void main() {
  testWidgets('selecting a sequence loads its body into the editor tree',
      (tester) async {
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWithValue(_FakeClient()),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SequencerTab())),
    ));

    // Before selection, the tree shows the demo (an "Untitled sequence" root).
    expect(container.read(sequenceControllerProvider).displayName,
        isNot('Loaded seq-42'));

    container.read(selectedSequenceIdProvider.notifier).select('seq-42');
    await tester.pumpAndSettle();

    // The tab's listener fetched + parsed the body and loaded it.
    expect(container.read(sequenceControllerProvider).displayName, 'Loaded seq-42');
  });
}
