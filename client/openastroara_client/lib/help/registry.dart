/// §69 help registry. Single source of truth for all in-app contextual help.
/// Parallel to §61 settings registry.
library;

class Help {
  final String key;
  final String title;
  final String body;
  final String? learnMoreUrl;
  final List<String> relatedHelpKeys;
  final List<String> relatedSettings;

  const Help({
    required this.key,
    required this.title,
    required this.body,
    this.learnMoreUrl,
    this.relatedHelpKeys = const <String>[],
    this.relatedSettings = const <String>[],
  });
}

const Map<String, Help> helpRegistry = {
  'guider.dither_pixels': Help(
    key: 'guider.dither_pixels',
    title: 'Dithering',
    body: 'Dithering is the process of slightly shifting the telescope position '
        'between exposures. This causes fixed-pattern noise (like hot pixels) '
        'to fall on different physical pixels in each frame, allowing them to '
        'be removed during stacking.',
    learnMoreUrl: 'https://openastro.net/wiki/guiding/dithering',
    relatedSettings: ['guider.dither_pixels'],
  ),
  'safety.policies.on_unsafe': Help(
    key: 'safety.policies.on_unsafe',
    title: 'Unsafe Weather Actions',
    body: 'Determines what the system does when the safety monitor reports '
        'unsafe conditions (rain, high wind, clouds). "Pause + Park" is the '
        'safest default for unattended imaging.',
    relatedSettings: ['safety.policies.on_unsafe'],
  ),
  'safety.policies.auto_resume': Help(
    key: 'safety.policies.auto_resume',
    title: 'Auto-resume',
    body: 'If enabled, the sequence will automatically resume as soon as the '
        'safety monitor reports "Safe" again. If disabled, the sequence '
        'stays paused until you manually resume it.',
    relatedSettings: ['safety.policies.auto_resume'],
  ),
  'safety.policies.resume_delay': Help(
    key: 'safety.policies.resume_delay',
    title: 'Resume Delay',
    body: 'The number of minutes to wait after a "Safe" signal before '
        'actually resuming. Useful to ensure that a passing cloud bank '
        'has fully cleared before starting the next exposure.',
    relatedSettings: ['safety.policies.resume_delay'],
  ),
  'diagnostics.mode': Help(
    key: 'diagnostics.mode',
    title: 'Diagnostics Mode',
    body: 'Controls how Ara responds to acquisition issues like star loss or focus drift.\n\n'
        '* **Notify only**: Logs the issue and notifies you, but takes no action.\n'
        '* **Balanced**: Performs low-risk recoveries (like auto-refocus on drift).\n'
        '* **Aggressive**: Takes corrective action for almost all issues (e.g. abort/restart on star loss).',
    relatedSettings: ['diagnostics.mode'],
  ),
  // §37.9 Imaging Defaults — help only on the non-obvious controls (per
  // §69.1 default-is-no-tooltip). Exposure / target temp / frame type are
  // self-explanatory by their labels.
  'imaging.defaults.gain': Help(
    key: 'imaging.defaults.gain',
    title: 'Default gain',
    body: 'CMOS sensor gain (amplification before the ADC). Higher gain = more sensitivity per photon, but also more read noise floor. '
        'For deep-sky targets most CMOS cameras have a "unity gain" sweet spot listed in their datasheet — start there.',
    relatedSettings: ['imaging.defaults.gain'],
  ),
  'imaging.defaults.offset': Help(
    key: 'imaging.defaults.offset',
    title: 'Default offset',
    body: 'A small DC pedestal added to every pixel before readout. Prevents the black level from clipping at zero, which would '
        'break dark-frame and bias subtraction. Camera-specific — your camera\'s manual usually recommends a value.',
    relatedSettings: ['imaging.defaults.offset'],
  ),
  'imaging.defaults.bin': Help(
    key: 'imaging.defaults.bin',
    title: 'Default binning',
    body: 'Pixel binning combines a NxN grid of pixels into one larger virtual pixel. 2x2 quadruples sensitivity per pixel but halves resolution. '
        'On CMOS cameras binning is typically done in software (post-readout) and equivalent to downsampling — the gain in SNR is smaller than on CCD.',
    relatedSettings: ['imaging.defaults.bin'],
  ),
  'imaging.defaults.cooler_ramp_c_per_min': Help(
    key: 'imaging.defaults.cooler_ramp_c_per_min',
    title: 'Cooler ramp rate',
    body: 'How fast the sensor cools toward the target temperature. Faster ramps stress the TEC and risk condensation on the sensor cover '
        'as the temperature crosses the dew point. 1°C/min is a safe default; some sensors handle 2-3°C/min fine.',
    relatedSettings: ['imaging.defaults.cooler_ramp_c_per_min'],
  ),
  'imaging.defaults.warmup_at_session_end': Help(
    key: 'imaging.defaults.warmup_at_session_end',
    title: 'Warm up at session end',
    body: 'CCD sensors can crack under repeated thermal shock if disconnected cold. CMOS sensors are tolerant — most users leave this off. '
        'When enabled, the cooler ramps the sensor back to within ~5°C of ambient before disconnecting at session end.',
    relatedSettings: ['imaging.defaults.warmup_at_session_end'],
  ),

  // §29 Storage — help on the non-obvious controls (format, compression,
  // filename template, plus a brief save-directory note because the default
  // `/media/openastroara` mount point isn't obvious to novices).
  'session.storage.save_directory': Help(
    key: 'session.storage.save_directory',
    title: 'Save directory',
    body: 'Base path where captured frames are written. Must be a mounted writable directory. '
        'Default `/media/openastroara` assumes the §29.1.3 ext4 wizard set up a USB drive there. '
        'Capturing to the SD card is fine for testing but will wear the card out over a single all-night session — use external storage for real sessions.',
    relatedSettings: ['session.storage.save_directory'],
  ),
  'session.storage.file_format': Help(
    key: 'session.storage.file_format',
    title: 'File format',
    body: 'FITS is the historical standard for astronomy and the safest choice for downstream tools (DSS, Siril, PixInsight, AstroPixelProcessor — all read FITS). '
        'XISF is PixInsight\'s native format with richer metadata (per-frame statistics, processing history) but smaller tool support. '
        'RICE-compressed FITS halves file size on light frames with minimal CPU cost and is widely supported. '
        'Gzipped FITS is universal but slower to write and read.',
    relatedSettings: ['session.storage.file_format'],
  ),
  'session.storage.compression': Help(
    key: 'session.storage.compression',
    title: 'Compression',
    body: 'Optional lossless compression applied as each frame is written to disk.\n\n'
        '* **Off**: No compression — fastest, biggest files.\n'
        '* **RICE**: Astronomy-tuned algorithm. ~2x compression on lights, ~10x on darks/bias. Fast both ways. Recommended.\n'
        '* **gzip**: General-purpose. Smaller files than RICE but ~5x slower to write. Use only if a downstream tool requires it.',
    relatedSettings: ['session.storage.compression'],
  ),
  'session.storage.filename_template': Help(
    key: 'session.storage.filename_template',
    title: 'Filename template',
    body: 'Output paths are built by substituting \$\$TOKEN\$\$ placeholders in this template. Tokens are uppercased and surrounded by double-dollars.\n\n'
        '**Common tokens:**\n'
        '* `\$\$DATEMINUS12\$\$` — session date (rolls over at noon UTC-12, so all-night exposures share one date)\n'
        '* `\$\$DATETIME\$\$` — exposure-start timestamp\n'
        '* `\$\$IMAGETYPE\$\$` — Light / Dark / Bias / Flat\n'
        '* `\$\$FILTER\$\$` — current filter slot label\n'
        '* `\$\$EXPOSURETIME\$\$` — exposure seconds\n'
        '* `\$\$TARGETNAME\$\$` — target from the active sequence\n\n'
        'Use `\\` (or `/`) as the path separator. Subdirectories are created automatically.',
    relatedSettings: ['session.storage.filename_template'],
  ),

  // §54 Notifications — help on the genuinely non-obvious controls (token
  // setup + the events with hidden semantics like retry budgets / thresholds).
  // The plain "trigger on X" channel + event toggles are self-explanatory.
  'session.notifications.pushover_token': Help(
    key: 'session.notifications.pushover_token',
    title: 'Pushover token',
    body: 'Pushover is a paid (one-time \$5) push-notification service that delivers messages to your phone or desktop. '
        'To use: sign up at pushover.net, then copy the User Key from your dashboard into this field.\n\n'
        'Leave empty to disable Pushover delivery entirely. Other channels (in-app banner, OS notification, sound) work independently.',
    learnMoreUrl: 'https://pushover.net/',
    relatedSettings: ['session.notifications.telegram_bot_token'],
  ),
  'session.notifications.telegram_bot_token': Help(
    key: 'session.notifications.telegram_bot_token',
    title: 'Telegram bot token',
    body: 'Telegram bots are free and deliver messages to a Telegram chat you control. '
        'To use: message @BotFather in Telegram, send `/newbot`, follow the prompts, then paste the bot token here.\n\n'
        'You\'ll also need to send `/start` to your new bot once so it can DM you. Leave empty to disable Telegram delivery.',
    learnMoreUrl: 'https://core.telegram.org/bots#how-do-i-create-a-bot',
    relatedSettings: ['session.notifications.pushover_token'],
  ),
  'session.notifications.on_critical_diagnostic': Help(
    key: 'session.notifications.on_critical_diagnostic',
    title: 'Critical diagnostic events',
    body: '"Critical" is §51\'s top severity level — events that indicate something is actively wrong inside Ara and may require intervention. '
        'Examples: sensor cooler runaway, mount tracking deviation > 30″, guider RMS suddenly tripled, autofocus position drifted past the backlash budget.\n\n'
        'Distinct from "Safety event" — safety events are §35 environmental conditions (weather, altitude limits, guider loss) that already trigger automated park/pause actions. '
        'Critical diagnostics surface in-app problems that don\'t themselves trigger safety actions.',
    relatedSettings: ['session.notifications.on_safety_event', 'diagnostics.mode'],
  ),
  'session.notifications.on_safety_event': Help(
    key: 'session.notifications.on_safety_event',
    title: 'Safety event',
    body: 'Fires when the §35 safety monitor reports a condition that triggers a safety action. Three classes of events qualify:\n\n'
        '* **Unsafe weather** — rain, clouds, high wind, humidity past dew point\n'
        '* **Altitude limit** — target is below the minimum-altitude policy\n'
        '* **Guider lost** — guider stops reporting valid frames\n\n'
        'These events trigger pause/park/abort actions configured per the §35 safety policies. This toggle controls only whether you also get a notification when one fires; the underlying action runs regardless.',
    relatedSettings: ['session.notifications.on_critical_diagnostic', 'safety.policies.on_unsafe'],
  ),
  'session.notifications.on_plate_solve_failed': Help(
    key: 'session.notifications.on_plate_solve_failed',
    title: 'Plate solve failed (×N)',
    body: 'Fires after N consecutive plate-solve failures — single-try failures are common (clouds, blooming, framing issue) and not worth alerting on. '
        'The retry count N is set by the guider-retry-timeout in §35 Safety Policies; the default is 3 tries before giving up.',
    relatedSettings: ['session.notifications.on_plate_solve_failed', 'safety.policies.guider_retry_timeout'],
  ),
  'session.notifications.on_disk_space_low': Help(
    key: 'session.notifications.on_disk_space_low',
    title: 'Disk space low',
    body: 'Fires when free space on the §29 save directory drops below ~10 GB — about one hour of LRGB capture at 4096x4096 16-bit FITS. '
        'Threshold is fixed in v0.0.1; making it configurable is a v0.1.0 enhancement.',
    relatedSettings: ['session.storage.save_directory'],
  ),

  // §37.12 Site — help on the genuinely non-obvious controls. Site name,
  // lat/lon, elevation, time zone are self-explanatory by label.
  'safety.site.use_custom_horizon': Help(
    key: 'safety.site.use_custom_horizon',
    title: 'Custom horizon polygon',
    body: 'A measured azimuth/altitude polygon describing actual obstructions at your site (trees, roof line, neighbor\'s house). '
        'When on, target visibility checks use this polygon instead of the flat default-altitude floor — much more accurate for low-altitude targets.\n\n'
        'The polygon import + measurement workflow lives in §36.8 Sky Atlas → "Capture horizon mask". '
        'Until you\'ve imported one, leave this off and the flat horizon will be used.',
    relatedSettings: ['safety.site.use_custom_horizon', 'safety.site.default_horizon_altitude_deg'],
  ),
  'safety.site.default_horizon_altitude_deg': Help(
    key: 'safety.site.default_horizon_altitude_deg',
    title: 'Default horizon altitude',
    body: 'Flat altitude floor used for visibility checks when no custom horizon polygon is loaded. '
        'Targets transiting below this altitude are flagged as below-horizon by the §38 framing assistant and skipped by the §35 altitude-limit safety policy.\n\n'
        '20° is a sensible default for backyard sites (covers most trees + suburban roof lines); 0° turns the floor off; 30° is conservative for low-precision tracking.',
    relatedSettings: ['safety.site.use_custom_horizon'],
  ),
  'safety.site.bortle_class': Help(
    key: 'safety.site.bortle_class',
    title: 'Bortle dark-sky class',
    body: 'A 1-9 scale rating your site\'s sky darkness, where 1 is an excellent dark site (SQM ≥21.99 mag/arcsec²) and 9 is inner-city light pollution (Milky Way invisible).\n\n'
        '* **1-2**: Excellent / true dark site\n'
        '* **3-4**: Rural / rural-suburban transition\n'
        '* **5-6**: Suburban / bright suburban (most backyard astrophotographers)\n'
        '* **7-8**: Suburban-urban transition / urban\n'
        '* **9**: Inner city — narrowband filters required\n\n'
        'Used by §50 quality-score estimation + suggested exposure ranges in §38. Don\'t know your class? lightpollutionmap.info or darkskies.org.',
    learnMoreUrl: 'https://en.wikipedia.org/wiki/Bortle_scale',
    relatedSettings: ['safety.site.typical_seeing_arcsec'],
  ),
  'safety.site.typical_seeing_arcsec': Help(
    key: 'safety.site.typical_seeing_arcsec',
    title: 'Typical seeing',
    body: 'The median FWHM of star images at your site, in arcseconds — a measure of atmospheric turbulence.\n\n'
        '* **<1.0″**: Excellent (high-altitude observatory class)\n'
        '* **1.0-2.0″**: Very good\n'
        '* **2.0-3.0″**: Typical backyard\n'
        '* **3.0-4.0″**: Poor / windy / heat-cell turbulence\n'
        '* **>4.0″**: Severe — usually rules out planetary or short-FL imaging\n\n'
        'Used as the baseline for §50 quality scoring (frames worse than 2x typical seeing get auto-rated down) and for autofocus convergence thresholds.',
    relatedSettings: ['safety.site.bortle_class'],
  ),
  'safety.site.twilight_definition': Help(
    key: 'safety.site.twilight_definition',
    title: 'Twilight definition',
    body: 'Determines when "night" begins/ends for sequence scheduling.\n\n'
        '* **Civil (−6°)**: Sun 6° below horizon — sky is still bright; brightest planets visible. Used by sequence-start with skip-twilight off.\n'
        '* **Nautical (−12°)**: Sun 12° below — horizon visible to the eye, bright stars + globulars OK for testing or wide-field.\n'
        '* **Astronomical (−18°)**: Sun 18° below — sky is fully dark, deep-sky imaging window. Recommended default.',
  ),
};
