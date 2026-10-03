import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/autofocus_run.dart';

void main() {
  test('parses the daemon run record', () {
    final run = AutofocusRun.fromJson({
      'state': 'complete',
      'mode': 'classic',
      'phase': 'done',
      'trigger': 'manual',
      'started_utc': '2026-10-03T04:00:00+00:00',
      'completed_utc': '2026-10-03T04:01:30+00:00',
      'duration_seconds': 90.2,
      'start_position': 10000,
      'final_position': 9850,
      'final_hfr': 1.42,
      'final_stars': 41,
      'filter': 'L',
      'focuser_temperature_c': 12.5,
      'total_steps': 9,
      'completed_steps': 9,
      'sweep_attempt': 1,
      'step_size': 23,
      'step_size_source': 'measured',
      'probes': [
        {'index': 1, 'phase': 'coarse', 'position': 10000, 'hfr': 6.1, 'stars': 0, 'kept': true},
        {'index': 2, 'phase': 'fine', 'position': 10200, 'hfr': 2.9, 'stars': 40, 'kept': true},
        {'index': 3, 'phase': 'fine', 'position': 10400, 'hfr': 0, 'stars': 1, 'kept': false},
      ],
      'fit': {
        'algorithm': 'parabolic',
        'r_squared': 0.98,
        'best_position': 9851.3,
        'predicted_hfr': 1.44,
        'within_sampled_range': true,
        'curve': [
          {'position': 9600.0, 'hfr': 3.1},
          {'position': 9850.0, 'hfr': 1.44},
        ],
      },
      'reason': null,
      'restored_position': null,
      'has_frame': true,
      'frame_seq': 7,
      'frame_position': 9850,
      'frame_hfr': 1.42,
    });
    expect(run.isComplete, isTrue);
    expect(run.trigger, 'manual');
    expect(run.finalPosition, 9850);
    expect(run.finalHfr, 1.42);
    expect(run.probes, hasLength(3));
    expect(run.stepSize, 23);
    expect(run.stepSizeSource, 'measured');
    expect(run.coarseProbes, hasLength(1));
    expect(run.sweepProbes, hasLength(2));
    expect(run.sweepProbes.last.kept, isFalse);
    expect(run.keptCount, 2);
    expect(run.fit!.algorithm, 'parabolic');
    expect(run.fit!.curve, hasLength(2));
    expect(run.fit!.curve.last.hfr, 1.44);
    expect(run.hasFrame, isTrue);
    expect(run.frameSeq, 7);
    expect(run.startedUtc!.isUtc, isTrue);
  });

  test('an idle record parses with defaults', () {
    final run = AutofocusRun.fromJson({'state': 'idle', 'probes': [], 'fit': null});
    expect(run.state, AutofocusRunStates.idle);
    expect(run.isRunning, isFalse);
    expect(run.probes, isEmpty);
    expect(run.fit, isNull);
    expect(run.hasFrame, isFalse);
  });

  test('a failed record carries the reason and restored position', () {
    final run = AutofocusRun.fromJson({
      'state': 'failed',
      'reason': 'only 2 of 9 probes had measurable stars',
      'restored_position': 10000,
      'probes': [],
    });
    expect(run.isFailed, isTrue);
    expect(run.reason, contains('measurable'));
    expect(run.restoredPosition, 10000);
  });
}
