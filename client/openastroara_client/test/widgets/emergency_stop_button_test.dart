import 'dart:math' as math;

import 'package:flutter/gestures.dart'
    show PointerDeviceKind, kSecondaryMouseButton;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/simple/simple_message_pills.dart';
import 'package:flutter/rendering.dart' show RenderParagraph;
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/server_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/ws/client_session_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/theme/ara_theme.dart';
import 'package:openastroara/widgets/emergency_stop_button.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';

/// Records §35.3 emergency-stop calls; the handshake/§27 members are unused
/// by the button.
class _FakeServerApi implements ServerApi {
  int calls = 0;
  EmergencyStopResult result = const EmergencyStopResult(
    alreadyInProgress: false,
    runsAborted: 1,
    exposureAborted: true,
    guidingStopped: true,
    parkRequested: true,
    flatPanelLightOff: true,
  );

  /// The daemon can't be reached: the stop never happened.
  Object? error;
  @override
  Future<EmergencyStopResult> emergencyStop() async {
    calls++;
    if (error != null) throw error!;
    return result;
  }

  @override
  dynamic noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

class _FixedServers extends SavedServersNotifier {
  @override
  Future<List<AraServer>> build() async => const [
    AraServer(hostname: 'pi-test', port: 5555),
  ];
}

class _Prefs extends SimpleModePrefsService {
  _Prefs(this.value);
  final bool value;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async {}
}

void main() {
  late _FakeServerApi fake;

  Future<void> pumpButton(
    WidgetTester tester, {
    EmergencyStopButton button = const EmergencyStopButton(),
    bool simpleMode = false,
    bool pills = false,
  }) async {
    fake = _FakeServerApi();
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          savedServersProvider.overrideWith(_FixedServers.new),
          serverApiFactoryProvider.overrideWithValue((server) => fake),
          simpleModePrefsProvider.overrideWithValue(_Prefs(simpleMode)),
          // Simple Mode shows only with the Experimental switch on too.
          simpleModeFeaturePrefsProvider.overrideWithValue(_Prefs(true)),
        ],
        child: MaterialApp(
          // The app's own theme: its onError is black, not the default white.
          theme: araTheme,
          home: Scaffold(
            body: pills
                // The pill the stop's message lands in, as Simple Mode shows it.
                ? Column(children: [const SizedBox(width: 420, child: SimpleMessagePills(maxHeight: 600)), button])
                : button,
          ),
        ),
      ),
    );
    // Materialize + settle the async saved-servers load so
    // activeServerProvider resolves to the fixed server at tap time.
    final container = ProviderScope.containerOf(
      tester.element(find.byType(EmergencyStopButton)),
    );
    await container.read(savedServersProvider.future);
    await container.read(simpleModeProvider.future);
    await container.read(simpleModeFeatureProvider.future);
    await tester.pumpAndSettle();
  }

  /// Keyboard Enter / a screen reader's activate: no hold possible, so the
  /// button falls back to the confirm dialog.
  Future<void> activateWithoutPointer(WidgetTester tester) async {
    tester
        .widget<FilledButton>(
          find.descendant(
            of: find.byType(EmergencyStopButton),
            matching: find.byType(FilledButton),
          ),
        )
        .onPressed!();
  }

  for (final (name, button) in [
    ('compact', const EmergencyStopButton(compact: true)),
    ('simple', const EmergencyStopButton(simple: true)),
    ('full UI', const EmergencyStopButton()),
  ]) {
    testWidgets(
      'Emergency Stop ($name): the label reads at 4.5:1 on the red, at rest and while held',
      (tester) async {
        await pumpButton(tester, button: button, simpleMode: true);
        double ratio(Color a, Color b) {
          final (x, y) = (a.computeLuminance(), b.computeLuminance());
          return (math.max(x, y) + 0.05) / (math.min(x, y) + 0.05);
        }

        final style = tester
            .widget<FilledButton>(find.byType(FilledButton))
            .style!;
        final fill = style.backgroundColor!.resolve({})!;
        final text = style.foregroundColor!.resolve({})!;
        expect(fill, AraColors.emergencyStop, reason: 'every Stop shares one red');
        expect(text, Colors.white, reason: 'the label is white, not the scheme\'s black');
        expect(ratio(fill, text), greaterThanOrEqualTo(4.5), reason: 'at rest');
        final g = await tester.startGesture(
          tester.getCenter(find.byType(EmergencyStopButton)),
        );
        await tester.pump(const Duration(seconds: 1));
        final sweep = tester
            .widgetList<ColoredBox>(
              find.descendant(
                of: find.byType(EmergencyStopButton),
                matching: find.byType(ColoredBox),
              ),
            )
            .last
            .color;
        expect(
          ratio(Color.alphaBlend(sweep, fill), text),
          greaterThanOrEqualTo(4.5),
          reason: 'under the sweep',
        );
        await g.up();
        await tester.pumpAndSettle();
      },
    );
  }

  testWidgets('Simple Mode: a failed rung leads the message, so its two lines show what failed', (tester) async {
    await pumpButton(tester, button: const EmergencyStopButton(compact: true), simpleMode: true);
    fake.result = const EmergencyStopResult(
      alreadyInProgress: false,
      runsAborted: 1,
      exposureAborted: true,
      guidingStopped: true,
      parkRequested: false,
      flatPanelLightOff: true,
      failedRungs: ['park'],
    );
    await activateWithoutPointer(tester);
    await activateWithoutPointer(tester);
    await tester.pumpAndSettle();
    final container = ProviderScope.containerOf(tester.element(find.byType(EmergencyStopButton)));
    final title = container.read(simpleMessagesProvider).first.title;
    expect(title, startsWith('CHECK THE RIG: MOUNT PARK FAILED'));
    expect(title, contains('1 sequence run(s) aborted'), reason: 'what worked still follows');
  });

  testWidgets('a rotator that would not halt is called out by name (#1671)', (tester) async {
    await pumpButton(tester, button: const EmergencyStopButton(compact: true), simpleMode: true);
    fake.result = const EmergencyStopResult(
      alreadyInProgress: false,
      runsAborted: 0,
      exposureAborted: true,
      guidingStopped: true,
      parkRequested: true,
      flatPanelLightOff: false,
      failedRungs: ['halt_rotator'],
    );
    await activateWithoutPointer(tester);
    await activateWithoutPointer(tester);
    await tester.pumpAndSettle();
    final container = ProviderScope.containerOf(tester.element(find.byType(EmergencyStopButton)));
    expect(container.read(simpleMessagesProvider).first.title, startsWith('CHECK THE RIG: ROTATOR HALT FAILED'));
  });

  testWidgets('Simple Mode: a stop that went through stays in its pill until closed (user, 2026-11-04: 6 s was too fast)',
      (tester) async {
    await pumpButton(tester, button: const EmergencyStopButton(compact: true), simpleMode: true, pills: true);
    fake.result = const EmergencyStopResult(
      alreadyInProgress: false,
      runsAborted: 1,
      exposureAborted: true,
      guidingStopped: true,
      parkRequested: true,
      flatPanelLightOff: true,
      failedRungs: [],
    );
    await activateWithoutPointer(tester);
    await activateWithoutPointer(tester);
    await tester.pumpAndSettle();
    await tester.pump(const Duration(minutes: 1));
    final pill = find.byKey(const Key('simple-message-0'));
    expect(pill, findsOneWidget, reason: 'a safety summary waits to be read');
    final container = ProviderScope.containerOf(tester.element(find.byType(EmergencyStopButton)));
    expect(container.read(simpleMessagesProvider).single.kind, SimpleMessageKind.warning,
        reason: 'amber: done, but the rig needs a look');
    await tester.tap(find.descendant(of: pill, matching: find.byTooltip('Dismiss')));
    await tester.pump();
    expect(pill, findsNothing);
  });

  testWidgets('Simple Mode: a stop with a failed rung is a red pill that outlives the 20 s error fade, until × '
      '(review, #1490: the fade had taken it)', (tester) async {
    await pumpButton(tester, button: const EmergencyStopButton(compact: true), simpleMode: true, pills: true);
    fake.result = const EmergencyStopResult(
      alreadyInProgress: false,
      runsAborted: 1,
      exposureAborted: true,
      guidingStopped: true,
      parkRequested: false,
      flatPanelLightOff: true,
      failedRungs: ['park'],
    );
    await activateWithoutPointer(tester);
    await activateWithoutPointer(tester);
    await tester.pumpAndSettle();
    await tester.pump(kSimpleErrorLife + const Duration(seconds: 1));
    final pill = find.byKey(const Key('simple-message-0'));
    expect(pill, findsOneWidget, reason: 'a failed rung waits to be read');
    final container = ProviderScope.containerOf(tester.element(find.byType(EmergencyStopButton)));
    expect(container.read(simpleMessagesProvider).single.kind, SimpleMessageKind.error, reason: 'red');
    await tester.tap(find.descendant(of: pill, matching: find.byTooltip('Dismiss')));
    await tester.pump();
    expect(pill, findsNothing);
  });

  testWidgets('Simple Mode: a stop that never reached the daemon is a red pill that waits to be read, past the '
      '20 s fade (ALP-5016)', (tester) async {
    await pumpButton(tester, button: const EmergencyStopButton(compact: true), simpleMode: true, pills: true);
    fake.error = Exception('connection refused');
    await activateWithoutPointer(tester);
    await activateWithoutPointer(tester);
    await tester.pumpAndSettle();
    await tester.pump(kSimpleErrorLife + const Duration(seconds: 1));
    final pill = find.byKey(const Key('simple-message-0'));
    expect(pill, findsOneWidget, reason: 'the stop did NOT happen: nobody may have read it yet');
    final container = ProviderScope.containerOf(tester.element(find.byType(EmergencyStopButton)));
    expect(container.read(simpleMessagesProvider).single.kind, SimpleMessageKind.error);
  });

  testWidgets('Simple Mode: the stop\'s message shows whole in its pill, every line (user, 2026-11-04: it was cut)',
      (tester) async {
    tester.view.physicalSize = const Size(1024, 768);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await pumpButton(tester, button: const EmergencyStopButton(compact: true), simpleMode: true, pills: true);
    fake.result = const EmergencyStopResult(
      alreadyInProgress: false,
      runsAborted: 1,
      exposureAborted: true,
      guidingStopped: true,
      parkRequested: true,
      flatPanelLightOff: true,
      failedRungs: ['park'],
    );
    await activateWithoutPointer(tester);
    await activateWithoutPointer(tester);
    await tester.pumpAndSettle();
    final pill = find.byKey(const Key('simple-message-0'));
    expect(pill, findsOneWidget);
    for (final p in tester.renderObjectList<RenderParagraph>(find.descendant(of: pill, matching: find.byType(RichText)))) {
      expect(p.didExceedMaxLines, isFalse, reason: '"${p.text.toPlainText()}" is cut short');
    }
  });

  group('Simple Mode: keyboard and screen readers press again, no dialog', () {
    testWidgets(
      'the first press arms it (PRESS AGAIN, announced), the second within 3 s stops',
      (tester) async {
        await pumpButton(
          tester,
          button: const EmergencyStopButton(compact: true),
          simpleMode: true,
        );
        await activateWithoutPointer(tester);
        await tester.pump();
        expect(
          find.text('Stop everything now?'),
          findsNothing,
          reason: 'no popup in Simple Mode',
        );
        expect(find.text('PRESS\nAGAIN'), findsOneWidget);
        expect(fake.calls, 0);
        await activateWithoutPointer(tester);
        await tester.pump();
        expect(fake.calls, 1);
        final container = ProviderScope.containerOf(
          tester.element(find.byType(EmergencyStopButton)),
        );
        expect(
          container.read(simpleMessagesProvider).single.title,
          startsWith('Emergency stop executed'),
          reason: 'the result is a pill',
        );
      },
    );

    testWidgets('left alone for 3 s it disarms and sends nothing', (
      tester,
    ) async {
      await pumpButton(
        tester,
        button: const EmergencyStopButton(compact: true),
        simpleMode: true,
      );
      await activateWithoutPointer(tester);
      await tester.pump(const Duration(seconds: 3, milliseconds: 100));
      expect(find.text('STOP'), findsOneWidget);
      await activateWithoutPointer(tester);
      await tester.pump();
      expect(fake.calls, 0, reason: 'that press only armed it again');
    });
  });

  group('EmergencyStopButton hold to confirm', () {
    testWidgets(
      'pressing shows Hold to confirm; holding 2 s stops with no dialog',
      (tester) async {
        await pumpButton(tester);
        final gesture = await tester.startGesture(
          tester.getCenter(find.byType(EmergencyStopButton)),
        );
        await tester.pump();
        expect(find.text('Hold to confirm'), findsOneWidget);
        await tester.pump(const Duration(milliseconds: 1000));
        expect(fake.calls, 0, reason: 'not yet: a 2 s hold');
        await tester.pump(const Duration(milliseconds: 1100));
        expect(fake.calls, 1);
        expect(
          find.text('Stop everything now?'),
          findsNothing,
          reason: 'the hold is the confirmation',
        );
        await gesture.up();
        await tester.pumpAndSettle();
        expect(find.textContaining('Emergency stop executed'), findsOneWidget);
      },
    );

    testWidgets('letting go early sends nothing and opens no dialog', (
      tester,
    ) async {
      await pumpButton(tester);
      final gesture = await tester.startGesture(
        tester.getCenter(find.byType(EmergencyStopButton)),
      );
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 1500));
      await gesture.up();
      await tester.pump(const Duration(seconds: 3));
      expect(fake.calls, 0);
      expect(find.text('Stop everything now?'), findsNothing);
      expect(
        find.text('Emergency Stop'),
        findsOneWidget,
        reason: 'back to its label',
      );
    });

    testWidgets('a right-click held 2 s does not stop', (tester) async {
      await pumpButton(tester);
      final gesture = await tester.startGesture(
        tester.getCenter(find.byType(EmergencyStopButton)),
        kind: PointerDeviceKind.mouse,
        buttons: kSecondaryMouseButton,
      );
      await tester.pump(const Duration(milliseconds: 2500));
      expect(fake.calls, 0);
      await gesture.up();
      await tester.pump();
    });
  });

  group('EmergencyStopButton (§35.3), keyboard / screen-reader path', () {
    testWidgets('cancel in the confirm dialog sends nothing', (tester) async {
      await pumpButton(tester);

      await activateWithoutPointer(tester);
      await tester.pumpAndSettle();
      expect(find.text('Stop everything now?'), findsOneWidget);

      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();

      expect(
        fake.calls,
        0,
        reason: 'the stop must never fire without confirmation',
      );
    });

    testWidgets('confirming posts the stop and reports the honest result', (
      tester,
    ) async {
      await pumpButton(tester);

      await activateWithoutPointer(tester);
      await tester.pumpAndSettle();
      await tester.tap(find.text('EMERGENCY STOP'));
      await tester.pumpAndSettle();

      expect(fake.calls, 1);
      expect(find.textContaining('Emergency stop executed'), findsOneWidget);
      expect(find.textContaining('1 sequence run(s) aborted'), findsOneWidget);
      expect(find.textContaining('mount told to park'), findsOneWidget);
    });

    testWidgets(
      'every attempted-but-failed rung is called out, not papered over',
      (tester) async {
        await pumpButton(tester);
        fake.result = const EmergencyStopResult(
          alreadyInProgress: false,
          runsAborted: 0,
          exposureAborted: false,
          guidingStopped: false,
          parkRequested: false,
          flatPanelLightOff: false,
          failedRungs: ['stop_guiding', 'park'],
        );

        await activateWithoutPointer(tester);
        await tester.pumpAndSettle();
        await tester.tap(find.text('EMERGENCY STOP'));
        await tester.pumpAndSettle();

        expect(
          find.textContaining('CHECK THE RIG'),
          findsOneWidget,
          reason: 'failed rungs must be loud — the user must go look',
        );
        expect(find.textContaining('MOUNT PARK FAILED'), findsOneWidget);
        expect(
          find.textContaining('GUIDING MAY STILL BE ACTIVE'),
          findsOneWidget,
          reason:
              'a hung guider is as dangerous as an unparked mount — '
              'no rung fails silently',
        );
      },
    );

    testWidgets('the summary says the camera cooler was switched off', (
      tester,
    ) async {
      await pumpButton(tester);
      fake.result = EmergencyStopResult.fromJson(const {
        'runs_aborted': 1,
        'park_requested': true,
        'cooler_off': true,
        'failed_rungs': <String>[],
      });

      await activateWithoutPointer(tester);
      await tester.pumpAndSettle();
      await tester.tap(find.text('EMERGENCY STOP'));
      await tester.pumpAndSettle();

      expect(find.textContaining('camera cooler off'), findsOneWidget);
    });
  });
}
