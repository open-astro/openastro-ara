import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/state/rig_setup_state.dart';

const _gap = RigSetupGap.site;

/// A fully set-up rig without a Guider.
RigSetupFacts _facts({
  double lat = -33.856784,
  double lon = 151.215297,
  bool optics = true,
  bool guider = false,
  int guideFocal = 240,
  double guidePixel = 3.75,
  bool connected = true,
  bool data = true,
  String verdict = 'good',
}) => RigSetupFacts(
  latitudeDeg: lat,
  longitudeDeg: lon,
  opticsConfigured: optics,
  hasGuider: guider,
  guideFocalLengthMm: guideFocal,
  guidePixelSizeUm: guidePixel,
  guiderConnected: connected,
  calibrationHasData: data,
  calibrationVerdict: verdict,
);

void main() {
  group('rigSetupGaps', () {
    test('a set-up rig has no gaps', () {
      expect(rigSetupGaps(_facts()), isEmpty);
      expect(rigSetupGaps(_facts(guider: true)), isEmpty);
    });

    test('the site is missing at exactly 0,0 and at no other coordinate', () {
      expect(rigSetupGaps(_facts(lat: 0, lon: 0)), [_gap]);
      expect(rigSetupGaps(_facts(lat: 0, lon: 151.215297)), isEmpty);
      expect(rigSetupGaps(_facts(lat: -33.856784, lon: 0)), isEmpty);
      expect(rigSetupGaps(_facts(lat: 51.477833, lon: -0.0015)), isEmpty);
    });

    test('the main camera and telescope are missing until the optics are configured', () {
      expect(rigSetupGaps(_facts(optics: false)), [RigSetupGap.mainOptics]);
    });

    test('the guide scope applies only with a Guider, and needs a focal length and a pixel size above 0', () {
      expect(
        rigSetupGaps(_facts(guideFocal: 0, guidePixel: 0)),
        isEmpty,
        reason: 'no Guider, no guiding lines',
      );
      expect(rigSetupGaps(_facts(guider: true, guideFocal: 0)), [
        RigSetupGap.guideScope,
      ]);
      expect(rigSetupGaps(_facts(guider: true, guidePixel: 0)), [
        RigSetupGap.guideScope,
      ]);
      expect(
        rigSetupGaps(_facts(guider: true, guideFocal: 1, guidePixel: 0.1)),
        isEmpty,
      );
    });

    test(
      'the Guider calibration is missing when connected with no stored data',
      () {
        expect(rigSetupGaps(_facts(guider: true, data: false)), [
          RigSetupGap.guiderCalibration,
        ]);
      },
    );

    test('the verdicts: good and check count as done, redo as missing', () {
      expect(rigSetupGaps(_facts(guider: true, verdict: 'good')), isEmpty);
      expect(rigSetupGaps(_facts(guider: true, verdict: 'check')), isEmpty);
      expect(rigSetupGaps(_facts(guider: true, verdict: 'redo')), [
        RigSetupGap.guiderCalibration,
      ]);
    });

    test(
      'a Guider that is not connected stays on the list as connect to check',
      () {
        final facts = _facts(
          guider: true,
          connected: false,
          data: false,
          verdict: 'redo',
        );
        expect(rigSetupGaps(facts), [RigSetupGap.guiderCalibration]);
        expect(facts.calibrationUnchecked, isTrue);
        expect(_facts(guider: true, data: false).calibrationUnchecked, isFalse);
        expect(
          _facts(guider: false, connected: false).calibrationUnchecked,
          isFalse,
        );
        expect(
          rigSetupGaps(_facts(connected: false)),
          isEmpty,
          reason: 'no Guider in the profile: nothing to connect',
        );
      },
    );

    test('every rule together lists them in the sheet order', () {
      final facts = _facts(
        lat: 0,
        lon: 0,
        optics: false,
        guider: true,
        guideFocal: 0,
        data: false,
      );
      expect(rigSetupGaps(facts), [
        RigSetupGap.site,
        RigSetupGap.mainOptics,
        RigSetupGap.guideScope,
        RigSetupGap.guiderCalibration,
      ]);
    });
  });

  group('rigSetupRigKey', () {
    test('keys on the rig uuid when it has one, else its address, safe as a file name', () {
      const withUuid = AraServer(
        hostname: '192.0.2.10',
        port: 8080,
        uuid: 'a1b2-c3',
      );
      const without = AraServer(hostname: '192.0.2.10', port: 8080);
      expect(rigSetupRigKey(withUuid), 'a1b2-c3');
      expect(rigSetupRigKey(without), '192_0_2_10-8080');
      expect(rigSetupRigKey(null), isNull);
      expect(
        rigSetupRigKey(
          const AraServer(hostname: 'x', port: 1, uuid: '../../etc'),
        ),
        '______etc',
      );
    });
  });
}
