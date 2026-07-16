import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/screens/tabs/sequencer_tab.dart';
import 'package:openastroara/models/sequence/sequence_share_export.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_editor_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';

// A body whose single child is a Take Exposure, so the tree renders a row the
// test can see — and the root Name carries the id to prove the right load.
Map<String, dynamic> _bodyFor(String id) => {
      r'$type':
          'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
      'Name': 'Seq $id',
      'Items': {
        r'$type': itemsWrapperType,
        r'$values': [
          (instructionForType(
                      'OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure, OpenAstroAra.Sequencer') ??
                  (throw StateError('TakeExposure missing from the catalog')))
              .build(),
        ],
      },
    };

class _FakeClient implements SequenceClient {
  @override
  Future<String> decideAutoFlats(String id,
          {required String choice, required bool remember}) =>
      throw UnimplementedError();
  @override
  Future<bool> deleteSequence(String id) => throw UnimplementedError();
  @override
  Future<SequenceValidationResult> validate(Map<String, dynamic> body) async =>
      const SequenceValidationResult(valid: true);
  @override
  Future<SequenceImportResult> importNina(String n, Map<String, dynamic> f,
          {bool treatWarningsAsErrors = false}) async =>
      const SequenceImportResult(createdSequenceId: 'new');
  @override
  Future<SequenceDetail> getSequenceDetail(String id) async =>
      SequenceDetail(id: id, name: id, body: _bodyFor(id));
  @override
  Future<SequenceDetail> updateSequence(String id,
          {String? name, String? description, Map<String, dynamic>? body}) async =>
      SequenceDetail(id: id, name: name ?? id, description: description, body: body ?? const {});
  @override
  Future<SequenceNode> getSequence(String id) async =>
      SequenceNode(id: 'root', kind: SequenceNodeKind.root, displayName: 'Loaded $id');
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
  Future<String> skipCurrent(String id) async => 'op';
  @override
  Future<String> abort(String id) async => 'op';
  @override
  Future<String> stop(String id) async => 'op';
  @override
  Future<SequenceShareExport> exportShare(String id) async => throw UnimplementedError();
  @override
  Future<List<SequenceTemplate>> listTemplates() async => const [];
  @override
  Future<String> instantiateTemplate(String t, String n) async => 'new-seq';
  @override
  Future<String> create(String name, Map<String, dynamic> body,
          {String? description, String? idempotencyKey}) async =>
      'new-seq';
  @override
  void close() {}
}

/// getSequenceDetail returns a future the test completes by hand (keyed by id),
/// so a concurrent-load race can be ordered deterministically.
class _ControllableClient extends _FakeClient {
  final Map<String, Completer<SequenceDetail>> calls = {};
  @override
  Future<SequenceDetail> getSequenceDetail(String id) {
    final c = Completer<SequenceDetail>();
    calls[id] = c;
    return c.future;
  }
}

class _ThrowingClient extends _FakeClient {
  @override
  Future<SequenceDetail> getSequenceDetail(String id) async =>
      throw Exception('boom');
}

/// Throws on the first call (transient failure), succeeds afterwards.
class _FlakyOnceClient extends _FakeClient {
  int calls = 0;
  @override
  Future<SequenceDetail> getSequenceDetail(String id) async {
    calls++;
    if (calls == 1) throw Exception('transient');
    return SequenceDetail(id: id, name: id, body: _bodyFor(id));
  }
}

String? _loadedId(ProviderContainer c) => c.read(sequenceEditorProvider)?.id;

void main() {
  testWidgets('selecting a sequence loads its body into the editor', (tester) async {
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWithValue(_FakeClient()),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SequencerTab())),
    ));

    // Nothing loaded before selection.
    expect(_loadedId(container), isNull);
    expect(find.text('No sequence loaded'), findsOneWidget);

    container.read(selectedSequenceIdProvider.notifier).select('seq-42');
    await tester.pumpAndSettle();

    // The tab fetched the body, loaded the editor, and the tree renders it.
    expect(_loadedId(container), 'seq-42');
    expect(find.text('Seq seq-42'), findsOneWidget); // root row (tree only)
    // 'Take Exposure' now appears twice: the palette tile + the loaded tree row.
    expect(find.text('Take Exposure'), findsNWidgets(2));
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

    container.read(selectedSequenceIdProvider.notifier).select('seq-1');
    await tester.pump();
    container.read(selectedSequenceIdProvider.notifier).select('seq-2');
    await tester.pump();

    // Newer (seq-2) resolves first, then the stale seq-1.
    client.calls['seq-2']!.complete(SequenceDetail(id: 'seq-2', body: _bodyFor('seq-2')));
    client.calls['seq-1']!.complete(SequenceDetail(id: 'seq-1', body: _bodyFor('seq-1')));
    await tester.pumpAndSettle();

    expect(_loadedId(container), 'seq-2'); // seq-1 stale, must not overwrite
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

    client.calls['seq-2']!.complete(SequenceDetail(id: 'seq-2', body: _bodyFor('seq-2')));
    await tester.pumpAndSettle();
    client.calls['seq-1']!.completeError(Exception('late failure'));
    await tester.pumpAndSettle();

    expect(find.textContaining("Couldn't load the sequence"), findsNothing);
    expect(container.read(selectedSequenceIdProvider), 'seq-2');
    expect(_loadedId(container), 'seq-2');
  });

  testWidgets('loads a sequence already selected when the tab mounts',
      (tester) async {
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWithValue(_FakeClient()),
    ]);
    addTearDown(container.dispose);
    container.read(selectedSequenceIdProvider.notifier).select('seq-7');

    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SequencerTab())),
    ));
    await tester.pumpAndSettle();

    expect(_loadedId(container), 'seq-7');
  });

  testWidgets('a load failure shows a SnackBar', (tester) async {
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWithValue(_ThrowingClient()),
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

    container.read(selectedSequenceIdProvider.notifier).select('seq-r');
    await tester.pumpAndSettle();
    expect(container.read(selectedSequenceIdProvider), isNull); // cleared on fail

    container.read(selectedSequenceIdProvider.notifier).select('seq-r');
    await tester.pumpAndSettle();
    expect(_loadedId(container), 'seq-r'); // retry succeeded
  });
}
