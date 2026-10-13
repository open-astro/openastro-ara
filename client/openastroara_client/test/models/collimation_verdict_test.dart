import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/collimation_verdict.dart';

void main() {
  final at = DateTime.utc(2026, 10, 13, 2);

  test('parses the WS payload; insufficient or malformed is no verdict', () {
    final v = CollimationVerdict.fromPayload(const {
      'severity': 'slight',
      'offset_percent': 12.3,
      'direction_degrees': 90.0,
      'stars_used': 14,
    }, at: at)!;
    expect(v.severity, CollimationSeverity.slight);
    expect(v.offsetPercent, 12.3);
    expect(v.starsUsed, 14);
    expect(
      CollimationVerdict.fromPayload(const {
        'severity': 'insufficient',
        'offset_percent': 0,
      }, at: at),
      isNull,
    );
    expect(
      CollimationVerdict.fromPayload(const {'severity': 'good'}, at: at),
      isNull,
      reason: 'no offset',
    );
  });

  test(
    'clock position is in the image frame: +x is 3, +y (down) is 6, up is 12',
    () {
      CollimationVerdict at_(double deg) => CollimationVerdict(
        severity: CollimationSeverity.slight,
        offsetPercent: 10,
        directionDegrees: deg,
        starsUsed: 9,
        atUtc: at,
      );
      expect(at_(0).clockHour, 3);
      expect(at_(90).clockHour, 6);
      expect(at_(180).clockHour, 9);
      expect(at_(270).clockHour, 12);
      expect(at_(-90).clockHour, 12);
      expect(at_(44).clockHour, 4, reason: 'nearest hour');
    },
  );

  test('headline, detail and the design hint', () {
    final v = CollimationVerdict(
      severity: CollimationSeverity.significant,
      offsetPercent: 18.04,
      directionDegrees: 30,
      starsUsed: 1,
      atUtc: at,
    );
    expect(
      v.headline,
      'Significant miscollimation — collimate before continuing',
    );
    expect(
      v.detail,
      contains('18.0 % of the donut diameter toward 4 o\'clock in the image'),
    );
    expect(v.detail, contains('from 1 donut star.'));
    expect(collimationHintFor('sct', v.severity), contains('secondary mirror'));
    expect(
      collimationHintFor('newtonian', v.severity),
      contains('primary and/or secondary'),
    );
    expect(
      collimationHintFor('refractor', v.severity),
      contains('tilted camera'),
    );
    expect(
      collimationHintFor('sct', CollimationSeverity.good),
      'Nothing to do.',
    );
  });
}
