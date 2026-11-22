import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/screens/simple/simple_polar_panel.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart'
    show SimpleShutterButton;
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/services/refusal.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';

import '../../support/polar_align_section.dart';
import '../../support/polar_preflight.dart';

/// A rig whose pre-flight list follows the profile's polar-align section.
class _Rig implements PolarAlignClient {
  final section = FakePolarAlignSection(loaded: true);
  bool parked = true;

  /// How far the fine run turns RA each way (the list's), and a Check (#2165).
  double reachDeg = 20.5;
  double checkReachDeg = 20.5;
  final List<PolarAlignStartPlan> started = [];

  @override
  Future<PolarAlignPreflight?> preflight({String? stage}) async =>
      rigPreflightFor(section.initial, parked: parked, reachDeg: reachDeg);

  // As the rig: a start is refused while its own reach is not confirmed.
  @override
  Future<PolarAlignStartPlan> startPlan({String? stage}) async {
    final reach = stage == PolarAlignStages.check ? checkReachDeg : reachDeg;
    if (!rigPreflightFor(section.initial, reachDeg: reach).ready) {
      throw Refusal(
        cause: RefusalCause.polarAlignClearanceNotConfirmed,
        reason: 'polar-align-clearance-not-confirmed',
        detail: "Confirm this mount's clearance first",
        parameters: {'reach_deg': reach},
        status: 409,
      );
    }
    return PolarAlignStartPlan(stage: stage, direction: PolarAlignDirections.east);
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

class _Idle extends PolarAlignLiveNotifier {
  @override
  PolarAlignLive build() => const PolarAlignLive();
}

/// After a Stop: Check again shows.
class _Stopped extends PolarAlignLiveNotifier {
  @override
  PolarAlignLive build() => const PolarAlignLive(
    phase: PolarAlignStates.stopped,
    stage: PolarAlignStages.fine,
    totalErrorArcmin: 0.8,
  );
}

/// Simple Mode's panel after a Stop, its fine run's reach [reachDeg] past the
/// 20.5° the mount is confirmed for, a Check's [checkReachDeg].
Future<_Rig> _pumpStopped(
  WidgetTester tester, {
  required double reachDeg,
  required double checkReachDeg,
}) async {
  final rig = _Rig()
    ..parked = false
    ..reachDeg = reachDeg
    ..checkReachDeg = checkReachDeg;
  rig.section.initial = rigPolarAlign.withMountClearance(
    preflightMountKey,
    const PolarAlignMountClearance(confirmedReachDeg: 20.5),
  );
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        ...polarAlignSectionOverrides(rig.section),
        polarAlignApiProvider.overrideWithValue(rig),
        polarAlignLiveProvider.overrideWith(_Stopped.new),
      ],
      child: const MaterialApp(
        home: Scaffold(body: SingleChildScrollView(child: SimplePolarPanel())),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return rig;
}

VoidCallback? _checkAgain(WidgetTester tester) => tester
    .widget<OutlinedButton>(find.byKey(const Key('simple-polar-check-again')))
    .onPressed;

void main() {
  testWidgets('Start is disabled while a pre-flight check fails', (
    tester,
  ) async {
    // The clearance is confirmed: only the parked mount fails (#2170: an
    // unconfirmed clearance fails the list too).
    final rig = _Rig();
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
            body: SingleChildScrollView(
              child: Column(
                children: [SimplePolarShutter(), SimplePolarPanel()],
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(
      find.byKey(const Key('polar-preflight-check-mount_parked')),
      findsOneWidget,
      reason: "the failing check is listed in Simple Mode's Polar align panel",
    );
    VoidCallback? shutter() => tester
        .widget<SimpleShutterButton>(find.byType(SimpleShutterButton))
        .onPressed;
    expect(shutter(), isNull, reason: "the rail's shutter is the Start");

    rig.parked = false;
    await tester.tap(find.byKey(const Key('polar-preflight-refresh')));
    await tester.pumpAndSettle();
    expect(shutter(), isNotNull);
  });

  // #2165: the list's clearance is the fine stage's; a Check turns by its own
  // step, so Check again stays on and the rig decides.
  testWidgets(
    "Check again stays on while the fine stage's clearance fails and starts when the rig accepts it",
    (tester) async {
      final rig = await _pumpStopped(tester, reachDeg: 30.5, checkReachDeg: 20.5);
      expect(
        _checkAgain(tester),
        isNotNull,
        reason: "the Check's 20.5° reach is covered",
      );

      await tester.tap(find.byKey(const Key('simple-polar-check-again')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-clearance-dialog')), findsNothing);
      await tester.tap(find.byKey(const Key('polar-start-confirm-go')));
      await tester.pumpAndSettle();
      expect(rig.started.single.stage, PolarAlignStages.check);
    },
  );

  testWidgets(
    "Check again refused while the fine stage's clearance fails asks about the Check's reach",
    (tester) async {
      final rig = await _pumpStopped(tester, reachDeg: 30.5, checkReachDeg: 40.5);
      expect(_checkAgain(tester), isNotNull);

      await tester.tap(find.byKey(const Key('simple-polar-check-again')));
      await tester.pumpAndSettle();
      expect(
        tester.widget<Text>(find.byKey(const Key('polar-clearance-dialog'))).data,
        contains('up to 40.5° each way'),
      );
      await tester.tap(find.byKey(const Key('polar-clearance-confirm')));
      await tester.pumpAndSettle();
      expect(
        rig.section.puts.last.mountClearance[preflightMountKey],
        const PolarAlignMountClearance(confirmedReachDeg: 40.5),
      );
    },
  );
}
