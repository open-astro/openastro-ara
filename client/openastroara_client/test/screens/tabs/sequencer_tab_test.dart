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
  Future<SequenceImportResult> importNina(String n, Map<String, dynamic> f,
          {bool treatWarningsAsErrors = false}) async =>
      const SequenceImportResult(createdSequenceId: 'new');
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
  Future<List<SequenceTemplate>> listTemplates() async => const [];
  @override
  Future<String> instantiateTemplate(String t, String n) async => 'new-seq';
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

/// getSequence always throws, to exercise the load-failure path.
class _ThrowingClient extends _FakeClient {
  @override
  Future<SequenceNode> getSequence(String id) async =>
      throw Exception('boom');
}

/// Throws on the first call (transient failure), succeeds afterwards — to verify
/// a failed load can be retried.
class _FlakyOnceClient extends _FakeClient {
  int calls = 0;
  @override
  Future<SequenceNode> getSequence(String id) async {
    calls++;
    if (calls == 1) throw Exception('transient');
    return SequenceNode(
        id: 'root', kind: SequenceNodeKind.root, displayName: 'Loaded $id (retry)');
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

  testWidgets('a stale load FAILURE shows no SnackBar and keeps the selection',
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

    container.read(selectedSequenceIdProvider.notifier).select('seq-1');
    await tester.pump();
    container.read(selectedSequenceIdProvider.notifier).select('seq-2');
    await tester.pump();

    // seq-2 loads fine; the superseded seq-1 then *fails*.
    client.calls['seq-2']!.complete(_root('Loaded seq-2'));
    await tester.pumpAndSettle();
    client.calls['seq-1']!.completeError(Exception('late failure'));
    await tester.pumpAndSettle();

    // The stale failure must not nag the user or drop their (valid) selection.
    expect(find.textContaining("Couldn't load the sequence"), findsNothing);
    expect(container.read(selectedSequenceIdProvider), 'seq-2');
    expect(container.read(sequenceControllerProvider).displayName, 'Loaded seq-2');
  });

  testWidgets('loads a sequence already selected when the tab mounts',
      (tester) async {
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWithValue(_FakeClient()),
    ]);
    addTearDown(container.dispose);
    // Selection exists BEFORE the tab is built — ref.listen won't fire for it, so
    // this exercises the initState pickup.
    container.read(selectedSequenceIdProvider.notifier).select('seq-7');

    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SequencerTab())),
    ));
    await tester.pumpAndSettle();

    expect(container.read(sequenceControllerProvider).displayName, 'Loaded seq-7');
  });

  testWidgets('a load failure shows a SnackBar and keeps the tree', (tester) async {
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWithValue(_ThrowingClient()),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SequencerTab())),
    ));
    container.read(selectedSequenceIdProvider.notifier).select('seq-x');
    await tester.pump(); // run the async load + catch
    await tester.pumpAndSettle();
    expect(find.textContaining("Couldn't load the sequence"), findsOneWidget);
  });

  testWidgets('no client (disconnected) shows the SnackBar too', (tester) async {
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWithValue(null),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SequencerTab())),
    ));
    container.read(selectedSequenceIdProvider.notifier).select('seq-x');
    await tester.pump();
    await tester.pumpAndSettle();
    expect(find.textContaining("Couldn't load the sequence"), findsOneWidget);
  });

  testWidgets('a failed load can be retried by re-selecting the sequence',
      (tester) async {
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWithValue(_FlakyOnceClient()),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SequencerTab())),
    ));

    // First pick fails → selection is cleared so a re-pick re-emits.
    container.read(selectedSequenceIdProvider.notifier).select('seq-r');
    await tester.pumpAndSettle();
    expect(container.read(selectedSequenceIdProvider), isNull);

    // Re-pick the same sequence → reloads, this time succeeding.
    container.read(selectedSequenceIdProvider.notifier).select('seq-r');
    await tester.pumpAndSettle();
    expect(container.read(sequenceControllerProvider).displayName,
        'Loaded seq-r (retry)');
  });
}
