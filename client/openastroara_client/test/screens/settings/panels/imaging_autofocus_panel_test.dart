import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/imaging_autofocus_panel.dart';
import 'package:openastroara/services/autofocus_api.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/settings/registry.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/advanced_settings_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/focus/focus_presets.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/state/settings/panel_save_registry.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/widgets/settings/editable_field.dart';

import '../../../support/fake_section_rig.dart';
import '../../../support/imaging_sections.dart';

/// The profile's autofocus section the last pump loaded (no HTTP).
late FakeAutofocusSection _af;

// §59.8 — the Automatic step size switch; #1544 — the rows the Advanced
// toggle and a settings search hit show. The section loads the rig's copy
// ([loaded] false: a rig that does not answer).
Future<void> _pump(
  WidgetTester tester, {
  AutofocusApi? api,
  bool advanced = true,
  String? searchHit,
  bool loaded = true,
}) async {
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
        profileApiProvider.overrideWithValue(FakeSectionRig({}).api()),
        autofocusSettingsProvider.overrideWith(
          () => _af = FakeAutofocusSection(
            initial: loaded ? const AutofocusSettings() : null,
          ),
        ),
        autofocusApiProvider.overrideWithValue(api),
        // #2036: the switch rules in Simple Mode only.
        simpleModeActiveProvider.overrideWithValue(true),
        advancedSettingsProvider.overrideWith(() => _Advanced(advanced)),
        // A ⌘K hit picked before the panel opened (command_palette.dart).
        if (searchHit != null)
          highlightedSettingProvider.overrideWith(() => _Hit(searchHit)),
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

class _Advanced extends AdvancedSettings {
  _Advanced(this.on);
  final bool on;
  @override
  Future<bool> build() async => on;
}

class _Hit extends HighlightedSettingNotifier {
  _Hit(this.id);
  final String id;
  @override
  String? build() => id;
}

class _StepApi implements AutofocusApi {
  @override
  Future<AutofocusStepSize> stepSize() async =>
      const AutofocusStepSize(stepSize: 26, source: 'cfz');
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

class _RecalibrateApi implements AutofocusApi {
  int recalibrates = 0;
  @override
  Future<void> recalibrate() async => recalibrates++;
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

void main() {
  testWidgets('Reset focus calibration asks the rig to forget it (#1543)', (
    tester,
  ) async {
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
    final hit = settingsRegistry.singleWhere(
      (s) => s.id == 'img.autofocus.reset_calibration',
    );
    expect(hit.keywords, containsAll(['reset', 'calibration']));
  });

  testWidgets('with no rig the reset is off', (tester) async {
    await _pump(tester);
    expect(
      tester
          .widget<OutlinedButton>(find.byKey(const Key('af-reset-calibration')))
          .onPressed,
      isNull,
    );
  });

  testWidgets(
    'automatic step size is on by default; the raw knobs show with the global Advanced toggle (#1544)',
    (tester) async {
      await _pump(tester);
      expect(tester.widget<Switch>(_autoSwitch()).value, isTrue);
      expect(
        find.text('First sweep wide, then sized from the measured V-curve'),
        findsOneWidget,
      );
      expect(find.text('Number of steps (3..31)'), findsOneWidget);
      expect(
        find.text('First-sweep step size (focuser steps)'),
        findsOneWidget,
      );
    },
  );

  testWidgets(
    'with Advanced off only the Simple tier shows: autofocus on/off, Keep focus sharp, AF filter (#1544)',
    (tester) async {
      await _pump(tester, advanced: false);
      expect(find.text('Autofocus during plans'), findsOneWidget);
      expect(find.text('Keep focus sharp'), findsOneWidget);
      expect(find.text('Filter for AF'), findsOneWidget);
      for (final hidden in [
        'Method',
        'Automatic step size',
        'Exposure time (s)',
        'Binning',
        'After temp delta (°C)',
        'Every N hours (0 = off)',
        'Number of steps (3..31)',
      ]) {
        expect(
          find.text(hidden),
          findsNothing,
          reason: '$hidden is Advanced tier',
        );
      }
    },
  );

  testWidgets(
    'a search hit on an Advanced setting shows it with the toggle off (#1544)',
    (tester) async {
      await _pump(tester, advanced: false, searchHit: 'img.autofocus.binning');
      expect(find.text('Binning'), findsOneWidget);
      expect(find.text('Algorithm'), findsOneWidget, reason: 'its section');
      expect(find.text('Method'), findsNothing, reason: 'only the hit shows');

      // A second hit while the panel is open.
      final c = ProviderScope.containerOf(
        tester.element(find.byType(ImagingAutofocusPanel)),
      );
      c
          .read(highlightedSettingProvider.notifier)
          .highlight('img.autofocus.method');
      await tester.pump();
      expect(find.text('Method'), findsOneWidget);

      // The highlight fades after 2 s; the rows stay while the panel is open.
      await tester.pump(const Duration(seconds: 3));
      expect(find.text('Binning'), findsOneWidget);
      expect(find.text('Method'), findsOneWidget);
      expect(find.text('Telescope type'), findsNothing);
      expect(
        c.read(advancedSettingsProvider).value,
        isFalse,
        reason: 'the saved toggle is left alone',
      );
    },
  );

  testWidgets(
    'Keep focus sharp here sets the four triggers, and each trigger names the preset (#1544)',
    (tester) async {
      await _pump(tester);
      final c = ProviderScope.containerOf(
        tester.element(find.byType(ImagingAutofocusPanel)),
      );
      expect(find.text('Keep focus sharp: Normal'), findsNWidgets(4));
      c
          .read(autofocusSettingsProvider.notifier)
          .applyKeepFocusSharp(KeepFocusSharp.tight);
      await tester.pumpAndSettle();
      expect(find.text('Keep focus sharp: Tight'), findsNWidgets(4));
      c.read(autofocusSettingsProvider.notifier).setTriggerTempDeltaC(7);
      await tester.pumpAndSettle();
      expect(find.text('Keep focus sharp: Custom'), findsNWidgets(4));
    },
  );

  testWidgets(
    'the step size shows what the daemon derives next to the stored value (#1544)',
    (tester) async {
      await _pump(tester, api: _StepApi());
      expect(
        find.text('Derived: 26 steps, from half the critical focus zone'),
        findsOneWidget,
      );
    },
  );

  testWidgets(
    'switching automatic off relabels the step size as the one every sweep uses',
    (tester) async {
      await _pump(tester);

      await tester.tap(_autoSwitch());
      await tester.pumpAndSettle();

      final element = tester.element(find.byType(ImagingAutofocusPanel));
      expect(
        ProviderScope.containerOf(element)
            .read(autofocusSettingsProvider)
            .value!
            .stepSizeAuto,
        isFalse,
      );
      expect(find.text('Every sweep uses the step size below'), findsOneWidget);
      expect(find.text('Step size (focuser steps)'), findsOneWidget);
      expect(find.text('First-sweep step size (focuser steps)'), findsNothing);
    },
  );

  testWidgets(
    'Save with the section not loaded from the rig writes nothing and says so (ADR 0015)',
    (tester) async {
      await _pump(tester, api: _RecalibrateApi(), loaded: false);
      expect(find.textContaining('Not loaded'), findsOneWidget);
      expect(find.text('Method'), findsNothing, reason: 'no default shows');
      expect(
        find.byKey(const Key('af-reset-calibration')),
        findsOneWidget,
        reason: 'the reset is not the profile section',
      );
      final panel = tester.state(
        find.byType(ImagingAutofocusPanel),
      ) as PanelSaveRegistration;
      await panel.panelSave();
      await tester.pump();
      expect(_af.puts, isEmpty);
      expect(
        find.textContaining('Settings not loaded from the rig'),
        findsWidgets,
      );
    },
  );

  testWidgets(
    'Autofocus: Save sends the edit made on the rig copy (ADR 0015)',
    (tester) async {
      await _pump(tester);
      await tester.tap(_autoSwitch());
      await tester.pump();
      final panel = tester.state(
        find.byType(ImagingAutofocusPanel),
      ) as PanelSaveRegistration;
      await panel.panelSave();
      await tester.pump();
      expect(_af.puts.single.stepSizeAuto, isFalse);
      expect(find.text('Saved.'), findsOneWidget);
    },
  );
}
