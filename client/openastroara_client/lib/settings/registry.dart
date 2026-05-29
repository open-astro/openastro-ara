/// §61 settings registry. Single source of truth for every settable field
/// across all panels. Used for:
/// 1. Search index (Command Palette)
/// 2. Inline edit rendering
/// 3. Cross-linking hints
/// 4. Mechanical enforcement gate (COMMIT-PR-RULES.md)
library;

enum SettingTypeKind {
  bool,
  intRange,
  doubleRange,
  enumValue,
  string,
  duration,
  color,
  keyboardShortcut,
  path,
  complex,
}

class SettingType {
  final SettingTypeKind kind;
  final num? min;
  final num? max;
  final num? step;
  final List<String>? enumValues;

  const SettingType.bool() : kind = SettingTypeKind.bool, min = null, max = null, step = null, enumValues = null;
  // ignore: prefer_initializing_formals
  const SettingType.intRange({required int min, required int max}) : kind = SettingTypeKind.intRange, min = min, max = max, step = 1, enumValues = null;
  // ignore: prefer_initializing_formals
  const SettingType.doubleRange({required double min, required double max, double step = 0.1}) : kind = SettingTypeKind.doubleRange, min = min, max = max, step = step, enumValues = null;
  const SettingType.enumValue(List<String> values) : kind = SettingTypeKind.enumValue, min = null, max = null, step = null, enumValues = values;
  const SettingType.string() : kind = SettingTypeKind.string, min = null, max = null, step = null, enumValues = null;
  const SettingType.duration() : kind = SettingTypeKind.duration, min = null, max = null, step = null, enumValues = null;
  const SettingType.path() : kind = SettingTypeKind.path, min = null, max = null, step = null, enumValues = null;
  const SettingType.complex() : kind = SettingTypeKind.complex, min = null, max = null, step = null, enumValues = null;
}

class Setting {
  final String id;
  final String label;
  final String description;
  final List<String> keywords;
  final List<String> path;
  final SettingType type;
  final dynamic defaultValue;
  final String? profilePath;
  final List<String> relatedSettings;

  const Setting({
    required this.id,
    required this.label,
    required this.description,
    required this.keywords,
    required this.path,
    required this.type,
    required this.defaultValue,
    this.profilePath,
    this.relatedSettings = const <String>[],
  });
}

/// The canonical settings registry.
const List<Setting> settingsRegistry = [
  // §35 Safety Policies
  Setting(
    id: 'safety.policies.on_unsafe',
    label: 'On unsafe weather',
    description: 'What action to take when the safety monitor reports unsafe weather.',
    keywords: ['unsafe', 'weather', 'pause', 'park', 'abort', 'dome'],
    path: ['Settings', 'Safety', 'Policies'],
    type: SettingType.enumValue(['Pause + park', 'Park only', 'Abort + park', 'Ignore']),
    defaultValue: 'Pause + park',
    profilePath: 'safety.on_unsafe',
  ),
  Setting(
    id: 'safety.policies.auto_resume',
    label: 'Auto-resume when safe',
    description: 'Whether to automatically resume the sequence when weather becomes safe again.',
    keywords: ['resume', 'safe', 'automatic', 'weather'],
    path: ['Settings', 'Safety', 'Policies'],
    type: SettingType.bool(),
    defaultValue: true,
    profilePath: 'safety.auto_resume',
  ),
  Setting(
    id: 'safety.policies.resume_delay',
    label: 'Resume delay (min)',
    description: 'How long to wait after weather becomes safe before resuming.',
    keywords: ['delay', 'wait', 'resume', 'safe'],
    path: ['Settings', 'Safety', 'Policies'],
    type: SettingType.intRange(min: 0, max: 60),
    defaultValue: 5,
    profilePath: 'safety.resume_delay_min',
  ),

  // §63 PHD2 / Guider
  Setting(
    id: 'guider.dither_pixels',
    label: 'Dither pixels',
    description: 'How many pixels PHD2 dithers between exposures. Larger values '
        'randomize hot-pixel positions more aggressively; smaller values '
        'settle faster.',
    keywords: ['dither', 'guide', 'guider', 'phd2', 'randomize', 'hot pixel'],
    path: ['Settings', 'Guider', 'PHD2'],
    type: SettingType.intRange(min: 0, max: 50),
    defaultValue: 5,
    profilePath: 'guider.dither_pixels',
    relatedSettings: [
      'guider.dither_settle_threshold',
      'guider.dither_timeout_action',
    ],
  ),

  // §51 Diagnostics
  Setting(
    id: 'diagnostics.mode',
    label: 'Diagnostics mode',
    description: 'Smart correction policy: notify only, balanced, or aggressive auto-recovery.',
    keywords: ['diagnostics', 'smart', 'correction', 'auto-recovery', 'notify'],
    path: ['Settings', 'Safety', 'Diagnostics'],
    type: SettingType.enumValue(['Notify only', 'Balanced', 'Aggressive']),
    defaultValue: 'Notify only',
    profilePath: 'diagnostics.mode',
  ),

  // §37.9 Imaging Defaults — 8 fields. State lives in `imagingDefaultsProvider`.
  Setting(
    id: 'imaging.defaults.exposure',
    label: 'Default exposure (s)',
    description: 'Seed exposure time for the Imaging tab when no target-specific override exists. New sequences inherit this.',
    keywords: ['exposure', 'time', 'seconds', 'default', 'shutter'],
    path: ['Settings', 'Imaging', 'Defaults'],
    type: SettingType.intRange(min: 1, max: 3600),
    defaultValue: 5,
    profilePath: 'imaging.default_exposure_s',
  ),
  Setting(
    id: 'imaging.defaults.gain',
    label: 'Default gain',
    description: 'CMOS sensor gain in camera units. Higher = more sensitivity but more read noise. Range depends on the camera.',
    keywords: ['gain', 'sensitivity', 'iso', 'amplification', 'cmos'],
    path: ['Settings', 'Imaging', 'Defaults'],
    type: SettingType.intRange(min: 0, max: 1000),
    defaultValue: 100,
    profilePath: 'imaging.default_gain',
  ),
  Setting(
    id: 'imaging.defaults.offset',
    label: 'Default offset',
    description: 'Sensor offset (pedestal) — keeps the black level off zero so subtraction operations don\'t clip. Camera-specific.',
    keywords: ['offset', 'pedestal', 'bias', 'black level', 'cmos'],
    path: ['Settings', 'Imaging', 'Defaults'],
    type: SettingType.intRange(min: 0, max: 500),
    defaultValue: 50,
    profilePath: 'imaging.default_offset',
  ),
  Setting(
    id: 'imaging.defaults.bin',
    label: 'Default binning',
    description: 'Pixel binning factor. 1 = native (full resolution); 2 = 2x2 grouped pixels, more sensitivity, less resolution.',
    keywords: ['bin', 'binning', 'resolution', 'pixel', 'downsample'],
    path: ['Settings', 'Imaging', 'Defaults'],
    type: SettingType.intRange(min: 1, max: 4),
    defaultValue: 1,
    profilePath: 'imaging.default_bin',
  ),
  Setting(
    id: 'imaging.defaults.frame_kind',
    label: 'Default frame type',
    description: 'Frame type for the next exposure: light (target), dark (calibration with shutter closed), bias (zero-second), flat (uniform field).',
    keywords: ['frame', 'type', 'light', 'dark', 'bias', 'flat', 'calibration'],
    path: ['Settings', 'Imaging', 'Defaults'],
    type: SettingType.enumValue(['Light', 'Dark', 'Bias', 'Flat']),
    defaultValue: 'Light',
    profilePath: 'imaging.default_frame_kind',
  ),
  Setting(
    id: 'imaging.defaults.cooler_target_c',
    label: 'Cooling target (°C)',
    description: 'Sensor target temperature when the cooler is active. Typical: -10°C to -20°C depending on the sensor and ambient.',
    keywords: ['cooler', 'temperature', 'target', 'celsius', 'cooling', 'tec'],
    path: ['Settings', 'Imaging', 'Defaults'],
    type: SettingType.doubleRange(min: -60, max: 30, step: 1),
    defaultValue: -10.0,
    profilePath: 'imaging.cooler_target_c',
  ),
  Setting(
    id: 'imaging.defaults.cooler_ramp_c_per_min',
    label: 'Cooler ramp rate (°C/min)',
    description: 'How fast to step the cooler toward the target. Slow ramps avoid thermal shock + condensation on the sensor cover.',
    keywords: ['ramp', 'cooler', 'thermal shock', 'condensation', 'cooldown'],
    path: ['Settings', 'Imaging', 'Defaults'],
    type: SettingType.doubleRange(min: 0, max: 10, step: 0.5),
    defaultValue: 1.0,
    profilePath: 'imaging.cooler_ramp_c_per_min',
  ),
  Setting(
    id: 'imaging.defaults.warmup_at_session_end',
    label: 'Warm-up cooler at session end',
    description: 'Ramp the sensor back to ambient at the end of a session instead of disconnecting cold. CCDs need this; CMOS usually don\'t.',
    keywords: ['warmup', 'cooler', 'shutdown', 'thermal shock', 'session end', 'ccd', 'cmos'],
    path: ['Settings', 'Imaging', 'Defaults'],
    type: SettingType.bool(),
    defaultValue: false,
    profilePath: 'imaging.warmup_at_session_end',
  ),

  // §29 Storage — 4 fields. State lives in `storageSettingsProvider`.
  Setting(
    id: 'session.storage.save_directory',
    label: 'Save directory',
    description: 'Base directory where captured frames are written. The §29.1.3 ext4 wizard helps migrate from the SD card to a USB drive.',
    keywords: ['save', 'directory', 'folder', 'path', 'storage', 'disk', 'usb', 'sd card'],
    path: ['Settings', 'Session', 'Storage'],
    type: SettingType.path(),
    defaultValue: '/media/openastroara',
    profilePath: 'storage.save_directory',
  ),
  Setting(
    id: 'session.storage.file_format',
    label: 'File format',
    description: 'On-disk frame format. FITS is the de-facto astronomy standard; XISF is PixInsight\'s native format with richer metadata.',
    keywords: ['format', 'fits', 'xisf', 'rice', 'gzip', 'file type', 'extension'],
    path: ['Settings', 'Session', 'Storage'],
    type: SettingType.enumValue(['FITS', 'XISF', 'FITS + RICE compression', 'FITS + gzip']),
    defaultValue: 'FITS',
    profilePath: 'storage.file_format',
  ),
  Setting(
    id: 'session.storage.compression',
    label: 'Compression',
    description: 'Optional lossless compression of frame files at write time. RICE is fast + astronomy-tuned; gzip is universal but slower.',
    keywords: ['compression', 'rice', 'gzip', 'lossless', 'disk space', 'size'],
    path: ['Settings', 'Session', 'Storage'],
    type: SettingType.enumValue(['Off', 'RICE', 'gzip']),
    defaultValue: 'RICE',
    profilePath: 'storage.compression',
  ),
  Setting(
    id: 'session.storage.filename_template',
    label: 'Filename template',
    description: 'Pattern for output filenames using \$\$TOKEN\$\$ placeholders. Tokens: DATEMINUS12, DATETIME, IMAGETYPE, FILTER, EXPOSURETIME, TARGETNAME, etc.',
    keywords: ['filename', 'template', 'naming', 'tokens', 'pattern', 'date', 'filter'],
    path: ['Settings', 'Session', 'Storage'],
    type: SettingType.string(),
    defaultValue: r'\$\$DATEMINUS12\$\$\\\$\$IMAGETYPE\$\$\\\$\$DATETIME\$\$_\$\$FILTER\$\$_\$\$EXPOSURETIME\$\$s',
    profilePath: 'storage.filename_template',
  ),
];
