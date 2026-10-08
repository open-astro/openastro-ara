import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/widgets/sequencer/run_dashboard_band.dart';

class _FakeRunNotifier extends SequenceRunStateNotifier {
  _FakeRunNotifier(this._v);
  final SequenceRunStateInfo? _v;
  @override
  Future<SequenceRunStateInfo?> build() async => _v;
}

class _FakeWheel extends FilterWheelNotifier {
  _FakeWheel(this._v);
  final FilterWheelStatus? _v;
  @override
  Future<FilterWheelStatus?> build() async => _v;
}

Future<void> _pump(WidgetTester tester, SequenceRunStateInfo? run,
    {FilterWheelStatus? wheel}) async {
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        sequenceRunStateProvider.overrideWith(() => _FakeRunNotifier(run)),
        filterWheelProvider.overrideWith(() => _FakeWheel(wheel)),
      ],
      child: const MaterialApp(home: Scaffold(body: RunDashboardBand())),
    ),
  );
  await tester.pump();
}

void main() {
  testWidgets('idle / no run → band absent (compose mood untouched)', (
    tester,
  ) async {
    await _pump(tester, null);
    expect(find.byKey(const Key('run-dashboard-band')), findsNothing);
    await _pump(
      tester,
      const SequenceRunStateInfo(state: SequenceRunState.completed),
    );
    expect(find.byKey(const Key('run-dashboard-band')), findsNothing);
  });

  testWidgets('frames landed so far show next to the instruction count', (
    tester,
  ) async {
    await _pump(
      tester,
      const SequenceRunStateInfo(
        sequenceId: 's',
        runId: 'r',
        state: SequenceRunState.running,
        instructionsCompleted: 5,
        instructionsTotal: 6,
        framesCaptured: 24,
      ),
    );
    expect(find.text('24 frames'), findsOneWidget);
    expect(find.text('5/6'), findsOneWidget);
  });

  testWidgets('running → band with progress, counts and instruction line', (
    tester,
  ) async {
    await _pump(
      tester,
      SequenceRunStateInfo(
        sequenceId: 's1',
        state: SequenceRunState.running,
        instructionsCompleted: 3,
        instructionsTotal: 12,
        currentInstructionDescription: 'Take Exposure',
        startedUtc: DateTime.now().toUtc().subtract(const Duration(minutes: 5)),
      ),
    );
    expect(find.byKey(const Key('run-dashboard-band')), findsOneWidget);
    expect(find.text('3/12'), findsOneWidget);
    expect(find.textContaining('Take Exposure'), findsOneWidget);
    // S13 glides the bar to its fraction — settle the tween first.
    await tester.pump(const Duration(milliseconds: 500));
    final bar = tester.widget<LinearProgressIndicator>(
      find.byType(LinearProgressIndicator),
    );
    expect(bar.value, closeTo(0.25, 0.001));
  });

  testWidgets(
    'early in a run the header shows the daemon\'s remaining estimate '
    '(#1068)',
    (tester) async {
      await _pump(
        tester,
        SequenceRunStateInfo(
          sequenceId: 's1',
          state: SequenceRunState.running,
          instructionsCompleted: 1,
          instructionsTotal: 10,
          startedUtc: DateTime.now().toUtc().subtract(
            const Duration(minutes: 1),
          ),
          estimatedTotalSeconds: 1215,
          estimatedRemainingSeconds: 600,
        ),
      );
      expect(
        find.textContaining('~10:00 left'),
        findsOneWidget,
        reason:
            'the daemon figure is shown before the observed rate is trusted',
      );
    },
  );

  testWidgets('with no daemon estimate the header shows no remaining time', (
    tester,
  ) async {
    await _pump(
      tester,
      SequenceRunStateInfo(
        sequenceId: 's1',
        state: SequenceRunState.running,
        instructionsCompleted: 1,
        instructionsTotal: 10,
        startedUtc: DateTime.now().toUtc().subtract(const Duration(minutes: 1)),
      ),
    );
    expect(find.textContaining('left'), findsNothing);
  });

  testWidgets('needs-attention renders the urgent line', (tester) async {
    await _pump(
      tester,
      const SequenceRunStateInfo(
        state: SequenceRunState.pausedAwaitingUser,
        instructionsCompleted: 3,
        instructionsTotal: 12,
      ),
    );
    expect(find.textContaining('The rig needs you'), findsOneWidget);
  });

  testWidgets('#1298 a running run blocked on a manual filter swap asks for the filter',
      (tester) async {
    const run = SequenceRunStateInfo(
      state: SequenceRunState.running,
      instructionsCompleted: 3,
      instructionsTotal: 12,
      currentInstructionDescription: 'Switch Filter',
    );
    FilterWheelStatus wheel({int? pending}) => FilterWheelStatus(
          deviceId: 'ara-manual-filter-wheel',
          name: 'Manual filter wheel',
          connectionState: EquipmentConnectionState.connected,
          runtimeState: pending == null ? 'idle' : 'awaiting_user',
          currentSlot: 0,
          pendingSlot: pending,
          manual: true,
          slots: const [
            FilterSlot(position: 0, name: 'L', focusOffset: 0),
            FilterSlot(position: 1, name: 'Ha', focusOffset: 0),
          ],
        );
    await _pump(tester, run, wheel: wheel(pending: 1));
    await tester.pump(); // the wheel's async read lands a frame after the run's
    expect(find.text('The rig needs you — install the Ha filter'), findsOneWidget);

    // Swap confirmed: back to the ordinary running line. A fresh tree — a
    // ProviderScope keeps its first overrides across a re-pump.
    await tester.pumpWidget(const SizedBox());
    await _pump(tester, run, wheel: wheel());
    await tester.pump();
    expect(find.textContaining('The rig needs you'), findsNothing);
    expect(find.textContaining('Switch Filter'), findsOneWidget);
  });
}
