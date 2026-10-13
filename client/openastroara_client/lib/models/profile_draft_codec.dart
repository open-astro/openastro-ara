/// JSON codec for [ProfileDraft] (#1137, §2 offline profile authoring).
///
/// The draft is a mutable bag of nullable fields; persisting it locally lets
/// the wizard finish with no daemon in reach and replay the same
/// `saveWizardProfile` on reconnect. Every enum travels by name, every
/// Duration in seconds, nulls are omitted; unknown or malformed values decode
/// to the field's default so an old file never throws.
library;

import 'profile_draft.dart';

/// Wire version of the local file; bump when a field changes meaning.
const int profileDraftCodecVersion = 1;

Map<String, dynamic> profileDraftToJson(ProfileDraft d) {
  Map<String, dynamic> strip(Map<String, dynamic> m) => {
    for (final e in m.entries)
      if (e.value != null) e.key: e.value,
  };
  return {
    'v': profileDraftCodecVersion,
    ...strip({
      'profile_name': d.profileName,
      'site_name': d.siteName,
      'latitude_deg': d.latitudeDeg,
      'longitude_deg': d.longitudeDeg,
      'altitude_m': d.altitudeMeters,
      'timezone': d.timezone,
      'alpaca_bridge_address': d.alpacaBridgeAddress,
      'saved_profile_id': d.savedProfileId,
    }),
    'site_prefs_seeded': d.sitePrefsSeeded,
    'equipment_auto_assigned': d.equipmentAutoAssigned,
    'skipped_screens': [...d.skippedScreens]..sort(),
    'cleared_fields': [...d.clearedFields]..sort(),
    'device_names': Map<String, String>.from(d.deviceNames),
    'equipment': strip({
      'camera': d.equipment.cameraDeviceId,
      'filter_wheel': d.equipment.filterWheelDeviceId,
      'focuser': d.equipment.focuserDeviceId,
      'mount': d.equipment.mountDeviceId,
      'rotator': d.equipment.rotatorDeviceId,
      'dome': d.equipment.domeDeviceId,
      'observing_conditions': d.equipment.observingConditionsDeviceId,
      'switches': [...d.equipment.switchDeviceIds],
      'safety_monitor': d.equipment.safetyMonitorDeviceId,
      'flat_panel': d.equipment.flatPanelDeviceId,
    }),
    'telescope': strip({
      'name': d.telescope.name,
      'focal_length_mm': d.telescope.focalLengthMm,
      'aperture_mm': d.telescope.apertureMm,
    }),
    'camera': strip({
      'cooling_target_c': d.camera.coolingTargetC,
      'cooler_ramp_c_per_min': d.camera.coolerRampRateCPerMin,
      'warmup_mode': d.camera.warmupMode.name,
      'default_gain': d.camera.defaultGain,
      'default_offset': d.camera.defaultOffset,
      'default_bin': d.camera.defaultBin,
      'pixel_size_um': d.camera.pixelSizeMicrons,
      'read_noise_e': d.camera.readNoiseE,
      'qe_peak_pct': d.camera.qePeakPct,
    }),
    'filter_wheel': {
      'filters': [
        for (final f in d.filterWheel.filters)
          strip({
            'name': f.name,
            'type': f.type?.name,
            'bandwidth_nm': f.bandwidthNm,
            'focus_offset_steps': f.focusOffsetSteps,
          }),
      ],
    },
    'focuser': strip({
      'step_size_um': d.focuser.stepSizeMicrons,
      'backlash_in_steps': d.focuser.backlashInSteps,
      'backlash_out_steps': d.focuser.backlashOutSteps,
      'temperature_compensation_enabled':
          d.focuser.temperatureCompensationEnabled,
      'temperature_compensation_slope': d.focuser.temperatureCompensationSlope,
    }),
    'mount': strip({
      'name': d.mount.name,
      'slew_rate_deg_per_sec': d.mount.slewRateDegPerSec,
      'park_mode': d.mount.parkMode.name,
      'meridian_flip': d.mount.meridianFlip.name,
      'settle_seconds': d.mount.settleTimeAfterSlew?.inSeconds,
    }),
    'rotator': strip({
      'min_angle_deg': d.rotator.minAngleDeg,
      'max_angle_deg': d.rotator.maxAngleDeg,
      'step_deg': d.rotator.stepDeg,
      'reverse': d.rotator.reverse,
    }),
    'guider': strip({
      'host_port': d.guider.hostPort,
      'camera': d.guider.guiderCamera,
      'mount': d.guider.guiderMount,
      'aux_mount': d.guider.guiderAuxMount,
      'rotator': d.guider.guiderRotator,
      'setup_type': d.guider.setupType,
      'guide_focal_length_mm': d.guider.guideFocalLengthMm,
      'guide_pixel_size_um': d.guider.guidePixelSizeUm,
      'dither_pixels': d.guider.ditherPixels,
      'settle_threshold_px': d.guider.settleThresholdPx,
      'settle_seconds': d.guider.settleDuration.inSeconds,
      'calibration_cadence': d.guider.calibrationCadence.name,
      'dark_min_exposure_ms': d.guider.darkMinExposureMs,
      'dark_max_exposure_ms': d.guider.darkMaxExposureMs,
      'build_darks_on_finish': d.guider.buildDarksOnFinish,
      'dark_frame_count': d.guider.darkFrameCount,
    }),
    'plate_solve': strip({
      'astap_binary_path': d.plateSolve.astapBinaryPath,
      'star_database_path': d.plateSolve.starDatabasePath,
      'search_radius_deg': d.plateSolve.searchRadiusDeg,
      'downsample_factor': d.plateSolve.downsampleFactor,
    }),
    'autofocus': strip({
      'exposure_seconds': d.autofocus.exposureSeconds,
      'steps': d.autofocus.steps,
      'step_size': d.autofocus.stepSize,
      'run_after_filter_change': d.autofocus.runAfterFilterChange,
      'telescope_type': d.autofocus.telescopeType,
    }),
    'file_saving': strip({
      'save_directory': d.fileSaving.saveDirectory,
      'format': d.fileSaving.format?.name,
      'compress': d.fileSaving.compress,
      'filename_template': d.fileSaving.filenameTemplate,
    }),
    'imaging_defaults': strip({
      'exposure_seconds': d.imagingDefaults.exposure?.inSeconds,
      'frame_type': d.imagingDefaults.frameType?.name,
    }),
    'safety': strip({
      'on_unsafe': d.safety.onUnsafe?.name,
      'auto_resume_when_safe': d.safety.autoResumeWhenSafe,
      'resume_delay_min': d.safety.resumeDelayMin,
      'weather_triggers_enabled': d.safety.weatherTriggersEnabled,
      'max_wind_kmh': d.safety.maxWindKmh,
      'max_humidity_pct': d.safety.maxHumidityPct,
      'min_dew_delta_c': d.safety.minDewDeltaC,
      'unattended_shutdown_enabled': d.safety.unattendedShutdownEnabled,
      'unattended_shutdown_wait_min': d.safety.unattendedShutdownWaitMin,
    }),
    'site': strip({
      'hard_min_altitude_deg': d.site.hardMinAltitudeDeg,
      'twilight': d.site.twilight?.name,
      'max_sequence_runtime_min': d.site.maxSequenceRuntimeMin,
      'soft_warning_altitude_deg': d.site.softWarningAltitudeDeg,
    }),
  };
}

/// Decode a draft written by [profileDraftToJson]. Never throws: a missing or
/// malformed section leaves that part of the draft at its defaults.
ProfileDraft profileDraftFromJson(Map<String, dynamic> json) {
  final d = ProfileDraft();
  String? str(Map<String, dynamic> m, String k) =>
      m[k] is String ? m[k] as String : null;
  double? dbl(Map<String, dynamic> m, String k) =>
      m[k] is num ? (m[k] as num).toDouble() : null;
  int? integer(Map<String, dynamic> m, String k) =>
      m[k] is num ? (m[k] as num).toInt() : null;
  bool? boolean(Map<String, dynamic> m, String k) =>
      m[k] is bool ? m[k] as bool : null;
  Map<String, dynamic> section(String k) =>
      json[k] is Map ? Map<String, dynamic>.from(json[k] as Map) : const {};
  T? enumByName<T extends Enum>(List<T> values, String? name) {
    if (name == null) return null;
    for (final v in values) {
      if (v.name == name) return v;
    }
    return null;
  }

  d.profileName = str(json, 'profile_name');
  d.siteName = str(json, 'site_name');
  d.latitudeDeg = dbl(json, 'latitude_deg');
  d.longitudeDeg = dbl(json, 'longitude_deg');
  d.altitudeMeters = dbl(json, 'altitude_m');
  d.timezone = str(json, 'timezone');
  d.alpacaBridgeAddress = str(json, 'alpaca_bridge_address');
  d.savedProfileId = str(json, 'saved_profile_id');
  d.sitePrefsSeeded = boolean(json, 'site_prefs_seeded') ?? false;
  d.equipmentAutoAssigned = boolean(json, 'equipment_auto_assigned') ?? false;
  if (json['skipped_screens'] is List) {
    d.skippedScreens.addAll([
      for (final s in json['skipped_screens'] as List)
        if (s is num) s.toInt(),
    ]);
  }
  if (json['cleared_fields'] is List) {
    d.clearedFields.addAll([
      for (final s in json['cleared_fields'] as List)
        if (s is String) s,
    ]);
  }
  if (json['device_names'] is Map) {
    for (final e in (json['device_names'] as Map).entries) {
      if (e.key is String && e.value is String) {
        d.deviceNames[e.key as String] = e.value as String;
      }
    }
  }

  final eq = section('equipment');
  d.equipment.cameraDeviceId = str(eq, 'camera');
  d.equipment.filterWheelDeviceId = str(eq, 'filter_wheel');
  d.equipment.focuserDeviceId = str(eq, 'focuser');
  d.equipment.mountDeviceId = str(eq, 'mount');
  d.equipment.rotatorDeviceId = str(eq, 'rotator');
  d.equipment.domeDeviceId = str(eq, 'dome');
  d.equipment.observingConditionsDeviceId = str(eq, 'observing_conditions');
  if (eq['switches'] is List) {
    d.equipment.switchDeviceIds.addAll([
      for (final s in eq['switches'] as List)
        if (s is String) s,
    ]);
  }
  d.equipment.safetyMonitorDeviceId = str(eq, 'safety_monitor');
  d.equipment.flatPanelDeviceId = str(eq, 'flat_panel');

  final tel = section('telescope');
  d.telescope.name = str(tel, 'name');
  d.telescope.focalLengthMm = dbl(tel, 'focal_length_mm');
  d.telescope.apertureMm = dbl(tel, 'aperture_mm');

  final cam = section('camera');
  d.camera.coolingTargetC = dbl(cam, 'cooling_target_c');
  d.camera.coolerRampRateCPerMin = dbl(cam, 'cooler_ramp_c_per_min');
  d.camera.warmupMode =
      enumByName(CoolerWarmupMode.values, str(cam, 'warmup_mode')) ??
      CoolerWarmupMode.off;
  d.camera.defaultGain = integer(cam, 'default_gain');
  d.camera.defaultOffset = integer(cam, 'default_offset');
  d.camera.defaultBin = integer(cam, 'default_bin');
  d.camera.pixelSizeMicrons = dbl(cam, 'pixel_size_um');
  d.camera.readNoiseE = dbl(cam, 'read_noise_e');
  d.camera.qePeakPct = dbl(cam, 'qe_peak_pct');

  final fw = section('filter_wheel');
  if (fw['filters'] is List) {
    for (final raw in fw['filters'] as List) {
      if (raw is! Map) continue;
      final f = Map<String, dynamic>.from(raw);
      d.filterWheel.filters.add(
        FilterDef()
          ..name = str(f, 'name')
          ..type = enumByName(FilterType.values, str(f, 'type'))
          ..bandwidthNm = dbl(f, 'bandwidth_nm')
          ..focusOffsetSteps = integer(f, 'focus_offset_steps'),
      );
    }
  }

  final foc = section('focuser');
  d.focuser.stepSizeMicrons = dbl(foc, 'step_size_um');
  d.focuser.backlashInSteps = integer(foc, 'backlash_in_steps');
  d.focuser.backlashOutSteps = integer(foc, 'backlash_out_steps');
  d.focuser.temperatureCompensationEnabled =
      boolean(foc, 'temperature_compensation_enabled') ?? false;
  d.focuser.temperatureCompensationSlope = dbl(
    foc,
    'temperature_compensation_slope',
  );

  final mnt = section('mount');
  d.mount.name = str(mnt, 'name');
  d.mount.slewRateDegPerSec = dbl(mnt, 'slew_rate_deg_per_sec');
  d.mount.parkMode =
      enumByName(ParkPositionMode.values, str(mnt, 'park_mode')) ??
      ParkPositionMode.syncCurrent;
  d.mount.meridianFlip =
      enumByName(MeridianFlipBehavior.values, str(mnt, 'meridian_flip')) ??
      MeridianFlipBehavior.auto;
  final settle = integer(mnt, 'settle_seconds');
  d.mount.settleTimeAfterSlew = settle == null
      ? null
      : Duration(seconds: settle);

  final rot = section('rotator');
  d.rotator.minAngleDeg = dbl(rot, 'min_angle_deg');
  d.rotator.maxAngleDeg = dbl(rot, 'max_angle_deg');
  d.rotator.stepDeg = dbl(rot, 'step_deg');
  d.rotator.reverse = boolean(rot, 'reverse') ?? false;

  final g = section('guider');
  d.guider.hostPort = str(g, 'host_port') ?? d.guider.hostPort;
  d.guider.guiderCamera = str(g, 'camera');
  d.guider.guiderMount = str(g, 'mount');
  d.guider.guiderAuxMount = str(g, 'aux_mount');
  d.guider.guiderRotator = str(g, 'rotator');
  d.guider.setupType = str(g, 'setup_type');
  d.guider.guideFocalLengthMm = integer(g, 'guide_focal_length_mm');
  d.guider.guidePixelSizeUm = dbl(g, 'guide_pixel_size_um');
  d.guider.ditherPixels = dbl(g, 'dither_pixels') ?? d.guider.ditherPixels;
  d.guider.settleThresholdPx =
      dbl(g, 'settle_threshold_px') ?? d.guider.settleThresholdPx;
  final gs = integer(g, 'settle_seconds');
  if (gs != null) d.guider.settleDuration = Duration(seconds: gs);
  d.guider.calibrationCadence =
      enumByName(CalibrationCadence.values, str(g, 'calibration_cadence')) ??
      d.guider.calibrationCadence;
  d.guider.darkMinExposureMs =
      integer(g, 'dark_min_exposure_ms') ?? d.guider.darkMinExposureMs;
  d.guider.darkMaxExposureMs =
      integer(g, 'dark_max_exposure_ms') ?? d.guider.darkMaxExposureMs;
  d.guider.buildDarksOnFinish =
      boolean(g, 'build_darks_on_finish') ?? d.guider.buildDarksOnFinish;
  d.guider.darkFrameCount =
      integer(g, 'dark_frame_count') ?? d.guider.darkFrameCount;

  final ps = section('plate_solve');
  d.plateSolve.astapBinaryPath = str(ps, 'astap_binary_path');
  d.plateSolve.starDatabasePath = str(ps, 'star_database_path');
  d.plateSolve.searchRadiusDeg = dbl(ps, 'search_radius_deg');
  d.plateSolve.downsampleFactor = integer(ps, 'downsample_factor');

  final af = section('autofocus');
  d.autofocus.exposureSeconds = integer(af, 'exposure_seconds');
  d.autofocus.steps = integer(af, 'steps');
  d.autofocus.stepSize = integer(af, 'step_size');
  d.autofocus.runAfterFilterChange = boolean(af, 'run_after_filter_change');
  d.autofocus.telescopeType = str(af, 'telescope_type');

  final fs = section('file_saving');
  d.fileSaving.saveDirectory = str(fs, 'save_directory');
  d.fileSaving.format = enumByName(ImageFormat.values, str(fs, 'format'));
  d.fileSaving.compress = boolean(fs, 'compress');
  d.fileSaving.filenameTemplate = str(fs, 'filename_template');

  final im = section('imaging_defaults');
  final exp = integer(im, 'exposure_seconds');
  d.imagingDefaults.exposure = exp == null ? null : Duration(seconds: exp);
  d.imagingDefaults.frameType = enumByName(
    FrameType.values,
    str(im, 'frame_type'),
  );

  final sf = section('safety');
  d.safety.onUnsafe = enumByName(
    UnsafeConditionAction.values,
    str(sf, 'on_unsafe'),
  );
  d.safety.autoResumeWhenSafe = boolean(sf, 'auto_resume_when_safe');
  d.safety.resumeDelayMin = integer(sf, 'resume_delay_min');
  d.safety.weatherTriggersEnabled = boolean(sf, 'weather_triggers_enabled');
  d.safety.maxWindKmh = integer(sf, 'max_wind_kmh');
  d.safety.maxHumidityPct = integer(sf, 'max_humidity_pct');
  d.safety.minDewDeltaC = dbl(sf, 'min_dew_delta_c');
  d.safety.unattendedShutdownEnabled = boolean(
    sf,
    'unattended_shutdown_enabled',
  );
  d.safety.unattendedShutdownWaitMin = integer(
    sf,
    'unattended_shutdown_wait_min',
  );

  final st = section('site');
  d.site.hardMinAltitudeDeg = dbl(st, 'hard_min_altitude_deg');
  d.site.twilight = enumByName(TwilightOption.values, str(st, 'twilight'));
  d.site.maxSequenceRuntimeMin = integer(st, 'max_sequence_runtime_min');
  d.site.softWarningAltitudeDeg = dbl(st, 'soft_warning_altitude_deg');
  return d;
}
