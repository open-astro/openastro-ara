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
import 'package:openastroara/state/equipment/mount_home_state.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/imaging/capture_progress_state.dart';
import 'package:openastroara/state/polar_align/polar_align_flow_state.dart';
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

import '../../support/fake_mount.dart';
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
  final calls = <String>[];
  late final section = _Section(calls);

  /// The rig's copy of the profile's polar-align section.
  PolarAlignSettings get settings => section.initial!;
  set settings(PolarAlignSettings s) => section.initial = s;
  @override
  Future<PolarAlignStatus?> getStatus() async => null;
  @override
  Future<void> start() async => calls.add('start');
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
  Future<void> start() async => throw StateError('no camera');
}

/// A rig that refuses every start with [refusal].
class _RefusingApi extends _Api {
  _RefusingApi(this.refusal);
  final Refusal refusal;
  @override
  Future<void> start() async => throw refusal;
}

/// A start that waits for [gate]: the shutter's call in flight, then refused.
class _SlowFailingApi extends _Api {
  final gate = Completer<void>();
  @override
  Future<void> start() async {
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

const _verdict = PolarAlignCheckResult(
  altErrorArcmin: -0.4, azErrorArcmin: 0.3, totalErrorArcmin: 0.5, totalSigmaArcmin: 0.2, verdict: 'excellent');

/// The run's steps while the rig works, in order.
const _running = <(String, PolarAlignLive)>[
  ('measuring', PolarAlignLive(phase: PolarAlignStates.seeding, stage: PolarAlignStages.align)),
  ('adjusting', _adjusting),
  ('going Home', PolarAlignLive(phase: PolarAlignStates.homing, stage: PolarAlignStages.verify)),
  ('checking', PolarAlignLive(phase: PolarAlignStates.checking, stage: PolarAlignStages.verify, checkSolvesDone: 1)),
  (
    'returning Home',
    PolarAlignLive(phase: PolarAlignStates.homing, stage: PolarAlignStages.verify, checkSolvesDone: 3, checkResult: _verdict),
  ),
];

/// Lets the first reads land. A step that only needs watching shows a spinner
/// and never settles: a few timed frames stand in for it.
Future<void> _settle(WidgetTester tester, PolarAlignLive live) async {
  const still = {PolarFlowStep.ready, PolarFlowStep.adjusting, PolarFlowStep.result};
  if (still.contains(polarFlowStep(live))) {
    await tester.pumpAndSettle();
    return;
  }
  for (var i = 0; i < 4; i++) {
    await tester.pump(const Duration(milliseconds: 50));
  }
}

/// [mount]: at Home, and able to find it, unless a test says otherwise.
Future<_Api?> _pump(WidgetTester tester, PolarAlignLive live,
    {bool server = true,
    String loopMode = PolarAlignLoopModes.loop,
    double? tolerance,
    PolarAlignSettings rig = rigPolarAlign,
    FakeMount? mount,
    _Api? rigApi,
    Widget child = const SimplePolarPanel()}) async {
  final api = server
      ? ((rigApi ?? _Api())
        ..settings = tolerance == null
            ? rig.copyWith(loopMode: loopMode)
            : rig.copyWith(loopMode: loopMode, targetToleranceArcmin: tolerance))
      : null;
  await tester.pumpWidget(ProviderScope(
    overrides: [
      if (api != null) ...polarAlignSectionOverrides(api.section) else profileApiProvider.overrideWithValue(null),
      polarAlignApiProvider.overrideWithValue(api),
      polarAlignLiveProvider.overrideWith(() => _Live(live)),
      mountProvider.overrideWith(() => mount ?? FakeMount.atHome()),
    ],
    child: MaterialApp(home: Scaffold(body: Center(child: child))),
  ));
  await _settle(tester, live);
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

  testWidgets('Ready, the mount away from Home: Home is the one button, and a tap sends the mount Home', (tester) async {
    final mount = FakeMount.awayFromHome();
    final api = await _pump(tester, const PolarAlignLive(), mount: mount);
    expect(find.byKey(const Key('polar-align-start')), findsNothing, reason: 'Start waits for Home');
    expect(find.text('Polar alignment starts with the mount at Home.'), findsOneWidget);
    await tester.tap(find.widgetWithText(FilledButton, 'Home'));
    await tester.pumpAndSettle();
    expect(mount.calls, ['home']);
    expect(api!.calls, isEmpty);
  });

  testWidgets('Ready, the mount going Home: Going Home… with a Stop that stops the mount, then Start once it is Home', (tester) async {
    final mount = FakeMount.awayFromHome();
    await _pump(tester, const PolarAlignLive(), mount: mount);
    await tester.tap(find.byKey(const Key('polar-align-home')));
    await tester.pump();
    mount.report(mountWire(atHome: false, runtime: 'slewing'));
    await tester.pump();
    expect(find.text('Going Home…'), findsOneWidget);
    expect(find.byKey(const Key('polar-align-home')), findsNothing);
    await tester.tap(find.byKey(const Key('polar-align-home-stop')));
    await tester.pump();
    expect(mount.calls, ['home', 'abort']);
    mount.report(mountWire());
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('polar-align-start')), findsOneWidget);
  });

  testWidgets('Ready, the mount at Home: Start Polar Alignment with the turn it makes, and a tap posts the start', (tester) async {
    final mount = FakeMount.atHome();
    final api = await _pump(tester, const PolarAlignLive(), mount: mount);
    expect(find.byKey(const Key('polar-align-home')), findsNothing);
    expect(find.text('The mount turns 10° twice from Home and measures its axis.'), findsOneWidget);
    expect(
      tester.widget<FilledButton>(find.byKey(const Key('polar-align-start'))).style?.backgroundColor?.resolve({}),
      AraColors.accentConnected,
      reason: "Simple Mode's go button",
    );
    await tester.tap(find.widgetWithText(FilledButton, 'Start Polar Alignment'));
    await tester.pumpAndSettle();
    expect(find.byType(Dialog), findsNothing);
    expect(api!.calls, ['start']);
    expect(mount.calls, isEmpty);
  });

  testWidgets('Ready, a mount that cannot find Home by itself: Start, with no Home button', (tester) async {
    final api = await _pump(tester, const PolarAlignLive(), mount: FakeMount(mountWire(atHome: false, canFindHome: false)));
    expect(find.byKey(const Key('polar-align-home')), findsNothing);
    await tester.tap(find.byKey(const Key('polar-align-start')));
    await tester.pumpAndSettle();
    expect(api!.calls, ['start']);
  });

  testWidgets("Ready keeps the routine's settings under the button: a pick saves", (tester) async {
    final api = await _pump(tester, const PolarAlignLive());
    expect(find.text('Ready'), findsNothing);
    expect(find.text('1 s'), findsOneWidget);
    await tester.tap(find.text('Single'));
    await tester.pumpAndSettle();
    expect(api!.calls, ['save 1.0 single']);
  });

  testWidgets('a start the rig refuses for a mount away from Home shows that sentence in the panel', (tester) async {
    final api = _RefusingApi(const Refusal(
        cause: RefusalCause.polarAlignNotHome, reason: 'polar-align-not-home', detail: 'the daemon sentence', status: 409));
    await _pump(tester, const PolarAlignLive(), rigApi: api);
    await tester.tap(find.byKey(const Key('polar-align-start')));
    await tester.pumpAndSettle();
    expect(
      tester.widget<Text>(find.byKey(const Key('simple-polar-error'))).data,
      'The mount is not at Home. Send it Home first, then start polar alignment.',
    );
  });

  testWidgets('measuring shows one calm line with the point being taken: no bullseye, no settings, no Done', (tester) async {
    await _pump(tester, const PolarAlignLive(phase: PolarAlignStates.seeding, stage: PolarAlignStages.align, measuringPoint: 2));
    expect(find.text('Measuring — point 2 of 3'), findsOneWidget);
    expect(find.byKey(const Key('simple-polar-bullseye')), findsNothing);
    expect(find.byKey(const Key('simple-polar-setup')), findsNothing);
    expect(find.byKey(const Key('simple-polar-done')), findsNothing);
    expect(find.byKey(const Key('polar-align-start')), findsNothing);
  });

  testWidgets('after Done: Going Home…, then the verdict under Returning Home…', (tester) async {
    await _pump(tester, const PolarAlignLive(phase: PolarAlignStates.homing, stage: PolarAlignStages.verify));
    expect(find.text('Going Home…'), findsOneWidget);
    expect(find.byKey(const Key('polar-align-verdict')), findsNothing);
    final live = ProviderScope.containerOf(tester.element(find.byType(SimplePolarPanel)))
        .read(polarAlignLiveProvider.notifier) as _Live;
    live.frame(const PolarAlignLive(
      phase: PolarAlignStates.homing, stage: PolarAlignStages.verify, checkSolvesDone: 3,
      totalErrorArcmin: 0.5, checkResult: _verdict,
    ));
    await tester.pump();
    expect(tester.widget<Text>(find.byKey(const Key('polar-align-verdict-rating'))).data, 'Excellent');
    expect(tester.widget<Text>(find.byKey(const Key('polar-align-verdict-total'))).data, '0.5′ ± 0.2′ from the pole');
    expect(find.text('Returning Home…'), findsOneWidget);
    expect(find.byKey(const Key('simple-polar-result-done')), findsNothing, reason: 'the mount is still moving');
  });

  testWidgets('the result shows the verdict with Done and Adjust again: Adjust again posts a start, Done leaves the result and sends nothing', (tester) async {
    final api = await _pump(
      tester,
      const PolarAlignLive(
        phase: PolarAlignStates.checked, stage: PolarAlignStages.verify, totalErrorArcmin: 0.5, zone: 'green',
        checkResult: _verdict,
      ),
    );
    expect(tester.widget<Text>(find.byKey(const Key('polar-align-verdict-rating'))).data, 'Excellent');
    expect(find.text("Polar alignment won't limit your exposures."), findsOneWidget);
    expect(find.text('Altitude −0.4′ · Azimuth +0.3′'), findsOneWidget);
    await tester.tap(find.widgetWithText(OutlinedButton, 'Adjust again'));
    await tester.pumpAndSettle();
    expect(api!.calls, ['start']);
    await tester.tap(find.widgetWithText(FilledButton, 'Done'));
    await tester.pumpAndSettle();
    expect(api.calls, ['start'], reason: 'the run is over: Done sends nothing');
    expect(find.byKey(const Key('polar-align-verdict')), findsNothing);
    expect(find.byKey(const Key('polar-align-start')), findsOneWidget, reason: 'Ready again');
    expect(find.text('Last result: Excellent, 0.5′ ± 0.2′ from the pole.'), findsOneWidget);
  });

  testWidgets('a run that failed going Home says so in one sentence, with Start to go again', (tester) async {
    await _pump(
      tester,
      const PolarAlignLive(
        phase: PolarAlignStates.failed, stage: PolarAlignStages.verify,
        errorReason: 'home_failed', errorMessage: 'FindHome was refused by the driver',
      ),
    );
    expect(
      tester.widget<Text>(find.byKey(const Key('simple-polar-ready-note'))).data,
      'The mount did not go Home. Check the mount, send it Home, then start again.',
    );
    expect(find.byKey(const Key('polar-align-start')), findsOneWidget);
  });

  testWidgets('after a Stop the panel is Ready again: one sentence and Start', (tester) async {
    await _pump(
      tester,
      const PolarAlignLive(phase: PolarAlignStates.stopped, stage: PolarAlignStages.align, totalErrorArcmin: 0.8),
    );
    expect(find.text('Polar alignment stopped.'), findsOneWidget);
    expect(find.byKey(const Key('polar-align-start')), findsOneWidget);
    expect(find.byKey(const Key('simple-polar-check-again')), findsNothing);
  });

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
    'while it runs, the panel stays within 520 pt: its headings and caption make it scroll on an iPad (388 pt)',
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

  testWidgets('the panel shows no stepper, checklist or Rough first: idle, running, or after the guide camera could not solve', (tester) async {
    for (final live in const [
      PolarAlignLive(),
      _adjusting,
      PolarAlignLive(phase: PolarAlignStates.failed, stage: PolarAlignStages.align, errorReason: 'seed_solve_failed'),
    ]) {
      await _pump(tester, live);
      expect(find.byKey(const Key('simple-polar-stepper')), findsNothing);
      expect(find.byKey(const Key('polar-preflight')), findsNothing);
      expect(find.byKey(const Key('simple-polar-rough-first')), findsNothing);
    }
  });

  testWidgets(
    'checking shows only its calm line: no bullseye, no settings (#1875)',
    (tester) async {
      await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.checking,
          stage: PolarAlignStages.verify,
          checkSolvesDone: 1,
          checkSolvesTotal: 3,
          altErrorArcmin: 0.6,
          azErrorArcmin: 0.8,
          totalErrorArcmin: 1.0,
          zone: 'green',
        ),
      );
      expect(find.text('Checking the alignment — 2 of 3'), findsOneWidget);
      expect(find.byKey(const Key('simple-polar-bullseye')), findsNothing);
      expect(find.byKey(const Key('simple-polar-setup')), findsNothing);
    },
  );

  testWidgets(
    'verified shows only the verdict: no bullseye, no settings (#1875)',
    (tester) async {
      await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.checked,
          stage: PolarAlignStages.verify,
          altErrorArcmin: 0.3,
          azErrorArcmin: 0.2,
          totalErrorArcmin: 0.36,
          zone: 'green',
          checkResult: _verdict,
        ),
      );
      expect(find.byKey(const Key('polar-align-verdict')), findsOneWidget);
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
          stage: PolarAlignStages.align,
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
          stage: PolarAlignStages.align,
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
    "Simple Mode's verdict rates the error plus twice its 1σ: 0.2′ ± 1.3′ does not read Excellent (#2072)",
    (tester) async {
      await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.checked,
          stage: PolarAlignStages.verify,
          totalErrorArcmin: 0.2,
          totalSigmaArcmin: 1.3,
          checkResult: PolarAlignCheckResult(
              altErrorArcmin: 0.1, azErrorArcmin: 0.17, totalErrorArcmin: 0.2, verdict: 'excellent', totalSigmaArcmin: 1.3),
        ),
      );
      final rating = tester.widget<Text>(find.byKey(const Key('polar-align-verdict-rating')));
      expect(rating.data, 'Very good');
    },
  );

  testWidgets(
    'a hair above the tolerance, Done stays off and says why',
    (tester) async {
      final api = await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.adjusting,
          stage: PolarAlignStages.align,
          altErrorArcmin: 0.7,
          azErrorArcmin: 0.75,
          totalErrorArcmin: 1.01,
          zone: 'yellow',
        ),
        tolerance: 1.0,
      );
      expect(
        tester
            .widget<FilledButton>(find.byKey(const Key('simple-polar-done')))
            .onPressed,
        isNull,
      );
      expect(find.text('Done turns on once the total error is under 1′.'), findsOneWidget);
      expect(api!.calls, isEmpty);
    },
  );

  testWidgets(
    'at the tolerance exactly, Done posts complete: there is no Check button',
    (tester) async {
      final api = await _pump(
        tester,
        const PolarAlignLive(
          phase: PolarAlignStates.adjusting,
          stage: PolarAlignStages.align,
          altErrorArcmin: 0.6,
          azErrorArcmin: 0.8,
          totalErrorArcmin: 1.0,
          zone: 'green',
        ),
        tolerance: 1.0,
      );
      expect(find.byKey(const Key('simple-polar-check')), findsNothing);
      expect(find.byKey(const Key('simple-polar-done-waits')), findsNothing);
      await tester.tap(find.byKey(const Key('simple-polar-done')));
      await tester.pumpAndSettle();
      expect(find.byType(Dialog), findsNothing);
      expect(api!.calls, ['complete'], reason: 'enabled at the tolerance exactly (<=), not only below it');
    },
  );

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
      phase: PolarAlignStates.checked, stage: PolarAlignStages.verify, totalErrorArcmin: 0.36,
      checkResult: PolarAlignCheckResult(
          altErrorArcmin: 0.3, azErrorArcmin: 0.2, totalErrorArcmin: 0.36, verdict: 'excellent', noisy: true),
    ));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('polar-align-verdict-noisy')), findsOneWidget);
  });

  testWidgets('inside the tolerance Done completes it', (tester) async {
    final api = await _pump(
      tester,
      const PolarAlignLive(phase: PolarAlignStates.adjusting, altErrorArcmin: 0.3, azErrorArcmin: -0.5, totalErrorArcmin: 0.6),
    );
    await tester.tap(find.byKey(const Key('simple-polar-done')));
    await tester.pump();
    expect(api!.calls, ['complete']);
  });

  testWidgets("in Preflight, polar align is the last step: the result's Done reads Done · Start imaging and moves on", (tester) async {
    final api = _Api();
    final c = ProviderContainer(overrides: [
      ...polarAlignSectionOverrides(api.section),
      polarAlignApiProvider.overrideWithValue(api),
      // A result outside the green zone: Preflight does not skip the step.
      polarAlignLiveProvider.overrideWith(() => _Live(const PolarAlignLive(
          phase: PolarAlignStates.checked, stage: PolarAlignStages.verify, totalErrorArcmin: 12, zone: 'yellow',
          checkResult: PolarAlignCheckResult(altErrorArcmin: 8, azErrorArcmin: 9, totalErrorArcmin: 12, verdict: 'rough')))),
      mountProvider.overrideWith(FakeMount.atHome),
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
    await tester.tap(find.byKey(const Key('simple-polar-result-done')));
    await tester.pumpAndSettle();
    expect(api.calls, isEmpty, reason: 'the run is over: nothing to complete or stop');
    expect(c.read(simplePreflightProvider), isNot(SimplePreflightStep.polarAlign), reason: 'on to the next step');
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
    Future<_Api?> shutter(WidgetTester tester, PolarAlignLive live,
            {String loopMode = PolarAlignLoopModes.loop, bool server = true, FakeMount? mount}) =>
        _pump(tester, live, loopMode: loopMode, server: server, mount: mount, child: const SimplePolarShutter());

    testWidgets('the mount away from Home: the shutter is off, and a tap says Home comes first', (tester) async {
      final api = await shutter(tester, const PolarAlignLive(), mount: FakeMount.awayFromHome());
      final c = ProviderScope.containerOf(tester.element(find.byKey(const Key('simple-shutter'))));
      expect(tester.widget<SimpleShutterButton>(find.byType(SimpleShutterButton)).onPressed, isNull);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(api!.calls, isEmpty);
      expect(c.read(simpleMessagesProvider).single.title, 'Polar alignment starts with the mount at Home.');
      await tester.pump(const Duration(seconds: 11));
    });

    testWidgets('the mount going Home: the square stops the mount', (tester) async {
      final mount = FakeMount.awayFromHome();
      final api = await shutter(tester, const PolarAlignLive(), mount: mount);
      final c = ProviderScope.containerOf(tester.element(find.byKey(const Key('simple-shutter'))));
      await c.read(mountHomeProvider.notifier).home();
      mount.report(mountWire(atHome: false, runtime: 'slewing'));
      await tester.pump();
      expect(find.bySemanticsLabel('Stop'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(mount.calls, ['home', 'abort']);
      expect(api!.calls, isEmpty, reason: 'no run to stop');
    });

    for (final (step, live) in _running) {
      testWidgets('$step: the square posts stop', (tester) async {
        final api = await shutter(tester, live);
        expect(find.bySemanticsLabel('Stop'), findsOneWidget);
        await tester.tap(find.byKey(const Key('simple-shutter')));
        await tester.pump();
        expect(api!.calls, ['stop']);
      });
    }

    testWidgets('a start that fails says so in a pill', (tester) async {
      final api = _FailingApi();
      final c = ProviderContainer(overrides: [
        polarAlignApiProvider.overrideWithValue(api),
        polarAlignLiveProvider.overrideWith(() => _Live(const PolarAlignLive())),
          mountProvider.overrideWith(FakeMount.atHome),
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
      await tester.pump(); // the mount's state is in
        await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(c.read(simpleMessagesProvider).single.kind, SimpleMessageKind.error);
      expect(find.byType(SnackBar), findsNothing);
    });

    for (final (cause, words) in const [
      (RefusalCause.polarAlignMountParked, 'The mount is parked. Unpark it and send it Home first.'),
      (RefusalCause.polarAlignNotHome, 'The mount is not at Home. Send it Home first, then start polar alignment.'),
    ]) {
      testWidgets("a start the rig refuses (${cause.token}) says why in a pill, in the app's words", (tester) async {
        final api = _RefusingApi(Refusal(cause: cause, reason: cause.token, detail: 'the daemon sentence', status: 409));
        final c = ProviderContainer(overrides: [
          polarAlignApiProvider.overrideWithValue(api),
          polarAlignLiveProvider.overrideWith(() => _Live(const PolarAlignLive())),
          mountProvider.overrideWith(FakeMount.atHome),
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
        await tester.pump(); // the mount's state is in
        await tester.tap(find.byKey(const Key('simple-shutter')));
        await tester.pumpAndSettle();
        final pill = c.read(simpleMessagesProvider).single;
        expect(pill.kind, SimpleMessageKind.error);
        expect('${pill.title} ${pill.detail ?? ''}', contains(words));
      });
    }

    testWidgets('a start runs through the shared module: busy while it runs, its failure in the pill only (#1764)', (tester) async {
      final api = _SlowFailingApi();
      final c = ProviderContainer(overrides: [
        polarAlignApiProvider.overrideWithValue(api),
        polarAlignLiveProvider.overrideWith(() => _Live(const PolarAlignLive())),
          mountProvider.overrideWith(FakeMount.atHome),
      ]);
      addTearDown(c.dispose);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: c,
        child: const MaterialApp(home: Scaffold(body: Center(child: SimplePolarShutter()))),
      ));
      await tester.pump(); // the mount's state is in
        await tester.tap(find.byKey(const Key('simple-shutter')));
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
      expect(find.byType(Dialog), findsNothing);
      expect(api!.calls, ['start']);
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
