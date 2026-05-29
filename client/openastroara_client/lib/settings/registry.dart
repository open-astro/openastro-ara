/// §61 settings registry. Single source of truth for every settable field
/// across all panels. Used for:
/// 1. Search index (Command Palette)
/// 2. Inline edit rendering
/// 3. Cross-linking hints
/// 4. Mechanical enforcement gate (COMMIT-PR-RULES.md)

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
  const SettingType.intRange({required int min, required int max}) : kind = SettingTypeKind.intRange, min = min, max = max, step = 1, enumValues = null;
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
    this.relatedSettings = const [],
  });
}

/// The canonical settings registry.
const List<Setting> settingsRegistry = [
  // §35 Safety Policies
  Setting(
    id: 'safety.policies.on_unsafe',
    label: 'On unsafe weather',
    description: 'What action to take when the safety monitor reports unsafe weather.',
    keywords: const ['unsafe', 'weather', 'pause', 'park', 'abort', 'dome'],
    path: const ['Settings', 'Safety', 'Policies'],
    type: SettingType.enumValue(const ['Pause + park', 'Park only', 'Abort + park', 'Ignore']),
    defaultValue: 'Pause + park',
    profilePath: 'safety.on_unsafe',
  ),
  Setting(
    id: 'safety.policies.auto_resume',
    label: 'Auto-resume when safe',
    description: 'Whether to automatically resume the sequence when weather becomes safe again.',
    keywords: const ['resume', 'safe', 'automatic', 'weather'],
    path: const ['Settings', 'Safety', 'Policies'],
    type: SettingType.bool(),
    defaultValue: true,
    profilePath: 'safety.auto_resume',
  ),
  Setting(
    id: 'safety.policies.resume_delay',
    label: 'Resume delay (min)',
    description: 'How long to wait after weather becomes safe before resuming.',
    keywords: const ['delay', 'wait', 'resume', 'safe'],
    path: const ['Settings', 'Safety', 'Policies'],
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
    keywords: const ['dither', 'guide', 'guider', 'phd2', 'randomize', 'hot pixel'],
    path: const ['Settings', 'Guider', 'PHD2'],
    type: SettingType.intRange(min: 0, max: 50),
    defaultValue: 5,
    profilePath: 'guider.dither_pixels',
    relatedSettings: const [
      'guider.dither_settle_threshold',
      'guider.dither_timeout_action',
    ],
  ),

  // §51 Diagnostics
  Setting(
    id: 'diagnostics.mode',
    label: 'Diagnostics mode',
    description: 'Smart correction policy: notify only, balanced, or aggressive auto-recovery.',
    keywords: const ['diagnostics', 'smart', 'correction', 'auto-recovery', 'notify'],
    path: const ['Settings', 'Safety', 'Diagnostics'],
    type: SettingType.enumValue(const ['Notify only', 'Balanced', 'Aggressive']),
    defaultValue: 'Notify only',
    profilePath: 'diagnostics.mode',
  ),

  // §37.4 Imaging Defaults
  Setting(
    id: 'imaging.defaults.exposure',
    label: 'Default exposure (s)',
    description: 'Standard exposure time used when no target-specific override exists.',
    keywords: const ['exposure', 'time', 'seconds', 'default'],
    path: const ['Settings', 'Imaging', 'Defaults'],
    type: SettingType.intRange(min: 1, max: 3600),
    defaultValue: 300,
    profilePath: 'imaging.default_exposure_s',
  ),

  // §37.3 Camera
  Setting(
    id: 'camera.cooler_warmup_on_session_end',
    label: 'Cooler warmup on end',
    description: 'How to handle the sensor cooler when the imaging session finishes.',
    keywords: const ['warmup', 'cooler', 'shutdown', 'thermal shock', 'cooldown'],
    path: const ['Settings', 'Equipment', 'Camera'],
    type: SettingType.enumValue(const ['Off', 'Ramp', 'Immediate']),
    defaultValue: 'Off',
    profilePath: 'camera.cooler_warmup_mode',
  ),
];
