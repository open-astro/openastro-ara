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

// Single-session excerpts (made-up values), one with a settle and a dither.
const _head = '''
Guiding Begins at 2026-10-04 07:12:08
''';
const _cols =
    'Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,'
    'DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,'
    'YStep,StarMass,SNR,ErrorCode';
const _rows = '''
INFO: SETTLING STATE CHANGE, Settling started
1,8.408,"Mount",5.0,-4.0,1.601,6.315,1.0,4.4,1227,W,2500,S,,,326811,52.15,0
2,15.832,"Mount",3.0,-1.9,0.523,3.515,0.4,2.4,487,W,2355,S,,,325311,50.65,0
INFO: SETTLING STATE CHANGE, Settling complete
3,19.841,"Mount",1.1,-0.5,0.003,1.307,0.0,0.9,0,,875,S,,,309214,55.10,0
4,22.900,"Mount",0.1,0.2,-0.286,0.008,-0.1,0.0,157,E,0,,,,208796,43.97,0
INFO: DITHER by 3.402, -1.056, new lock pos = 1182.649, 267.370
INFO: SETTLING STATE CHANGE, Settling started
5,26.100,"Mount",3.0,-1.0,-2.900,-1.100,0.0,0.0,900,E,300,N,,,200000,40.0,0
6,29.000,"Mount",,,,,,,0,,0,,,,0,0.00,2
INFO: SETTLING STATE CHANGE, Settling complete
7,32.000,"Mount",0.1,0.1,0.100,0.100,0.0,0.0,0,,0,,,,210000,41.0,0
Guiding Ends at 2026-10-04 07:39:51
''';
const _log =
    '$_head'
    'Pixel scale = 6.45 arc-sec/px, Binning = 1, Focal length = 120 mm\n'
    '$_cols\n$_rows';
const _noScaleLog =
    '$_head'
    'Binning = 1, Focal length = 120 mm\n$_cols\n$_rows';

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

  test('RA oscillation does not pair frames across an excluded stretch', () {
    final s = GuideLogStats.compute([
      _f(0, 1, 0),
      _f(1, -1, 0),
      _f(2, 9, 9, dither: true),
      _f(3, -1, 0),
      _f(4, 1, 0),
    ]);
    expect(s.samples, 4);
    expect(s.raOscIndex, 1.0); // 2 flips of 2 pairs; (-1, -1) not formed
  });

  test('parsed log: dither and settling rows left out, arcsec from 6.45', () {
    final log = Phd2GuideLog.parse(_log).sessions.single;
    final s = GuideLogStats.compute(
      log.frames,
      pixelScaleArcsec: log.pixelScaleArcsec,
    );
    // Kept: frames 3, 4, 7 (RA = -RARawDistance). Worked with python
    // statistics.pstdev and a least-squares fit on minutes since frame 3.
    expect(s.inArcsec, isTrue);
    expect(s.samples, 3);
    expect(s.rmsRa, closeTo(1.0574984326, 1e-8));
    expect(s.rmsDec, closeTo(3.8175149594, 1e-8));
    expect(s.rmsTotal, closeTo(3.961278, 1e-6));
    expect(s.peakRa, closeTo(1.8447, 1e-9));
    expect(s.peakDec, closeTo(8.43015, 1e-9));
    expect(s.raDriftPerMin, closeTo(-6.1401306841, 1e-7));
    expect(s.decDriftPerMin, closeTo(-28.7213722556, 1e-7));
  });

  test('parsed log without a Pixel scale line gives px figures', () {
    final log = Phd2GuideLog.parse(_noScaleLog).sessions.single;
    expect(log.pixelScaleArcsec, isNull);
    final s = GuideLogStats.compute(
      log.frames,
      pixelScaleArcsec: log.pixelScaleArcsec,
    );
    expect(s.inArcsec, isFalse);
    expect(s.samples, 3);
    expect(s.rmsRa, closeTo(0.1639532453, 1e-8));
    expect(s.rmsDec, closeTo(0.5918627843, 1e-8));
    expect(s.peakDec, closeTo(1.307, 1e-12));
    expect(s.decDriftPerMin, closeTo(-4.4529255, 1e-6));
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
