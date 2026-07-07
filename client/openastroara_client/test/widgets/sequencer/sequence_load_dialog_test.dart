import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/sequence/sequence_share_export.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_editor_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/widgets/sequencer/sequence_load_dialog.dart';
import 'package:openastroara/widgets/sequencer/sequencer_toolbar.dart';

/// Pins sequenceListProvider to a chosen async result.
class _FakeListNotifier extends SequenceListNotifier {
  _FakeListNotifier(this._build);
  final Future<List<SequenceListItem>?> Function() _build;
  @override
  Future<List<SequenceListItem>?> build() => _build();
}

/// Minimal SequenceClient so sequenceApiProvider can be "connected" in toolbar
/// tests without a live server.
class _FakeClient implements SequenceClient {
  @override
  Future<String> decideAutoFlats(String id,
          {required String choice, required bool remember}) =>
      throw UnimplementedError();
  final deleted = <String>[];
  final aborted = <String>[];
  bool throwOnDelete = false;

  /// What getRunState reports; the stop-and-delete flow polls this, and
  /// abort() flips it to stopped (modelling the daemon ending the run).
  SequenceRunStateInfo? runState;

  @override
  Future<bool> deleteSequence(String id) async {
    if (throwOnDelete) throw Exception('boom');
    deleted.add(id);
    return true;
  }

  @override
  Future<SequenceValidationResult> validate(Map<String, dynamic> body) async =>
      const SequenceValidationResult(valid: true);
  @override
  Future<SequencePage> list({int limit = 50}) async => const SequencePage(items: []);
  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => runState;
  @override
  Future<SequenceImportResult> importNina(String n, Map<String, dynamic> f, {bool treatWarningsAsErrors = false}) async => const SequenceImportResult(createdSequenceId: 'new');
  @override
  Future<SequenceDetail> getSequenceDetail(String id) async =>
      SequenceDetail(id: id, name: id, body: const {});
  @override
  Future<SequenceDetail> updateSequence(String id,
          {String? name, String? description, Map<String, dynamic>? body}) async =>
      SequenceDetail(id: id, name: name ?? id, description: description, body: body ?? const {});
  @override
  Future<SequenceNode> getSequence(String id) async => SequenceNode(
      id: 'root', kind: SequenceNodeKind.root, displayName: 'fake');
  @override
  Future<String> start(String id) async => 'op';
  @override
  Future<String> pause(String id) async => 'op';
  @override
  Future<String> resume(String id) async => 'op';
  @override
  Future<String> skipCurrent(String id) async => 'op';
  @override
  Future<String> abort(String id) async {
    aborted.add(id);
    runState = const SequenceRunStateInfo(state: SequenceRunState.stopped);
    return 'op';
  }
  @override
  Future<String> stop(String id) async => 'op';
  @override
  Future<List<SequenceTemplate>> listTemplates() async => const [];
  @override
  Future<String> instantiateTemplate(String t, String n) async => 'new-seq';
  @override
  Future<String> create(String name, Map<String, dynamic> body,
          {String? description}) async =>
      'new-seq';
  @override
  Future<SequenceShareExport> exportShare(String id) async =>
      const SequenceShareExport(sequenceName: 'fake', manifest: {'schemaVersion': 'v1'});
  @override
  void close() {}
}

SequenceListItem _item(String id, String name, {SequenceRunState? runState}) =>
    SequenceListItem(
        id: id,
        name: name,
        instructionCount: 3,
        targetCount: 1,
        currentRunState: runState);

void main() {
  Future<ProviderContainer> pumpDialog(
    WidgetTester tester, {
    required Future<List<SequenceListItem>?> Function() build,
  }) async {
    final container = ProviderContainer(overrides: [
      sequenceListProvider.overrideWith(() => _FakeListNotifier(build)),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SequenceLoadDialog())),
    ));
    return container;
  }

  group('SequenceLoadDialog', () {
    testWidgets('spinner while loading', (tester) async {
      await pumpDialog(tester, build: () => Completer<List<SequenceListItem>?>().future);
      await tester.pump();
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('error message on failure', (tester) async {
      await pumpDialog(tester, build: () async => throw Exception('x'));
      await tester.pumpAndSettle();
      expect(find.textContaining("Couldn't load sequences"), findsOneWidget);
    });

    testWidgets('prompts to connect when no server', (tester) async {
      await pumpDialog(tester, build: () async => null);
      await tester.pumpAndSettle();
      expect(find.textContaining('Connect to a daemon'), findsOneWidget);
    });

    testWidgets('empty message when the server has no sequences', (tester) async {
      await pumpDialog(tester, build: () async => const <SequenceListItem>[]);
      await tester.pumpAndSettle();
      expect(find.textContaining('No saved sequences'), findsOneWidget);
    });

    testWidgets('each row has an Export action; export with no server hints + keeps the dialog',
        (tester) async {
      // List one sequence but leave sequenceApiProvider null (no connected
      // server), so the export tap exercises the no-server guard deterministically.
      final container = ProviderContainer(overrides: [
        sequenceListProvider.overrideWith(
            () => _FakeListNotifier(() async => [_item('s1', 'M31 LRGB')])),
        sequenceApiProvider.overrideWithValue(null),
      ]);
      addTearDown(container.dispose);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: Scaffold(body: SequenceLoadDialog())),
      ));
      await tester.pumpAndSettle();

      // The per-row Export action is present.
      expect(find.byIcon(Icons.save_alt), findsOneWidget);
      await tester.tap(find.byIcon(Icons.save_alt));
      await tester.pump(); // start _run → show the SnackBar
      expect(find.textContaining('Connect to a daemon to export'), findsOneWidget);
      // Export never pops the picker — the row is still there.
      expect(find.text('M31 LRGB'), findsOneWidget);
    });

    testWidgets('lists sequences; tapping one selects it and dismisses',
        (tester) async {
      // Launch via show() (not mounted directly) so the onTap pop has a route to
      // dismiss — this verifies the dialog actually closes on selection.
      final container = ProviderContainer(overrides: [
        sequenceListProvider.overrideWith(() => _FakeListNotifier(
            () async => [_item('s1', 'M31 LRGB'), _item('s2', 'Orion')])),
      ]);
      addTearDown(container.dispose);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: MaterialApp(
          home: Scaffold(
            body: Builder(
              builder: (context) => ElevatedButton(
                onPressed: () => SequenceLoadDialog.show(context),
                child: const Text('open'),
              ),
            ),
          ),
        ),
      ));
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();
      expect(find.text('M31 LRGB'), findsOneWidget);
      expect(find.text('Orion'), findsOneWidget);

      await tester.tap(find.text('Orion'));
      await tester.pumpAndSettle();
      expect(container.read(selectedSequenceIdProvider), 's2');
      // Dialog dismissed — its content is gone.
      expect(find.text('Load sequence'), findsNothing);
    });
  });

  group('SequenceLoadDialog delete', () {
    Future<(ProviderContainer, _FakeClient)> pumpWithRow(
      WidgetTester tester, {
      SequenceRunState? runState,
    }) async {
      final client = _FakeClient();
      final container = ProviderContainer(overrides: [
        sequenceListProvider.overrideWith(() => _FakeListNotifier(
            () async => [_item('s1', 'NGC7092', runState: runState)])),
        sequenceApiProvider.overrideWithValue(client),
      ]);
      addTearDown(container.dispose);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: Scaffold(body: SequenceLoadDialog())),
      ));
      await tester.pumpAndSettle();
      return (container, client);
    }

    testWidgets(
        'delete confirms, removes on the daemon, and clears the open selection',
        (tester) async {
      final (container, client) = await pumpWithRow(tester);
      // The doomed sequence is the one open in the Run tab.
      container.read(selectedSequenceIdProvider.notifier).select('s1');
      container
          .read(sequenceEditorProvider.notifier)
          .load(SequenceDetail(id: 's1', name: 'NGC7092'));

      await tester.tap(find.byIcon(Icons.delete_outline));
      await tester.pumpAndSettle();
      expect(find.text('Delete sequence?'), findsOneWidget);
      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle();

      expect(client.deleted, ['s1']);
      expect(find.textContaining('Deleted "NGC7092"'), findsOneWidget);
      // The Run tab must not keep editing a record that no longer exists.
      expect(container.read(selectedSequenceIdProvider), isNull);
      expect(container.read(sequenceEditorProvider), isNull);
    });

    testWidgets('cancelling the confirm deletes nothing', (tester) async {
      final (_, client) = await pumpWithRow(tester);
      await tester.tap(find.byIcon(Icons.delete_outline));
      await tester.pumpAndSettle();
      // Two Cancels are on screen (the Load dialog's own + the confirm's);
      // the confirm dialog's is the topmost route's — the last in the tree.
      await tester.tap(find.widgetWithText(TextButton, 'Cancel').last);
      await tester.pumpAndSettle();
      expect(client.deleted, isEmpty);
      expect(find.text('NGC7092'), findsOneWidget);
    });

    testWidgets('a running sequence offers Stop & Delete: aborts, then deletes',
        (tester) async {
      final (_, client) =
          await pumpWithRow(tester, runState: SequenceRunState.running);
      client.runState =
          const SequenceRunStateInfo(state: SequenceRunState.running);

      await tester.tap(find.byIcon(Icons.delete_outline));
      await tester.pumpAndSettle();
      expect(find.text('Stop the run and delete?'), findsOneWidget);
      expect(find.textContaining('aborts the run'), findsOneWidget);
      await tester.tap(find.text('Stop & Delete'));
      await tester.pumpAndSettle();

      // Abort first, delete only once the run reported over.
      expect(client.aborted, ['s1']);
      expect(client.deleted, ['s1']);
      expect(find.textContaining('Deleted "NGC7092"'), findsOneWidget);
    });

    testWidgets('an idle sequence gets the plain confirm even when the list '
        'row is stale-running', (tester) async {
      // The row was fetched with a running badge, but the run has since ended
      // — the live probe must downgrade the confirm to a plain delete.
      final (_, client) =
          await pumpWithRow(tester, runState: SequenceRunState.running);
      client.runState =
          const SequenceRunStateInfo(state: SequenceRunState.completed);

      await tester.tap(find.byIcon(Icons.delete_outline));
      await tester.pumpAndSettle();
      expect(find.text('Delete sequence?'), findsOneWidget);
      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle();
      expect(client.aborted, isEmpty);
      expect(client.deleted, ['s1']);
    });

    testWidgets('a run that starts while the confirm is open blocks the delete',
        (tester) async {
      // TOCTOU (#689 round-2): the confirm can sit open indefinitely and the
      // daemon deletes unconditionally — a run that began meanwhile must NOT be
      // silently aborted under a PLAIN delete confirm, nor deleted under a live
      // executor. The post-confirm re-probe bails instead.
      final (_, client) = await pumpWithRow(tester); // idle at probe time
      await tester.tap(find.byIcon(Icons.delete_outline));
      await tester.pumpAndSettle();
      expect(find.text('Delete sequence?'), findsOneWidget); // plain wording
      client.runState =
          const SequenceRunStateInfo(state: SequenceRunState.running);

      await tester.tap(find.widgetWithText(TextButton, 'Delete').last);
      await tester.pumpAndSettle();

      expect(client.deleted, isEmpty);
      expect(client.aborted, isEmpty);
      expect(find.textContaining('started running while the confirm was open'),
          findsOneWidget);
      expect(find.text('NGC7092'), findsOneWidget);
    });

    testWidgets('a failed delete keeps the row and shows the error',
        (tester) async {
      final (container, client) = await pumpWithRow(tester);
      client.throwOnDelete = true;
      container.read(selectedSequenceIdProvider.notifier).select('s1');

      await tester.tap(find.byIcon(Icons.delete_outline));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle();

      expect(find.textContaining("Couldn't delete"), findsOneWidget);
      expect(find.text('NGC7092'), findsOneWidget);
      // A failed delete must NOT kick the sequence out of the Run tab.
      expect(container.read(selectedSequenceIdProvider), 's1');
    });
  });

  group('SequencerToolbar Load button', () {
    Future<void> pumpToolbar(WidgetTester tester, {required bool connected}) async {
      final SequenceClient? client = connected ? _FakeClient() : null;
      await tester.pumpWidget(ProviderScope(
        overrides: [
          sequenceApiProvider.overrideWithValue(client),
        ],
        child: const MaterialApp(home: Scaffold(body: SequencerToolbar())),
      ));
      await tester.pump();
    }

    TextButton loadButton(WidgetTester tester) => tester.widget<TextButton>(
        find.ancestor(of: find.text('Load'), matching: find.byType(TextButton)));

    testWidgets('disabled with no server', (tester) async {
      await pumpToolbar(tester, connected: false);
      expect(loadButton(tester).onPressed, isNull);
    });

    testWidgets('enabled once connected', (tester) async {
      await pumpToolbar(tester, connected: true);
      expect(loadButton(tester).onPressed, isNotNull);
    });

    testWidgets('Delete acts on the open sequence: disabled with none, '
        'deletes + clears selection with one', (tester) async {
      final client = _FakeClient();
      final container = ProviderContainer(overrides: [
        sequenceApiProvider.overrideWithValue(client),
        sequenceListProvider.overrideWith(
            () => _FakeListNotifier(() async => [_item('s9', 'Veil Nebula')])),
      ]);
      addTearDown(container.dispose);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: Scaffold(body: SequencerToolbar())),
      ));
      await tester.pump();

      // No selection → the toolbar Delete is disabled.
      TextButton deleteButton() => tester.widget<TextButton>(find.ancestor(
          of: find.text('Delete'), matching: find.byType(TextButton)));
      expect(deleteButton().onPressed, isNull);

      container.read(selectedSequenceIdProvider.notifier).select('s9');
      await tester.pumpAndSettle();
      expect(deleteButton().onPressed, isNotNull);

      // The toolbar row is horizontally scrollable and Delete sits past the
      // test surface's 800px — bring it on screen before tapping.
      await tester.ensureVisible(find.text('Delete'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle();
      expect(find.text('Delete sequence?'), findsOneWidget);
      // Two "Delete" texts are on screen (toolbar + confirm); the confirm's is
      // the topmost route's — the last in the tree.
      await tester.tap(find.widgetWithText(TextButton, 'Delete').last);
      await tester.pumpAndSettle();

      expect(client.deleted, ['s9']);
      expect(container.read(selectedSequenceIdProvider), isNull);
      expect(find.textContaining('Deleted "Veil Nebula"'), findsOneWidget);
    });

    testWidgets('status line names the selected sequence', (tester) async {
      final container = ProviderContainer(overrides: [
        sequenceApiProvider.overrideWithValue(_FakeClient()),
        sequenceListProvider.overrideWith(
            () => _FakeListNotifier(() async => [_item('s9', 'Veil Nebula')])),
      ]);
      addTearDown(container.dispose);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: Scaffold(body: SequencerToolbar())),
      ));
      container.read(selectedSequenceIdProvider.notifier).select('s9');
      // pumpAndSettle so the now-watched (async) list provider resolves.
      await tester.pumpAndSettle();
      expect(find.textContaining('Veil Nebula'), findsOneWidget);
    });
  });
}
