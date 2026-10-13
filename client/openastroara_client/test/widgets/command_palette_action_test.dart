import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/widgets/command_palette.dart';
import 'package:openastroara/widgets/sky_atlas/session_plan_dialog.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';

/// No saved servers — the backup modal renders its "connect a server" empty
/// state without touching FlutterSecureStorage (absent in widget tests).
class _FakeSavedServerService implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const <AraServer>[];
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

/// Records the lifecycle calls; everything else is off-limits for the palette.
class _FakeLifecycleClient implements SequenceClient {
  final List<String> calls = [];
  @override
  Future<String> start(String id) async { calls.add('start $id'); return 'op'; }
  @override
  Future<String> pause(String id) async { calls.add('pause $id'); return 'op'; }
  @override
  Future<String> resume(String id, {bool recenter = true, bool refocus = false}) async {
    calls.add('resume $id recenter=$recenter refocus=$refocus');
    return 'op';
  }
  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => null;
  @override
  void close() {}
  @override
  dynamic noSuchMethod(Invocation invocation) => throw UnimplementedError('${invocation.memberName}');
}

class _SeededRunState extends SequenceRunStateNotifier {
  _SeededRunState(this.seed);
  final SequenceRunState? seed;
  @override
  Future<SequenceRunStateInfo?> build() async => SequenceRunStateInfo(sequenceId: 'seq-1', state: seed);
}

/// Opens the palette over a rig with `seq-1` selected and the run in [state].
Future<(ProviderContainer, _FakeLifecycleClient)> _pumpRunPalette(WidgetTester tester, SequenceRunState? state,
    {String? selected = 'seq-1', bool runTabAlive = true}) async {
  final client = _FakeLifecycleClient();
  final container = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService()),
    sequenceApiProvider.overrideWithValue(client),
    sequenceRunStateProvider.overrideWith(() => _SeededRunState(state)),
  ]);
  addTearDown(container.dispose);
  if (selected != null) container.read(selectedSequenceIdProvider.notifier).select(selected);
  // Keep the autoDispose run state alive, as the Run tab's watch does — or
  // not, for the case where that tab was never built.
  if (runTabAlive) {
    container.listen(sequenceRunStateProvider, (_, _) {});
    await container.read(sequenceRunStateProvider.future);
  }
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: MaterialApp(
      home: Scaffold(
        body: Builder(
          builder: (context) => Center(
            child: ElevatedButton(onPressed: () => showCommandPalette(context), child: const Text('open')),
          ),
        ),
      ),
    ),
  ));
  await tester.tap(find.text('open'));
  await tester.pumpAndSettle();
  return (container, client);
}

Future<void> _pick(WidgetTester tester, String query, String label) async {
  await tester.enterText(find.byType(TextField), query);
  await tester.pump();
  await tester.tap(find.text(label));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets(
      '§61.10 activating an action hit closes the palette and runs its launcher',
      (tester) async {
    // #829 r1 note — the dispatch's context timing is the fragile part: the
    // launcher must run on the ROOT navigator grabbed BEFORE the palette pops
    // (the palette's own context dies with the pop). One action proves the
    // mechanism; the per-action launchers are a 1:1 mirror of app_shell's
    // top-bar buttons.
    await tester.pumpWidget(ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService()),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: Builder(
            builder: (context) => Center(
              child: ElevatedButton(
                onPressed: () => showCommandPalette(context),
                child: const Text('open'),
              ),
            ),
          ),
        ),
      ),
    ));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();

    // A label-prefix query ranks the action first (500 > any keyword tier), so
    // the row is guaranteed to be built inside the palette's lazy ListView.
    await tester.enterText(find.byType(TextField), 'back up');
    await tester.pump();
    expect(find.text('Back up & restore…'), findsOneWidget,
        reason: 'the backup action is the top hit for its label prefix');

    await tester.tap(find.text('Back up & restore…'));
    await tester.pumpAndSettle();

    // The Backup & Restore dialog is open on the root navigator…
    expect(find.text('Backup & Restore'), findsOneWidget);
    // …and the palette itself popped (its search field is gone).
    expect(find.byType(TextField), findsNothing);
  });

  testWidgets('the search field owns focus on open (type-to-search)',
      (tester) async {
    await tester.pumpWidget(ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService()),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: Builder(
            builder: (context) => Center(
              child: ElevatedButton(
                onPressed: () => showCommandPalette(context),
                child: const Text('open'),
              ),
            ),
          ),
        ),
      ),
    ));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    // Typing with NO click must land in the search box — a competing
    // autofocus wrapper used to swallow focus until a manual click.
    // testTextInput.enterText types into the ATTACHED input client, which
    // only exists when an EditableText actually holds focus.
    tester.testTextInput.enterText('dither');
    await tester.pump();
    expect(find.widgetWithText(TextField, 'dither'), findsOneWidget);
  });

  testWidgets('#1154 "Plan tonight…" opens the session planner on the root navigator', (tester) async {
    await tester.pumpWidget(ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService()),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: Builder(
            builder: (context) => Center(
              child: ElevatedButton(
                onPressed: () => showCommandPalette(context),
                child: const Text('open'),
              ),
            ),
          ),
        ),
      ),
    ));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    await tester.enterText(find.byType(TextField), 'plan tonight');
    await tester.pump();
    await tester.tap(find.text('Plan tonight…'));
    await tester.pumpAndSettle();
    expect(find.byType(SessionPlanDialog), findsOneWidget);
    expect(find.text('Plan tonight…'), findsNothing, reason: 'the palette popped');
  });

  group('#1154 run actions dispatch through the Run tab helpers', () {
    testWidgets('Run on an idle, unaligned rig asks first, then "Run anyway" starts it after the palette is gone',
        (tester) async {
      final (_, client) = await _pumpRunPalette(tester, SequenceRunState.idle);
      await _pick(tester, 'run or resume', 'Run or resume the sequence (⌘R)');
      expect(find.text('Not polar aligned — run anyway?'), findsOneWidget);
      expect(find.byType(TextField), findsNothing, reason: 'the palette popped before the dialog');
      await tester.tap(find.text('Run anyway'));
      await tester.pumpAndSettle();
      expect(client.calls, ['start seq-1']);
    });

    testWidgets('Run while running only says so', (tester) async {
      final (_, client) = await _pumpRunPalette(tester, SequenceRunState.running);
      await _pick(tester, 'run or resume', 'Run or resume the sequence (⌘R)');
      expect(find.textContaining('already running'), findsOneWidget);
      expect(client.calls, isEmpty);
    });

    testWidgets('Run while paused offers the resume choice and resumes with it', (tester) async {
      final (_, client) = await _pumpRunPalette(tester, SequenceRunState.paused);
      await _pick(tester, 'run or resume', 'Run or resume the sequence (⌘R)');
      await tester.tap(find.text('Just resume'));
      await tester.pumpAndSettle();
      expect(client.calls, ['resume seq-1 recenter=false refocus=false']);
    });

    testWidgets('a paused run is still seen when the Run tab was never built (run state not yet loaded)',
        (tester) async {
      final (_, client) = await _pumpRunPalette(tester, SequenceRunState.paused, runTabAlive: false);
      await _pick(tester, 'pause', 'Pause or resume the sequence (Space)');
      expect(find.text('Resume imaging?'), findsOneWidget, reason: 'the palette waited for the run state');
      await tester.tap(find.text('Just resume'));
      await tester.pumpAndSettle();
      expect(client.calls, ['resume seq-1 recenter=false refocus=false']);
    });

    testWidgets('Pause while running pauses', (tester) async {
      final (_, client) = await _pumpRunPalette(tester, SequenceRunState.running);
      await _pick(tester, 'pause', 'Pause or resume the sequence (Space)');
      expect(client.calls, ['pause seq-1']);
    });

    testWidgets('Pause while idle says nothing is running', (tester) async {
      final (_, client) = await _pumpRunPalette(tester, SequenceRunState.idle);
      await _pick(tester, 'pause', 'Pause or resume the sequence (Space)');
      expect(find.textContaining('No sequence is running'), findsOneWidget);
      expect(client.calls, isEmpty);
    });

    testWidgets('with nothing selected the palette explains instead of silently doing nothing', (tester) async {
      final (_, client) = await _pumpRunPalette(tester, null, selected: null);
      await _pick(tester, 'run or resume', 'Run or resume the sequence (⌘R)');
      expect(find.text('Select a saved sequence on a connected rig first.'), findsOneWidget);
      expect(client.calls, isEmpty);
    });
  });
}
