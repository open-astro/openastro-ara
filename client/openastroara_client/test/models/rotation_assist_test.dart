import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/rotation_assist.dart';
import 'package:openastroara/state/rotation/rotation_assist_state.dart';

RotationAssistSample _s(int seq, double delta) => RotationAssistSample(
  seq: seq,
  solvedPositionAngleDeg: 120 - delta,
  deltaDeg: delta,
);

RotationAssistStatus _status(
  List<RotationAssistSample> recent, {
  double tolerance = 1,
  bool within = false,
  String state = 'running',
}) => RotationAssistStatus(
  active: state == 'running',
  state: state,
  targetPositionAngleDeg: 120,
  toleranceDeg: tolerance,
  seq: recent.isEmpty ? 0 : recent.last.seq,
  latest: recent.isEmpty ? null : recent.last,
  recent: recent,
  withinTolerance: within,
);

void main() {
  test('parses the daemon readout', () {
    final s = RotationAssistStatus.fromJson({
      'active': true,
      'state': 'running',
      'target_position_angle_deg': 299.0,
      'tolerance_deg': 2.0,
      'seq': 3,
      'started_utc': '2026-10-03T04:00:00+00:00',
      'latest': {
        'seq': 3,
        'solved_utc': '2026-10-03T04:00:30+00:00',
        'solved_position_angle_deg': 287.5,
        'delta_deg': 11.5,
        'ra_deg': 314.82,
        'dec_deg': 44.53,
        'pixel_scale_arcsec': 2.3,
        'flipped': true,
        'frame_width': 1024,
        'frame_height': 683,
      },
      'recent': [
        {'seq': 2, 'solved_position_angle_deg': 270.0, 'delta_deg': 29.0},
        {'seq': 3, 'solved_position_angle_deg': 287.5, 'delta_deg': 11.5},
      ],
      'within_tolerance': false,
      'error': null,
      'consecutive_failures': 0,
      'has_frame': true,
      'frame_seq': 3,
    });
    expect(s.active, isTrue);
    expect(s.targetPositionAngleDeg, 299);
    expect(s.latest!.deltaDeg, 11.5);
    expect(s.recent, hasLength(2));
    expect(s.withinTolerance, isFalse);
    expect(s.latest!.flipped, isTrue);
    expect(s.latest!.pixelScaleArcsec, 2.3);
    expect(s.latest!.frameWidth, 1024);
    expect(s.hasFrame, isTrue);
    expect(s.frameSeq, 3);
    expect(RotationAssistStatus.fromJson(const {}).state, 'idle');
  });

  group('rotationHint — advice relative to the last turn', () {
    test('waits for the first solve', () {
      expect(rotationHint(_status(const [])).advice, RotateAdvice.wait);
    });
    test('on target once within tolerance', () {
      expect(
        rotationHint(_status([_s(1, 0.6)], tolerance: 1)).advice,
        RotateAdvice.onTarget,
      );
      expect(
        rotationHint(_status([_s(1, 5)], within: true)).advice,
        RotateAdvice.onTarget,
      );
    });
    test('a first reading asks for a move of about the delta', () {
      final h = rotationHint(_status([_s(1, 20)]));
      expect(h.advice, RotateAdvice.makeAMove);
      expect(h.detail, contains('20.0°'));
    });
    test('solver jitter is not a move', () {
      expect(
        rotationHint(_status([_s(1, 20), _s(2, 20.2)])).advice,
        RotateAdvice.makeAMove,
      );
    });
    test('a turn that shrank the delta: keep going, the rest of the way', () {
      final h = rotationHint(_status([_s(1, 20), _s(2, 12)]));
      expect(h.advice, RotateAdvice.keepGoing);
      expect(h.detail, contains('12.0°'));
    });
    test(
      'a turn that grew the delta (or flipped its sign past 90): go back',
      () {
        expect(
          rotationHint(_status([_s(1, 20), _s(2, 31)])).advice,
          RotateAdvice.goBack,
        );
        expect(
          rotationHint(_status([_s(1, 20), _s(2, -40)])).advice,
          RotateAdvice.goBack,
        );
      },
    );
    test('the comparison skips jittery repeats back to the real last move', () {
      expect(
        rotationHint(_status([_s(1, 20), _s(2, 12), _s(3, 12.1)])).advice,
        RotateAdvice.keepGoing,
      );
    });
    test('overshooting past the target (sign flip, smaller delta) is still go back', () {
      final h = rotationHint(_status([_s(1, 11), _s(2, -6)]));
      expect(h.advice, RotateAdvice.goBack);
      expect(h.detail, contains('past it'));
      expect(h.detail, contains('6.0°'));
    });
    test('an errored readout says so', () {
      expect(
        rotationHint(_status([_s(1, 20)], state: 'error')).advice,
        RotateAdvice.noSolve,
      );
    });
  });
}
