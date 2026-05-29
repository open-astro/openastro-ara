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
    // Raw string matches `StorageSettings.filenameTemplate` default verbatim.
    // The doubled-backslash + doubled-dollar syntax is the template engine's
    // own convention (`\\` as path separator, `$$TOKEN$$` for substitution);
    // the `r''` prefix means Dart doesn't process either as escapes.
    defaultValue: r'$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s',
    profilePath: 'storage.filename_template',
  ),

  // §54 Notifications — 12 fields (5 channels + 7 triggers). State lives in
  // `notificationsSettingsProvider`.
  // Channels.
  Setting(
    id: 'session.notifications.in_app_banner',
    label: 'In-app banner',
    description: 'Show a transient banner inside Ara when an event fires.',
    keywords: ['banner', 'in-app', 'notification', 'toast', 'popup'],
    path: ['Settings', 'Session', 'Notifications'],
    type: SettingType.bool(),
    defaultValue: true,
    profilePath: 'notifications.in_app_banner',
  ),
  Setting(
    id: 'session.notifications.os_desktop',
    label: 'OS desktop notification',
    description: 'Send a native OS notification (macOS Notification Center / Linux libnotify / Windows toast) when an event fires.',
    keywords: ['desktop', 'os', 'notification', 'native', 'system', 'tray', 'toast'],
    path: ['Settings', 'Session', 'Notifications'],
    type: SettingType.bool(),
    defaultValue: true,
    profilePath: 'notifications.os_desktop',
  ),
  Setting(
    id: 'session.notifications.sound_alert',
    label: 'Sound alert',
    description: 'Play the §35 alarm sound on safety/critical events. Independent of OS notification sound.',
    keywords: ['sound', 'alarm', 'audio', 'alert', 'beep'],
    path: ['Settings', 'Session', 'Notifications'],
    type: SettingType.bool(),
    defaultValue: true,
    profilePath: 'notifications.sound_alert',
  ),
  Setting(
    id: 'session.notifications.pushover_token',
    label: 'Pushover token',
    description: 'Pushover user key for push notifications to phone. Leave empty to disable.',
    keywords: ['pushover', 'token', 'phone', 'mobile', 'push', 'remote'],
    path: ['Settings', 'Session', 'Notifications'],
    type: SettingType.string(),
    defaultValue: '',
    profilePath: 'notifications.pushover_token',
  ),
  Setting(
    id: 'session.notifications.telegram_bot_token',
    label: 'Telegram bot token',
    description: 'Telegram bot token for sending messages to your chat. Leave empty to disable.',
    keywords: ['telegram', 'bot', 'token', 'chat', 'remote', 'mobile'],
    path: ['Settings', 'Session', 'Notifications'],
    type: SettingType.string(),
    defaultValue: '',
    profilePath: 'notifications.telegram_bot_token',
  ),
  // Triggers.
  Setting(
    id: 'session.notifications.on_sequence_complete',
    label: 'Trigger on sequence complete',
    description: 'Notify when a sequence finishes its last instruction.',
    keywords: ['sequence', 'complete', 'finish', 'done', 'trigger'],
    path: ['Settings', 'Session', 'Notifications'],
    type: SettingType.bool(),
    defaultValue: true,
    profilePath: 'notifications.on_sequence_complete',
  ),
  Setting(
    id: 'session.notifications.on_sequence_paused',
    label: 'Trigger on sequence paused',
    description: 'Notify when a sequence pauses (user request, safety policy, or meridian flip wait).',
    keywords: ['sequence', 'paused', 'pause', 'wait', 'trigger'],
    path: ['Settings', 'Session', 'Notifications'],
    type: SettingType.bool(),
    defaultValue: true,
    profilePath: 'notifications.on_sequence_paused',
  ),
  Setting(
    id: 'session.notifications.on_critical_diagnostic',
    label: 'Trigger on critical diagnostic',
    description: 'Notify on §51 critical-severity diagnostic events (sensor temp out of range, mount drift, etc).',
    keywords: ['diagnostic', 'critical', 'severity', 'fault', 'health', 'trigger'],
    path: ['Settings', 'Session', 'Notifications'],
    type: SettingType.bool(),
    defaultValue: true,
    profilePath: 'notifications.on_critical_diagnostic',
  ),
  Setting(
    id: 'session.notifications.on_safety_event',
    label: 'Trigger on safety event',
    description: 'Notify when the §35 safety monitor reports unsafe weather, an altitude limit, or guider loss.',
    keywords: ['safety', 'weather', 'unsafe', 'park', 'alarm', 'trigger'],
    path: ['Settings', 'Session', 'Notifications'],
    type: SettingType.bool(),
    defaultValue: true,
    profilePath: 'notifications.on_safety_event',
  ),
  Setting(
    id: 'session.notifications.on_autofocus_failed',
    label: 'Trigger on autofocus failed',
    description: 'Notify when an autofocus run gives up without converging.',
    keywords: ['autofocus', 'failed', 'focus', 'hfr', 'trigger'],
    path: ['Settings', 'Session', 'Notifications'],
    type: SettingType.bool(),
    defaultValue: true,
    profilePath: 'notifications.on_autofocus_failed',
  ),
  Setting(
    id: 'session.notifications.on_plate_solve_failed',
    label: 'Trigger on plate solve failed',
    description: 'Notify when the plate solver fails N times in a row (per §35 guider-lost retry budget).',
    keywords: ['plate solve', 'failed', 'astrometry', 'astap', 'retry', 'trigger'],
    path: ['Settings', 'Session', 'Notifications'],
    type: SettingType.bool(),
    defaultValue: true,
    profilePath: 'notifications.on_plate_solve_failed',
  ),
  Setting(
    id: 'session.notifications.on_disk_space_low',
    label: 'Trigger on disk space low',
    description: 'Notify when free space on the save directory drops below ~10 GB.',
    keywords: ['disk', 'space', 'low', 'storage', 'full', 'free', 'trigger'],
    path: ['Settings', 'Session', 'Notifications'],
    type: SettingType.bool(),
    defaultValue: true,
    profilePath: 'notifications.on_disk_space_low',
  ),

  // §37.12 Site preferences — 10 fields. State lives in `siteSettingsProvider`.
  Setting(
    id: 'safety.site.site_name',
    label: 'Site name',
    description: 'Friendly label for the observing location. Used in session metadata + FITS header SITE keyword.',
    keywords: ['site', 'name', 'location', 'place', 'observatory'],
    path: ['Settings', 'Safety', 'Site'],
    type: SettingType.string(),
    defaultValue: 'Backyard',
    profilePath: 'site.name',
  ),
  Setting(
    id: 'safety.site.latitude_deg',
    label: 'Latitude (°)',
    description: 'Site latitude in decimal degrees. North positive (+), south negative (−). Used for hour-angle, twilight, and altitude calculations.',
    keywords: ['latitude', 'lat', 'location', 'gps', 'decimal degrees', 'coordinates'],
    path: ['Settings', 'Safety', 'Site'],
    type: SettingType.doubleRange(min: -90, max: 90, step: 0.000001),
    defaultValue: 0.0,
    profilePath: 'site.latitude_deg',
  ),
  Setting(
    id: 'safety.site.longitude_deg',
    label: 'Longitude (°)',
    description: 'Site longitude in decimal degrees. East positive (+), west negative (−). Used for sidereal time + meridian flip calculations.',
    keywords: ['longitude', 'lon', 'lng', 'location', 'gps', 'decimal degrees', 'coordinates'],
    path: ['Settings', 'Safety', 'Site'],
    type: SettingType.doubleRange(min: -180, max: 180, step: 0.000001),
    defaultValue: 0.0,
    profilePath: 'site.longitude_deg',
  ),
  Setting(
    id: 'safety.site.elevation_m',
    label: 'Elevation (m)',
    description: 'Height above sea level. Used for atmospheric refraction corrections in §38 framing + plate-solve fitting.',
    keywords: ['elevation', 'altitude', 'height', 'meters', 'sea level', 'asl'],
    path: ['Settings', 'Safety', 'Site'],
    type: SettingType.doubleRange(min: -500, max: 9000, step: 1),
    defaultValue: 0.0,
    profilePath: 'site.elevation_m',
  ),
  Setting(
    id: 'safety.site.time_zone',
    label: 'Time zone',
    description: 'IANA time zone identifier (e.g. America/Los_Angeles). Used for session-local timestamps + scheduling.',
    keywords: ['time zone', 'timezone', 'iana', 'tz', 'utc', 'dst'],
    path: ['Settings', 'Safety', 'Site'],
    type: SettingType.string(),
    defaultValue: 'UTC',
    profilePath: 'site.time_zone',
  ),
  Setting(
    id: 'safety.site.use_custom_horizon',
    label: 'Use custom horizon polygon',
    description: 'When on, target visibility uses the imported azimuth/altitude polygon (§36.8) instead of the flat default-altitude floor.',
    keywords: ['horizon', 'custom', 'polygon', 'mask', 'obstruction', 'trees', 'building'],
    path: ['Settings', 'Safety', 'Site'],
    type: SettingType.bool(),
    defaultValue: false,
    profilePath: 'site.use_custom_horizon',
  ),
  Setting(
    id: 'safety.site.default_horizon_altitude_deg',
    label: 'Default horizon altitude (°)',
    description: 'Flat horizon floor used when no custom polygon is loaded. Targets below this altitude are flagged as below-horizon.',
    keywords: ['horizon', 'altitude', 'floor', 'minimum', 'limit', 'degrees'],
    path: ['Settings', 'Safety', 'Site'],
    type: SettingType.doubleRange(min: 0, max: 90, step: 1),
    defaultValue: 20.0,
    profilePath: 'site.default_horizon_altitude_deg',
  ),
  Setting(
    id: 'safety.site.bortle_class',
    label: 'Bortle class',
    description: 'Bortle dark-sky classification, 1 (excellent dark site) through 9 (inner-city). Used for SNR estimation + suggested exposure ranges.',
    keywords: ['bortle', 'dark sky', 'light pollution', 'sqm', 'class', 'sky quality'],
    path: ['Settings', 'Safety', 'Site'],
    type: SettingType.intRange(min: 1, max: 9),
    defaultValue: 6,
    profilePath: 'site.bortle_class',
  ),
  Setting(
    id: 'safety.site.typical_seeing_arcsec',
    label: 'Typical seeing (″)',
    description: 'Median atmospheric seeing FWHM in arcseconds at this site. Used for §50 quality-score baselining + autofocus convergence.',
    keywords: ['seeing', 'fwhm', 'atmosphere', 'turbulence', 'arcsec', 'quality'],
    path: ['Settings', 'Safety', 'Site'],
    type: SettingType.doubleRange(min: 0, max: 20, step: 0.1),
    defaultValue: 2.5,
    profilePath: 'site.typical_seeing_arcsec',
  ),
  Setting(
    id: 'safety.site.twilight_definition',
    label: 'Twilight definition',
    description: 'Which twilight threshold counts as "night" for sequence start/end and observation-window calculations.',
    keywords: ['twilight', 'civil', 'nautical', 'astronomical', 'dusk', 'dawn', 'sun', 'night'],
    path: ['Settings', 'Safety', 'Site'],
    type: SettingType.enumValue(['Civil (−6°)', 'Nautical (−12°)', 'Astronomical (−18°)']),
    defaultValue: 'Astronomical (−18°)',
    profilePath: 'site.twilight_definition',
  ),
];
