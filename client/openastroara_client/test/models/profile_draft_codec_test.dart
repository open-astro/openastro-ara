import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_draft.dart';
import 'package:openastroara/models/profile_draft_codec.dart';

void main() {
  test('#1137: a fully populated draft round-trips through JSON', () {
    final d = ProfileDraft()
      ..profileName = 'RedCat 51'
      ..siteName = 'Backyard'
      ..latitudeDeg = 34.1
      ..longitudeDeg = -84.2
      ..altitudeMeters = 300
      ..timezone = 'America/New_York'
      ..alpacaBridgeAddress = '192.168.1.235:32323'
      ..savedProfileId = 'p-77'
      ..sitePrefsSeeded = true
      ..equipmentAutoAssigned = true;
    d.skippedScreens.addAll([2, 4]);
    d.clearedFields.add(ClearableField.saveDirectory);
    d.deviceNames['cam-1'] = 'ZWO ASI2600MC';
    d.equipment
      ..cameraDeviceId = 'cam-1'
      ..mountDeviceId = 'mnt-1'
      ..switchDeviceIds.addAll(['sw-1', 'sw-2']);
    d.telescope
      ..name = 'RedCat 51'
      ..focalLengthMm = 250
      ..apertureMm = 51;
    d.camera
      ..coolingTargetC = -10
      ..warmupMode = CoolerWarmupMode.ramp
      ..defaultGain = 100
      ..pixelSizeMicrons = 3.76;
    d.filterWheel.filters.add(
      FilterDef()
        ..name = 'Ha'
        ..type = FilterType.narrowband
        ..bandwidthNm = 7
        ..focusOffsetSteps = 12,
    );
    d.focuser
      ..stepSizeMicrons = 1.2
      ..backlashInSteps = 30
      ..temperatureCompensationEnabled = true
      ..temperatureCompensationSlope = -2.5;
    d.mount
      ..name = 'EQM-35'
      ..parkMode = ParkPositionMode.defineManually
      ..meridianFlip = MeridianFlipBehavior.prompt
      ..settleTimeAfterSlew = const Duration(seconds: 8);
    d.rotator
      ..minAngleDeg = 0
      ..maxAngleDeg = 180
      ..reverse = true;
    d.guider
      ..hostPort = 'openastro.local:4400'
      ..guiderCamera = 'ASI120MM'
      ..setupType = 'oag'
      ..guideFocalLengthMm = 250
      ..guidePixelSizeUm = 3.75
      ..ditherPixels = 3
      ..settleDuration = const Duration(seconds: 15)
      ..calibrationCadence = CalibrationCadence.onceReuse
      ..buildDarksOnFinish = false
      ..darkFrameCount = 7;
    d.plateSolve
      ..astapBinaryPath = '/usr/bin/astap_cli'
      ..searchRadiusDeg = 30
      ..downsampleFactor = 2;
    d.autofocus
      ..exposureSeconds = 4
      ..steps = 9
      ..runAfterFilterChange = true;
    d.fileSaving
      ..saveDirectory = '/media/openastroara'
      ..format = ImageFormat.fits
      ..compress = false;
    d.imagingDefaults
      ..exposure = const Duration(seconds: 180)
      ..frameType = FrameType.light;
    d.safety
      ..onUnsafe = UnsafeConditionAction.parkOnly
      ..autoResumeWhenSafe = true
      ..maxWindKmh = 40
      ..unattendedShutdownEnabled = true;
    d.site
      ..hardMinAltitudeDeg = 20
      ..twilight = TwilightOption.astronomical
      ..maxSequenceRuntimeMin = 480;

    final back = profileDraftFromJson(profileDraftToJson(d));

    expect(back.profileName, 'RedCat 51');
    expect(back.latitudeDeg, 34.1);
    expect(back.savedProfileId, 'p-77');
    expect(back.sitePrefsSeeded, isTrue);
    expect(back.skippedScreens, {2, 4});
    expect(back.clearedFields, {ClearableField.saveDirectory});
    expect(back.deviceNames, {'cam-1': 'ZWO ASI2600MC'});
    expect(back.equipment.switchDeviceIds, ['sw-1', 'sw-2']);
    expect(back.equipment.filterWheelDeviceId, isNull);
    expect(back.telescope.focalLengthMm, 250);
    expect(back.camera.warmupMode, CoolerWarmupMode.ramp);
    expect(back.camera.defaultOffset, isNull);
    expect(back.filterWheel.filters.single.type, FilterType.narrowband);
    expect(back.filterWheel.filters.single.focusOffsetSteps, 12);
    expect(back.focuser.temperatureCompensationEnabled, isTrue);
    expect(back.mount.meridianFlip, MeridianFlipBehavior.prompt);
    expect(back.mount.settleTimeAfterSlew, const Duration(seconds: 8));
    expect(back.rotator.reverse, isTrue);
    expect(back.guider.hostPort, 'openastro.local:4400');
    expect(back.guider.settleDuration, const Duration(seconds: 15));
    expect(back.guider.calibrationCadence, CalibrationCadence.onceReuse);
    expect(back.guider.buildDarksOnFinish, isFalse);
    expect(back.plateSolve.downsampleFactor, 2);
    expect(back.autofocus.runAfterFilterChange, isTrue);
    expect(back.fileSaving.format, ImageFormat.fits);
    expect(back.imagingDefaults.exposure, const Duration(seconds: 180));
    expect(back.safety.onUnsafe, UnsafeConditionAction.parkOnly);
    expect(back.site.twilight, TwilightOption.astronomical);
  });

  test('#1137: an empty draft round-trips to an empty draft; garbage decodes to defaults', () {
    final back = profileDraftFromJson(profileDraftToJson(ProfileDraft()));
    expect(back.profileName, isNull);
    expect(back.filterWheel.filters, isEmpty);
    expect(back.guider.hostPort, 'localhost:4400');
    expect(back.camera.warmupMode, CoolerWarmupMode.off);

    final bad = profileDraftFromJson({
      'profile_name': 7,
      'camera': {'warmup_mode': 'toasty', 'default_gain': 'high'},
      'mount': 'nope',
      'filter_wheel': {
        'filters': [
          1,
          {'name': 'L', 'type': 'weird'},
        ],
      },
      'skipped_screens': ['a', 3],
    });
    expect(bad.profileName, isNull);
    expect(bad.camera.warmupMode, CoolerWarmupMode.off);
    expect(bad.camera.defaultGain, isNull);
    expect(bad.mount.parkMode, ParkPositionMode.syncCurrent);
    expect(bad.filterWheel.filters.single.name, 'L');
    expect(bad.filterWheel.filters.single.type, isNull);
    expect(bad.skippedScreens, {3});
  });
}
