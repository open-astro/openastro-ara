import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guide_focus.dart';

void main() {
  test('parses the loop status with samples', () {
    final status = GuideFocusStatus.fromJson({
      'active': true,
      'state': 'running',
      'exposure_sec': 2.0,
      'seq': 12,
      'started_utc': '2026-10-03T04:00:00+00:00',
      'latest': {'seq': 12, 'captured_utc': '2026-10-03T04:00:30+00:00', 'hfr': 2.31, 'stars': 6, 'peak_adu': 21000, 'fwhm': 3.9},
      'best_hfr': 2.1,
      'best_seq': 9,
      'recent': [
        {'seq': 11, 'hfr': 2.4, 'stars': 6, 'peak_adu': 20000, 'fwhm': 4.0},
        {'seq': 12, 'hfr': 2.31, 'stars': 6, 'peak_adu': 21000, 'fwhm': 3.9},
      ],
      'error': null,
      'consecutive_failures': 0,
      'has_frame': true,
    });
    expect(status.active, isTrue);
    expect(status.state, GuideFocusStates.running);
    expect(status.latest!.hfr, 2.31);
    expect(status.latest!.peakAdu, 21000);
    expect(status.bestHfr, 2.1);
    expect(status.recent, hasLength(2));
    expect(status.hasFrame, isTrue);
  });

  test('an idle status parses with defaults', () {
    final status = GuideFocusStatus.fromJson({'active': false, 'state': 'idle', 'recent': []});
    expect(status.active, isFalse);
    expect(status.latest, isNull);
    expect(status.bestHfr, isNull);
    expect(status.recent, isEmpty);
  });

  test('an error status carries the message', () {
    final status = GuideFocusStatus.fromJson({
      'active': false,
      'state': 'error',
      'error': 'gave up after 5 failed frames — last: camera not connected',
      'consecutive_failures': 5,
    });
    expect(status.state, GuideFocusStates.error);
    expect(status.error, contains('gave up'));
    expect(status.consecutiveFailures, 5);
  });
}
