import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/guide_log_stats.dart';
import 'package:openastroara/util/phd2_guide_log.dart';

final _t0 = DateTime(2026, 10, 4, 22);

Phd2LogFrame _f(
  int minute,
  double? ra,
  double? dec, {
  bool dither = false,
  bool settling = false,
  bool lost = false,
}) => Phd2LogFrame(
  at: _t0.add(Duration(minutes: minute)),
  raPx: ra,
  decPx: dec,
  dither: dither,
  settling: settling,
  starLost: lost,
);

// Clean frames: RA 1,-1,1,-1,1 px; Dec 0..4 px, one frame a minute.
// Worked independently (python statistics.pstdev): rmsRa 0.9797958971,
// rmsDec sqrt(2) = 1.4142135624, total 1.7204650534. Dec slope = 1 px/min,
// RA slope = 0.
final _clean = [
  _f(0, 1, 0),
  _f(1, -1, 1),
  _f(2, 1, 2),
  _f(3, -1, 3),
  _f(4, 1, 4),
];

void main() {
  test('px stats when the log has no pixel scale', () {
    final s = GuideLogStats.compute(_clean);
    expect(s.inArcsec, isFalse);
    expect(s.samples, 5);
    expect(s.rmsRa, closeTo(0.9797958971, 1e-9));
    expect(s.rmsDec, closeTo(1.4142135624, 1e-9));
    expect(s.rmsTotal, closeTo(1.7204650534, 1e-9));
    expect(s.peakRa, 1);
    expect(s.peakDec, 4);
    expect(s.raOscIndex, 1.0); // 4 of 4 consecutive pairs flip
    expect(s.raDriftPerMin, closeTo(0, 1e-12));
    expect(s.decDriftPerMin, closeTo(1, 1e-12));
    // Dec moves 4 px over the range, RMS 1.41: steady; Dec is the weaker axis.
    expect(s.verdict.weakerAxis, GuideAxis.dec);
    expect(s.verdict.steadyDriftAxis, GuideAxis.dec);
  });

  test('arcsec from the pixel scale (2 arcsec/px doubles every figure)', () {
    final s = GuideLogStats.compute(_clean, pixelScaleArcsec: 2);
    expect(s.inArcsec, isTrue);
    expect(s.rmsRa, closeTo(1.9595917942, 1e-9));
    expect(s.rmsTotal, closeTo(3.4409301068, 1e-9));
    expect(s.peakDec, 8);
    expect(s.decDriftPerMin, closeTo(2, 1e-12));
  });

  test('dither and star-lost frames always out; settling out unless asked', () {
    final frames = [
      ..._clean,
      _f(5, 9, 9, dither: true),
      _f(6, null, null, lost: true, settling: true),
      _f(7, 5, -5, settling: true),
    ];
    final def = GuideLogStats.compute(frames);
    expect(def.samples, 5);
    expect(def.peakRa, 1);
    expect(def.rmsRa, closeTo(0.9797958971, 1e-9));

    final inc = GuideLogStats.compute(frames, includeSettling: true);
    expect(inc.samples, 6);
    expect(inc.peakRa, 5);
    expect(inc.peakDec, 5);
  });

  test('no usable frames gives null figures and no verdict', () {
    final s = GuideLogStats.compute([_f(0, null, null, lost: true)]);
    expect(s.samples, 0);
    expect(s.rmsRa, isNull);
    expect(s.verdict.weakerAxis, isNull);
    expect(s.verdict.steadyDriftAxis, isNull);
  });

  test('flat noisy session has no steady drift', () {
    // 8 alternating frames: fitted RA drift over the range 0.33 px (fitted
    // with numpy) against RMS 0.5, so below the steady threshold.
    final s = GuideLogStats.compute([
      for (var i = 0; i < 8; i++)
        _f(i, i.isEven ? 0.5 : -0.5, i.isEven ? 0.2 : -0.2),
    ]);
    expect(s.verdict.steadyDriftAxis, isNull);
    expect(s.verdict.weakerAxis, GuideAxis.ra);
  });
}
