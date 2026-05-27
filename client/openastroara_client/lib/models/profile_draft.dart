/// Mutable working copy of a profile being built by the §37 wizard.
/// Every field is nullable / has a default — the wizard can be exited at any
/// stage and ARA fills missing fields with the defaults documented in §37.8.
///
/// Per-screen form widgets land in Phase 12b follow-ups; this class is the
/// canonical bag of state the shell hands them.
class ProfileDraft {
  // Stage 1 — Profile basics
  String? profileName;
  String? siteName;
  double? latitudeDeg;
  double? longitudeDeg;
  double? altitudeMeters;
  String? timezone;

  // Stage 2 — Equipment discovery
  String? alpacaBridgeAddress;

  // Per-device slots (Stage 2 + 3). Null = "— None" per §37.2.
  final EquipmentSlots equipment = EquipmentSlots();

  // Stage 3 per-device specifics
  final TelescopeSettings telescope = TelescopeSettings();
  final CameraSettings camera = CameraSettings();
  final FilterWheelSettings filterWheel = FilterWheelSettings();
  final FocuserSettings focuser = FocuserSettings();
  final MountSettings mount = MountSettings();
  final RotatorSettings rotator = RotatorSettings();
  final GuiderSettings guider = GuiderSettings();

  // Stage 4 — Imaging tools
  final PlateSolveSettings plateSolve = PlateSolveSettings();
  final AutofocusSettings autofocus = AutofocusSettings();
  final FileSavingSettings fileSaving = FileSavingSettings();
  final ImagingDefaults imagingDefaults = ImagingDefaults();

  // Stage 5 — Safety + site
  final SafetyPolicies safety = SafetyPolicies();
  final SitePreferences site = SitePreferences();

  // Stage 6 — Sky data downloads (download IDs to fetch)
  final Set<String> skyDataDownloadIds = <String>{};

  // Per-screen "skipped" flags so the profile knows to surface "Default —
  // please review in Settings" markers per §37.8.
  final Set<int> skippedScreens = <int>{};
}

class EquipmentSlots {
  String? cameraDeviceId;
  String? filterWheelDeviceId;
  String? focuserDeviceId;
  String? mountDeviceId;
  String? rotatorDeviceId;
  String? domeDeviceId;
  String? observingConditionsDeviceId;
  String? switchDeviceId;
  String? safetyMonitorDeviceId;
  String? flatPanelDeviceId;
  String? guiderDeviceId;
}

class TelescopeSettings {
  String? name;
  double? focalLengthMm;
  double? apertureMm;
  // Focal ratio derived from focalLengthMm / apertureMm when both set.
}

class CameraSettings {
  double? coolingTargetC;
  double? coolerRampRateCPerMin;
  CoolerWarmupMode warmupMode = CoolerWarmupMode.off;
  int? defaultGain;
  int? defaultOffset;
  int? defaultBin;
  double? pixelSizeMicrons;
}

enum CoolerWarmupMode { off, ramp, immediate }

class FilterWheelSettings {
  final List<FilterDef> filters = <FilterDef>[];
}

class FilterDef {
  String? name;
  FilterType? type;
  int? wavelengthNm;
  int? focusOffsetSteps;
}

enum FilterType { broadband, narrowband, clear, luminance }

class FocuserSettings {
  double? stepSizeMicrons;
  int? backlashInSteps;
  int? backlashOutSteps;
  bool temperatureCompensationEnabled = false;
  double? temperatureCompensationSlope;
}

class MountSettings {
  String? name;
  double? slewRateDegPerSec;
  ParkPositionMode parkMode = ParkPositionMode.syncCurrent;
  MeridianFlipBehavior meridianFlip = MeridianFlipBehavior.auto;
  Duration? settleTimeAfterSlew;
}

enum ParkPositionMode { syncCurrent, defineManually }

enum MeridianFlipBehavior { auto, prompt, never }

class RotatorSettings {
  double? minAngleDeg;
  double? maxAngleDeg;
  double? stepDeg;
  bool reverse = false;
}

class GuiderSettings {
  String hostPort = 'localhost:4400';
  double ditherPixels = 5.0;
  double settleThresholdPx = 1.5;
  Duration settleDuration = const Duration(seconds: 10);
  CalibrationCadence calibrationCadence = CalibrationCadence.eachSession;
}

enum CalibrationCadence { eachSession, onceReuse, neverRecalibrate }

class PlateSolveSettings {
  String? astapBinaryPath;
  String? starDatabasePath;
  double searchRadiusDeg = 30;
  int downsampleFactor = 2;
}

class AutofocusSettings {
  Duration exposure = const Duration(seconds: 5);
  double? stepSizeMicrons;
  int maxRetries = 3;
  bool autoDiscoverFilterOffsets = true;
}

class FileSavingSettings {
  String? saveDirectory;
  ImageFormat format = ImageFormat.fits;
  bool compress = true;
  String filenameTemplate = r'$$DATEMINUS12$$\$$IMAGETYPE$$\$$DATETIME$$_$$FILTER$$_$$SENSORTEMP$$_$$EXPOSURETIME$$s_$$FRAMENR$$';
}

enum ImageFormat { fits, xisf }

class ImagingDefaults {
  Duration? exposure;
  int? gain;
  int? offset;
  FrameType frameType = FrameType.light;
}

enum FrameType { light, dark, bias, flat }

class SafetyPolicies {
  WeatherAction cloudsAction = WeatherAction.pause;
  WeatherAction windAction = WeatherAction.pause;
  double windThresholdKmh = 30;
  WeatherAction rainAction = WeatherAction.abortAndPark;
  Duration wilmaOfflineAutoAbortAfter = const Duration(minutes: 5);
  AlarmUnansweredAction alarmUnanswered = AlarmUnansweredAction.continueAlarm;
  String alarmSound = 'Default';
  bool alarmVibrate = true;
}

enum WeatherAction { pause, abortAndPark, ignore }

enum AlarmUnansweredAction { continueAlarm, escalate, stop }

class SitePreferences {
  double hardMinAltitudeDeg = 5;
  double softWarningAltitudeDeg = 30;
  // Twilight margins + max sequence runtime have defaults that we'll model in
  // a Phase 12b follow-up alongside the sky-math service.
  Duration? maxSequenceRuntime;
}
