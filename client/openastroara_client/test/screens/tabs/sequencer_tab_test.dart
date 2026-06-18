import 'dart:async';

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

/// getSequence returns a future the test completes by hand (keyed by id), so a
/// concurrent-load race can be ordered deterministically.
class _ControllableClient extends _FakeClient {
  final Map<String, Completer<SequenceNode>> calls = {};
  @override
  Future<SequenceNode> getSequence(String id) {
    final c = Completer<SequenceNode>();
    calls[id] = c;
    return c.future;
  }
}

SequenceNode _root(String name) =>
    SequenceNode(id: 'root', kind: SequenceNodeKind.root, displayName: name);

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

  testWidgets('a stale load response does not clobber a newer selection',
      (tester) async {
    final client = _ControllableClient();
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWithValue(client),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SequencerTab())),
    ));

    // Pick seq-1 (slow), then seq-2 (fast) — both loads now in flight.
    container.read(selectedSequenceIdProvider.notifier).select('seq-1');
    await tester.pump();
    container.read(selectedSequenceIdProvider.notifier).select('seq-2');
    await tester.pump();

    // The newer (seq-2) response resolves first, then the stale seq-1 one.
    client.calls['seq-2']!.complete(_root('Loaded seq-2'));
    client.calls['seq-1']!.complete(_root('Loaded seq-1'));
    await tester.pumpAndSettle();

    // seq-1 is stale (no longer selected) and must not overwrite seq-2.
    expect(container.read(sequenceControllerProvider).displayName, 'Loaded seq-2');
  });
}
