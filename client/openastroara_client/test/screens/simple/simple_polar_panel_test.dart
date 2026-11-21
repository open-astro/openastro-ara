import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/imaging/polar_align_readout.dart' show BullseyePainter;
import 'package:openastroara/screens/simple/simple_preflight.dart';
import 'package:openastroara/l10n/l10n.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/screens/simple/simple_polar_panel.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart' show SimpleShutterButton;
import 'package:openastroara/services/refusal.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/imaging/capture_progress_state.dart';
import 'package:openastroara/state/polar_align/polar_align_routine_state.dart' show polarAlignActionProvider;
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart' show Tagged;

import 'package:openastroara/state/settings/polar_align_settings_state.dart';

import '../../support/imaging_sections.dart' show staleSave;
import '../../support/polar_align_section.dart';

/// The profile's polar-align section (ADR 0015), each save sent also in [calls].
class _Section extends FakePolarAlignSection {
  // In from the rig from the first frame: these tests are about the routine.
  _Section(this.calls) : super(loaded: true);
  final List<String> calls;
  @override
  Future<Tagged<PolarAlignSettings>> send(ProfileApi api, PolarAlignSettings value, String? etag) {
    calls.add('save ${value.exposureSeconds} ${value.loopMode}');
    return super.send(api, value, etag);
  }
}

class _Api implements PolarAlignClient {
  @override
  Future<PolarAlignPreflight?> preflight({String? stage, bool handTurn = false}) async => null;
  @override
  Future<PolarAlignStartPlan> startPlan({String? stage, bool handTurn = false}) async =>
      PolarAlignStartPlan(stage: stage, direction: PolarAlignDirections.east);
  final calls = <String>[];
  late final section = _Section(calls);

  /// The stage of each start the user confirmed (null: the default measurement).
  final stages = <String?>[];

  /// The rig's copy of the profile's polar-align section.
  PolarAlignSettings get settings => section.initial!;
  set settings(PolarAlignSettings s) => section.initial = s;
  @override
  Future<PolarAlignStatus?> getStatus() async => null;
  @override
  Future<void> start(PolarAlignStartPlan plan) async {
    calls.add('start');
    stages.add(plan.stage);
  }
  @override
  Future<void> stop() async => calls.add('stop');
  @override
  Future<void> complete() async => calls.add('complete');
  @override
  Future<void> requestCapture() async => calls.add('capture');
  @override
  Future<Uint8List?> getLiveFrame() async => null;
  @override
  void close() {}
}

class _FailingApi extends _Api {
  @override
  Future<void> start(PolarAlignStartPlan plan) async => throw StateError('no camera');
}

/// A rig whose mount is parked: the start plan is refused (#2048), so no confirm step opens.
class _ParkedApi extends _Api {
  @override
  Future<PolarAlignStartPlan> startPlan({String? stage, bool handTurn = false}) async => throw const Refusal(
        cause: RefusalCause.polarAlignMountParked,
        reason: 'polar-align-mount-parked',
        detail: 'Mount is parked — unpark it and point near the pole first',
        status: 409,
      );
}

/// A start that waits for [gate]: the shutter's call in flight, then refused.
class _SlowFailingApi extends _Api {
  final gate = Completer<void>();
  @override
  Future<void> start(PolarAlignStartPlan plan) async {
    await gate.future;
    throw StateError('no camera');
  }
}

class _SimplePrefs extends SimpleModePrefsService {
  @override
  Future<bool> load() async => true;
  @override
  Future<void> save(bool enabled) async {}
}

class _Live extends PolarAlignLiveNotifier {
  _Live(this.live);
  final PolarAlignLive live;
  @override
  PolarAlignLive build() => live;
  void frame(PolarAlignLive next) => state = next;
}

const _adjusting = PolarAlignLive(
  phase: PolarAlignStates.adjusting,
  altErrorArcmin: 3.2,
  azErrorArcmin: 14.1,
  totalErrorArcmin: 14.4,
  zone: 'yellow',
);

Future<_Api?> _pump(WidgetTester tester, PolarAlignLive live,
    {bool server = true,
    String loopMode = PolarAlignLoopModes.loop,
    double? tolerance,
    PolarAlignSettings rig = rigPolarAlign,
    Widget child = const SimplePolarPanel()}) async {
  final api = server
      ? (_Api()
        ..settings = tolerance == null
            ? rig.copyWith(loopMode: loopMode)
            : rig.copyWith(loopMode: loopMode, targetToleranceArcmin: tolerance))
      : null;
  await tester.pumpWidget(ProviderScope(
    overrides: [
      if (api != null) ...polarAlignSectionOverrides(api.section) else profileApiProvider.overrideWithValue(null),
      polarAlignApiProvider.overrideWithValue(api),
      polarAlignLiveProvider.overrideWith(() => _Live(live)),
    ],
    child: MaterialApp(home: Scaffold(body: Center(child: child))),
  ));
  await tester.pumpAndSettle();
  return api;
}

void main() {
  test('simplePolarSummary: the settings, tight enough for the 60 px chip', () {
    final en = lookupAppLocalizations(const Locale('en'));
    expect(simplePolarSummary(rigPolarAlign, en), '1 s\nLoop');
    expect(simplePolarSummary(rigPolarAlign.copyWith(exposureSeconds: 45, loopMode: PolarAlignLoopModes.single), en),
        '45 s\nSingle');
    expect(simplePolarSummary(null, en), '', reason: 'not loaded from the rig: no made-up setup');
  });

  test('simplePolarSideText names the side of the pole in the chosen language', () {
    final en = lookupAppLocalizations(const Locale('en'));
    final pt = lookupAppLocalizations(const Locale('pt', 'BR'));
    const rough = PolarAlignLive(
        phase: PolarAlignStates.adjusting, stage: PolarAlignStages.coarse, altErrorArcmin: 40, azErrorArcmin: 12);
    expect(simplePolarSideText(rough, en), 'The mount axis sits above and east of the pole.');
    expect(simplePolarSideText(rough, pt), 'O eixo da montagem fica acima e a leste do polo.');
    expect(simplePolarSideText(rough.copyWith(altErrorArcmin: -3, azErrorArcmin: 0.1), en),
        'The mount axis sits below the pole.');
    expect(simplePolarSideText(rough.copyWith(altErrorArcmin: 0.1, azErrorArcmin: -4), pt),
        'O eixo da montagem fica a oeste do polo.');
    expect(simplePolarSideText(rough.copyWith(altErrorArcmin: 0.2, azErrorArcmin: -0.3), en),
        'The mount axis is on the pole.');
    expect(simplePolarSideText(const PolarAlignLive(stage: PolarAlignStages.coarse), en), isNull);
  });

  test('simplePolarPhase names each phase', () {
    final en = lookupAppLocalizations(const Locale('en'));
    expect(simplePolarPhase(const PolarAlignLive(), en), 'Ready');
    expect(simplePolarPhase(const PolarAlignLive(phase: PolarAlignStates.seeding), en), 'Finding the pole…');
    expect(simplePolarPhase(_adjusting, en), 'Adjusting');
    expect(simplePolarPhase(const PolarAlignLive(phase: PolarAlignStates.failed), en), 'Polar alignment failed');
  });

  testWidgets(
    'idle: only the settings, no Ready and no Start (the shutter starts it)',
    (tester) async {
      final api = await _pump(tester, const PolarAlignLive());
      expect(find.text('Ready'), findsNothing);
      expect(find.text('Start'), findsNothing);
      expect(find.text('1 s'), findsOneWidget);
      await tester.tap(find.text('Single'));
      await tester.pumpAndSettle();
      expect(api!.calls, ['save 1.0 single']);
    },
  );

  testWidgets(
    'adjusting: the error, the turns, no Stop (the shutter stops it); Done waits for the tolerance',
    (tester) async {
      final api = await _pump(tester, _adjusting);
      expect(find.text('14.4′'), findsOneWidget);
      expect(find.text('Lower 3.2′'), findsOneWidget);
      expect(find.text('Move west 14.1′'), findsOneWidget);
      expect(
        find.text('1 s'),
        findsOneWidget,
        reason: 'the settings stay, for the next frame',
      );
      expect(
        tester
            .widget<FilledButton>(find.byKey(const Key('simple-polar-done')))
            .onPressed,
        isNull,
      );
      expect(
        tester
            .widget<FilledButton>(find.byKey(const Key('simple-polar-done')))
            .style
            ?.backgroundColor
            ?.resolve({}),
        AraColors.accentConnected,
        reason: 'Done is green',
      );
      expect(find.byKey(const Key('simple-polar-stop')), findsNothing);
      expect(
        find.byKey(const Key('simple-polar-capture')),
        findsNothing,
        reason: 'Loop shoots on its own',
      );
      expect(api!.calls, isEmpty);
    },
  );

  testWidgets(
    'the turns read at a glance: big arrows beside a big bullseye, like the full UI',
    (tester) async {
      await _pump(tester, _adjusting);
      for (final axis in ['alt', 'az']) {
        final arrow = find.descendant(
          of: find.byKey(Key('simple-polar-turn-$axis')),
          matching: find.byType(Icon),
        );
        expect(
          tester.widget<Icon>(arrow).size,
          greaterThanOrEqualTo(40),
          reason: axis,
        );
      }
      final bullseye = find.byKey(const Key('simple-polar-bullseye'));
      expect(tester.getSize(bullseye).width, greaterThanOrEqualTo(180));
    },
  );

  testWidgets(
    'the arrows fill their tiles and the bullseye fills the two tiles\' height (#1721)',
    (tester) async {
      await _pump(tester, _adjusting);
      final alt = find.byKey(const Key('simple-polar-turn-alt'));
      final az = find.byKey(const Key('simple-polar-turn-az'));
      for (final tile in [alt, az]) {
        expect(
          tester
              .getSize(find.descendant(of: tile, matching: find.byType(Icon)))
              .height,
          70,
        );
        expect(
          tester.getSize(tile).height,
          86,
          reason: 'the tile keeps its size',
        );
      }
      final bullseye = find.byKey(const Key('simple-polar-bullseye'));
      final painter =
          tester
                  .widget<CustomPaint>(
                    find.descendant(
                      of: bullseye,
                      matching: find.byType(CustomPaint),
                    ),
                  )
                  .painter!
              as BullseyePainter;
      final tiles = tester.getBottomLeft(az).dy - tester.getTopLeft(alt).dy;
      expect(
        painter.ringRadius(tester.getSize(bullseye)) * 2,
        closeTo(tiles, 2),
      );
    },
  );

  testWidgets(
    'while it runs, the panel stays within 520 pt: the stepper, headings and caption of #1875 make it scroll on an iPad (388 pt)',
    (tester) async {
      // The shell scrolls a panel taller than its room; the chosen mock accepts it. 520 stops it growing further.
      await _pump(tester, _adjusting, loopMode: PolarAlignLoopModes.single);
      expect(
        tester.getSize(find.byType(SimplePolarPanel)).height,
        lessThanOrEqualTo(520),
      );
    },
  );

  testWidgets('Single running: Take frame takes the next one', (tester) async {
    final api = await _pump(tester, _adjusting, loopMode: PolarAlignLoopModes.single);
    await tester.tap(find.byKey(const Key('simple-polar-capture')));
    await tester.pump();
    expect(api!.calls, ['capture']);
  });

  testWidgets(
    'rough stage outside the tolerance: no Done, and the caption says so (#1875)',
    (tester) async {
      await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.adjusting,
          stage: PolarAlignStages.coarse,
          altErrorArcmin: 5,
          azErrorArcmin: 9,
          totalErrorArcmin: 11.0,
          zone: 'red',
        ),
        tolerance: 2.0,
      );
      expect(find.byKey(const Key('simple-polar-done-rough')), findsNothing);
      expect(
        find.text(
          'Outside your 2′ tolerance: stop with the shutter, then start again to refine it with the guide camera.',
        ),
        findsOneWidget,
      );
    },
  );

  testWidgets(
    'rough stage inside the tolerance: Done is outlined, and no Next: fine leads on (#1875, #2082)',
    (tester) async {
      final api = await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.adjusting,
          stage: PolarAlignStages.coarse,
          altErrorArcmin: 0.6,
          azErrorArcmin: 1.4,
          totalErrorArcmin: 1.5,
          zone: 'green',
        ),
        tolerance: 2.0,
      );
      expect(
        find.text(
          'Inside your 2′ tolerance. Done keeps this alignment; stop and start again to refine it with the guide camera.',
        ),
        findsOneWidget,
      );
      expect(find.byType(OutlinedButton), findsOneWidget);
      expect(find.byKey(const Key('simple-polar-next-fine')), findsNothing, reason: 'one measurement per Start');
      expect(find.byKey(const Key('simple-polar-check')), findsNothing, reason: 'the rough step has no Check');
      await tester.tap(find.byKey(const Key('simple-polar-done-rough')));
      await tester.pump();
      expect(api!.calls, ['complete']);
    },
  );

  testWidgets('the stepper marks Rough and the steps behind it (#1875)', (
    tester,
  ) async {
    await _pump(
      tester,
      const PolarAlignLive(
        phase: PolarAlignStates.adjusting,
        stage: PolarAlignStages.coarse,
        totalErrorArcmin: 9,
      ),
    );
    expect(find.text('Rough'), findsOneWidget);
    expect(find.byIcon(Icons.check), findsNothing);
    expect(find.text('Fine'), findsOneWidget);
    expect(find.text('Check'), findsOneWidget);
  });

  testWidgets(
    'the current step is selectionBg with white text: 4.6:1, not the brightest block at night (#1875)',
    (tester) async {
      await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.adjusting,
          stage: PolarAlignStages.coarse,
          totalErrorArcmin: 9,
        ),
      );
      final box =
          tester
                  .widget<Container>(
                    find.byKey(const Key('simple-polar-step-0')),
                  )
                  .decoration!
              as BoxDecoration;
      expect(box.color, AraColors.selectionBg);
      expect(
        tester.widget<Text>(find.text('Rough')).style!.color,
        AraColors.selectionFg,
      );
    },
  );

  testWidgets(
    'the stepper ticks no step before the one running, and a finished check its own (#2082)',
    (tester) async {
      await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.checked,
          stage: PolarAlignStages.check,
          totalErrorArcmin: 0.4,
        ),
      );
      expect(
        find.descendant(
          of: find.byKey(const Key('simple-polar-step-2')),
          matching: find.byIcon(Icons.check),
        ),
        findsOneWidget,
      );
      expect(find.byIcon(Icons.check), findsOneWidget, reason: 'each step is its own Start: Rough and Fine never ran');
      expect(find.text('Rough'), findsOneWidget);
      expect(find.text('Fine'), findsOneWidget);
      expect(
        tester.widget<Icon>(find.byIcon(Icons.check)).semanticLabel,
        'Done',
        reason: 'a screen reader hears the tick',
      );
    },
  );

  testWidgets("the setup rows wait for the profile from the rig: Not loaded with Retry, then save over the rig's copy (ADR 0015)",
      (tester) async {
    final api = _Api();
    api.section.initial = null; // the rig does not answer
    await tester.pumpWidget(ProviderScope(
      overrides: [
        ...polarAlignSectionOverrides(api.section),
        polarAlignApiProvider.overrideWithValue(api),
        polarAlignLiveProvider.overrideWith(() => _Live(const PolarAlignLive())),
      ],
      child: const MaterialApp(home: Scaffold(body: Center(child: SimplePolarPanel()))),
    ));
    await tester.pumpAndSettle();
    expect(find.text('Single'), findsNothing, reason: 'no made-up Loop/Single');
    expect(find.text('1 s'), findsNothing, reason: 'no made-up exposure');
    expect(find.byKey(const Key('section-retry')), findsOneWidget);
    expect(api.calls.where((c) => c.startsWith('save')), isEmpty);

    api.settings = rigPolarAlign;
    await tester.tap(find.byKey(const Key('section-retry')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Single'));
    await tester.pumpAndSettle();
    expect(api.calls, ['save 1.0 single'], reason: "saved over the rig's copy once it is in");
  });

  testWidgets("a Setup row refused as stale shows the rig's copy again, and the next change saves (ADR 0015)",
      (tester) async {
    final api = (await _pump(tester, const PolarAlignLive()))!;
    api.section.refuse = staleSave();
    await tester.tap(find.text('Single'));
    await tester.pumpAndSettle();
    expect(api.section.puts, isEmpty);
    expect(api.section.loads, 1, reason: "the rig's copy is loaded again");
    final container = ProviderScope.containerOf(tester.element(find.byType(SimplePolarPanel)));
    expect(container.read(polarAlignSettingsProvider).value!.loopMode, PolarAlignLoopModes.loop,
        reason: 'the rig still loops: Single must not stay on screen');
    expect(container.read(polarAlignSettingsProvider).conflict, isFalse, reason: 'no Reload here to clear it');

    api.section.refuse = null;
    await tester.tap(find.text('Single'));
    await tester.pumpAndSettle();
    expect(api.section.puts.single.loopMode, PolarAlignLoopModes.single);
  });

  testWidgets('idle: no stepper, only the settings (#1875)', (tester) async {
    await _pump(tester, const PolarAlignLive());
    expect(find.byKey(const Key('simple-polar-stepper')), findsNothing);
  });

  test('simplePolarStep: verifying and the verdict are the Verify step', () {
    expect(
      simplePolarStep(
        const PolarAlignLive(
          phase: PolarAlignStates.checking,
          stage: PolarAlignStages.check,
        ),
      ),
      2,
    );
    expect(
      simplePolarStep(
        const PolarAlignLive(
          phase: PolarAlignStates.checked,
          stage: PolarAlignStages.check,
        ),
      ),
      2,
    );
    expect(
      simplePolarStep(
        const PolarAlignLive(
          phase: PolarAlignStates.adjusting,
          stage: PolarAlignStages.coarse,
        ),
      ),
      0,
    );
    expect(
      simplePolarStep(const PolarAlignLive(phase: PolarAlignStates.adjusting)),
      1,
      reason: 'an older daemon: one fine stage',
    );
    expect(simplePolarStep(const PolarAlignLive()), isNull);
  });

  testWidgets(
    'fine stage: Verify carries its caption and the groups sit under Setup and Error (#1875)',
    (tester) async {
      await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.adjusting,
          stage: PolarAlignStages.fine,
          altErrorArcmin: 0.6,
          azErrorArcmin: 0.8,
          totalErrorArcmin: 1.0,
          zone: 'green',
        ),
      );
      expect(
        find.text(
          'Check turns the mount 2 × 10° and takes three frames to measure the alignment.',
        ),
        findsOneWidget,
      );
      expect(
        find.text('Check'),
        findsNWidgets(2),
        reason: 'the stepper step and the button',
      );
      expect(find.text('Setup'), findsOneWidget);
      expect(find.text('Error'), findsOneWidget);
    },
  );

  testWidgets(
    'Layout A order: Setup above Error, the Verify caption above the fine buttons (#1875)',
    (tester) async {
      await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.adjusting,
          stage: PolarAlignStages.fine,
          altErrorArcmin: 0.6,
          azErrorArcmin: 0.8,
          totalErrorArcmin: 1.0,
          zone: 'green',
        ),
      );
      double top(Finder f) => tester.getTopLeft(f).dy;
      expect(top(find.text('Setup')), lessThan(top(find.text('Error'))));
      expect(
        top(find.byKey(const Key('simple-polar-verify-caption'))),
        lessThan(top(find.byKey(const Key('simple-polar-check')))),
      );
    },
  );

  testWidgets(
    'verifying shows only the phase line: no bullseye, no settings (#1875)',
    (tester) async {
      await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.checking,
          stage: PolarAlignStages.check,
          checkSolvesDone: 1,
          checkSolvesTotal: 3,
          altErrorArcmin: 0.6,
          azErrorArcmin: 0.8,
          totalErrorArcmin: 1.0,
          zone: 'green',
        ),
      );
      expect(find.byKey(const Key('simple-polar-bullseye')), findsNothing);
      expect(find.byKey(const Key('simple-polar-setup')), findsNothing);
      expect(find.text('Setup'), findsNothing);
      expect(find.text('Error'), findsNothing);
    },
  );

  testWidgets(
    'verified shows only the verdict: no bullseye, no settings (#1875)',
    (tester) async {
      await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.checked,
          stage: PolarAlignStages.check,
          altErrorArcmin: 0.3,
          azErrorArcmin: 0.2,
          totalErrorArcmin: 0.36,
          zone: 'green',
        ),
      );
      expect(find.byKey(const Key('simple-polar-bullseye')), findsNothing);
      expect(find.byKey(const Key('simple-polar-setup')), findsNothing);
    },
  );

  testWidgets(
    "Simple Mode shows the total with its 1σ; inside the tolerance but not with twice it, Done is allowed and the panel says it can't confirm (#2072)",
    (tester) async {
      // The noisy test's window: a caution line takes room.
      tester.view.physicalSize = const Size(1024, 768);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.adjusting,
          stage: PolarAlignStages.fine,
          altErrorArcmin: 0.3,
          azErrorArcmin: 0.4,
          totalErrorArcmin: 0.5,
          totalSigmaArcmin: 0.4,
          zone: 'green',
        ),
        tolerance: 1.0,
      );
      expect(find.text('0.5′ ± 0.4′'), findsOneWidget);
      expect(tester.widget<FilledButton>(find.byKey(const Key('simple-polar-done'))).onPressed, isNotNull,
          reason: 'Done is not held by the σ (Joey, 2026-11-21)');
      final line = tester.widget<Text>(find.byKey(const Key('simple-polar-unconfirmed')));
      expect(line.data, startsWith("Inside your 1′ tolerance, but at ± 0.4′ this measurement can't confirm the alignment."));
      expect(tester.widget<OutlinedButton>(find.byKey(const Key('simple-polar-check'))).onPressed, isNotNull,
          reason: 'Check measures afresh');
    },
  );

  testWidgets(
    'Simple Mode: with the error plus 2σ inside the tolerance the panel does not call it unconfirmed (#2072)',
    (tester) async {
      // The noisy test's window: a caution line takes room.
      tester.view.physicalSize = const Size(1024, 768);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      final api = await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.adjusting,
          stage: PolarAlignStages.fine,
          altErrorArcmin: 0.3,
          azErrorArcmin: 0.4,
          totalErrorArcmin: 0.5,
          totalSigmaArcmin: 0.25,
          zone: 'green',
        ),
        tolerance: 1.0,
      );
      expect(find.byKey(const Key('simple-polar-unconfirmed')), findsNothing, reason: '0.5 + 2 × 0.25 is exactly the 1′ tolerance');
      await tester.tap(find.byKey(const Key('simple-polar-done')));
      await tester.pump();
      expect(api!.calls, ['complete']);
    },
  );

  testWidgets(
    "the rough stage offers Done inside the tolerance; its caption says inside only once the error plus 2σ is, and can't confirm before (#2072)",
    (tester) async {
      // The noisy test's window: a caution line takes room.
      tester.view.physicalSize = const Size(1024, 768);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.adjusting,
          stage: PolarAlignStages.coarse,
          altErrorArcmin: 0.6,
          azErrorArcmin: 1.4,
          totalErrorArcmin: 1.5,
          totalSigmaArcmin: 0.4,
          zone: 'green',
        ),
        tolerance: 2.0,
      );
      expect(find.byKey(const Key('simple-polar-done-rough')), findsOneWidget, reason: 'Done is not held by the σ');
      final caption = tester.widget<Text>(find.byKey(const Key('simple-polar-tolerance-caption')));
      expect(caption.data, startsWith("Inside your 2′ tolerance, but at ± 0.4′ this measurement can't confirm the alignment."));
      expect(caption.data, isNot(startsWith('Inside your 2′ tolerance. Done keeps')));
    },
  );

  testWidgets(
    "Simple Mode's checked line rates the error plus twice its 1σ: 0.2′ ± 1.3′ does not read Excellent (#2072)",
    (tester) async {
      await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.checked,
          stage: PolarAlignStages.check,
          totalErrorArcmin: 0.2,
          totalSigmaArcmin: 1.3,
          checkResult: PolarAlignCheckResult(
              altErrorArcmin: 0.1, azErrorArcmin: 0.17, totalErrorArcmin: 0.2, verdict: 'excellent', totalSigmaArcmin: 1.3),
        ),
      );
      final line = tester.widget<Text>(find.byKey(const Key('simple-polar-checked')));
      expect(line.data, 'Three-point check: Very good');
    },
  );

  testWidgets(
    'fine stage: a hair above the tolerance, Check and Done stay off',
    (tester) async {
      final api = await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.adjusting,
          stage: PolarAlignStages.fine,
          altErrorArcmin: 0.7,
          azErrorArcmin: 0.75,
          totalErrorArcmin: 1.01,
          zone: 'yellow',
        ),
        tolerance: 1.0,
      );
      expect(
        tester
            .widget<OutlinedButton>(find.byKey(const Key('simple-polar-check')))
            .onPressed,
        isNull,
      );
      expect(
        tester
            .widget<FilledButton>(find.byKey(const Key('simple-polar-done')))
            .onPressed,
        isNull,
      );
      expect(api!.calls, isEmpty);
    },
  );

  testWidgets(
    'fine stage: at the tolerance exactly, Check asks the daemon for the check',
    (tester) async {
      final api = await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.adjusting,
          stage: PolarAlignStages.fine,
          altErrorArcmin: 0.6,
          azErrorArcmin: 0.8,
          totalErrorArcmin: 1.0,
          zone: 'green',
        ),
        tolerance: 1.0,
      );
      expect(
        tester
            .widget<OutlinedButton>(find.byKey(const Key('simple-polar-check')))
            .onPressed,
        isNotNull,
        reason: 'enabled at the tolerance exactly (<=), not only below it',
      );
      await tester.tap(find.byKey(const Key('simple-polar-check')));
      await tester.pumpAndSettle();
      expect(api!.calls, isEmpty, reason: 'nothing moves before the confirm step (#2082)');
      await tester.tap(find.byKey(const Key('polar-start-confirm-go')));
      await tester.pumpAndSettle();
      expect(api.stages, [PolarAlignStages.check], reason: 'a confirmed Start(check), never Next');
    },
  );

  testWidgets(
    'after Done, Check again says how far it turns and starts a confirmed check (#2082)',
    (tester) async {
      final api = await _pump(
        tester,
        const PolarAlignLive(phase: PolarAlignStates.stopped, stage: PolarAlignStages.fine, totalErrorArcmin: 0.8),
      );
      expect(
        find.text('Check turns the mount 2 × 10° and takes three frames to measure the alignment.'),
        findsOneWidget,
      );
      await tester.tap(find.byKey(const Key('simple-polar-check-again')));
      await tester.pumpAndSettle();
      expect(api!.calls, isEmpty, reason: 'nothing moves before the confirm step');
      await tester.tap(find.byKey(const Key('polar-start-confirm-go')));
      await tester.pumpAndSettle();
      expect(api.stages, [PolarAlignStages.check]);
    },
  );

  testWidgets('after the guide camera could not solve, Rough first says how far it turns and starts a confirmed rough run (#2082)',
      (tester) async {
    tester.view.physicalSize = const Size(1024, 768);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    // A seed turn unlike the check step (10°): the caption is the rig's seed turn, not the check's.
    final api = await _pump(
        tester,
        const PolarAlignLive(
            phase: PolarAlignStates.failed, stage: PolarAlignStages.fine, errorReason: 'seed_solve_failed',
            errorMessage: 'the seed-a seed frame failed to solve'),
        rig: rigPolarAlign.copyWith(seedRotationDeg: 15));
    expect(find.text('Rough first turns the mount 2 × 15° and measures with the main camera.'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-polar-rough-first')));
    await tester.pumpAndSettle();
    expect(api!.stages, isEmpty, reason: 'nothing moves before the confirm step');
    await tester.tap(find.byKey(const Key('polar-start-confirm-go')));
    await tester.pumpAndSettle();
    expect(api.stages, [PolarAlignStages.coarse]);
  });

  testWidgets('a main-camera run that could not solve offers no Rough first (#2082)', (tester) async {
    await _pump(tester, const PolarAlignLive(
        phase: PolarAlignStates.failed, stage: PolarAlignStages.fine, camera: PolarAlignCameras.main,
        errorReason: 'seed_solve_failed'));
    expect(find.byKey(const Key('simple-polar-rough-first')), findsNothing);
  });

  testWidgets('another failure offers no Rough first (#2082)', (tester) async {
    await _pump(tester, const PolarAlignLive(
        phase: PolarAlignStates.failed, stage: PolarAlignStages.fine, errorReason: 'slew_failed'));
    expect(find.byKey(const Key('simple-polar-rough-first')), findsNothing);
  });

  testWidgets('a noisy measurement says so while adjusting and beside the verdict (#2082)', (tester) async {
    tester.view.physicalSize = const Size(1024, 768);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await _pump(tester, _adjusting);
    expect(find.byKey(const Key('simple-polar-noisy')), findsNothing);
    final live = ProviderScope.containerOf(tester.element(find.byType(SimplePolarPanel)))
        .read(polarAlignLiveProvider.notifier) as _Live;
    live.frame(_adjusting.copyWith(measurementNoisy: true));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('simple-polar-noisy')), findsOneWidget);
    live.frame(const PolarAlignLive(
      phase: PolarAlignStates.checked, stage: PolarAlignStages.check, totalErrorArcmin: 0.36,
      checkResult: PolarAlignCheckResult(
          altErrorArcmin: 0.3, azErrorArcmin: 0.2, totalErrorArcmin: 0.36, verdict: 'excellent', noisy: true),
    ));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('simple-polar-noisy')), findsOneWidget);
  });

  testWidgets("Check again names only the rig's own step: no caption until the profile loads (ADR 0015)",
      (tester) async {
    final api = _Api();
    api.section.initial = null; // the rig does not answer
    await tester.pumpWidget(ProviderScope(
      overrides: [
        ...polarAlignSectionOverrides(api.section),
        polarAlignApiProvider.overrideWithValue(api),
        polarAlignLiveProvider.overrideWith(() => _Live(const PolarAlignLive(
            phase: PolarAlignStates.stopped, stage: PolarAlignStages.fine, totalErrorArcmin: 0.8))),
      ],
      child: const MaterialApp(home: Scaffold(body: Center(child: SimplePolarPanel()))),
    ));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('simple-polar-check-again-caption')), findsNothing,
        reason: 'no made-up step is said before the mount turns');
    expect(find.byKey(const Key('simple-polar-check-again')), findsOneWidget);

    api.settings = rigPolarAlign.copyWith(checkStepDeg: 20);
    await tester.tap(find.byKey(const Key('section-retry')));
    await tester.pumpAndSettle();
    expect(
      find.text('Check turns the mount 2 × 20° and takes three frames to measure the alignment.'),
      findsOneWidget,
    );
  });

  testWidgets(
    'checked: Done completes the routine and leaves the verdict alone',
    (tester) async {
      final api = await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.checked,
          stage: PolarAlignStages.check,
          altErrorArcmin: 0.3,
          azErrorArcmin: 0.2,
          totalErrorArcmin: 0.36,
          zone: 'green',
        ),
      );
      expect(find.byKey(const Key('simple-polar-check')), findsNothing);
      await tester.tap(find.byKey(const Key('simple-polar-done')));
      await tester.pump();
      expect(api!.calls, ['complete']);
    },
  );

  testWidgets('inside the tolerance Done completes it', (tester) async {
    final api = await _pump(
      tester,
      const PolarAlignLive(phase: PolarAlignStates.adjusting, altErrorArcmin: 0.3, azErrorArcmin: -0.5, totalErrorArcmin: 0.6),
    );
    await tester.tap(find.byKey(const Key('simple-polar-done')));
    await tester.pump();
    expect(api!.calls, ['complete']);
  });

  testWidgets('in Preflight, polar align is the last step: Done reads Done · Start imaging', (tester) async {
    final api = _Api();
    final c = ProviderContainer(overrides: [
      ...polarAlignSectionOverrides(api.section),
      polarAlignApiProvider.overrideWithValue(api),
      polarAlignLiveProvider.overrideWith(() => _Live(const PolarAlignLive(
          phase: PolarAlignStates.adjusting, altErrorArcmin: 0.3, azErrorArcmin: -0.5, totalErrorArcmin: 0.6))),
    ]);
    addTearDown(c.dispose);
    c.read(simplePreflightProvider.notifier).back(); // no-op outside Preflight
    await tester.pumpWidget(UncontrolledProviderScope(
      container: c,
      child: const MaterialApp(home: Scaffold(body: Center(child: SimplePolarPanel()))),
    ));
    await tester.pumpAndSettle();
    expect(find.text('Done'), findsOneWidget, reason: 'outside Preflight');
    c.read(simplePreflightProvider.notifier).start();
    c.read(simplePreflightProvider.notifier).next(); // past the plan, to Focus
    c.read(simplePreflightProvider.notifier).next(); // past Focus, to polar align
    await tester.pumpAndSettle();
    expect(c.read(simplePreflightProvider), SimplePreflightStep.polarAlign);
    expect(find.text('Done · Start imaging'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-polar-done')));
    await tester.pumpAndSettle();
    expect(api.calls.last, 'complete');
  });

  testWidgets('a bolt turned the wrong way gets the full UI\'s nudge', (tester) async {
    PolarAlignLive at(int i, double alt, double az) => PolarAlignLive(
        phase: PolarAlignStates.adjusting, iteration: i, altErrorArcmin: alt, azErrorArcmin: az, totalErrorArcmin: 10);
    // The iPad, as the shell tests.
    tester.view.physicalSize = const Size(1024, 768);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await _pump(tester, at(1, 10, -4));
    final live = ProviderScope.containerOf(tester.element(find.byType(SimplePolarPanel)))
        .read(polarAlignLiveProvider.notifier) as _Live;
    expect(find.textContaining('got worse'), findsNothing);
    live.frame(at(2, 13, -4.2));
    await tester.pumpAndSettle();
    expect(find.text('Altitude got worse. Turn that bolt back past where you started.'), findsOneWidget);
  });

  testWidgets('a readout weak in altitude says so while adjusting, in the chosen words (#2072)', (tester) async {
    tester.view.physicalSize = const Size(1024, 768);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final en = lookupAppLocalizations(const Locale('en'));
    await _pump(tester, _adjusting);
    expect(find.byKey(const Key('simple-polar-readout-caution')), findsNothing);
    final live = ProviderScope.containerOf(tester.element(find.byType(SimplePolarPanel)))
        .read(polarAlignLiveProvider.notifier) as _Live;
    live.frame(_adjusting.copyWith(readoutCaution: PolarAlignReadoutCautions.altitudeWeak));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('simple-polar-readout-caution')), findsOneWidget);
    expect(find.text(en.polarReadoutCautionAltitudeWeak), findsOneWidget);
  });

  testWidgets('a Dec that moved across the three points is warned about while adjusting (#2081)', (tester) async {
    tester.view.physicalSize = const Size(1024, 768);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final en = lookupAppLocalizations(const Locale('en'));
    await _pump(tester, _adjusting);
    expect(find.byKey(const Key('simple-polar-dec-spread')), findsNothing);
    final live = ProviderScope.containerOf(tester.element(find.byType(SimplePolarPanel)))
        .read(polarAlignLiveProvider.notifier) as _Live;
    live.frame(_adjusting.copyWith(decSpreadArcsec: 2.6));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('simple-polar-dec-spread')), findsOneWidget);
    expect(find.text(en.polarDecSpreadWarning('2.6')), findsOneWidget);
  });

  test('the Dec-spread warning is the verdict one once checked, the seed one only while the readout runs (#2081)', () {
    const verdict = PolarAlignCheckResult(altErrorArcmin: 0, azErrorArcmin: 0, totalErrorArcmin: 0.5, decSpreadArcsec: 3.0);
    expect(
      simplePolarDecSpread(
          const PolarAlignLive(phase: PolarAlignStates.checked, checkResult: verdict, decSpreadArcsec: 9.0),
          verifying: true),
      3.0,
    );
    expect(simplePolarDecSpread(const PolarAlignLive(phase: PolarAlignStates.adjusting, decSpreadArcsec: 2.5), verifying: false), 2.5);
    expect(simplePolarDecSpread(const PolarAlignLive(phase: PolarAlignStates.paused, decSpreadArcsec: 2.5), verifying: false), 2.5);
    expect(simplePolarDecSpread(const PolarAlignLive(phase: PolarAlignStates.checking, decSpreadArcsec: 2.5), verifying: true), isNull);
    expect(simplePolarDecSpread(const PolarAlignLive(phase: PolarAlignStates.stopped, decSpreadArcsec: 2.5), verifying: false), isNull);
  });

  testWidgets('failed solves say so', (tester) async {
    await _pump(tester, _adjusting.copyWith(consecutiveSolveFailures: 2));
    expect(find.text('No solve (2 in a row). Check the sky.'), findsOneWidget);
  });

  testWidgets('a failed routine shows why, and Start again', (tester) async {
    await _pump(tester, const PolarAlignLive(phase: PolarAlignStates.failed, errorMessage: 'The mount would not rotate.'));
    expect(find.text('The mount would not rotate.'), findsOneWidget);
  });

  // #2107 a stop from outside is worded by what stopped the mount, in the app's
  // language, not the daemon's English (which always said "Stop Mount" before).
  testWidgets('Simple: a run a pad stop ended names the direction pad, not the daemon English', (tester) async {
    await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.failed,
          errorReason: 'slew_aborted_externally',
          errorMessage: 'the mount was stopped from the direction pad — polar alignment ended. Start again when ready',
          errorStoppedBy: 'pad_stop',
        ));
    final en = lookupAppLocalizations(const Locale('en'));
    expect(find.text(en.polarAlignStoppedFromOutside('pad_stop')), findsOneWidget);
    expect(find.textContaining('the mount was stopped from the direction pad —'), findsNothing);
  });

  group('the shutter runs the routine', () {
    Future<_Api?> shutter(WidgetTester tester, PolarAlignLive live, {String loopMode = PolarAlignLoopModes.loop, bool server = true}) =>
        _pump(tester, live, loopMode: loopMode, server: server, child: const SimplePolarShutter());

    testWidgets('a start that fails says so in a pill', (tester) async {
      final api = _FailingApi();
      final c = ProviderContainer(overrides: [
        polarAlignApiProvider.overrideWithValue(api),
        polarAlignLiveProvider.overrideWith(() => _Live(const PolarAlignLive())),
        simpleModePrefsProvider.overrideWithValue(_SimplePrefs()),
        // Simple Mode on: the Experimental switch too, or the full UI shows (snackbars).
        simpleModeFeaturePrefsProvider.overrideWithValue(_SimplePrefs()),
      ]);
      addTearDown(c.dispose);
      await c.read(simpleModeProvider.future);
      await c.read(simpleModeFeatureProvider.future);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: c,
        child: const MaterialApp(home: Scaffold(body: Center(child: SimplePolarShutter()))),
      ));
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('polar-start-confirm-go')));
      await tester.pump();
      expect(c.read(simpleMessagesProvider).single.kind, SimpleMessageKind.error);
      expect(find.byType(SnackBar), findsNothing);
    });

    testWidgets('a parked mount is refused before the confirm step, in a pill, and nothing starts (#2048)', (tester) async {
      final api = _ParkedApi();
      final c = ProviderContainer(overrides: [
        polarAlignApiProvider.overrideWithValue(api),
        polarAlignLiveProvider.overrideWith(() => _Live(const PolarAlignLive())),
        simpleModePrefsProvider.overrideWithValue(_SimplePrefs()),
        simpleModeFeaturePrefsProvider.overrideWithValue(_SimplePrefs()),
      ]);
      addTearDown(c.dispose);
      await c.read(simpleModeProvider.future);
      await c.read(simpleModeFeatureProvider.future);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: c,
        child: const MaterialApp(home: Scaffold(body: Center(child: SimplePolarShutter()))),
      ));
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-start-confirm')), findsNothing, reason: 'no confirm step for a start the rig refuses');
      expect(api.calls, isNot(contains('start')));
      final pill = c.read(simpleMessagesProvider).single;
      expect(pill.kind, SimpleMessageKind.error);
      expect('${pill.title} ${pill.detail ?? ''}', contains('The mount is parked. Unpark it and point it near the pole first.'));
    });

    testWidgets('a start runs through the shared module: busy while it runs, its failure in the pill only (#1764)', (tester) async {
      final api = _SlowFailingApi();
      final c = ProviderContainer(overrides: [
        polarAlignApiProvider.overrideWithValue(api),
        polarAlignLiveProvider.overrideWith(() => _Live(const PolarAlignLive())),
      ]);
      addTearDown(c.dispose);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: c,
        child: const MaterialApp(home: Scaffold(body: Center(child: SimplePolarShutter()))),
      ));
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      expect(c.read(polarAlignActionProvider).busy, isTrue, reason: 'the confirm step is part of the start in flight');
      await tester.tap(find.byKey(const Key('polar-start-confirm-go')));
      await tester.pump();
      expect(c.read(polarAlignActionProvider).busy, isTrue, reason: 'the module knows the start is in flight');
      expect(tester.widget<SimpleShutterButton>(find.byType(SimpleShutterButton)).onPressed, isNull,
          reason: 'a second press would send a second start');
      api.gate.complete();
      await tester.pump();
      await tester.pump();
      expect(c.read(polarAlignActionProvider).busy, isFalse);
      // The panel shows the module's error inline; the shutter's own pill already says it.
      expect(c.read(polarAlignActionProvider).error, isNull, reason: 'one failure, one message');
    });

    testWidgets('while another polar call is in flight, Stop still takes the press (#1770)', (tester) async {
      final c = ProviderContainer(overrides: [
        polarAlignApiProvider.overrideWithValue(_Api()),
        polarAlignLiveProvider.overrideWith(() => _Live(_adjusting)),
      ]);
      addTearDown(c.dispose);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: c,
        child: const MaterialApp(home: Scaffold(body: Center(child: SimplePolarShutter()))),
      ));
      // A settings save from the panel, still waiting on the rig.
      final save = Completer<void>();
      unawaited(c.read(polarAlignActionProvider.notifier).run((_) => save.future, describe: (_) => null));
      await tester.pump();
      expect(c.read(polarAlignActionProvider).busy, isTrue);
      expect(tester.widget<SimpleShutterButton>(find.byType(SimpleShutterButton)).onPressed, isNotNull,
          reason: 'stopping a routine that moves the mount never waits');
      save.complete();
      await tester.pump();
    });

    testWidgets('idle: starts it', (tester) async {
      final api = await shutter(tester, const PolarAlignLive());
      expect(find.descendant(of: find.byKey(const Key('simple-shutter')), matching: find.byIcon(Icons.play_arrow)),
          findsOneWidget, reason: 'a routine starts with play');
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-start-confirm')), findsOneWidget, reason: '#2048: the confirm step first');
      expect(api!.calls, isEmpty);
      await tester.tap(find.byKey(const Key('polar-start-confirm-go')));
      await tester.pumpAndSettle();
      expect(api.calls, ['start']);
    });
    testWidgets('Loop running: the square stops it', (tester) async {
      final api = await shutter(tester, _adjusting);
      expect(find.bySemanticsLabel('Stop'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(api!.calls, ['stop']);
    });
    testWidgets('Single running: the square stops it too', (tester) async {
      final api = await shutter(tester, _adjusting, loopMode: PolarAlignLoopModes.single);
      expect(find.bySemanticsLabel('Stop'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(api!.calls, ['stop']);
    });
    testWidgets('the stop square is red, the start disc white', (tester) async {
      Color fill() => tester
          .widgetList<Container>(find.descendant(of: find.byKey(const Key('simple-shutter')), matching: find.byType(Container)))
          .map((c) => (c.decoration as BoxDecoration?)?.color)
          .whereType<Color>()
          .single;
      await shutter(tester, _adjusting);
      expect(fill(), AraColors.accentError);
    });
    testWidgets('idle: the start triangle is white', (tester) async {
      await shutter(tester, const PolarAlignLive());
      expect(
          tester
              .widget<Icon>(find.descendant(of: find.byKey(const Key('simple-shutter')), matching: find.byType(Icon)))
              .color,
          AraColors.textPrimary);
    });
    testWidgets('the main camera busy: off, and a tap says why (user, 2026-11-04: it turns the mount)', (tester) async {
      final api = await shutter(tester, const PolarAlignLive());
      final c = ProviderScope.containerOf(tester.element(find.byKey(const Key('simple-shutter'))));
      c.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 30));
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(api!.calls, isEmpty, reason: 'no start under a frame');
      expect(c.read(simpleMessagesProvider).single.title, 'The main camera is busy: wait for its frame, or stop it.');
      c.read(captureProgressProvider.notifier).reset();
      await tester.pump(const Duration(seconds: 11));
    });

    testWidgets('no server: off, and a tap says why (user, 2026-11-04)', (tester) async {
      await shutter(tester, const PolarAlignLive(), server: false);
      final c = ProviderScope.containerOf(tester.element(find.byKey(const Key('simple-shutter'))));
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(c.read(simpleMessagesProvider).single.title, 'Rig not connected');
      await tester.pump(const Duration(seconds: 11));
    });
  });
}
