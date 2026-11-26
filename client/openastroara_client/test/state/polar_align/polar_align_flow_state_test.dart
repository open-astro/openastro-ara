import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/state/equipment/mount_home_state.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/polar_align/polar_align_flow_state.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';

import '../../support/fake_mount.dart';

const _result = PolarAlignCheckResult(
  altErrorArcmin: 0.3,
  azErrorArcmin: 0.4,
  totalErrorArcmin: 0.5,
);

MountStatus _mount({
  bool atHome = true,
  bool canFindHome = true,
  bool parked = false,
  String runtime = 'idle',
  String state = 'connected',
}) => MountStatus.fromJson(
  mountWire(
    atHome: atHome,
    canFindHome: canFindHome,
    parked: parked,
    runtime: runtime,
    state: state,
  ),
);

void main() {
  test('polarFlowStep: every state of the run has its step', () {
    PolarFlowStep at(
      String phase, {
      PolarAlignCheckResult? result,
      bool dismissed = false,
    }) => polarFlowStep(
      PolarAlignLive(
        phase: phase,
        checkResult: result,
        resultDismissed: dismissed,
      ),
    );
    expect(at(PolarAlignStates.idle), PolarFlowStep.ready);
    expect(at(PolarAlignStates.stopped), PolarFlowStep.ready);
    expect(at(PolarAlignStates.failed), PolarFlowStep.ready);
    expect(at(PolarAlignStates.seeding), PolarFlowStep.measuring);
    expect(at(PolarAlignStates.adjusting), PolarFlowStep.adjusting);
    expect(at(PolarAlignStates.paused), PolarFlowStep.adjusting);
    expect(at(PolarAlignStates.homing), PolarFlowStep.goingHome);
    expect(at(PolarAlignStates.checking), PolarFlowStep.checking);
    expect(
      at(PolarAlignStates.homing, result: _result),
      PolarFlowStep.returningHome,
    );
    expect(at(PolarAlignStates.checked, result: _result), PolarFlowStep.result);
    expect(
      at(PolarAlignStates.checked, result: _result, dismissed: true),
      PolarFlowStep.ready,
    );
    expect(
      at(PolarAlignStates.checked),
      PolarFlowStep.ready,
      reason: 'no verdict to show',
    );
  });

  test('polarReadyGate: Home until the mount is at Home, Start for a mount at Home or one that cannot find Home', () {
    expect(
      polarReadyGate(_mount(atHome: false), homing: false),
      PolarReadyGate.home,
    );
    expect(polarReadyGate(_mount(), homing: false), PolarReadyGate.start);
    expect(
      polarReadyGate(_mount(atHome: false, canFindHome: false), homing: false),
      PolarReadyGate.start,
    );
    expect(
      polarReadyGate(_mount(atHome: false), homing: true),
      PolarReadyGate.goingHome,
    );
    expect(
      polarReadyGate(_mount(atHome: false, parked: true), homing: false),
      PolarReadyGate.parked,
    );
    expect(
      polarReadyGate(_mount(runtime: 'slewing'), homing: false),
      PolarReadyGate.moving,
    );
    expect(polarReadyGate(null, homing: false), PolarReadyGate.noMount);
    expect(
      polarReadyGate(_mount(state: 'disconnected'), homing: false),
      PolarReadyGate.noMount,
    );
  });

  test('the gate follows the mount: Home, Going Home while it moves, then Start once it reports Home', () async {
    final mount = FakeMount.awayFromHome();
    final c = ProviderContainer(
      overrides: [mountProvider.overrideWith(() => mount)],
    );
    addTearDown(c.dispose);
    final sub = c.listen(polarReadyGateProvider, (_, _) {});
    addTearDown(sub.close);
    expect(c.read(polarReadyGateProvider), isA<AsyncLoading<PolarReadyGate>>());
    await c.read(mountProvider.future);
    expect(c.read(polarReadyGateProvider).value, PolarReadyGate.home);

    await c.read(mountHomeProvider.notifier).home();
    expect(mount.calls, ['home']);
    expect(c.read(polarReadyGateProvider).value, PolarReadyGate.goingHome);
    mount.report(mountWire(atHome: false, runtime: 'slewing'));
    expect(c.read(polarReadyGateProvider).value, PolarReadyGate.goingHome);
    mount.report(mountWire());
    expect(c.read(polarReadyGateProvider).value, PolarReadyGate.start);
  });
}
