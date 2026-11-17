import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/imaging_polar_panel.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/state/saved_server_state.dart'
    show activeServerProvider;
import 'package:openastroara/widgets/imaging/polar_advanced_row.dart';
import 'package:openastroara/widgets/imaging/polar_align_panel.dart';

/// The fake Polar Align client of the Settings panel's tests: scripted
/// settings, every PUT recorded.
class _Api implements PolarAlignClient {
  PolarAlignSettings settings = const PolarAlignSettings();
  final saved = <PolarAlignSettings>[];
  Object? getError;
  Object? putError;
  @override
  Future<PolarAlignStatus?> getStatus() async => null;
  @override
  Future<PolarAlignSettings> getSettings() async {
    if (getError != null) throw getError!;
    return settings;
  }

  @override
  Future<PolarAlignSettings> putSettings(PolarAlignSettings next) async {
    saved.add(next);
    if (putError != null) throw putError!;
    return settings = next;
  }

  @override
  Future<void> start({String? stage}) async {}
  @override
  Future<void> next() async {}
  @override
  Future<void> stop() async {}
  @override
  Future<void> complete() async {}
  @override
  Future<void> requestCapture() async {}
  @override
  Future<Uint8List?> getLiveFrame() async => null;
  @override
  void close() {}
}

class _Live extends PolarAlignLiveNotifier {
  _Live(this.live);
  final PolarAlignLive live;
  @override
  PolarAlignLive build() => live;
}

const _toggle = Key('polar-advanced-toggle');
const _home = Key('polar-advanced-use-home');
const _seed = Key('polar-advanced-seed');
const _check = Key('polar-advanced-check-step');
const _coarse = Key('polar-advanced-coarse');

/// The Start card and, under it, the Settings panel: one rig, one module.
Future<_Api> _pump(
  WidgetTester tester, {
  _Api? rig,
  PolarAlignLive live = const PolarAlignLive(),
  bool open = true,
}) async {
  tester.view.physicalSize = const Size(1200, 2400);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final api = rig ?? _Api();
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        polarAlignApiProvider.overrideWithValue(api),
        polarAlignLiveProvider.overrideWith(() => _Live(live)),
        activeServerProvider.overrideWithValue(
          const AraServer(hostname: 'rig', port: 1),
        ),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const Scaffold(
          body: SingleChildScrollView(
            child: Column(
              children: [
                PolarAlignPanel(),
                SizedBox(height: 900, child: ImagingPolarPanel()),
              ],
            ),
          ),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
  if (open) {
    await tester.ensureVisible(find.byKey(_toggle));
    await tester.tap(find.byKey(_toggle));
    await tester.pumpAndSettle();
  }
  return api;
}

Future<void> _enter(WidgetTester tester, Key key, String text) async {
  await tester.ensureVisible(find.byKey(key));
  await tester.enterText(find.byKey(key), text);
  await tester.testTextInput.receiveAction(TextInputAction.done);
  await tester.pumpAndSettle();
}

String _text(WidgetTester tester, Key key) => tester
    .widget<TextField>(
      find.descendant(of: find.byKey(key), matching: find.byType(TextField)),
    )
    .controller!
    .text;

void main() {
  testWidgets(
    'collapsed: the Advanced row shows no knob until it is opened (#1736)',
    (tester) async {
      await _pump(tester, open: false);
      expect(find.byKey(_toggle), findsOneWidget);
      expect(find.byKey(_seed), findsNothing);
      await tester.tap(find.byKey(_toggle));
      await tester.pumpAndSettle();
      expect(find.byKey(_seed), findsOneWidget);
      expect(find.byKey(_check), findsOneWidget);
      expect(find.byKey(_coarse), findsOneWidget);
      expect(find.byKey(_home), findsOneWidget);
    },
  );

  testWidgets(
    'an edit in the row saves at once and the Settings panel shows it (#1736)',
    (tester) async {
      final api = await _pump(tester);
      await _enter(tester, _seed, '45');
      expect(api.saved.single.seedRotationDeg, 45.0);
      // Settings rows, in order: tolerance, turn, verify step, rough exposure.
      final settingsFields = find.descendant(
        of: find.byType(ImagingPolarPanel),
        matching: find.byType(TextField),
      );
      expect(
        tester.widget<TextField>(settingsFields.at(1)).controller!.text,
        '45',
      );
    },
  );

  testWidgets('an edit in the Settings panel shows in the row (#1736)', (
    tester,
  ) async {
    final api = await _pump(tester);
    final settingsFields = find.descendant(
      of: find.byType(ImagingPolarPanel),
      matching: find.byType(TextField),
    );
    await tester.ensureVisible(settingsFields.at(2));
    await tester.enterText(settingsFields.at(2), '20');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pumpAndSettle();
    expect(api.saved.single.checkStepDeg, 20.0);
    expect(_text(tester, _check), '20');
  });

  testWidgets('the Home switch in the row saves use_home (#1736)', (
    tester,
  ) async {
    final api = await _pump(tester);
    await tester.ensureVisible(find.byKey(_home));
    await tester.tap(find.byKey(_home));
    await tester.pumpAndSettle();
    expect(api.saved.single.useHome, isFalse);
  });

  testWidgets(
    'a failed save puts the row\'s field back to the rig\'s value and says so (#1736)',
    (tester) async {
      final rig = _Api()..putError = StateError('rig said no');
      await _pump(tester, rig: rig);
      await _enter(tester, _coarse, '9');
      expect(rig.saved, hasLength(1));
      expect(_text(tester, _coarse), '3');
      expect(
        find.byKey(const Key('polar-advanced-save-error')),
        findsOneWidget,
      );
    },
  );

  testWidgets(
    'seed 4 and check 61 are refused in the field with the range, and nothing is sent (#1736)',
    (tester) async {
      final api = await _pump(tester);
      await _enter(tester, _seed, '4');
      await _enter(tester, _check, '61');
      expect(api.saved, isEmpty);
      expect(find.textContaining('5 to 120'), findsWidgets);
      expect(find.textContaining('5 to 60'), findsWidgets);
      expect(_text(tester, _seed), '4');
      expect(find.text('Out of range: use 5 to 120'), findsOneWidget);
      expect(find.text('Out of range: use 5 to 60'), findsOneWidget);
    },
  );

  testWidgets(
    'not loaded: Retry instead of the knobs, nothing saved; Retry loads them (#1736)',
    (tester) async {
      final rig = _Api()..getError = StateError('rig gone');
      await _pump(tester, rig: rig);
      final advanced = find.descendant(
        of: find.byKey(const Key('polar-advanced-row')),
        matching: find.byKey(const Key('section-retry')),
      );
      expect(advanced, findsOneWidget);
      expect(find.byKey(_seed), findsNothing);
      expect(find.byKey(_home), findsNothing);
      expect(rig.saved, isEmpty);
      rig.getError = null;
      await tester.tap(advanced);
      await tester.pumpAndSettle();
      expect(find.byKey(_seed), findsOneWidget);
    },
  );

  testWidgets(
    'while the routine runs the open row says a change applies from the next stage or frame (#1736)',
    (tester) async {
      final api = _Api();
      for (final live in [
        const PolarAlignLive(),
        const PolarAlignLive(phase: PolarAlignStates.adjusting),
      ]) {
        await tester.pumpWidget(
          ProviderScope(
            key: UniqueKey(),
            overrides: [
              polarAlignApiProvider.overrideWithValue(api),
              polarAlignLiveProvider.overrideWith(() => _Live(live)),
            ],
            child: const MaterialApp(home: Scaffold(body: PolarAdvancedRow())),
          ),
        );
        await tester.pumpAndSettle();
        await tester.tap(find.byKey(_toggle));
        await tester.pumpAndSettle();
        expect(
          find.byKey(const Key('polar-advanced-running-note')),
          live.isActive ? findsOneWidget : findsNothing,
        );
      }
    },
  );
}
