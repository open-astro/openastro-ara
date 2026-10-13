import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/preflight_checklist.dart';
import 'package:openastroara/widgets/preflight/preflight_checklist_card.dart';

PreflightChecklist _checklist({bool fail = false, bool alarmOn = true}) =>
    PreflightChecklist(
      generatedUtc: DateTime.utc(2026, 7, 19, 5, 0),
      items: [
        const PreflightItem(
          key: 'equipment',
          status: 'ok',
          title: 'Equipment',
          detail: 'All 4 devices connected and healthy.',
        ),
        const PreflightItem(
          key: 'storage',
          status: 'ok',
          title: 'Storage',
          detail: '412 GB free.',
        ),
        PreflightItem(
          key: 'mount',
          status: fail ? 'fail' : 'warn',
          title: 'Mount safety',
          detail: fail ? 'Mount is parked.' : 'Mount is not tracking.',
        ),
        const PreflightItem(
          key: 'cooler',
          status: 'unknown',
          title: 'Cooler',
          detail: 'The cooler state could not be read.',
        ),
      ],
      flips: [
        PreflightFlip(
          target: 'M81',
          atUtc: DateTime.utc(2026, 7, 19, 8, 14),
          altitudeDeg: 67.2,
          standing: 'ok',
        ),
        PreflightFlip(
          target: 'NGC 6188',
          atUtc: DateTime.utc(2026, 7, 19, 10, 33),
          altitudeDeg: 12.4,
          standing: 'soft_warning',
        ),
      ],
      unforecastTargets: 1,
      unattendedWindowStart: '23:00',
      unattendedWindowEnd: '06:30',
      unattendedWindowSource: 'clock',
      alarmDevice: 'macbook-bedroom.local',
      alarmSoundEnabled: alarmOn,
      allGood: !fail,
    );

Widget _wrap(Widget child) => ProviderScope(
  child: MaterialApp(
    home: Scaffold(body: SingleChildScrollView(child: child)),
  ),
);

Widget _dialog(Widget child) => ProviderScope(child: MaterialApp(home: child));

void main() {
  testWidgets('#1138 §58.14 the card prints every line of the spec mock-up', (
    tester,
  ) async {
    await tester.pumpWidget(
      _wrap(PreflightChecklistCard(checklist: _checklist(alarmOn: false))),
    );
    expect(find.text("Tonight's sequence — pre-flight check"), findsOneWidget);
    expect(
      find.textContaining('All 4 devices connected and healthy.'),
      findsOneWidget,
    );
    expect(find.textContaining('Mount is not tracking.'), findsOneWidget);
    expect(find.byIcon(Icons.check_circle), findsNWidgets(2));
    expect(find.byIcon(Icons.warning_amber_rounded), findsOneWidget);
    expect(
      find.byIcon(Icons.help_outline),
      findsOneWidget,
      reason: 'unknown is shown as a question, never hidden',
    );
    expect(find.textContaining('M81 (alt 67° at flip, OK)'), findsOneWidget);
    expect(
      find.textContaining(
        'NGC 6188 (alt 12° at flip, below the soft warning, above the hard floor)',
      ),
      findsOneWidget,
    );
    expect(
      find.textContaining('1 more target in the sequence is not forecast'),
      findsOneWidget,
    );
    expect(find.text('Unattended hours: 23:00 — 06:30'), findsOneWidget);
    expect(
      find.text('Alarm device: macbook-bedroom.local — alarm sound is OFF'),
      findsOneWidget,
    );
    expect(find.textContaining('not test-solved'), findsOneWidget);
  });

  testWidgets(
    '#1138 the dialog names the two ways out; a failure changes the headline button',
    (tester) async {
      var allGood = 0;
      var adjust = 0;
      await tester.pumpWidget(
        _dialog(
          PreflightChecklistDialog(
            checklist: _checklist(),
            onAllGood: () => allGood++,
            onAdjust: () => adjust++,
          ),
        ),
      );
      expect(find.text('Before you sleep'), findsOneWidget);
      await tester.tap(find.text('All good — let me go to bed'));
      await tester.tap(find.text('Adjust'));
      expect((allGood, adjust), (1, 1));

      await tester.pumpWidget(
        _dialog(
          PreflightChecklistDialog(
            checklist: _checklist(fail: true),
            onAllGood: () {},
            onAdjust: () {},
          ),
        ),
      );
      expect(find.text('I know — let me go to bed'), findsOneWidget);
      expect(find.byIcon(Icons.report_problem_outlined), findsOneWidget);
    },
  );
}
