import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/collimation_verdict.dart';
import 'package:openastroara/widgets/focus/collimation_dial.dart';

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
    expect(
      CollimationVerdict.fromPayload(const {
        'severity': 'good',
        'offset_percent': double.nan,
      }, at: at),
      isNull,
      reason: 'NaN offset',
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

  test('the dial\'s soft scale keeps slight and significant offsets apart', () {
    final f5 = CollimationDialPainter.shiftFraction(5);
    final f10 = CollimationDialPainter.shiftFraction(10);
    final f15 = CollimationDialPainter.shiftFraction(15);
    final f30 = CollimationDialPainter.shiftFraction(30);
    expect(
      f5,
      inInclusiveRange(0.3, 0.4),
      reason: 'a slight shift is visibly off-centre',
    );
    expect(f10, greaterThan(f5));
    expect(
      f15,
      greaterThan(f10 + 0.1),
      reason: '10 % and 15 % do not draw in the same place',
    );
    expect(f30, greaterThan(f15));
    expect(f30, lessThan(1));
    expect(CollimationDialPainter.shiftFraction(0), 0);
  });
}
