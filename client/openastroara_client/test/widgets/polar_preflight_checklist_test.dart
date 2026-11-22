import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/services/refusal.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/polar_align/polar_align_preflight_state.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/widgets/imaging/polar_align_panel.dart';
import 'package:openastroara/widgets/imaging/polar_preflight_checklist.dart';

import '../support/polar_align_section.dart';
import '../support/polar_preflight.dart';

/// A rig whose pre-flight list follows the profile's polar-align section
/// ([section]): the clearance check reads the connected mount's entry.
class _Rig implements PolarAlignClient {
  final section = FakePolarAlignSection(loaded: true);
  int preflightReads = 0;
  bool mountConnected = true;
  bool pointingFails = false;
  bool parked = false;

  /// How far the run turns RA each way: the turn size on the Start card.
  double reachDeg = 20.5;
  final List<PolarAlignStartPlan> started = [];

  bool get _confirmed =>
      rigPreflightFor(section.initial, reachDeg: reachDeg).ready;

  @override
  Future<PolarAlignPreflight?> preflight({String? stage, bool handTurn = false}) async {
    preflightReads++;
    if (!mountConnected) return _noMount;
    if (pointingFails) return _pointingFails;
    return rigPreflightFor(
      section.initial,
      handTurn: handTurn,
      parked: parked,
      reachDeg: reachDeg,
    );
  }

  // As the rig: a motor start's plan is refused while the clearance is not
  // confirmed; a hand-turn start's is not (Joey, 2026-11-21).
  @override
  Future<PolarAlignStartPlan> startPlan({String? stage, bool handTurn = false}) async {
    if (!handTurn && !_confirmed) {
      throw const Refusal(
        cause: RefusalCause.polarAlignClearanceNotConfirmed,
        reason: 'polar-align-clearance-not-confirmed',
        detail: "Confirm this mount's clearance first",
        status: 409,
      );
    }
    return PolarAlignStartPlan(
      stage: stage,
      direction: PolarAlignDirections.east,
      handTurnAvailable: true,
    );
  }

  @override
  Future<PolarAlignStatus?> getStatus() async => null;
  @override
  Future<void> start(PolarAlignStartPlan plan) async => started.add(plan);
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

// The rig's list with no mount connected: its check fails, so Start is off.
const _noMount = PolarAlignPreflight(
  ready: false,
  checks: [
    PolarAlignPreflightCheck(
      id: 'mount_connected',
      status: PolarAlignPreflightStatuses.fail,
      detail: 'The mount is not connected.',
    ),
  ],
  planned: PolarAlignPreflightPlanned(
    turnStepDeg: 10,
    checkStepDeg: 10,
    turns: 2,
    maxSweepDeg: 20,
    reachDeg: 20.5,
  ),
);

// The fine stage's start pointing fails (its sweep limit); the clearance is
// confirmed. A Check plans with its own step, so the rig judges it on Start.
const _pointingFails = PolarAlignPreflight(
  ready: false,
  checks: [
    PolarAlignPreflightCheck(
      id: PolarAlignPreflightChecks.clearanceConfirmed,
      status: PolarAlignPreflightStatuses.ok,
      detail: 'You confirmed this mount clears the pier.',
    ),
    PolarAlignPreflightCheck(
      id: PolarAlignPreflightChecks.startPointing,
      status: PolarAlignPreflightStatuses.fail,
      detail: 'The turns would pass the 30° sweep limit.',
    ),
  ],
  planned: PolarAlignPreflightPlanned(
    turnStepDeg: 10,
    checkStepDeg: 30,
    turns: 2,
    maxSweepDeg: 20,
    reachDeg: 20.5,
  ),
);

MountStatus _mountStatus(String state) => MountStatus.fromJson({
  'device_id': preflightMountKey,
  'name': 'Mount',
  'state': state,
  'runtime': const <String, dynamic>{},
});

/// The mount's status, set by the test (Settings › Devices connecting it).
class _Mount extends MountNotifier {
  _Mount(this.initial);
  final MountStatus initial;
  @override
  Future<MountStatus?> build() async => initial;
  void set(MountStatus s) => state = AsyncData(s);
}

class _Idle extends PolarAlignLiveNotifier {
  @override
  PolarAlignLive build() => const PolarAlignLive();
}

/// The routine's phase, set by the test (a run starting, turning, adjusting).
class _Live extends PolarAlignLiveNotifier {
  @override
  PolarAlignLive build() => const PolarAlignLive();
  void enter(String phase) => state = state.copyWith(phase: phase);
}

MountStatus _connectedMount({required bool slewing}) => MountStatus.fromJson({
  'device_id': preflightMountKey,
  'name': 'Mount',
  'state': 'connected',
  'runtime': {'state': slewing ? 'slewing' : 'idle'},
});

Future<_Rig> _pump(
  WidgetTester tester, {
  bool parked = false,
  PolarAlignSettings? settings,
  Widget child = const PolarAlignPanel(),
}) async {
  final rig = _Rig()..parked = parked;
  if (settings != null) rig.section.initial = settings;
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        ...polarAlignSectionOverrides(rig.section),
        polarAlignApiProvider.overrideWithValue(rig),
        polarAlignLiveProvider.overrideWith(_Idle.new),
      ],
      child: MaterialApp(
        home: Scaffold(body: SingleChildScrollView(child: child)),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return rig;
}

VoidCallback? _onPressed(WidgetTester tester, String key) =>
    tester.widget<ButtonStyleButton>(find.byKey(Key(key))).onPressed;

void main() {
  testWidgets('Start is disabled while a pre-flight check fails', (
    tester,
  ) async {
    final rig = await _pump(tester, parked: true);

    expect(
      find.byKey(const Key('polar-preflight-check-mount_parked')),
      findsOneWidget,
      reason: 'the failing check is listed on the Start card',
    );
    expect(_onPressed(tester, 'polar-align-start'), isNull);
    expect(
      _onPressed(tester, 'polar-align-start-check'),
      isNull,
      reason: 'Check is a start too',
    );

    // The rig's list passes now (the mount unparked elsewhere): Start is back.
    rig.parked = false;
    await tester.tap(find.byKey(const Key('polar-preflight-refresh')));
    await tester.pumpAndSettle();
    expect(_onPressed(tester, 'polar-align-start'), isNotNull);
  });

  testWidgets(
    "A failing start pointing turns Start off but not Check, whose step is its own",
    (tester) async {
      final rig = _Rig()..pointingFails = true;
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            ...polarAlignSectionOverrides(rig.section),
            polarAlignApiProvider.overrideWithValue(rig),
            polarAlignLiveProvider.overrideWith(_Idle.new),
          ],
          child: const MaterialApp(
            home: Scaffold(
              body: SingleChildScrollView(child: PolarAlignPanel()),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(_onPressed(tester, 'polar-align-start'), isNull);
      expect(
        _onPressed(tester, 'polar-align-start-check'),
        isNotNull,
        reason: "the list is the fine stage's; the rig plans the Check itself",
      );
    },
  );

  testWidgets(
    'Connecting the mount turns Start back on without a manual refresh',
    (tester) async {
      final mount = _Mount(_mountStatus('disconnected'));
      final rig = _Rig()..mountConnected = false;
      rig.section.initial = rigPolarAlign.withMountClearance(
        preflightMountKey,
        const PolarAlignMountClearance(confirmedReachDeg: 20.5),
      );
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            ...polarAlignSectionOverrides(rig.section),
            polarAlignApiProvider.overrideWithValue(rig),
            polarAlignLiveProvider.overrideWith(_Idle.new),
            mountProvider.overrideWith(() => mount),
          ],
          child: const MaterialApp(
            home: Scaffold(
              body: SingleChildScrollView(child: PolarAlignPanel()),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(_onPressed(tester, 'polar-align-start'), isNull);

      // Settings › Devices connects the mount; the Start card stays mounted.
      rig.mountConnected = true;
      mount.set(_mountStatus('connected'));
      await tester.pumpAndSettle();

      expect(
        _onPressed(tester, 'polar-align-start'),
        isNotNull,
        reason: 'the list was read again when the mount connected',
      );
      expect(
        find.byKey(const Key('polar-preflight-check-mount_connected')),
        findsNothing,
      );
    },
  );

  testWidgets(
    'The clearance dialog confirms the mount and its check turns ok',
    (tester) async {
      final rig = await _pump(tester);

      await tester.tap(find.byKey(const Key('polar-preflight-clearance')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-clearance-dialog')), findsOneWidget);
      await tester.tap(find.byKey(const Key('polar-clearance-confirm')));
      await tester.pumpAndSettle();

      expect(rig.section.puts, hasLength(1));
      expect(
        rig.section.puts.single.mountClearance[preflightMountKey],
        const PolarAlignMountClearance(confirmedReachDeg: 20.5),
        reason: "the mount's own entry, confirmed for the run's reach",
      );
      expect(
        find.text('Change clearance…'),
        findsOneWidget,
        reason: 'the clearance check passes now',
      );
      expect(
        find.byKey(const Key('polar-preflight-check-clearance_confirmed')),
        findsNothing,
        reason: 'a passing check folds away with the others',
      );
      expect(_onPressed(tester, 'polar-align-start'), isNotNull);
    },
  );

  // Joey, 2026-11-21: a Turn RA by hand start moves nothing, so an unconfirmed
  // clearance only warns for it; the card reads that list and Start stays on.
  testWidgets(
    'The list is read for a Turn RA by hand start: an unconfirmed clearance warns and Start stays on',
    (tester) async {
      await _pump(tester);

      final row = find.byKey(
        const Key('polar-preflight-check-clearance_confirmed'),
      );
      expect(row, findsOneWidget, reason: 'a warning is always shown');
      expect(
        find.descendant(
          of: row,
          matching: find.byIcon(Icons.warning_amber_outlined),
        ),
        findsOneWidget,
        reason: 'a warn, not a fail',
      );
      expect(_onPressed(tester, 'polar-align-start'), isNotNull);
      expect(_onPressed(tester, 'polar-align-start-check'), isNotNull);
      expect(find.text('Confirm clearance…'), findsOneWidget);
    },
  );

  testWidgets(
    'With the clearance unconfirmed, Start offers Turn RA by hand alone and starts a hand-turn run',
    (tester) async {
      final rig = await _pump(tester);

      await tester.tap(find.byKey(const Key('polar-align-start')));
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('polar-start-confirm')), findsOneWidget);
      expect(
        _onPressed(tester, 'polar-start-confirm-go'),
        isNull,
        reason: 'the motor waits for the clearance',
      );
      expect(
        find.textContaining("This mount's clearance is not confirmed"),
        findsOneWidget,
      );
      await tester.tap(find.byKey(const Key('polar-start-confirm-hand')));
      await tester.pumpAndSettle();

      expect(rig.started, hasLength(1));
      expect(rig.started.single.handTurn, isTrue, reason: 'nothing moves');
    },
  );

  testWidgets(
    "Take back in Settings withdraws this mount's clearance confirmation",
    (tester) async {
      final rig = await _pump(
        tester,
        settings: rigPolarAlign.withMountClearance(
          preflightMountKey,
          const PolarAlignMountClearance(confirmedReachDeg: 45),
        ),
        child: const PolarClearanceSettingsRow(),
      );
      expect(
        find.text('Turns up to 20.5° each way, room confirmed for this mount'),
        findsOneWidget,
      );

      await tester.tap(
        find.byKey(const Key('polar-clearance-setting-take-back')),
      );
      await tester.pumpAndSettle();

      expect(
        rig.section.puts.single.mountClearance.containsKey(preflightMountKey),
        isFalse,
        reason: "the mount's confirmation removed",
      );
      expect(
        find.textContaining('Turns up to 20.5° each way, not confirmed'),
        findsOneWidget,
        reason: 'the clearance check does not pass any more',
      );
      expect(
        find.byKey(const Key('polar-clearance-setting-take-back')),
        findsNothing,
      );
    },
  );

  testWidgets('The clearance dialog takes back a confirmed mount', (
    tester,
  ) async {
    final rig = await _pump(
      tester,
      settings: rigPolarAlign.withMountClearance(
        preflightMountKey,
        const PolarAlignMountClearance(confirmedReachDeg: 20.5),
      ),
    );

    await tester.tap(find.byKey(const Key('polar-preflight-clearance')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('polar-clearance-take-back')));
    await tester.pumpAndSettle();

    expect(
      rig.section.puts.single.mountClearance.containsKey(preflightMountKey),
      isFalse,
    );
    expect(find.text('Confirm clearance…'), findsOneWidget);
  });

  // #2165 (Joey, 2026-11-21): no slider, no angle, no tick: one question
  // with the rig's reach and one button.
  testWidgets(
    "The clearance dialog asks once about the rig's reach, with no slider or tick",
    (tester) async {
      await _pump(tester);

      await tester.tap(find.byKey(const Key('polar-preflight-clearance')));
      await tester.pumpAndSettle();

      expect(
        find.text(
          'Polar Align will turn the mount up to 20.5° each way. Is there room for the tube, counterweight and cables to move that far?',
        ),
        findsOneWidget,
        reason: "the rig's reach: two 10° turns and the 0.5° test",
      );
      expect(find.byType(Slider), findsNothing);
      expect(find.byType(Checkbox), findsNothing);
      expect(find.text('Yes, there is room'), findsOneWidget);
      expect(
        _onPressed(tester, 'polar-clearance-confirm'),
        isNotNull,
        reason: 'one tap confirms',
      );
    },
  );

  testWidgets(
    'A bigger turn size asks again and the dialog confirms the new reach',
    (tester) async {
      final rig = _Rig()..reachDeg = 60.5;
      rig.section.initial = rigPolarAlign.withMountClearance(
        preflightMountKey,
        const PolarAlignMountClearance(confirmedReachDeg: 20.5),
      );
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            ...polarAlignSectionOverrides(rig.section),
            polarAlignApiProvider.overrideWithValue(rig),
            polarAlignLiveProvider.overrideWith(_Idle.new),
          ],
          child: const MaterialApp(
            home: Scaffold(
              body: SingleChildScrollView(child: PolarAlignPanel()),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(
        find.text('Confirm clearance…'),
        findsOneWidget,
        reason: 'the 20.5° confirmation does not cover a 60.5° run',
      );

      await tester.tap(find.byKey(const Key('polar-preflight-clearance')));
      await tester.pumpAndSettle();
      expect(
        tester.widget<Text>(find.byKey(const Key('polar-clearance-dialog'))).data,
        contains('up to 60.5° each way'),
      );
      await tester.tap(find.byKey(const Key('polar-clearance-confirm')));
      await tester.pumpAndSettle();

      expect(
        rig.section.puts.single.mountClearance[preflightMountKey],
        const PolarAlignMountClearance(confirmedReachDeg: 60.5),
      );
      expect(find.text('Change clearance…'), findsOneWidget);
    },
  );

  testWidgets(
    'The Start card says how far the run turns the mount even when every check passes',
    (tester) async {
      await _pump(
        tester,
        settings: rigPolarAlign.withMountClearance(
          preflightMountKey,
          const PolarAlignMountClearance(confirmedReachDeg: 20.5),
        ),
      );

      expect(
        find.byKey(const Key('polar-preflight-check-clearance_confirmed')),
        findsNothing,
        reason: 'the passing clearance row folds away',
      );
      expect(
        find.text('Polar align turns the mount up to 20.5° each way.'),
        findsOneWidget,
      );
    },
  );

  test('The list is not read again while a run moves the mount', () async {
    final rig = _Rig();
    final mount = _Mount(_connectedMount(slewing: false));
    final container = ProviderContainer(
      overrides: [
        ...polarAlignSectionOverrides(rig.section),
        polarAlignApiProvider.overrideWithValue(rig),
        polarAlignLiveProvider.overrideWith(_Live.new),
        mountProvider.overrideWith(() => mount),
      ],
    );
    addTearDown(container.dispose);
    final sub = container.listen(polarAlignPreflightProvider, (_, _) {});
    addTearDown(sub.close);
    await container.read(polarAlignPreflightProvider.future);
    await pumpEventQueue();
    final live = container.read(polarAlignLiveProvider.notifier) as _Live;

    live.enter(PolarAlignStates.seeding);
    await pumpEventQueue();
    final readsAtStart = rig.preflightReads;
    // The seed's turns: the mount's state flips with each one.
    for (var i = 0; i < 3; i++) {
      mount.set(_connectedMount(slewing: true));
      await pumpEventQueue();
      mount.set(_connectedMount(slewing: false));
      await pumpEventQueue();
    }
    expect(
      rig.preflightReads,
      readsAtStart,
      reason: 'no rig read while the run turns the mount',
    );

    live.enter(PolarAlignStates.adjusting);
    await pumpEventQueue();
    expect(
      rig.preflightReads,
      readsAtStart + 1,
      reason: 'read once the turns are over, for the adjust screen\'s Check',
    );
  });
}
