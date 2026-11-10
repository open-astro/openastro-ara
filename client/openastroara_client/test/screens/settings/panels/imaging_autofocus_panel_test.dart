import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/imaging_autofocus_panel.dart';
import 'package:openastroara/services/autofocus_api.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/settings/registry.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/widgets/settings/editable_field.dart';

// §59.8 — the Automatic step size switch and the Advanced disclosure that
// hides the raw sweep knobs. No server: the panel keeps the notifier's
// defaults instead of hydrating.
Future<void> _pump(WidgetTester tester, {AutofocusApi? api}) async {
  // Wide desktop surface + half text scale, as the sibling panel tests do:
  // Ahem renders every glyph at full point size and the fixed labels overflow.
  tester.view.physicalSize = const Size(1600, 1600);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  // The fixed-width number fields still overflow by a few px under Ahem; only
  // that layout complaint is ignored (the session_notifications panel test's approach).
  final prior = FlutterError.onError;
  FlutterError.onError = (d) {
    if (!d.exceptionAsString().contains('overflowed')) prior?.call(d);
  };
  addTearDown(() => FlutterError.onError = prior);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        activeServerProvider.overrideWithValue(null),
        autofocusApiProvider.overrideWithValue(api),
      ],
      child: MaterialApp(
        builder: (context, child) => MediaQuery(
          data: MediaQuery.of(context)
              .copyWith(textScaler: const TextScaler.linear(0.5)),
          child: child!,
        ),
        home: const Scaffold(body: ImagingAutofocusPanel()),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

Finder _autoSwitch() => find.descendant(
      of: find.widgetWithText(SettingsSwitchRow, 'Automatic step size'),
      matching: find.byType(Switch),
    );

class _RecalibrateApi implements AutofocusApi {
  int recalibrates = 0;
  @override
  Future<void> recalibrate() async => recalibrates++;
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

void main() {
  testWidgets('Reset focus calibration asks the rig to forget it (#1543)', (tester) async {
    final api = _RecalibrateApi();
    await _pump(tester, api: api);
    await tester.ensureVisible(find.byKey(const Key('af-reset-calibration')));
    await tester.pump();

    await tester.tap(find.byKey(const Key('af-reset-calibration')));
    await tester.pumpAndSettle();
    expect(api.recalibrates, 0, reason: 'it asks first');

    await tester.tap(find.byKey(const Key('af-reset-calibration-confirm')));
    await tester.pumpAndSettle();
    expect(api.recalibrates, 1);
  });

  test('settings search finds the reset (#1543)', () {
    final hit = settingsRegistry.singleWhere((s) => s.id == 'img.autofocus.reset_calibration');
    expect(hit.keywords, containsAll(['reset', 'calibration']));
  });

  testWidgets('with no rig the reset is off', (tester) async {
    await _pump(tester);
    expect(tester.widget<OutlinedButton>(find.byKey(const Key('af-reset-calibration'))).onPressed, isNull);
  });

  testWidgets('automatic step size is on by default and the raw knobs sit behind Advanced', (tester) async {
    await _pump(tester);

    expect(tester.widget<Switch>(_autoSwitch()).value, isTrue);
    expect(find.text('First sweep wide, then sized from the measured V-curve'), findsOneWidget);
    expect(find.text('Advanced'), findsOneWidget);
    expect(find.textContaining('step size (focuser steps)'), findsNothing,
        reason: 'collapsed: the step knobs are not part of the everyday panel');

    await tester.tap(find.text('Advanced'));
    await tester.pumpAndSettle();
    expect(find.text('Number of steps (3..31)'), findsOneWidget);
    expect(find.text('First-sweep step size (focuser steps)'), findsOneWidget);
  });

  testWidgets('switching automatic off relabels the step size as the one every sweep uses', (tester) async {
    await _pump(tester);
    await tester.tap(find.text('Advanced'));
    await tester.pumpAndSettle();

    await tester.tap(_autoSwitch());
    await tester.pumpAndSettle();

    final element = tester.element(find.byType(ImagingAutofocusPanel));
    expect(ProviderScope.containerOf(element).read(autofocusSettingsProvider).stepSizeAuto, isFalse);
    expect(find.text('Every sweep uses the step size under Advanced'), findsOneWidget);
    expect(find.text('Step size (focuser steps)'), findsOneWidget);
    expect(find.text('First-sweep step size (focuser steps)'), findsNothing);
  });
}
