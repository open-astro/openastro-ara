import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sky_model_calibration.dart';

void main() {
  final wire = <String, dynamic>{
    'fitted_utc': '2026-10-13T06:00:00Z',
    'frames_considered': 42,
    'filters': [
      {'kind': 'l', 'bin': 1, 'k': 0.82, 'scatter_pct': 6.5, 'pedestal_adu': 501.0, 'frame_count': 30, 'passed': true, 'note': null},
      {'kind': 'l', 'bin': 2, 'k': 0.95, 'scatter_pct': 4.0, 'frame_count': 6, 'passed': true},
      {'kind': 'ha', 'bin': 1, 'k': 2.4, 'scatter_pct': 8.0, 'frame_count': 9, 'passed': false, 'note': 'k outside the 0.4–1.5 band: the model is missing physics for this rig.'},
      {'kind': 'oiii', 'bin': 1, 'frame_count': 1, 'passed': false, 'note': 'Only 1 frame; the gate needs 3.'},
      {'nope': true},
    ],
  };

  test('#1140 P2 parses the daemon record and keeps only passed kinds at the smallest binning for the planner', () {
    final c = SkyModelCalibration.fromJson(wire)!;
    expect(c.fittedUtc, DateTime.utc(2026, 10, 13, 6));
    expect(c.framesConsidered, 42);
    expect(c.filters.map((f) => f.kind), ['l', 'l', 'ha', 'oiii']);
    expect(c.appliedK, {'l': 0.82}, reason: 'bin 1 beats bin 2; failed kinds are never applied');
  });

  test('#1140 P2 each row says its standing in one line', () {
    final c = SkyModelCalibration.fromJson(wire)!;
    expect(c.filters[0].standing, 'k 0.82 · scatter 7 % · 30 frames · applies');
    expect(c.filters[2].standing, startsWith('k 2.40 · scatter 8 % · 9 frames · not applied: k outside'));
    expect(c.filters[3].standing, 'Only 1 frame; the gate needs 3.');
  });

  test('#1140 P2 a malformed body is null', () {
    expect(SkyModelCalibration.fromJson(null), isNull);
    expect(SkyModelCalibration.fromJson({'filters': []}), isNull);
    expect(SkyModelCalibration.fromJson({'fitted_utc': 'x', 'filters': []}), isNull);
  });
}
