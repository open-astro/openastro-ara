import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/screens/simple/simple_safety_alarm.dart';
import 'package:openastroara/state/alarm/safety_alarm_state.dart';
import 'package:openastroara/state/settings/notifications_settings_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/widgets/emergency_stop_button.dart';

import '../../support/section_load.dart';

class _Pending extends SafetyAlarmController {
  @override
  SafetyAlarmState build() =>
      const SafetyAlarmState(pending: true, reason: 'wind over the limit');
}

void main() {
  Future<ProviderContainer> pump(
    WidgetTester tester, {
    required bool companion,
    Locale? locale,
    double textScale = 1,
    bool soundAlert = true,
    bool loaded = true,
  }) async {
    tester.view.physicalSize = const Size(390, 844);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          companionModeProvider.overrideWithValue(companion),
          safetyAlarmProvider.overrideWith(_Pending.new),
        ],
        child: MaterialApp(
          locale: locale,
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          builder: (context, child) => MediaQuery(
            data: MediaQuery.of(context)
                .copyWith(textScaler: TextScaler.linear(textScale)),
            child: child!,
          ),
          home: const Stack(children: [SimpleSafetyAlarm()]),
        ),
      ),
    );
    final container = ProviderScope.containerOf(
      tester.element(find.byType(SimpleSafetyAlarm)),
    );
    if (loaded) {
      await tester.runAsync(
        () => loadSection(
          container,
          notificationsSettingsProvider,
          'notifications',
        ),
      );
      container
          .read(notificationsSettingsProvider.notifier)
          .setSoundAlert(soundAlert);
    }
    await tester.pump();
    return container;
  }

  // The label's own paragraph: find.text matches a cut-off line too.
  bool cut(WidgetTester tester, String text) =>
      tester.renderObject<RenderParagraph>(find.text(text)).didExceedMaxLines;

  testWidgets(
    'on a phone the alarm says sound and vibration and offers a held Emergency Abort',
    (tester) async {
      await pump(tester, companion: true);
      expect(find.textContaining('sounds and vibrates'), findsOneWidget);
      expect(find.byKey(const Key('simple-safety-abort')), findsOneWidget);
      expect(find.text('Emergency Abort'), findsOneWidget);

      // A tap does not abort (a pointer press only starts the hold).
      await tester.tap(find.byKey(const Key('simple-safety-abort')));
      await tester.pump();
      expect(find.text('Emergency Abort'), findsOneWidget);
      expect(find.text('Stopping…'), findsNothing);

      // Held: the label turns to Hold to confirm and a release short of 2 s sends nothing.
      final g = await tester.startGesture(
        tester.getCenter(find.byKey(const Key('simple-safety-abort'))),
      );
      await tester.pump(const Duration(milliseconds: 500));
      expect(find.text('Hold to confirm'), findsOneWidget);
      await g.up();
      await tester.pump();
      expect(find.text('Hold to confirm'), findsNothing);
    },
  );

  test('the abort hold is the STOP hold', () {
    expect(kEmergencyStopHold, const Duration(seconds: 2));
  });

  testWidgets('off a phone the alarm has no abort button or phone note', (
    tester,
  ) async {
    await pump(tester, companion: false);
    expect(find.byKey(const Key('simple-safety-abort')), findsNothing);
    expect(find.textContaining('vibrates'), findsNothing);
  });

  testWidgets(
    'the phone alarm speaks Brazilian Portuguese at 2x text without overflow',
    (tester) async {
      await pump(
        tester,
        companion: true,
        locale: const Locale('pt', 'BR'),
        textScale: 2,
      );
      expect(find.text('Abortar em emergência'), findsOneWidget);
      expect(find.textContaining('toca e vibra'), findsOneWidget);
      expect(tester.takeException(), isNull);
    },
  );

  for (final (name, locale, abort, hold) in [
    ('English', null, 'Emergency Abort', 'Hold to confirm'),
    (
      'Brazilian Portuguese',
      const Locale('pt', 'BR'),
      'Abortar em emergência',
      'Segure para confirmar',
    ),
  ]) {
    testWidgets('the Emergency Abort label is not cut at 2x text in $name', (
      tester,
    ) async {
      await pump(tester, companion: true, locale: locale, textScale: 2);
      expect(cut(tester, abort), isFalse);
      // At 2x the alarm scrolls: bring the button on screen to hold it.
      await tester.ensureVisible(find.byKey(const Key('simple-safety-abort')));
      final g = await tester.startGesture(
        tester.getCenter(find.byKey(const Key('simple-safety-abort'))),
      );
      await tester.pump(const Duration(milliseconds: 500));
      expect(cut(tester, hold), isFalse);
      await g.up();
      await tester.pump();
    });
  }

  testWidgets('with Sound alert off the phone alarm has no sound note', (
    tester,
  ) async {
    await pump(tester, companion: true, soundAlert: false);
    expect(find.textContaining('vibrates'), findsNothing);
    // Emergency Abort stays: it is not part of the sound.
    expect(find.byKey(const Key('simple-safety-abort')), findsOneWidget);
  });

  testWidgets(
    'on a phone with notifications not loaded the alarm shows the sound note',
    (tester) async {
      final container = await pump(tester, companion: true, loaded: false);
      expect(container.read(notificationsSettingsProvider).value, isNull);
      // Not loaded, the alarm still rings (it fails loud), so the note shows.
      expect(find.textContaining('sounds and vibrates'), findsOneWidget);
    },
  );
}
