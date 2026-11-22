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
  final List<PolarAlignStartPlan> started = [];

  bool get _confirmed => rigPreflightFor(section.initial).ready;

  @override
  Future<PolarAlignPreflight?> preflight({String? stage}) async {
    preflightReads++;
    if (!mountConnected) return _noMount;
    if (pointingFails) return _pointingFails;
    return rigPreflightFor(section.initial, parked: parked);
  }

  // As the rig: a start's plan is refused while the clearance is not confirmed.
  @override
  Future<PolarAlignStartPlan> startPlan({String? stage}) async {
    if (!_confirmed) {
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
    maxCwAngleDeg: 60,
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
    maxCwAngleDeg: 60,
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
    // The clearance is confirmed: only the parked mount fails (#2170: an
    // unconfirmed clearance fails the list too).
    final rig = await _pump(
      tester,
      parked: true,
      settings: rigPolarAlign.withMountClearance(
        preflightMountKey,
        const PolarAlignMountClearance.confirmedAt(60),
      ),
    );

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
        const PolarAlignMountClearance.confirmedAt(60),
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
      expect(
        find.text('Counterweight limit: 60° from counterweight-down'),
        findsOneWidget,
        reason: "pre-filled with the rig's limit for this mount",
      );
      expect(
        _onPressed(tester, 'polar-clearance-confirm'),
        isNull,
        reason: 'Confirm waits for the tick',
      );

      await tester.tap(find.byKey(const Key('polar-clearance-checked')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('polar-clearance-confirm')));
      await tester.pumpAndSettle();

      expect(rig.section.puts, hasLength(1));
      expect(
        rig.section.puts.single.mountClearance[preflightMountKey],
        const PolarAlignMountClearance.confirmedAt(60),
        reason: "the mount's own entry, confirmed for its limit",
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

  // #2170 — there is no hand-turn start to fall back on: an unconfirmed
  // clearance fails the list, so Start and Check stay off until it is confirmed.
  testWidgets(
    'An unconfirmed clearance fails the list and Start stays off',
    (tester) async {
      final rig = await _pump(tester);

      final row = find.byKey(
        const Key('polar-preflight-check-clearance_confirmed'),
      );
      expect(row, findsOneWidget, reason: 'the failing check is listed');
      expect(
        find.descendant(
          of: row,
          matching: find.byIcon(Icons.warning_amber_outlined),
        ),
        findsNothing,
        reason: 'a fail, not a warn',
      );
      expect(_onPressed(tester, 'polar-align-start'), isNull);
      expect(_onPressed(tester, 'polar-align-start-check'), isNull);
      expect(find.text('Confirm clearance…'), findsOneWidget);
      expect(rig.started, isEmpty);
    },
  );

  testWidgets(
    "Take back in Settings withdraws this mount's clearance confirmation",
    (tester) async {
      final rig = await _pump(
        tester,
        settings: rigPolarAlign.withMountClearance(
          preflightMountKey,
          const PolarAlignMountClearance.confirmedAt(45),
        ),
        child: const PolarClearanceSettingsRow(),
      );
      expect(
        find.text('45° from counterweight-down, confirmed for this mount'),
        findsOneWidget,
      );

      await tester.tap(
        find.byKey(const Key('polar-clearance-setting-take-back')),
      );
      await tester.pumpAndSettle();

      expect(
        rig.section.puts.single.mountClearance[preflightMountKey],
        const PolarAlignMountClearance.notConfirmed(45),
        reason: 'the limit kept, not confirmed',
      );
      expect(
        find.textContaining('45° from counterweight-down, not confirmed'),
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
        const PolarAlignMountClearance.confirmedAt(60),
      ),
    );

    await tester.tap(find.byKey(const Key('polar-preflight-clearance')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('polar-clearance-take-back')));
    await tester.pumpAndSettle();

    expect(
      rig.section.puts.single.mountClearance[preflightMountKey],
      const PolarAlignMountClearance.notConfirmed(60),
    );
    expect(find.text('Confirm clearance…'), findsOneWidget);
  });

  testWidgets('Moving the limit in the clearance dialog clears its tick', (
    tester,
  ) async {
    await _pump(tester);

    await tester.tap(find.byKey(const Key('polar-preflight-clearance')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('polar-clearance-checked')));
    await tester.pumpAndSettle();
    expect(_onPressed(tester, 'polar-clearance-confirm'), isNotNull);

    await tester.drag(
      find.byKey(const Key('polar-clearance-limit')),
      const Offset(-120, 0),
    );
    await tester.pumpAndSettle();

    expect(
      find.text('Counterweight limit: 60° from counterweight-down'),
      findsNothing,
      reason: 'the limit moved',
    );
    expect(
      _onPressed(tester, 'polar-clearance-confirm'),
      isNull,
      reason: 'a confirmation is for one limit: tick again',
    );
  });

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
