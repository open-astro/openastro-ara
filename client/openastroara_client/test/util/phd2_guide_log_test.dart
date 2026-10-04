import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/guider/guide_step_state.dart';
import 'package:openastroara/util/phd2_guide_log.dart';

/// Trimmed from a real PHD2 log (2026-10-04, 6.45″/px): a calibration block
/// that must be skipped, then a guiding session with a settle, a dither and
/// a lost-star row.
const _log = '''
PHD2 version 2.6.13 [OpenAstro Guider], Log version 2.5. Log enabled at 2026-10-04 00:31:22

Calibration Begins at 2026-10-04 06:58:40
Pixel scale = 6.45 arc-sec/px, Binning = 1, Focal length = 120 mm
West,1,1.000,2.000
Calibration complete

Guiding Begins at 2026-10-04 07:12:08
Pixel scale = 6.45 arc-sec/px, Binning = 1, Focal length = 120 mm
Lock position = 1185.020, 270.028, Star position = 1185.020, 270.028, HFD = 3.66 px
Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode
INFO: SETTLING STATE CHANGE, Settling started
1,8.408,"Mount",5.061,-4.064,1.601,6.315,1.009,4.421,1227,W,2500,S,,,326811,52.15,0
2,15.832,"Mount",2.975,-1.930,0.523,3.515,0.400,2.461,487,W,2355,S,,,325311,50.65,0
INFO: SETTLING STATE CHANGE, Settling complete
3,19.841,"Mount",1.188,-0.545,0.003,1.307,0.000,0.915,0,,875,S,,,309214,55.10,0
4,22.900,"Mount",0.130,0.255,-0.286,0.008,-0.129,0.000,157,E,0,,,,208796,43.97,0
INFO: DITHER by 3.402, -1.056, new lock pos = 1182.649, 267.370
INFO: SETTLING STATE CHANGE, Settling started
5,26.100,"Mount",3.0,-1.0,-2.900,-1.100,0.0,0.0,900,E,300,N,,,200000,40.0,0
6,29.000,"Mount",,,,,,,0,,0,,,,0,0.00,2
INFO: SETTLING STATE CHANGE, Settling complete
7,32.000,"Mount",0.1,0.1,0.100,0.100,0.0,0.0,0,,0,,,,210000,41.0,0
Guiding Ends at 2026-10-04 07:39:51
''';

void main() {
  test('parses the guiding session only, with PHD2 sign conventions', () {
    final log = Phd2GuideLog.parse(_log);
    expect(log.sessions.length, 1);
    final s = log.sessions.single;
    expect(s.beganAt, DateTime(2026, 10, 4, 7, 12, 8));
    expect(s.pixelScaleArcsec, 6.45);
    expect(s.steps.length, 7);

    final first = s.steps.first;
    expect(first.at, DateTime(2026, 10, 4, 7, 12, 8).add(const Duration(milliseconds: 8408)));
    // RA raw negated (daemon / NINA convention), Dec as logged.
    expect(first.raPx, closeTo(-1.601, 1e-9));
    expect(first.decPx, closeTo(6.315, 1e-9));
    expect(first.raArcsec, closeTo(-1.601 * 6.45, 1e-6));
    // W / S pulses: W positive, S negative.
    expect(first.raPulseMs, 1227);
    expect(first.decPulseMs, -2500);
    expect(first.snr, 52.15);
    // E / N pulses: E negative, N positive.
    expect(s.steps[4].raPulseMs, -900);
    expect(s.steps[4].decPulseMs, 300);
    // Error code 2 with blank distances → a lost-star frame (no offset).
    expect(s.steps[5].raPx, isNull);
    expect(s.steps[5].decPx, isNull);
  });

  test('markers land between the rows PHD2 wrote them between', () {
    final s = Phd2GuideLog.parse(_log).sessions.single;
    final kinds = s.markers.map((m) => m.kind).toList();
    expect(kinds, [
      GuideMarkerKind.guidingStarted,
      GuideMarkerKind.settling,
      GuideMarkerKind.settleDone,
      GuideMarkerKind.dithered,
      GuideMarkerKind.settling,
      // Row 6's error code: a lost star the replay marks like a live one,
      // though this log has no "star lost" INFO line.
      GuideMarkerKind.starLost,
      GuideMarkerKind.settleDone,
      GuideMarkerKind.guidingStopped,
    ]);
    expect(s.markers[5].at, s.steps[5].at);
    final dither = s.markers[3];
    expect(dither.dxPx, 3.402);
    expect(dither.dyPx, -1.056);
    // After row 4 (t=22.9) and before row 5 (t=26.1).
    expect(dither.at.isAfter(s.steps[3].at), isTrue);
    expect(dither.at.isBefore(s.steps[4].at), isTrue);
    // The first settle closes after row 2 and before row 3.
    expect(s.markers[2].at.isAfter(s.steps[1].at), isTrue);
    expect(s.markers[2].at.isBefore(s.steps[2].at), isTrue);
  });

  test('a log with no guiding session yields nothing', () {
    expect(Phd2GuideLog.parse('Calibration Begins at 2026-10-04 06:58:40\n').sessions, isEmpty);
    expect(Phd2GuideLog.parse('').allSteps, isEmpty);
  });
}
