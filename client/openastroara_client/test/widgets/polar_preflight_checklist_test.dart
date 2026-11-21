import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/widgets/imaging/polar_align_panel.dart';

import '../support/polar_align_section.dart';
import '../support/polar_preflight.dart';

/// A rig whose pre-flight list follows the profile's polar-align section
/// ([section]): the clearance check reads the connected mount's entry.
class _Rig implements PolarAlignClient {
  final section = FakePolarAlignSection(loaded: true);
  int preflightReads = 0;
  bool mountConnected = true;
  bool pointingFails = false;

  @override
  Future<PolarAlignPreflight?> preflight({String? stage}) async {
    preflightReads++;
    if (!mountConnected) return _noMount;
    if (pointingFails) return _pointingFails;
    return rigPreflightFor(section.initial);
  }

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

Future<_Rig> _pump(WidgetTester tester) async {
  final rig = _Rig();
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        ...polarAlignSectionOverrides(rig.section),
        polarAlignApiProvider.overrideWithValue(rig),
        polarAlignLiveProvider.overrideWith(_Idle.new),
      ],
      child: const MaterialApp(
        home: Scaffold(body: SingleChildScrollView(child: PolarAlignPanel())),
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
    final rig = await _pump(tester);

    expect(
      find.byKey(const Key('polar-preflight-check-clearance_confirmed')),
      findsOneWidget,
      reason: 'the failing check is listed on the Start card',
    );
    expect(_onPressed(tester, 'polar-align-start'), isNull);
    expect(
      _onPressed(tester, 'polar-align-start-check'),
      isNull,
      reason: 'Check is a start too',
    );

    // The rig's list passes now (the mount's entry confirmed elsewhere): Start is back.
    rig.section.initial = rigPolarAlign.withMountClearance(
      preflightMountKey,
      const PolarAlignMountClearance(
        maxCwAngleDeg: 60,
        clearanceConfirmed: true,
      ),
    );
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
        const PolarAlignMountClearance(
          maxCwAngleDeg: 60,
          clearanceConfirmed: true,
        ),
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
        const PolarAlignMountClearance(
          maxCwAngleDeg: 60,
          clearanceConfirmed: true,
        ),
        reason: "the mount's own entry, confirmed",
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
}
