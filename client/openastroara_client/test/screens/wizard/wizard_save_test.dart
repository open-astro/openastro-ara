import 'package:flutter_test/flutter_test.dart';
// Hide the draft's ImagingDefaults; the section model of the same name comes from
// imaging_defaults_state below.
import 'package:openastroara/models/profile_draft.dart' hide ImagingDefaults;
import 'package:openastroara/screens/wizard/wizard_save.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';

void main() {
  group('wizard draft → section mappers', () {
    test('applyDraftToSite overrides set fields, preserves the rest', () {
      final d = ProfileDraft()
        ..siteName = 'Backyard'
        ..latitudeDeg = 30.1
        ..longitudeDeg = -97.7
        ..altitudeMeters = 200
        ..timezone = 'America/Chicago';
      const base = SiteSettings(bortleClass: 4); // a field the wizard doesn't set
      final out = applyDraftToSite(base, d);

      expect(out.siteName, 'Backyard');
      expect(out.latitudeDeg, 30.1);
      expect(out.longitudeDeg, -97.7);
      expect(out.elevationM, 200);
      expect(out.timeZone, 'America/Chicago');
      expect(out.bortleClass, 4, reason: 'unset-by-wizard fields keep their base value');
    });

    test('applyDraftToSite leaves base untouched when the draft is empty', () {
      const base = SiteSettings(siteName: 'Existing', latitudeDeg: 12.0);
      final out = applyDraftToSite(base, ProfileDraft());
      expect(out.siteName, 'Existing');
      expect(out.latitudeDeg, 12.0);
    });

    test('applyDraftToOptics maps focal length + pixel size', () {
      final d = ProfileDraft();
      d.telescope.focalLengthMm = 714;
      d.camera.pixelSizeMicrons = 3.76;
      const base = OpticsSettings(reducerFactor: 0.8);
      final out = applyDraftToOptics(base, d);
      expect(out.focalLengthMm, 714);
      expect(out.pixelSizeUm, 3.76);
      expect(out.reducerFactor, 0.8, reason: 'reducer is not a wizard field');
    });

    test('applyDraftToImaging maps camera fields + warmup enum→bool', () {
      final d = ProfileDraft();
      d.camera
        ..coolingTargetC = -15
        ..coolerRampRateCPerMin = 2
        ..defaultGain = 120
        ..defaultOffset = 30
        ..defaultBin = 2
        ..warmupMode = CoolerWarmupMode.ramp;
      final out = applyDraftToImaging(const ImagingDefaults(), d);
      expect(out.coolerTargetC, -15);
      expect(out.coolerRampRatePerMin, 2);
      expect(out.defaultGain, 120);
      expect(out.defaultOffset, 30);
      expect(out.defaultBin, 2);
      expect(out.warmupAtSessionEnd, isTrue);
    });

    test('applyDraftToImaging maps warmup "off" to false', () {
      final d = ProfileDraft();
      d.camera.warmupMode = CoolerWarmupMode.off;
      final out = applyDraftToImaging(const ImagingDefaults(), d);
      expect(out.warmupAtSessionEnd, isFalse);
    });

    test('applyDraftToPhd2 splits host:port and maps guider settings', () {
      final d = ProfileDraft();
      d.guider
        ..hostPort = 'guidepi:4401'
        ..ditherPixels = 4
        ..settleThresholdPx = 1.2
        ..settleDuration = const Duration(seconds: 12)
        ..calibrationCadence = CalibrationCadence.onceReuse;
      final out = applyDraftToPhd2(const Phd2Settings(), d);
      expect(out.host, 'guidepi');
      expect(out.port, 4401);
      expect(out.ditherPixels, 4);
      expect(out.settlePixels, 1.2);
      expect(out.settleTimeSec, 12);
      expect(out.forceCalibrationEachSession, isFalse);
    });

    test('applyDraftToPhd2 sets forceCalibrationEachSession for each-session', () {
      final d = ProfileDraft();
      d.guider.calibrationCadence = CalibrationCadence.eachSession;
      final out = applyDraftToPhd2(const Phd2Settings(), d);
      expect(out.forceCalibrationEachSession, isTrue);
    });

    test('applyDraftToPhd2 keeps a bracketed IPv6 host intact (last-colon split)', () {
      final d = ProfileDraft();
      d.guider.hostPort = '[::1]:4401';
      final out = applyDraftToPhd2(const Phd2Settings(), d);
      expect(out.host, '[::1]');
      expect(out.port, 4401);
    });

    test('applyDraftToPhd2 with a bare host (no colon) keeps the base port', () {
      final d = ProfileDraft();
      d.guider.hostPort = 'guidepi';
      const base = Phd2Settings(port: 4400);
      final out = applyDraftToPhd2(base, d);
      expect(out.host, 'guidepi');
      expect(out.port, 4400);
    });
  });
}
