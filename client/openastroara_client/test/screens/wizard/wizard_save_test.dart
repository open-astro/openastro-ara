import 'package:flutter_test/flutter_test.dart';
// Hide the draft's ImagingDefaults; the section model of the same name comes from
// imaging_defaults_state below.
import 'package:openastroara/models/profile_draft.dart'
    hide ImagingDefaults, PlateSolveSettings, AutofocusSettings, SafetyPolicies;
import 'package:openastroara/screens/wizard/wizard_save.dart';
import 'package:openastroara/state/imaging/exposure_state.dart' show FrameKind;
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/settings/plate_solve_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/settings/storage_settings_state.dart';

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

    test('applyDraftToSite maps screen-16 horizon + twilight', () {
      final d = ProfileDraft();
      d.site
        ..hardMinAltitudeDeg = 25
        ..twilight = TwilightOption.nautical;
      final out = applyDraftToSite(const SiteSettings(), d);
      expect(out.defaultHorizonAltitudeDeg, 25);
      expect(out.twilightDefinition, TwilightDefinition.nautical);
    });

    test('applyDraftToSite keeps base horizon/twilight on a blank draft', () {
      final base = const SiteSettings().copyWith(
          defaultHorizonAltitudeDeg: 35,
          twilightDefinition: TwilightDefinition.civil);
      final out = applyDraftToSite(base, ProfileDraft());
      expect(out.defaultHorizonAltitudeDeg, 35);
      expect(out.twilightDefinition, TwilightDefinition.civil);
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

    test('applyDraftToImaging maps screen-14 exposure + frame kind', () {
      final d = ProfileDraft();
      d.imagingDefaults
        ..exposure = const Duration(seconds: 90)
        ..frameType = FrameType.flat;
      final out = applyDraftToImaging(const ImagingDefaults(), d);
      expect(out.defaultExposure, const Duration(seconds: 90));
      expect(out.defaultFrameKind, FrameKind.flat);
    });

    test('applyDraftToImaging keeps base exposure/frame kind on a blank draft', () {
      final base = const ImagingDefaults().copyWith(
          defaultExposure: const Duration(seconds: 300),
          defaultFrameKind: FrameKind.dark);
      final out = applyDraftToImaging(base, ProfileDraft());
      expect(out.defaultExposure, const Duration(seconds: 300));
      expect(out.defaultFrameKind, FrameKind.dark);
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

    test('applyDraftToPlateSolve maps ASTAP paths + search tuning', () {
      final d = ProfileDraft();
      d.plateSolve
        ..astapBinaryPath = '/usr/local/bin/astap'
        ..starDatabasePath = '/opt/astap/d50'
        ..searchRadiusDeg = 15
        ..downsampleFactor = 4;
      final out = applyDraftToPlateSolve(const PlateSolveSettings(), d);
      expect(out.pathOrEndpoint, '/usr/local/bin/astap');
      expect(out.indexDownloadPath, '/opt/astap/d50');
      expect(out.searchRadiusDeg, 15);
      expect(out.downsampleFactor, 4);
    });

    test('applyDraftToPlateSolve preserves base values when the draft is blank',
        () {
      final base = const PlateSolveSettings().copyWith(
        pathOrEndpoint: '/keep/astap',
        indexDownloadPath: '/keep/db',
        searchRadiusDeg: 12,
        downsampleFactor: 4,
      );
      final out = applyDraftToPlateSolve(base, ProfileDraft());
      // A blank draft (all-null) keeps every base value, including the numerics.
      expect(out.pathOrEndpoint, '/keep/astap');
      expect(out.indexDownloadPath, '/keep/db');
      expect(out.searchRadiusDeg, 12);
      expect(out.downsampleFactor, 4);
    });

    test('applyDraftToFilterSet builds planning filters from named draft filters', () {
      final d = ProfileDraft();
      d.filterWheel.filters.addAll([
        FilterDef()..name = 'Ha',
        FilterDef()..name = '  OIII ',
        FilterDef()..name = '', // unnamed rows are skipped
        FilterDef()..name = 'Red',
      ]);
      final out = applyDraftToFilterSet(const FilterSetSettings(), d);
      expect(out.filters.map((f) => f.name), ['Ha', 'OIII', 'Red']);
      expect(out.filters[0].kind, FilterKind.ha, reason: 'same inference as the Settings seed');
      expect(out.filters[1].kind, FilterKind.oiii);
      expect(out.filters[2].kind, FilterKind.r);
    });

    test('applyDraftToFilterSet lets an explicit wavelength rescue an ambiguous name', () {
      // "Filter 1" + 656 nm must land on Hα, not silently become broadband L —
      // the user's explicit entries beat the name fallback. An informative
      // name still wins over a contradictory wavelength.
      final d = ProfileDraft();
      d.filterWheel.filters.addAll([
        FilterDef()
          ..name = 'Filter 1'
          ..wavelengthNm = 656,
        FilterDef()
          ..name = 'Filter 2'
          ..wavelengthNm = 501,
        FilterDef()
          ..name = 'Filter 3'
          ..wavelengthNm = 672,
        FilterDef()
          ..name = 'Red'
          ..wavelengthNm = 656, // informative name wins
        FilterDef()..name = 'Filter 5', // no wavelength → the L fallback stands
      ]);
      final out = applyDraftToFilterSet(const FilterSetSettings(), d);
      expect(out.filters.map((f) => f.kind), [
        FilterKind.ha,
        FilterKind.oiii,
        FilterKind.sii,
        FilterKind.r,
        FilterKind.l,
      ]);
    });

    test('applyDraftToFilterSet dedupes names case-insensitively (keep-first)', () {
      // The daemon 400s the whole filter-set PUT on a duplicate name; the
      // Settings paths dedupe, so the wizard must too — a repeated wheel label
      // must not fail the entire wizard save.
      final d = ProfileDraft();
      d.filterWheel.filters.addAll([
        FilterDef()..name = 'Ha',
        FilterDef()..name = 'ha ',
        FilterDef()..name = 'HA',
        FilterDef()..name = 'OIII',
      ]);
      final out = applyDraftToFilterSet(const FilterSetSettings(), d);
      expect(out.filters.map((f) => f.name), ['Ha', 'OIII']);
    });

    test('applyDraftToFilterSet preserves the base when the draft has no named filters', () {
      const base = FilterSetSettings(
          filters: [PlanningFilter(name: 'L', kind: FilterKind.l)]);
      expect(applyDraftToFilterSet(base, ProfileDraft()).filters, base.filters);
      final unnamedOnly = ProfileDraft()..filterWheel.filters.add(FilterDef());
      expect(applyDraftToFilterSet(base, unnamedOnly).filters, base.filters);
    });

    test('applyDraftToCameraElectronics converts QE percent and preserves ASCOM fields', () {
      // The wizard collects the two user-owned values; the ASCOM-auto-captured
      // fields (sensor, full well, e-/ADU, gain) must survive the merge.
      const base = CameraElectronics(
        sensorName: 'IMX571',
        fullWellE: 51000,
        electronsPerAdu: 0.78,
        gain: 100,
        autoCaptured: true,
      );
      final d = ProfileDraft()
        ..camera.readNoiseE = 1.5
        ..camera.qePeakPct = 80;
      final out = applyDraftToCameraElectronics(base, d);
      expect(out.readNoiseE, 1.5);
      expect(out.quantumEfficiencyPeak, closeTo(0.80, 1e-9));
      expect(out.sensorName, 'IMX571');
      expect(out.fullWellE, 51000);
      expect(out.electronsPerAdu, 0.78);
      expect(out.gain, 100);
      expect(out.autoCaptured, isTrue);
    });

    test('applyDraftToCameraElectronics preserves base on a blank draft', () {
      const base = CameraElectronics(readNoiseE: 2.2, quantumEfficiencyPeak: 0.6);
      final out = applyDraftToCameraElectronics(base, ProfileDraft());
      expect(out.readNoiseE, 2.2);
      expect(out.quantumEfficiencyPeak, 0.6);
    });

    test('applyDraftToCameraElectronics rejects an out-of-range QE percent', () {
      const base = CameraElectronics(quantumEfficiencyPeak: 0.6);
      final d = ProfileDraft()..camera.qePeakPct = 250; // typo'd fraction-vs-percent
      final out = applyDraftToCameraElectronics(base, d);
      expect(out.quantumEfficiencyPeak, 0.6, reason: 'implausible % keeps the stored value');
    });

    test('applyDraftToAutofocus maps the wizard subset', () {
      final d = ProfileDraft();
      d.autofocus
        ..exposureSeconds = 8
        ..steps = 9
        ..stepSize = 40
        ..runAfterFilterChange = false;
      final out = applyDraftToAutofocus(const AutofocusSettings(), d);
      expect(out.exposureSeconds, 8);
      expect(out.steps, 9);
      expect(out.stepSize, 40);
      expect(out.runAfterFilterChange, isFalse);
    });

    test('applyDraftToAutofocus preserves base on a blank draft', () {
      final base = const AutofocusSettings()
          .copyWith(exposureSeconds: 11, steps: 5, runAfterFilterChange: false);
      final out = applyDraftToAutofocus(base, ProfileDraft());
      expect(out.exposureSeconds, 11);
      expect(out.steps, 5);
      expect(out.runAfterFilterChange, isFalse);
    });

    test('applyDraftToStorage maps dir/template + format/compress enums', () {
      final d = ProfileDraft();
      d.fileSaving
        ..saveDirectory = '/media/usb/astro'
        ..filenameTemplate = r'$$DATETIME$$'
        ..format = ImageFormat.xisf
        ..compress = false;
      final out = applyDraftToStorage(const StorageSettings(), d);
      expect(out.saveDirectory, '/media/usb/astro');
      expect(out.filenameTemplate, r'$$DATETIME$$');
      expect(out.fileFormat, StorageFileFormat.xisf);
      expect(out.compression, StorageCompression.off);
    });

    test('applyDraftToStorage maps compress=true to Rice and keeps base on blank',
        () {
      final yes = ProfileDraft()..fileSaving.compress = true;
      expect(applyDraftToStorage(const StorageSettings(), yes).compression,
          StorageCompression.rice);
      // Blank draft preserves base (here: the section default save directory).
      final base = const StorageSettings().copyWith(saveDirectory: '/keep/dir');
      final out = applyDraftToStorage(base, ProfileDraft());
      expect(out.saveDirectory, '/keep/dir');
      expect(out.fileFormat, base.fileFormat);
      expect(out.compression, base.compression);
    });

    test('applyDraftToSafety maps the unsafe-conditions subset', () {
      final d = ProfileDraft();
      d.safety
        ..onUnsafe = UnsafeConditionAction.abortAndPark
        ..autoResumeWhenSafe = false
        ..resumeDelayMin = 3;
      final out = applyDraftToSafety(const SafetyPolicies(), d);
      expect(out.onUnsafe, UnsafeAction.abortAndPark);
      expect(out.autoResumeWhenSafe, isFalse);
      expect(out.resumeDelayMin, 3);
    });

    test('applyDraftToSafety preserves base on a blank draft', () {
      final base = const SafetyPolicies()
          .copyWith(onUnsafe: UnsafeAction.ignore, resumeDelayMin: 20);
      final out = applyDraftToSafety(base, ProfileDraft());
      expect(out.onUnsafe, UnsafeAction.ignore);
      expect(out.resumeDelayMin, 20);
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

    test('applyDraftToPhd2 keeps a bracketed IPv6 host with no port intact', () {
      final d = ProfileDraft();
      d.guider.hostPort = '[::1]';
      const base = Phd2Settings(port: 4400);
      final out = applyDraftToPhd2(base, d);
      expect(out.host, '[::1]');
      expect(out.port, 4400, reason: 'no port appended → keep the base port');
    });

    test('applyDraftToPhd2 with a bare host (no colon) keeps the base port', () {
      final d = ProfileDraft();
      d.guider.hostPort = 'guidepi';
      const base = Phd2Settings(port: 4400);
      final out = applyDraftToPhd2(base, d);
      expect(out.host, 'guidepi');
      expect(out.port, 4400);
    });

    test('applyDraftToPhd2 treats a bare (unbracketed) IPv6 as host-only', () {
      final d = ProfileDraft();
      d.guider.hostPort = '::1';
      const base = Phd2Settings(port: 4400);
      final out = applyDraftToPhd2(base, d);
      expect(out.host, '::1', reason: '2+ colons, no brackets → whole value is the IPv6 host');
      expect(out.port, 4400, reason: 'IPv6 ports require brackets, so no port to split → base port');
    });

    test('applyDraftToPhd2 keeps a longer bare IPv6 literal intact', () {
      final d = ProfileDraft();
      d.guider.hostPort = 'fe80::1';
      const base = Phd2Settings(port: 4400);
      final out = applyDraftToPhd2(base, d);
      expect(out.host, 'fe80::1');
      expect(out.port, 4400);
    });
  });
}
