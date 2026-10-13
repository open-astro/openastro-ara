import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/notifications_api.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/session/session_summary_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/util/session_summary.dart';
import 'package:openastroara/widgets/session_ended_summary_listener.dart';

class _Seeded extends SessionSummaryNotifier {
  _Seeded(this._v);
  final SessionSummary? _v;
  final acked = <String>[];
  @override
  Future<SessionSummary?> build() async => _v;
  @override
  Future<void> acknowledge(String runId) async {
    acked.add(runId);
    state = const AsyncData(null);
  }
}

void main() {
  final summary = SessionSummary(
    sequenceId: 'seq-1',
    runId: 'r1',
    sequenceName: 'M81 night',
    state: SequenceRunState.pausedAwaitingUser,
    targetName: 'M81',
    startedUtc: DateTime.utc(2026, 10, 12, 22),
    endedUtc: DateTime.utc(2026, 10, 13, 3, 24),
    framesCaptured: 47,
    incidents: [
      AraNotification(
        id: 'n1',
        postedUtc: DateTime.utc(2026, 10, 13, 3, 24),
        severity: NotificationSeverity.warning,
        category: NotificationCategory.safety,
        title: 'Unattended shutdown executed',
        message: 'the equipment was put to bed: Cooler warmed. Mount parked.',
        read: false,
        dismissed: false,
      ),
    ],
  );

  Future<(ProviderContainer, _Seeded)> pump(
    WidgetTester tester,
    SessionSummary? s,
  ) async {
    final notifier = _Seeded(s);
    final container = ProviderContainer(
      overrides: [
        sessionSummaryProvider.overrideWith(() => notifier),
        wsEventStreamProvider.overrideWithValue(null),
      ],
    );
    addTearDown(container.dispose);
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(
          home: Scaffold(
            body: SessionEndedSummaryListener(child: Text('shell')),
          ),
        ),
      ),
    );
    await tester.pump();
    await tester.pump();
    return (container, notifier);
  }

  testWidgets(
    '#1138 the morning card names the run, the frames and the incidents',
    (tester) async {
      await pump(tester, summary);
      expect(find.text('Session paused — the rig needs you'), findsOneWidget);
      expect(find.textContaining('M81 night (M81) paused and is waiting'), findsOneWidget);
      expect(find.text('47 frames captured'), findsOneWidget);
      expect(
        find.textContaining('Unattended shutdown executed'),
        findsOneWidget,
      );
      expect(find.textContaining('Mount parked'), findsOneWidget);
      expect(find.text('Review details'), findsOneWidget);
    },
  );

  testWidgets(
    '#1138 Got it acknowledges the run; Open the run selects it and jumps to Run',
    (tester) async {
      final (container, notifier) = await pump(tester, summary);
      await tester.tap(find.text('Open the run'));
      await tester.pumpAndSettle();
      expect(container.read(selectedSequenceIdProvider), 'seq-1');
      expect(container.read(selectedTabIndexProvider), kRunTabIndex);
      expect(notifier.acked, ['r1'], reason: 'any way out acknowledges');
      expect(find.text('Open the run'), findsNothing);
    },
  );

  testWidgets('#1138 nothing to say → no dialog', (tester) async {
    await pump(tester, null);
    expect(find.byType(AlertDialog), findsNothing);
  });

  testWidgets(
    '#1138 Got it just acknowledges; Review details opens the notification centre',
    (tester) async {
      var (container, notifier) = await pump(tester, summary);
      await tester.tap(find.text('Got it'));
      await tester.pumpAndSettle();
      expect(notifier.acked, ['r1']);
      expect(
        container.read(selectedSequenceIdProvider),
        isNull,
        reason: 'Got it does not navigate',
      );
      expect(find.byType(AlertDialog), findsNothing);

      await tester.pumpWidget(const SizedBox());
      (container, notifier) = await pump(tester, summary);
      await tester.tap(find.text('Review details'));
      await tester.pumpAndSettle();
      expect(notifier.acked, ['r1']);
      expect(
        find.text('Notifications'),
        findsOneWidget,
        reason: 'the notification centre is up',
      );
    },
  );
}
