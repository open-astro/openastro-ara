import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/l10n.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/screens/simple/simple_polar_panel.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/widgets/imaging/polar_align_panel.dart';
import 'package:openastroara/state/settings/polar_align_settings_state.dart';
import '../support/polar_align_section.dart';

/// #2083 — NINA parity on the polar-align screens: the hand-turn run's prompt,
/// the azimuth readout's convention, and NINA's large- and huge-error
/// warnings, in the Full UI and Simple Mode.

class _Api implements PolarAlignClient {
  @override
  Future<PolarAlignPreflight?> preflight({String? stage, bool handTurn = false}) async => null;
  @override
  Future<PolarAlignStartPlan> startPlan({String? stage, bool handTurn = false}) async =>
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

/// [settle] false for a screen with a spinner, which never settles.
Future<void> _pump(
  WidgetTester tester,
  PolarAlignLive live,
  Widget panel, {
  bool settle = true,
}) async {
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        polarAlignApiProvider.overrideWithValue(_Api()),
        polarAlignLiveProvider.overrideWith(() => _Live(live)),
        // #1807 — the polar-align settings are the rig's profile section.
        polarAlignSettingsProvider.overrideWith(
          () => FakePolarAlignSection(loaded: true),
        ),
      ],
      child: MaterialApp(
        home: Scaffold(body: SingleChildScrollView(child: panel)),
      ),
    ),
  );
  if (settle) {
    await tester.pumpAndSettle();
  } else {
    await tester.pump(const Duration(milliseconds: 100));
  }
}

WsEvent _event(String type, Map<String, dynamic> payload) => WsEvent(
  type: type,
  ts: DateTime.utc(2026, 11, 20, 4),
  seq: 1,
  payload: payload,
);

final _en = lookupAppLocalizations(const Locale('en'));
final _pt = lookupAppLocalizations(const Locale('pt', 'BR'));

const _waiting = PolarAlignLive(
  phase: PolarAlignStates.seeding,
  stage: PolarAlignStages.fine,
  handTurn: true,
  handTurnWait: PolarAlignHandTurn(
    point: 2,
    direction: PolarAlignDirections.west,
    targetDeg: 10,
    turnedDeg: 4.2,
  ),
);

const _adjusting = PolarAlignLive(
  phase: PolarAlignStates.adjusting,
  stage: PolarAlignStages.fine,
  altErrorArcmin: 3.2,
  azErrorArcmin: 14.1,
  totalErrorArcmin: 14.4,
  zone: 'yellow',
);

void main() {
  test("NINA's azimuth figure is Ara's true arc over cos(latitude): about 1.5x at 48° (#2083)", () {
    // A 10′ true arc at latitude 48° is 10 / cos 48° = 14.94′ in NINA's convention.
    expect(ninaAzimuthArcmin(10.0, 48.0), closeTo(14.945, 0.001));
    expect(
      ninaAzimuthArcmin(-10.0, -48.0),
      closeTo(-14.945, 0.001),
      reason: 'the south, signed as Ara',
    );
    expect(ninaAzimuthArcmin(null, 48.0), isNull);
    expect(ninaAzimuthArcmin(10.0, null), isNull);
  });

  test(
    "NINA's warnings: an initial error over 2° is large, over 10° huge (#2083)",
    () {
      expect(
        polarInitialErrorWarning(3 * 60.0),
        PolarInitialErrorWarning.large,
      );
      expect(
        polarInitialErrorWarning(12 * 60.0),
        PolarInitialErrorWarning.huge,
      );
      expect(polarInitialErrorWarning(90.0), PolarInitialErrorWarning.none);
      expect(polarInitialErrorWarning(null), PolarInitialErrorWarning.none);
      expect(
        polarInitialErrorText(_pt, 12 * 60.0),
        startsWith('O erro no início passou de 10°'),
      );
    },
  );

  test('the hand-turn prompt says the turn, then how far, or that it went the other way (#2083)', () {
    const ask = PolarAlignHandTurn(
      point: 2,
      direction: PolarAlignDirections.east,
      targetDeg: 10,
    );
    expect(
      polarHandTurnText(_en, ask),
      'Turn RA by hand about 10° toward the east, then let go.',
    );
    expect(
      polarHandTurnText(
        _en,
        const PolarAlignHandTurn(
          point: 2,
          direction: PolarAlignDirections.east,
          targetDeg: 10,
          turnedDeg: 4.24,
        ),
      ),
      'Turn RA by hand about 10° toward the east, then let go. Turned 4.2° so far.',
    );
    expect(
      polarHandTurnText(
        _en,
        const PolarAlignHandTurn(
          point: 3,
          direction: PolarAlignDirections.east,
          targetDeg: 10,
          turnedDeg: -3,
        ),
      ),
      'Turn RA by hand about 10° toward the east, then let go. That went the other way: turn toward the east.',
    );
    expect(polarHandTurnText(_en, null), isNull);
    expect(
      polarHandTurnText(_pt, ask),
      'Gire a AR à mão cerca de 10° em direção ao leste e solte.',
    );
  });

  test('polar_align.hand_turn folds into the live view; the next stage and progress clear it (#2083)', () {
    final started = foldPolarAlignEvent(
      const PolarAlignLive(),
      _event(PolarAlignWsEvents.started, {
        'state': 'seeding',
        'stage': 'fine',
        'hand_turn': true,
      }),
    )!;
    expect(started.handTurn, isTrue);
    final asked = foldPolarAlignEvent(
      started,
      _event(PolarAlignWsEvents.handTurn, {
        'point': 2,
        'direction': 'west',
        'target_deg': 10.0,
        'turned_deg': 3.5,
        'waiting': true,
      }),
    )!;
    expect(
      (
        asked.handTurnWait?.point,
        asked.handTurnWait?.direction,
        asked.handTurnWait?.turnedDeg,
      ),
      (2, 'west', 3.5),
    );
    final taken = foldPolarAlignEvent(
      asked,
      _event(PolarAlignWsEvents.handTurn, {
        'point': 2,
        'direction': 'west',
        'target_deg': 10.0,
        'turned_deg': 9.6,
        'waiting': false,
      }),
    )!;
    expect(taken.handTurnWait, isNull, reason: 'the turn is in');
    final nextStage = foldPolarAlignEvent(
      asked,
      _event(PolarAlignWsEvents.stage, {'stage': 'check', 'state': 'checking'}),
    )!;
    expect(
      nextStage.handTurnWait,
      isNull,
      reason: "a stage that ended during the wait leaves no turn prompt behind",
    );
    final seeded = foldPolarAlignEvent(
      asked,
      _event(PolarAlignWsEvents.progress, {
        'iteration': 0,
        'altitude_error_arcmin': 150.0,
        'azimuth_error_arcmin': 90.0,
        'total_error_arcmin': 174.9,
      }),
    )!;
    expect(seeded.handTurnWait, isNull);
    expect(
      seeded.initialErrorArcmin,
      174.9,
      reason:
          "iteration 0 is the seed's readout: the error the stage started from",
    );
    final later = foldPolarAlignEvent(
      seeded,
      _event(PolarAlignWsEvents.progress, {
        'iteration': 3,
        'altitude_error_arcmin': 1.0,
        'azimuth_error_arcmin': 1.0,
        'total_error_arcmin': 1.4,
      }),
    )!;
    expect(
      later.initialErrorArcmin,
      174.9,
      reason: 'a live frame does not move the start',
    );
    final status = PolarAlignStatus.fromJson({
      'state': 'seeding',
      'hand_turn': true,
      'hand_turn_wait': {
        'point': 3,
        'direction': 'east',
        'target_deg': 10.0,
        'turned_deg': null,
        'waiting': true,
      },
    });
    expect((status.handTurn, status.handTurnWait?.point), (true, 3));
  });

  testWidgets(
    'the Full UI asks for the hand turn while it measures, and never says to leave the mount alone (#2083)',
    (tester) async {
      await _pump(tester, _waiting, const PolarAlignPanel(), settle: false);
      expect(find.byKey(const Key('polar-align-hand-ask')), findsOneWidget);
      expect(
        find.text(
          'Turn RA by hand about 10° toward the west, then let go. Turned 4.2° so far.',
        ),
        findsOneWidget,
      );
      expect(find.textContaining('Leave the mount alone'), findsNothing);
    },
  );

  testWidgets('Simple Mode asks for the hand turn while it measures (#2083)', (
    tester,
  ) async {
    await _pump(tester, _waiting, const SimplePolarPanel());
    expect(find.byKey(const Key('simple-polar-hand-turn')), findsOneWidget);
    expect(
      find.text(
        'Turn RA by hand about 10° toward the west, then let go. Turned 4.2° so far.',
      ),
      findsOneWidget,
    );
  });

  testWidgets(
    "the Full UI's azimuth says it is a true arc, and warns on a huge start (#2083)",
    (tester) async {
      tester.view.physicalSize = const Size(1400, 2400);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await _pump(
        tester,
        _adjusting.copyWith(initialErrorArcmin: 12 * 60.0),
        const PolarAlignPanel(),
      );
      expect(find.textContaining('Azimuth · true arc (cos alt)'), findsWidgets);
      expect(
        find.byKey(const Key('polar-align-initial-error')),
        findsOneWidget,
      );
      expect(find.textContaining('over 10°'), findsOneWidget);
    },
  );

  testWidgets(
    "Simple Mode's azimuth says it is a true arc, and warns on a large start (#2083)",
    (tester) async {
      await _pump(
        tester,
        _adjusting.copyWith(initialErrorArcmin: 3 * 60.0),
        const SimplePolarPanel(),
      );
      expect(find.text('Azimuth · true arc (cos alt)'), findsOneWidget);
      expect(
        find.byKey(const Key('simple-polar-initial-error')),
        findsOneWidget,
      );
      expect(find.textContaining('over 2°'), findsOneWidget);
    },
  );
}
