import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/widgets/sequencer/sequencer_toolbar.dart';

/// Connected client; getRunState returns whatever's configured. Lifecycle calls
/// record which action fired so the gating wiring can be asserted.
class _FakeClient implements SequenceClient {
  _FakeClient({this.runState});
  final SequenceRunStateInfo? runState;
  final List<String> calls = [];

  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => runState;
  @override
  Future<SequencePage> list({int limit = 50}) async => const SequencePage(items: []);
  @override
  Future<String> start(String id) async => (calls..add('start')).last;
  @override
  Future<String> pause(String id) async => (calls..add('pause')).last;
  @override
  Future<String> resume(String id) async => (calls..add('resume')).last;
  @override
  Future<String> abort(String id) async => (calls..add('abort')).last;
  @override
  Future<String> stop(String id) async => (calls..add('stop')).last;
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
    SequenceRunStateInfo(state: s, framesCompleted: done, framesTotal: total);

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
      expect(info.framesCompleted, 3);
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
      await pump(tester, run: _info(SequenceRunState.paused));
      expect(find.text('Resume'), findsOneWidget);
      expect(btn(tester, 'Resume').onPressed, isNotNull);
      expect(btn(tester, 'Pause').onPressed, isNull);
      expect(btn(tester, 'Abort').onPressed, isNotNull);
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
  });
}
