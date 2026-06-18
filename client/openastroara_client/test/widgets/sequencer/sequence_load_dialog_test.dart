import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/sequence_api.dart';
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

SequenceListItem _item(String id, String name) =>
    SequenceListItem(id: id, name: name, instructionCount: 3, targetCount: 1);

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
  });
}
