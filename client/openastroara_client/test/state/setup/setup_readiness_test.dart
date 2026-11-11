import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/state/setup/setup_readiness.dart';

MountStatus _mount(String state) => MountStatus.fromJson({'state': state});
CameraStatus _camera(String state) => CameraStatus.fromJson({'state': state});

void main() {
  group('tonight, across a relaunch (#1565)', () {
    final now = DateTime(2026, 11, 12, 0, 30); // night of 11 Nov
    final tonight = DateTime(2026, 11, 11, 22, 0);
    final lastNight = DateTime(2026, 11, 10, 22, 0);
    AutofocusLive live(DateTime? completed, {String state = AutofocusRunStates.complete}) =>
        AutofocusLive(
          run: AutofocusRun.fromJson({
            'state': state,
            if (completed != null) 'completed_utc': completed.toUtc().toIso8601String(),
          }),
        );
    PolarAlignLastResult last(double arcmin, DateTime at) =>
        PolarAlignLastResult(errorArcmin: arcmin, completedUtc: at.toUtc());

    test('focus: a run completed tonight is done; last night, a failed or cancelled one is not', () {
      expect(focusStepState(live(tonight), now: now), SetupStepState.done);
      expect(focusStepState(live(lastNight), now: now), SetupStepState.pending);
      expect(focusStepState(live(tonight, state: AutofocusRunStates.cancelled), now: now),
          SetupStepState.pending);
      expect(focusStepState(live(tonight, state: AutofocusRunStates.failed), now: now),
          SetupStepState.problem);
    });

    test('focus: the Full UI passes no clock and keeps the this-session rule', () {
      expect(focusStepState(live(tonight)), SetupStepState.pending);
    });

    test('polar: a green last result from tonight is done; yellow, last night and no clock are not', () {
      expect(polarAlignStepState(const PolarAlignLive(), last: last(4, tonight), now: now),
          SetupStepState.done);
      expect(polarAlignStepState(const PolarAlignLive(), last: last(25, tonight), now: now),
          SetupStepState.pending);
      expect(polarAlignStepState(const PolarAlignLive(), last: last(4, lastNight), now: now),
          SetupStepState.pending);
      expect(polarAlignStepState(const PolarAlignLive(), last: last(4, tonight)),
          SetupStepState.pending);
    });

    test('polar: a routine running now wins over the remembered result', () {
      expect(
        polarAlignStepState(const PolarAlignLive(phase: PolarAlignStates.adjusting),
            last: last(4, tonight), now: now),
        SetupStepState.inProgress,
      );
    });

    test('the status carries last_result; an older daemon has none', () {
      final s = PolarAlignStatus.fromJson({
        'state': 'idle',
        'last_result': {'error_arcmin': 3.5, 'completed_utc': '2026-11-11T11:00:00Z'},
      });
      expect(s.lastResult?.errorArcmin, 3.5);
      expect(s.lastResult?.completedUtc, DateTime.utc(2026, 11, 11, 11));
      expect(PolarAlignStatus.fromJson({'state': 'idle'}).lastResult, isNull);
    });
  });

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

    test("the rough stage's green zone is not done yet", () {
      // Rough is a first pass on the main camera; fine and check are still ahead.
      const rough = PolarAlignLive(
          phase: PolarAlignStates.adjusting, stage: PolarAlignStages.coarse, zone: 'green');
      expect(polarAlignStepState(rough), SetupStepState.inProgress);
      // A fine-stage green is the fine stage's own (a new stage starts with no
      // readout), so it counts.
      expect(polarAlignStepState(rough.copyWith(stage: PolarAlignStages.fine)),
          SetupStepState.done);
    });

    test('a finished check outside the green zone needs work', () {
      const checked = PolarAlignLive(
          phase: PolarAlignStates.checked, stage: PolarAlignStages.check, zone: 'yellow');
      expect(polarAlignStepState(checked), SetupStepState.problem);
      expect(polarAlignStepState(checked.copyWith(zone: 'green')), SetupStepState.done);
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
