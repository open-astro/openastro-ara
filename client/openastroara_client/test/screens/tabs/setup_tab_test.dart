import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/screens/tabs/setup_tab.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/widgets/imaging/polar_align_panel.dart';

/// Overridable live-state stub — same shape the polar align panel tests use.
class _StubLiveNotifier extends PolarAlignLiveNotifier {
  final PolarAlignLive initial;
  _StubLiveNotifier(this.initial);
  @override
  PolarAlignLive build() => initial;
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
}) {
  return ProviderScope(
    overrides: [
      polarAlignLiveProvider.overrideWith(() => _StubLiveNotifier(live)),
      if (guiderConfigured)
        phd2SettingsProvider.overrideWith(_ConfiguredPhd2Notifier.new),
    ],
    child: const MaterialApp(home: Scaffold(body: SetupTab())),
  );
}

void main() {
  testWidgets('shows the Tonight checklist with all three steps', (t) async {
    await t.pumpWidget(_harness());
    await t.pump();
    expect(find.text('Tonight'), findsOneWidget);
    expect(find.text('Connect equipment'), findsWidgets); // row + pane title
    expect(find.text('Polar align'), findsOneWidget);
    expect(find.text('Calibration frames'), findsOneWidget);
    // Default pane is the connect step.
    expect(find.text('Mount'), findsOneWidget);
    expect(find.text('Camera'), findsOneWidget);
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
