import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/alarm/safety_alarm_state.dart';
import 'package:openastroara/widgets/safety_alarm_listener.dart';

/// Drives alarm state manually — no WS, no player, no prefs.
class _ManualAlarm extends SafetyAlarmController {
  @override
  SafetyAlarmState build() => const SafetyAlarmState();

  void startPending(String reason) =>
      state = state.copyWith(pending: true, reason: reason);

  @override
  void silence() {
    if (state.pending || state.ringing) {
      state = state.copyWith(pending: false, ringing: false);
    }
  }
}

void main() {
  testWidgets(
      'Silence dismisses the modal exactly once — the screen underneath survives',
      (tester) async {
    final container = ProviderContainer(overrides: [
      safetyAlarmProvider.overrideWith(_ManualAlarm.new),
    ]);
    addTearDown(container.dispose);

    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(
          home: Scaffold(
            body: SafetyAlarmListener(child: Text('UNDERLYING SCREEN')),
          ),
        ),
      ),
    );

    (container.read(safetyAlarmProvider.notifier) as _ManualAlarm)
        .startPending('wind over the limit');
    await tester.pumpAndSettle();

    expect(find.text('SAFETY ALERT'), findsOneWidget);
    expect(find.textContaining('wind'), findsOneWidget);

    await tester.tap(find.text('Silence alarm'));
    await tester.pumpAndSettle();

    expect(find.text('SAFETY ALERT'), findsNothing);
    expect(find.text('UNDERLYING SCREEN'), findsOneWidget,
        reason: 'a double pop would take the screen under the modal with it');
  });

  testWidgets('safety.safe clearing the state also drops the modal',
      (tester) async {
    final container = ProviderContainer(overrides: [
      safetyAlarmProvider.overrideWith(_ManualAlarm.new),
    ]);
    addTearDown(container.dispose);

    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(
          home: Scaffold(
            body: SafetyAlarmListener(child: Text('UNDERLYING SCREEN')),
          ),
        ),
      ),
    );

    final alarm =
        container.read(safetyAlarmProvider.notifier) as _ManualAlarm;
    alarm.startPending('unsafe');
    await tester.pumpAndSettle();
    expect(find.text('SAFETY ALERT'), findsOneWidget);

    alarm.silence(); // the safety.safe path lands here too
    await tester.pumpAndSettle();

    expect(find.text('SAFETY ALERT'), findsNothing);
    expect(find.text('UNDERLYING SCREEN'), findsOneWidget);
  });
}
