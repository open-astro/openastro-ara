import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_calibration_view.dart';

void main() {
  test('parses the measured Dec backlash and describes it', () {
    final view = GuiderCalibrationView.fromJson(<String, dynamic>{
      'connected': true,
      'run_state': 'complete',
      'steps': <dynamic>[],
      'verdict': 'good',
      'backlash_clearing_steps': 6,
      'calibration_step_ms': 250,
      'estimated_dec_backlash_ms': 1500,
    });
    expect(view.backlashClearingSteps, 6);
    expect(view.estimatedDecBacklashMs, 1500);
    expect(describeDecBacklash(view), 'Dec backlash ≈ 1.5 s (6 clearing steps × 250 ms)');
    expect(describeDecBacklash(GuiderCalibrationView.fromJson(<String, dynamic>{
      'connected': true, 'run_state': 'complete', 'steps': <dynamic>[], 'verdict': 'good', 'backlash_clearing_steps': 0,
    })), 'No measurable Dec backlash in this calibration');
    expect(describeDecBacklash(GuiderCalibrationView.fromJson(<String, dynamic>{
      'connected': true, 'run_state': 'complete', 'steps': <dynamic>[], 'verdict': 'good',
    })), isNull, reason: 'an older daemon says nothing');
  });
}
