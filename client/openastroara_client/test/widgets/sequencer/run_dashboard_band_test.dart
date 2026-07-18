import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/widgets/sequencer/run_dashboard_band.dart';

class _FakeRunNotifier extends SequenceRunStateNotifier {
  _FakeRunNotifier(this._v);
  final SequenceRunStateInfo? _v;
  @override
  Future<SequenceRunStateInfo?> build() async => _v;
}

Future<void> _pump(WidgetTester tester, SequenceRunStateInfo? run) async {
  await tester.pumpWidget(ProviderScope(
    overrides: [
      sequenceRunStateProvider.overrideWith(() => _FakeRunNotifier(run)),
    ],
    child: const MaterialApp(home: Scaffold(body: RunDashboardBand())),
  ));
  await tester.pump();
}

void main() {
  testWidgets('idle / no run → band absent (compose mood untouched)',
      (tester) async {
    await _pump(tester, null);
    expect(find.byKey(const Key('run-dashboard-band')), findsNothing);
    await _pump(
        tester, const SequenceRunStateInfo(state: SequenceRunState.completed));
    expect(find.byKey(const Key('run-dashboard-band')), findsNothing);
  });

  testWidgets('running → band with progress, counts and instruction line',
      (tester) async {
    await _pump(
        tester,
        SequenceRunStateInfo(
          sequenceId: 's1',
          state: SequenceRunState.running,
          instructionsCompleted: 3,
          instructionsTotal: 12,
          currentInstructionDescription: 'Take Exposure',
          startedUtc: DateTime.now().toUtc().subtract(const Duration(minutes: 5)),
        ));
    expect(find.byKey(const Key('run-dashboard-band')), findsOneWidget);
    expect(find.text('3/12'), findsOneWidget);
    expect(find.textContaining('Take Exposure'), findsOneWidget);
    // S13 glides the bar to its fraction — settle the tween first.
    await tester.pump(const Duration(milliseconds: 500));
    final bar =
        tester.widget<LinearProgressIndicator>(find.byType(LinearProgressIndicator));
    expect(bar.value, closeTo(0.25, 0.001));
  });

  testWidgets('needs-attention renders the urgent line', (tester) async {
    await _pump(
        tester,
        const SequenceRunStateInfo(
          state: SequenceRunState.pausedAwaitingUser,
          instructionsCompleted: 3,
          instructionsTotal: 12,
        ));
    expect(find.textContaining('The rig needs you'), findsOneWidget);
  });
}
