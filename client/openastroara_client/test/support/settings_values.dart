// ADR 0015 rule 3: the product holds no defaults for a section the rig owns,
// so a test that builds a section states its values. Each class here is the
// section with the values the client used to default to (the daemon's fresh
// profile, see `rigDefaults` in fake_section_rig.dart), every one overridable
// as before: `TestSiteSettings(latitudeDeg: 40)` is a `SiteSettings`.

import 'package:openastroara/state/imaging/exposure_state.dart' show FrameKind;
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';
import 'package:openastroara/state/settings/filenames_settings_state.dart';
import 'package:openastroara/state/settings/filter_wheel_policy_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';
import 'package:openastroara/state/settings/notifications_settings_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/settings/plate_solve_settings_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/settings/unguided_settings_state.dart';

class TestAutofocusSettings extends AutofocusSettings {
  const TestAutofocusSettings({
    super.method = AutofocusMethod.hfrVCurve,
    super.steps = 7,
    super.stepSize = 50,
    super.stepSizeAuto = true,
    super.exposureSeconds = 5,
    super.binning = 1,
    super.afFilter = 'L',
    super.runAfterFilterChange = true,
    super.triggerTempDeltaC = 2.0,
    super.triggerHfrDriftPct = 15,
    super.everyNHours = 2,
    super.abortSequenceOnAfFailure = true,
    super.restorePositionOnFailure = true,
    super.telescopeType = TelescopeType.other,
    super.backlashInSteps = 0,
    super.backlashOutSteps = 0,
  });
}

class TestCameraElectronics extends CameraElectronics {
  const TestCameraElectronics({
    super.sensorName = '',
    super.readNoiseE = 0,
    super.fullWellE = 0,
    super.electronsPerAdu = 0,
    super.gain = -1,
    super.quantumEfficiencyPeak = 0,
    super.autoCaptured = false,
  });
}

class TestEquipmentConnectionSettings extends EquipmentConnectionSettings {
  const TestEquipmentConnectionSettings({
    super.autoConnectOnBoot = const {
      EquipmentDeviceType.camera: true,
      EquipmentDeviceType.mount: true,
      EquipmentDeviceType.focuser: true,
      EquipmentDeviceType.filterWheel: true,
      EquipmentDeviceType.rotator: true,
      EquipmentDeviceType.guider: true,
      EquipmentDeviceType.flatPanel: true,
      EquipmentDeviceType.dome: true,
      EquipmentDeviceType.weather: true,
      EquipmentDeviceType.safetyMonitor: true,
      EquipmentDeviceType.switchDevice: true,
    },
  });
}

class TestFilenamesSettings extends FilenamesSettings {
  const TestFilenamesSettings({
    super.dateSeparator = DateSeparator.forwardSlash,
    super.compressDarksAndBias = true,
    super.headerIdentity = true,
    super.headerSite = true,
    super.headerOptics = true,
    super.headerTemperature = true,
    super.headerWeather = true,
    super.headerEphemeris = true,
  });
}

class TestFilterWheelPolicy extends FilterWheelPolicy {
  const TestFilterWheelPolicy({
    super.homeOnFirstConnect = true,
    super.useFilterWheelOffsets = false,
  });
}

class TestImagingDefaults extends ImagingDefaults {
  const TestImagingDefaults({
    super.defaultExposure = const Duration(seconds: 5),
    super.defaultGain = 100,
    super.defaultOffset = 50,
    super.defaultBin = 1,
    super.defaultFrameKind = FrameKind.light,
    super.coolerTargetC = -10,
    super.coolerRampRatePerMin = 1.0,
    super.warmupAtSessionEnd = false,
  });
}

class TestNotificationsSettings extends NotificationsSettings {
  const TestNotificationsSettings({
    super.inAppBanner = true,
    super.osDesktop = true,
    super.soundAlert = true,
    super.pushoverToken = '',
    super.pushoverUserKey = '',
    super.telegramChatId = '',
    super.telegramBotToken = '',
    super.onSequenceComplete = true,
    super.onSequencePaused = true,
    super.onCriticalDiagnostic = true,
    super.onSafetyEvent = true,
    super.onAutofocusFailed = true,
    super.onPlateSolveFailed = true,
    super.onDiskSpaceLow = true,
  });
}

class TestOpticsSettings extends OpticsSettings {
  const TestOpticsSettings({
    super.focalLengthMm = 0,
    super.reducerFactor = 1.0,
    super.sensorWidthPx = 0,
    super.sensorHeightPx = 0,
    super.pixelSizeUm = 0,
    super.apertureMm = 0,
    super.telescopeName = '',
  });
}

class TestPhd2Settings extends Phd2Settings {
  const TestPhd2Settings({
    super.host = 'localhost',
    super.port = 4400,
    super.phd2Profile = 'Default',
    super.ditherEnabled = true,
    super.ditherEveryNFrames = 1,
    super.ditherPixels = 5.0,
    super.settlePixels = 1.5,
    super.settleTimeSec = 10,
    super.settleTimeoutSec = 60,
    super.forceCalibrationEachSession = false,
    super.guiderSetupType = 'guide_scope',
    super.guideFocalLength = 0,
    super.guidePixelSize = 0,
    super.raAggressiveness = 0.7,
    super.decAggressiveness = 0.7,
    super.minimumMove = 0.15,
    super.decMinimumMove = 0.15,
    super.decGuideMode = 'auto',
    super.guiderCamera = '',
    super.guiderCameraId = '',
    super.guiderCameraAlpacaUniqueId = '',
    super.guiderMount = '',
    super.guiderAuxMount = '',
    super.guiderRotator = '',
    super.guiderAlpacaHost = '',
    super.guiderAlpacaPort = 0,
    super.guideExposureMinMs = 1000,
    super.guideExposureMaxMs = 6000,
    super.guideExposureMs = 0,
    super.guideCameraGain = 50,
    super.guiderProfileId = 0,
  });
}

class TestPlateSolveSettings extends PlateSolveSettings {
  const TestPlateSolveSettings({
    super.engine = PlateSolveEngine.astap,
    super.pathOrEndpoint = '/opt/openastroara/astap_cli',
    super.indexDownloadPath = '/var/lib/astap',
    super.searchRadiusDeg = 30,
    super.downsampleFactor = 0,
    super.timeoutSeconds = 60,
    super.useBlindFallback = true,
    super.centerAfterSlew = true,
    super.syncToCoordinates = true,
    super.maxIterations = 5,
    super.convergenceToleranceArcsec = 60,
  });
}

class TestSafetyPolicies extends SafetyPolicies {
  const TestSafetyPolicies({
    super.onUnsafe = UnsafeAction.pauseAndPark,
    super.autoResumeWhenSafe = true,
    super.resumeDelayMin = 10,
    super.meridianFlipAuto = true,
    super.meridianPauseMin = 5,
    super.meridianRecenter = true,
    super.meridianRecalGuider = true,
    super.meridianRefocus = MeridianRefocusMode.ifTempDrifted,
    super.meridianRefocusTempThresholdC = 2.0,
    super.meridianGuiderRecal = GuiderRecalMode.autoRestore,
    super.unattendedWindowStart = '',
    super.unattendedWindowEnd = '',
    super.onAltitudeLimit = AltitudeLimitAction.skipTarget,
    super.parkIfNoMoreTargets = true,
    super.onGuiderLost = GuiderLostAction.pauseAndRetry,
    super.guiderRetryTimeoutSec = 60,
    super.skipTargetIfRecoveryFails = true,
    super.onDiskSpaceCritical = DiskSpaceCriticalAction.warn,
    super.flipSafetyEnabled = true,
    super.expectedFlipSlewSeconds = 90,
    super.firstFlipConfirmed = false,
    super.firstFlipPierSideChanged = false,
    super.unattendedEscalation = true,
    super.unattendedShutdownEnabled = true,
    super.unattendedShutdownWaitMinutes = 30,
    super.calibrationCaptureDefault = CalibrationCaptureDefault.ask,
    super.weatherTriggersEnabled = false,
    super.maxWindKmh = 36,
    super.maxHumidityPct = 85,
    super.minDewDeltaC = 2.0,
    super.flatTargetAdu = 30000,
    super.flatTargetPercent = 50,
    super.flatTargetAduTolerancePct = 5.0,
    super.flatFramesPerFilter = 30,
    super.postFlatParkMount = false,
    super.skyFlatTargetAdu = 25000,
    super.skyFlatFramesPerFilter = 20,
    super.skyFlatTargetAzimuth = 90.0,
    super.skyFlatTargetAltitude = 75.0,
    super.skyFlatStopAtMaxAdu = 50000.0,
    super.skyFlatStopAtMinAdu = 5000.0,
    super.skyFlatSunAltitude = -9.0,
    super.firstFlipCompletedUtc,
    super.unmodeled,
  });
}

class TestSiteSettings extends SiteSettings {
  const TestSiteSettings({
    super.siteName = 'Backyard',
    super.latitudeDeg = 0,
    super.longitudeDeg = 0,
    super.elevationM = 0,
    super.timeZone = 'UTC',
    super.useCustomHorizon = false,
    super.defaultHorizonAltitudeDeg = 20,
    super.bortleClass = 6,
    super.sqmMagPerArcsec2 = 0,
    super.typicalSeeingArcsec = 2.5,
    super.twilightDefinition = TwilightDefinition.astronomical,
    super.maxSequenceRuntimeMin = 0,
    super.softWarningAltitudeDeg = 30,
    super.observerName = '',
    super.permanentPier = false,
  });
}

class TestUnguidedSettings extends UnguidedSettings {
  const TestUnguidedSettings({
    super.guardEnabled = true,
    super.eccentricityTolerance = 0.5,
    super.goodEccentricity = 0.3,
    super.stepDownAfter = 2,
    super.stepUpAfter = 10,
    super.probeRungsSeconds = UnguidedSettings.defaultProbeRungsSeconds,
    super.recentreDriftArcmin = 10,
    super.recentreSolveEveryN = 1,
    super.priorToleranceBasis = 'max',
  });
}
