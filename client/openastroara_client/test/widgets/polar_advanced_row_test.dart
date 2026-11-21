import 'dart:async';
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
import 'package:openastroara/widgets/imaging/polar_align_panel.dart';

import '../support/imaging_sections.dart' show staleSave;
import '../support/polar_align_section.dart';

/// The routine's fake Polar Align client: no routine runs.
class _Api implements PolarAlignClient {
  @override
  Future<PolarAlignPreflight?> preflight({String? stage}) async => null;
  @override
  Future<PolarAlignStartPlan> startPlan({String? stage}) async =>
      PolarAlignStartPlan(stage: stage, direction: PolarAlignDirections.east);
  @override
  Future<PolarAlignStatus?> getStatus() async => null;

  @override
  Future<void> start(PolarAlignStartPlan plan) async {}
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
const _seed = Key('polar-advanced-seed');
const _check = Key('polar-advanced-check-step');
const _coarse = Key('polar-advanced-coarse');

/// The Start card and, under it, the Settings panel: one rig, one module.
Future<FakePolarAlignSection> _pump(
  WidgetTester tester, {
  FakePolarAlignSection? rig,
  PolarAlignLive live = const PolarAlignLive(),
  bool open = true,
}) async {
  tester.view.physicalSize = const Size(1200, 2400);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final api = rig ?? FakePolarAlignSection();
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        ...polarAlignSectionOverrides(api),
        polarAlignApiProvider.overrideWithValue(_Api()),
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
      expect(find.byKey(const Key('polar-advanced-use-home')), findsNothing, reason: '#2048: no Home switch');
    },
  );

  testWidgets(
    'an edit in the row saves at once and the Settings panel shows it (#1736)',
    (tester) async {
      final api = await _pump(tester);
      await _enter(tester, _seed, '25');
      expect(api.puts.single.seedRotationDeg, 25.0);
      // Settings rows, in order: tolerance, turn, verify step, rough exposure.
      final settingsFields = find.descendant(
        of: find.byType(ImagingPolarPanel),
        matching: find.byType(TextField),
      );
      expect(
        tester.widget<TextField>(settingsFields.at(1)).controller!.text,
        '25',
      );
    },
  );

  testWidgets(
    'collapsed: the row sums up the rig\'s turn, step and exposure (#1736)',
    (tester) async {
      final rig = FakePolarAlignSection(
        initial: rigPolarAlign.copyWith(
          seedRotationDeg: 25,
          checkStepDeg: 20,
          coarseExposureSeconds: 5,
        ),
      );
      await _pump(tester, rig: rig, open: false);
      final row = find.byKey(const Key('polar-advanced-row'));
      expect(
        find.descendant(
          of: row,
          matching: find.text('25° turn · 20° step · 5 s'),
        ),
        findsOneWidget,
      );
    },
  );

  testWidgets(
    'collapsed after a failed load: no summary, never the client\'s defaults (#1736)',
    (tester) async {
      final rig = FakePolarAlignSection(initial: null);
      await _pump(tester, rig: rig, open: false);
      final row = find.byKey(const Key('polar-advanced-row'));
      expect(
        find.descendant(of: row, matching: find.textContaining('° turn')),
        findsNothing,
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
    expect(api.puts.single.checkStepDeg, 20.0);
    expect(_text(tester, _check), '20');
  });

  testWidgets(
    'a failed save puts the row\'s field back to the rig\'s value and says so (#1736)',
    (tester) async {
      final rig = FakePolarAlignSection()..refuse = StateError('rig said no');
      await _pump(tester, rig: rig);
      await _enter(tester, _coarse, '9');
      expect(rig.sent, hasLength(1));
      expect(rig.puts, isEmpty);
      expect(_text(tester, _coarse), '3');
      expect(
        find.byKey(const Key('polar-advanced-save-error')),
        findsOneWidget,
      );
    },
  );

  testWidgets(
    "a knob refused as stale shows the rig's copy again, and the next edit saves (ADR 0015)",
    (tester) async {
      final rig = await _pump(tester);
      final loads = rig.loads;
      rig.refuse = staleSave();
      await _enter(tester, _coarse, '9');
      expect(rig.puts, isEmpty);
      expect(rig.loads, loads + 1, reason: "the rig's copy is loaded again");
      expect(_text(tester, _coarse), '3', reason: 'the rig still uses 3 s');

      rig.refuse = null;
      await _enter(tester, _coarse, '9');
      expect(rig.puts.single.coarseExposureSeconds, 9.0);
    },
  );

  testWidgets(
    'seed 4 and check 31 are refused in the field with the range, and nothing is sent (#1736)',
    (tester) async {
      final api = await _pump(tester);
      await _enter(tester, _seed, '4');
      await _enter(tester, _check, '31');
      expect(api.sent, isEmpty);
      expect(find.textContaining('5 to 30'), findsWidgets);
      expect(_text(tester, _seed), '4');
      expect(find.text('Out of range: use 5 to 30'), findsNWidgets(2));
    },
  );

  testWidgets(
    'not loaded: Retry instead of the knobs, nothing saved; Retry loads them (#1736)',
    (tester) async {
      final rig = FakePolarAlignSection(initial: null);
      await _pump(tester, rig: rig);
      final advanced = find.descendant(
        of: find.byKey(const Key('polar-advanced-row')),
        matching: find.byKey(const Key('section-retry')),
      );
      expect(advanced, findsOneWidget);
      expect(find.byKey(_seed), findsNothing);
      expect(rig.sent, isEmpty);
      rig.initial = rigPolarAlign;
      await tester.tap(advanced);
      await tester.pumpAndSettle();
      expect(find.byKey(_seed), findsOneWidget);
    },
  );

  testWidgets('Start waits while a knob save is in flight (#1736)', (
    tester,
  ) async {
    final rig = FakePolarAlignSection()..gate = Completer<void>();
    await _pump(tester, rig: rig);
    const start = Key('polar-align-start');
    bool enabled() =>
        tester.widget<ButtonStyleButton>(find.byKey(start)).enabled;
    expect(enabled(), isTrue);
    await tester.ensureVisible(find.byKey(_check));
    await tester.enterText(find.byKey(_check), '20');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pump();
    expect(rig.sent, hasLength(1));
    expect(enabled(), isFalse);
    rig.gate!.complete();
    await tester.pumpAndSettle();
    expect(enabled(), isTrue);
  });

  testWidgets(
    'each of the three controls has its knob name for a screen reader (#1736)',
    (tester) async {
      final handle = tester.ensureSemantics();
      await _pump(tester);
      final l10n = AppLocalizations.of(tester.element(find.byKey(_toggle)))!;
      for (final (key, name) in [
        (_seed, l10n.polarKnobTurn),
        (_check, l10n.polarKnobVerifyStep),
        (_coarse, l10n.polarKnobRoughExposure),
      ]) {
        expect(
          find.descendant(
            of: find.byKey(const Key('polar-advanced-row')),
            matching: find.bySemanticsLabel(name),
          ),
          findsWidgets,
          reason: name,
        );
        expect(
          tester.getSemantics(find.byKey(key)).label,
          contains(name),
          reason: name,
        );
      }
      expect(
        tester
            .getSemantics(find.byKey(const Key('polar-align-exposure')))
            .label,
        contains('Exposure'),
      );
      handle.dispose();
    },
  );

  testWidgets(
    'a refusal goes when the Settings panel saves a new value (#1736)',
    (tester) async {
      final api = await _pump(tester);
      await _enter(tester, _seed, '4');
      expect(find.text('Out of range: use 5 to 30'), findsOneWidget);
      final settingsFields = find.descendant(
        of: find.byType(ImagingPolarPanel),
        matching: find.byType(TextField),
      );
      await tester.ensureVisible(settingsFields.at(1));
      await tester.enterText(settingsFields.at(1), '25');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();
      expect(api.puts.single.seedRotationDeg, 25.0);
      expect(_text(tester, _seed), '25');
      expect(find.textContaining('Out of range'), findsNothing);
    },
  );
}
