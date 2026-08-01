import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/state/setup/setup_readiness.dart';

MountStatus _mount(String state) => MountStatus.fromJson({'state': state});
CameraStatus _camera(String state) => CameraStatus.fromJson({'state': state});

void main() {
  group('connectStepState', () {
    test('done only when mount AND camera are connected', () {
      expect(connectStepState(_mount('connected'), _camera('connected')),
          SetupStepState.done);
      expect(connectStepState(_mount('connected'), _camera('disconnected')),
          SetupStepState.pending);
      expect(connectStepState(_mount('disconnected'), _camera('connected')),
          SetupStepState.pending);
    });

    test('null statuses (nothing ever connected) are pending', () {
      expect(connectStepState(null, null), SetupStepState.pending);
      expect(connectStepState(_mount('connected'), null),
          SetupStepState.pending);
    });

    test('a connecting device shows in-progress', () {
      expect(connectStepState(_mount('connecting'), _camera('connected')),
          SetupStepState.inProgress);
      expect(connectStepState(null, _camera('connecting')),
          SetupStepState.inProgress);
    });

    test('an errored device wins over everything else', () {
      expect(connectStepState(_mount('error'), _camera('connected')),
          SetupStepState.problem);
      expect(connectStepState(_mount('connecting'), _camera('error')),
          SetupStepState.problem);
    });
  });

  group('polarAlignStepState', () {
    test('fresh session is pending', () {
      expect(polarAlignStepState(const PolarAlignLive()),
          SetupStepState.pending);
    });

    test('seeding / adjusting / paused are in-progress', () {
      for (final phase in [
        PolarAlignStates.seeding,
        PolarAlignStates.adjusting,
        PolarAlignStates.paused,
      ]) {
        expect(polarAlignStepState(PolarAlignLive(phase: phase)),
            SetupStepState.inProgress,
            reason: 'failed for $phase');
      }
    });

    test('green zone is done, and survives a clean stop', () {
      const aligned = PolarAlignLive(
        phase: PolarAlignStates.adjusting,
        zone: 'green',
        totalErrorArcmin: 0.7,
      );
      expect(polarAlignStepState(aligned), SetupStepState.done);
      // Stop keeps the zone (copyWith semantics) — still satisfied.
      expect(
          polarAlignStepState(
              aligned.copyWith(phase: PolarAlignStates.stopped)),
          SetupStepState.done);
    });

    test('red/yellow zones are not done', () {
      expect(
          polarAlignStepState(const PolarAlignLive(
              phase: PolarAlignStates.adjusting, zone: 'red')),
          SetupStepState.inProgress);
      expect(
          polarAlignStepState(const PolarAlignLive(
              phase: PolarAlignStates.stopped, zone: 'yellow')),
          SetupStepState.pending);
    });

    test('failed routine is a problem (unless it had reached green)', () {
      expect(
          polarAlignStepState(
              const PolarAlignLive(phase: PolarAlignStates.failed)),
          SetupStepState.problem);
    });
  });
}
