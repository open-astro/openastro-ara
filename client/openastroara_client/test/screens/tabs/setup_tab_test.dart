import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/screens/tabs/setup_tab.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/focus/guide_focus_state.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/widgets/focus/focusing_pane.dart';
import 'package:openastroara/widgets/imaging/polar_align_panel.dart';

/// Overridable live-state stub — same shape the polar align panel tests use.
class _StubLiveNotifier extends PolarAlignLiveNotifier {
  final PolarAlignLive initial;
  _StubLiveNotifier(this.initial);
  @override
  PolarAlignLive build() => initial;
}

class _StubAutofocus extends AutofocusLiveNotifier {
  final AutofocusLive initial;
  _StubAutofocus(this.initial);
  @override
  AutofocusLive build() => initial;
  @override
  Future<void> refresh() async {}
}

class _StubGuideFocus extends GuideFocusNotifier {
  @override
  GuideFocusLive build() => GuideFocusLive.idle;
  @override
  Future<void> refresh() async {}
}

class _ConfiguredPhd2Notifier extends Phd2SettingsNotifier {
  @override
  Phd2Settings build() => const Phd2Settings(
      guiderCamera: 'Alpaca Camera [rc91.lan:6800/0]',
      guideFocalLength: 240,
      guidePixelSize: 2.9);
}

Widget _harness({
  PolarAlignLive live = const PolarAlignLive(),
  bool guiderConfigured = false,
  AutofocusLive autofocus = AutofocusLive.idle,
}) {
  return ProviderScope(
    overrides: [
      polarAlignLiveProvider.overrideWith(() => _StubLiveNotifier(live)),
      autofocusLiveProvider.overrideWith(() => _StubAutofocus(autofocus)),
      guideFocusProvider.overrideWith(_StubGuideFocus.new),
      if (guiderConfigured)
        phd2SettingsProvider.overrideWith(_ConfiguredPhd2Notifier.new),
    ],
    child: const MaterialApp(home: Scaffold(body: SetupTab())),
  );
}

void main() {
  testWidgets('shows the Tonight checklist with all four steps', (t) async {
    await t.pumpWidget(_harness());
    await t.pump();
    expect(find.text('Tonight'), findsOneWidget);
    expect(find.text('Connect equipment'), findsWidgets); // row + pane title
    expect(find.text('Smart Focus'), findsOneWidget);
    expect(find.text('Main scope and guide camera'), findsOneWidget);
    expect(find.text('Polar align'), findsOneWidget);
    expect(find.text('Calibration frames'), findsOneWidget);
    // Default pane is the connect step.
    expect(find.text('Mount'), findsOneWidget);
    expect(find.text('Camera'), findsOneWidget);
  });

  testWidgets('selecting Focusing shows the focusing pane', (t) async {
    await t.pumpWidget(_harness());
    await t.pump();
    await t.tap(find.text('Smart Focus'));
    await t.pumpAndSettle();
    expect(find.byType(FocusingPane), findsOneWidget);
    expect(find.text('Main telescope'), findsOneWidget);
    expect(find.text('Guide camera'), findsOneWidget);
    expect(find.text('MAIN TELESCOPE'), findsOneWidget);
  });

  testWidgets('a focused session shows the in-focus subtitle', (t) async {
    await t.pumpWidget(_harness(
      autofocus: const AutofocusLive(
        focusedThisSession: true,
        run: AutofocusRun(state: AutofocusRunStates.complete, finalPosition: 9850, finalHfr: 1.42),
      ),
    ));
    await t.pump();
    expect(find.text('In focus — HFR 1.42 at 9850'), findsOneWidget);
  });

  testWidgets('selecting Polar align shows the bullseye panel', (t) async {
    await t.pumpWidget(_harness());
    await t.pump();
    await t.tap(find.text('Polar align'));
    await t.pumpAndSettle();
    expect(find.byType(PolarAlignPanel), findsOneWidget);
  });

  testWidgets('aligned session shows the green-check subtitle', (t) async {
    await t.pumpWidget(_harness(
      live: const PolarAlignLive(
        phase: PolarAlignStates.stopped,
        zone: 'green',
        totalErrorArcmin: 0.8,
      ),
    ));
    await t.pump();
    expect(find.text('Aligned — 0.8′ from the pole'), findsOneWidget);
  });

  testWidgets('unaligned session shows the pending subtitle', (t) async {
    await t.pumpWidget(_harness());
    await t.pump();
    expect(find.text('Not aligned this session'), findsOneWidget);
  });

  testWidgets('selecting Calibration frames shows the shortcut pane',
      (t) async {
    await t.pumpWidget(_harness());
    await t.pump();
    await t.tap(find.text('Calibration frames'));
    await t.pumpAndSettle();
    expect(find.text('Open Calibration'), findsOneWidget);
  });

  testWidgets('unconfigured guider gets the first-run wizard callout',
      (t) async {
    await t.pumpWidget(_harness());
    await t.pump();
    expect(find.text('Set up the guider…'), findsOneWidget);
    expect(find.text('Re-run guider wizard…'), findsNothing);
  });

  testWidgets('configured guider demotes the wizard to a re-run link',
      (t) async {
    await t.pumpWidget(_harness(guiderConfigured: true));
    await t.pump();
    expect(find.text('Re-run guider wizard…'), findsOneWidget);
    expect(find.text('Set up the guider…'), findsNothing);
  });
}
