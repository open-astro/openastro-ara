import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/screens/simple/simple_polar_panel.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart'
    show SimpleShutterButton;
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';

import '../../support/polar_align_section.dart';
import '../../support/polar_preflight.dart';

/// A rig whose pre-flight list follows the profile's polar-align section.
class _Rig implements PolarAlignClient {
  @override
  Future<PolarAlignMoveToStartResult> moveToStart() =>
      throw UnimplementedError();
  final section = FakePolarAlignSection(loaded: true);
  bool parked = true;

  @override
  Future<PolarAlignPreflight?> preflight({String? stage}) async =>
      rigPreflightFor(section.initial, parked: parked);
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

class _Idle extends PolarAlignLiveNotifier {
  @override
  PolarAlignLive build() => const PolarAlignLive();
}

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
}
