import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/sequence/sequence_share_export.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/widgets/sequencer/sequencer_toolbar.dart';

/// Connected client; getRunState returns whatever's configured. Lifecycle calls
/// record which action fired so the gating wiring can be asserted.
class _FakeClient implements SequenceClient {
  @override
  Future<SequenceValidationResult> validate(Map<String, dynamic> body) async =>
      const SequenceValidationResult(valid: true);
  _FakeClient({this.runState, this.throwOnStart = false});
  final SequenceRunStateInfo? runState;
  final bool throwOnStart;
  final List<String> calls = [];

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
  Future<SequencePage> list({int limit = 50}) async => const SequencePage(items: []);
  @override
  Future<String> start(String id) async {
    calls.add('start');
    if (throwOnStart) {
      throw const FormatException('start accepted but no operation_id');
    }
    return 'op';
  }
  @override
  Future<String> pause(String id) async => (calls..add('pause')).last;
  @override
  Future<String> resume(String id) async => (calls..add('resume')).last;
  @override
  Future<String> skipCurrent(String id) async => (calls..add('skip-current')).last;
  @override
  Future<String> abort(String id) async => (calls..add('abort')).last;
  @override
  Future<String> stop(String id) async => (calls..add('stop')).last;
  @override
  Future<SequenceShareExport> exportShare(String id) async => throw UnimplementedError();
  @override
  Future<List<SequenceTemplate>> listTemplates() async => const [];
  @override
  Future<String> instantiateTemplate(String t, String n) async => 'new-seq';
  @override
  Future<String> create(String name, Map<String, dynamic> body,
          {String? description}) async =>
      'new-seq';
  @override
  void close() {}
}

/// Pins the run-state provider to a fixed value for gating tests.
class _FakeRunNotifier extends SequenceRunStateNotifier {
  _FakeRunNotifier(this._v);
  final SequenceRunStateInfo? _v;
  @override
  Future<SequenceRunStateInfo?> build() async => _v;
}

SequenceRunStateInfo _info(SequenceRunState s, {int done = 0, int total = 0}) =>
    SequenceRunStateInfo(state: s, instructionsCompleted: done, instructionsTotal: total);

void main() {
  group('run-state provider', () {
    test('null when nothing selected', () async {
      final container = ProviderContainer(overrides: [
        sequenceApiProvider.overrideWithValue(_FakeClient(runState: _info(SequenceRunState.running))),
      ]);
      addTearDown(container.dispose);
      expect(await container.read(sequenceRunStateProvider.future), isNull);
    });

    test('reads run state for the selected sequence', () async {
      final container = ProviderContainer(overrides: [
        sequenceApiProvider
            .overrideWithValue(_FakeClient(runState: _info(SequenceRunState.running, done: 3, total: 9))),
      ]);
      addTearDown(container.dispose);
      container.read(selectedSequenceIdProvider.notifier).select('seq-1');
      final info = await container.read(sequenceRunStateProvider.future);
      expect(info!.state, SequenceRunState.running);
      expect(info.instructionsCompleted, 3);
    });
  });

  group('toolbar run controls gating', () {
    Future<ProviderContainer> pump(
      WidgetTester tester, {
      SequenceRunStateInfo? run,
    }) async {
      final container = ProviderContainer(overrides: [
        sequenceApiProvider.overrideWithValue(_FakeClient()),
        sequenceRunStateProvider.overrideWith(() => _FakeRunNotifier(run)),
      ]);
      addTearDown(container.dispose);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: Scaffold(body: SequencerToolbar())),
      ));
      container.read(selectedSequenceIdProvider.notifier).select('seq-1');
      await tester.pumpAndSettle();
      return container;
    }

    TextButton btn(WidgetTester tester, String label) => tester.widget<TextButton>(
        find.ancestor(of: find.text(label), matching: find.byType(TextButton)));

    testWidgets('the status line counts INSTRUCTIONS, never "frames" (r1 on the '
        'frames_*→instructions_* rename)', (tester) async {
      await pump(tester, run: _info(SequenceRunState.running, done: 3, total: 9));
      expect(find.textContaining('3/9 instructions'), findsOneWidget,
          reason: 'the counters are sequence-tree leaves — slews and autofocus '
              'steps included — not camera exposures');
      expect(find.textContaining('frames'), findsNothing);
    });

    testWidgets('no active run → Run enabled, Pause/Abort disabled', (tester) async {
      await pump(tester, run: null);
      expect(btn(tester, 'Run').onPressed, isNotNull);
      expect(btn(tester, 'Pause').onPressed, isNull);
      expect(btn(tester, 'Abort').onPressed, isNull);
    });

    testWidgets('running → Pause + Abort enabled, Run disabled', (tester) async {
      await pump(tester, run: _info(SequenceRunState.running));
      expect(btn(tester, 'Run').onPressed, isNull);
      expect(btn(tester, 'Pause').onPressed, isNotNull);
      expect(btn(tester, 'Abort').onPressed, isNotNull);
    });

    testWidgets('paused → Run shows Resume (enabled), Abort enabled, Pause disabled',
        (tester) async {
      // The daemon's §38 instruction-boundary pause is real now: Paused means
      // the engine actually suspended, Resume releases it.
      await pump(tester, run: _info(SequenceRunState.paused));
      expect(find.text('Resume'), findsOneWidget);
      expect(btn(tester, 'Resume').onPressed, isNotNull);
      expect(btn(tester, 'Pause').onPressed, isNull);
      expect(btn(tester, 'Abort').onPressed, isNotNull);
    });

    testWidgets('starting → Run + Pause disabled, Abort enabled', (tester) async {
      await pump(tester, run: _info(SequenceRunState.starting));
      expect(btn(tester, 'Run').onPressed, isNull);
      expect(btn(tester, 'Pause').onPressed, isNull);
      expect(btn(tester, 'Abort').onPressed, isNotNull);
    });

    testWidgets('aborting → Abort disabled (already aborting)', (tester) async {
      await pump(tester, run: _info(SequenceRunState.aborting));
      expect(btn(tester, 'Abort').onPressed, isNull);
      expect(btn(tester, 'Run').onPressed, isNull);
    });

    testWidgets('completed → Run re-enabled (re-run), Pause disabled', (tester) async {
      await pump(tester, run: _info(SequenceRunState.completed));
      expect(btn(tester, 'Run').onPressed, isNotNull);
      expect(btn(tester, 'Pause').onPressed, isNull);
      expect(btn(tester, 'Abort').onPressed, isNull);
    });

    testWidgets('running → Skip enabled', (tester) async {
      await pump(tester, run: _info(SequenceRunState.running));
      expect(btn(tester, 'Skip').onPressed, isNotNull);
    });

    testWidgets('no active run → Skip disabled', (tester) async {
      await pump(tester, run: null);
      expect(btn(tester, 'Skip').onPressed, isNull);
    });

    testWidgets('aborting → Skip disabled (teardown already underway)',
        (tester) async {
      await pump(tester, run: _info(SequenceRunState.aborting));
      expect(btn(tester, 'Skip').onPressed, isNull);
    });

    testWidgets('paused → Skip enabled (parity with Abort)', (tester) async {
      // Skip shares Abort's gate exactly: paused is an active state, so both
      // controls stay enabled there. (The daemon never actually reaches paused
      // today — pause is a no-op — but the gate is pinned so the decision is
      // explicit rather than incidental.)
      await pump(tester, run: _info(SequenceRunState.paused));
      expect(btn(tester, 'Skip').onPressed, isNotNull);
      expect(btn(tester, 'Abort').onPressed, isNotNull);
    });

    testWidgets('pressing Skip fires skip-current', (tester) async {
      final container = ProviderContainer(overrides: [
        sequenceApiProvider.overrideWithValue(_FakeClient()),
        sequenceRunStateProvider
            .overrideWith(() => _FakeRunNotifier(_info(SequenceRunState.running))),
      ]);
      addTearDown(container.dispose);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: Scaffold(body: SequencerToolbar())),
      ));
      container.read(selectedSequenceIdProvider.notifier).select('seq-1');
      await tester.pumpAndSettle();
      btn(tester, 'Skip').onPressed!();
      await tester.pumpAndSettle();
      final fake = container.read(sequenceApiProvider) as _FakeClient;
      expect(fake.calls, contains('skip-current'));
    });

    testWidgets('a command in flight disables the controls', (tester) async {
      final container = await pump(tester, run: null);
      // Run is enabled at rest...
      expect(btn(tester, 'Run').onPressed, isNotNull);
      // ...but disabled while a command is busy (the in-flight guard).
      container.read(sequenceCommandBusyProvider.notifier).setBusy(true);
      await tester.pump();
      expect(btn(tester, 'Run').onPressed, isNull);
    });

    testWidgets('pressing Run starts the sequence', (tester) async {
      final container = await pump(tester, run: null);
      // Invoke the handler directly (the button is in a horizontal scroll view
      // and may be off the test viewport, so a hit-test tap is unreliable).
      btn(tester, 'Run').onPressed!();
      await tester.pumpAndSettle();
      final fake = container.read(sequenceApiProvider) as _FakeClient;
      expect(fake.calls, contains('start'));
    });

    testWidgets('a non-Dio lifecycle failure surfaces a SnackBar, not a crash',
        (tester) async {
      final container = ProviderContainer(overrides: [
        sequenceApiProvider.overrideWithValue(_FakeClient(throwOnStart: true)),
        sequenceRunStateProvider.overrideWith(() => _FakeRunNotifier(null)),
      ]);
      addTearDown(container.dispose);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: Scaffold(body: SequencerToolbar())),
      ));
      container.read(selectedSequenceIdProvider.notifier).select('seq-1');
      await tester.pumpAndSettle();
      btn(tester, 'Run').onPressed!(); // start() throws FormatException
      await tester.pump(); // flush the async handler's microtasks
      await tester.pump(const Duration(milliseconds: 400)); // SnackBar entrance
      expect(find.text('Sequence command failed.'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  });
}
