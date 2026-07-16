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

  // §37.5 — whether the Site step has seeded its safe starter numbers THIS
  // wizard session: rebuilds on back/forward must not re-seed over an
  // explicit blank.
  bool sitePrefsSeeded = false;

  // Per-screen "skipped" flags so the profile knows to surface "Default —
  // please review in Settings" markers per §37.8.
  final Set<int> skippedScreens = <int>{};

  /// §37 clear-field affordance — [ClearableField] keys the user explicitly
  /// reset. The wizard's new profile CLONES the active one, and the mappers
  /// treat a blank field as "keep the cloned value" — so without this, an old
  /// rig's ASTAP path or save directory silently survives into the new
  /// profile with no way to shed it. A key here makes the mapper write the
  /// section DEFAULT instead of keeping the clone; typing into the field
  /// again removes the key (the typed value wins).
  final Set<String> clearedFields = <String>{};

  // Server id of the profile this draft was persisted as, set on the first
  // successful create during Save. A retry after a mid-save failure re-uses it
  // (re-applying the sections) instead of orphaning a new profile each attempt.
  String? savedProfileId;
}

/// Canonical [ProfileDraft.clearedFields] keys — the string fields where a
/// stale cloned value is actively harmful (wrong solver paths on a new rig,
/// frames landing in the old rig's directory). One key per clearable field so
/// the screens and the wizard_save mappers can't drift on spelling.
abstract final class ClearableField {
  static const String astapBinaryPath = 'plateSolve.astapBinaryPath';
  static const String starDatabasePath = 'plateSolve.starDatabasePath';
  static const String saveDirectory = 'fileSaving.saveDirectory';
  static const String filenameTemplate = 'fileSaving.filenameTemplate';
}

class EquipmentSlots {
  String? cameraDeviceId;
  String? filterWheelDeviceId;
  String? focuserDeviceId;
  String? mountDeviceId;
  String? rotatorDeviceId;
  String? domeDeviceId;
  String? observingConditionsDeviceId;

  /// A rig can carry several switch hubs (power box, dew controller, relay
  /// board) — §6.4 multi-switch is wired daemon-side, so the wizard assigns
  /// them all, not just a default.
  final List<String> switchDeviceIds = [];
  String? safetyMonitorDeviceId;
  String? flatPanelDeviceId;
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
  // NEXTGEN §4 — the two electronics values ASCOM never reports (spec-sheet
  // numbers), so a fresh setup ends exposure-planning-ready without a Settings
  // detour. QE is entered as a percent (0–100); the profile stores a fraction.
  double? readNoiseE;
  double? qePeakPct;
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
  // Nullable so a blank/untouched field preserves the base profile's value on
  // Save (the wizard draft starts fresh; null = "not set by the user"). The
  // screen shows 30 / 2 as defaults but only writes on user input.
  double? searchRadiusDeg;
  int? downsampleFactor;
}

class AutofocusSettings {
  // Wizard-collected subset of the profile's autofocus section. All nullable so a
  // blank/untouched field preserves the base profile's value on Save (null =
  // "not set by the user"), matching the other §37.4 screens. These map 1:1 onto
  // the AutofocusSettings *section* DTO (see wizard_save.applyDraftToAutofocus).
  int? exposureSeconds;
  int? steps;
  int? stepSize;
  bool? runAfterFilterChange;
  // §59.4/§59.14 — the §59.13 wire string (`refractor|sct|mak|rc|newtonian|other`);
  // kept as a string here so this bag stays import-free, converted to the settings
  // enum in wizard_save.applyDraftToAutofocus.
  String? telescopeType;
}

class FileSavingSettings {
  // All nullable so a blank/untouched field preserves the base profile's value on
  // Save (null = "not set"), mapped onto the profile's storage section in
  // wizard_save.applyDraftToStorage. [compress] is a wizard simplification of the
  // section's 3-way compression (true → Rice, false → Off; gzip stays a
  // Settings-only choice).
  String? saveDirectory;
  ImageFormat? format;
  bool? compress;
  String? filenameTemplate;
}

enum ImageFormat { fits, xisf }

class ImagingDefaults {
  // Screen 14's subset. Nullable so a blank/untouched field keeps the base on
  // Save. Gain/offset/bin + cooling are collected on the Camera screen (5) and
  // mapped from `d.camera`, so they're intentionally not duplicated here.
  Duration? exposure;
  FrameType? frameType;
}

enum FrameType { light, dark, bias, flat }

class SafetyPolicies {
  // Wizard subset that maps onto the profile's safety section: the general
  // unsafe-conditions reaction + auto-resume behaviour, plus the §35.1
  // weather thresholds now that their enforcement landed (a breach reacts via
  // the same on_unsafe policy — per-trigger actions stay deferred by design,
  // PORT_DECISIONS 2026-07-07). All nullable (null keeps base). Alarm
  // sound/delay are device-local knobs (Settings → Notifications), not
  // profile fields, so the wizard doesn't carry them.
  UnsafeConditionAction? onUnsafe;
  bool? autoResumeWhenSafe;
  int? resumeDelayMin;
  bool? weatherTriggersEnabled;
  int? maxWindKmh;
  int? maxHumidityPct;
  double? minDewDeltaC;
  // §58.12 — the "when something's wrong and I'm not here" countdown
  // (playbook screen 15's WILMA-offline auto-abort): whether the unattended
  // graceful shutdown runs, and how many minutes of unattended silence arm it.
  bool? unattendedShutdownEnabled;
  int? unattendedShutdownWaitMin;
}

/// Mirrors the safety section's `UnsafeAction` (mapped in wizard_save) — what to
/// do when the safety monitor reports conditions are unsafe.
enum UnsafeConditionAction { pauseAndPark, parkOnly, abortAndPark, ignore }



class SitePreferences {
  // Wizard subset that maps onto the profile's site section. Nullable (null keeps
  // base). The horizon altitude is the hard floor below which targets aren't
  // observed. §37.5: the max-sequence-runtime cap landed with its sequencer
  // consumer (0 = no limit); a soft-warning altitude stays deferred (see
  // design/PORT_TODO.md).
  double? hardMinAltitudeDeg;
  TwilightOption? twilight;
  int? maxSequenceRuntimeMin;
  double? softWarningAltitudeDeg;
}

/// Mirrors the site section's `TwilightDefinition` (mapped in wizard_save) — how
/// dark it must be before imaging starts/ends.
enum TwilightOption { civil, nautical, astronomical }
