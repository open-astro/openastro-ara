import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/tracking_calibration.dart';
import 'package:openastroara/util/unguided_advice.dart';

void main() {
  final measured = TrackingCalibration(
    ceilingSeconds: 45,
    measuredUtc: DateTime.utc(2026, 10, 11, 12),
    source: 'measured',
  );

  test('a guided rig gets no line', () {
    expect(
      unguidedCeilingLine(
        guided: true,
        calibration: measured,
        skyLimitedSeconds: 300,
      ),
      isNull,
    );
  });

  test('unguided with no record explains the probe', () {
    expect(
      unguidedCeilingLine(
        guided: false,
        calibration: null,
        skyLimitedSeconds: 300,
      ),
      contains('Tracking Probe'),
    );
  });

  test('#1264 before anything is measured the spec-sheet prior stands in, named by its source', () {
    expect(
      unguidedCeilingLine(guided: false, calibration: null, skyLimitedSeconds: 300, specPriorSeconds: 18.7, specSource: 'manufacturer'),
      'Unguided: about 19 s (manufacturer prior from the mount\'s spec sheet; the first run measures it) — the run uses this lower figure until it has measured.',
    );
    expect(
      unguidedCeilingLine(guided: false, calibration: null, skyLimitedSeconds: 10, specPriorSeconds: 60),
      contains('the sky limit above is the lower of the two'),
    );
    expect(
      unguidedCeilingLine(guided: false, calibration: measured, skyLimitedSeconds: 300, specPriorSeconds: 18.7),
      startsWith('Unguided: about 45 s (measured'),
      reason: 'a measurement beats the sheet',
    );
  });

  test('a measured ceiling below the sky limit is the number the run uses', () {
    expect(
      unguidedCeilingLine(
        guided: false,
        calibration: measured,
        skyLimitedSeconds: 300,
      ),
      'Unguided: about 45 s (measured 2026-10-11) — the run uses this lower figure.',
    );
  });

  test('a prior above the sky limit defers to the sky limit', () {
    final prior = TrackingCalibration(
      ceilingSeconds: 600,
      measuredUtc: DateTime.utc(2026, 10, 11, 12),
      source: 'prior',
    );
    expect(
      unguidedCeilingLine(
        guided: false,
        calibration: prior,
        skyLimitedSeconds: 300,
      ),
      'Unguided: about 10 min (prior kept, not yet measured) — the sky limit above is the lower of the two.',
    );
  });
}
