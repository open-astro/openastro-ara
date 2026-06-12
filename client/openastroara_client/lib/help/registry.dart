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
  // (Old `guider.dither_pixels` starter entry retired in 12h.4 — superseded
  // by the proper `eq.guider.*` namespace below.)
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
    relatedSettings: ['safety.policies.auto_resume'],
  ),
  'safety.policies.meridian_flip_auto': Help(
    key: 'safety.policies.meridian_flip_auto',
    title: 'Auto meridian flip',
    body: 'A meridian flip is when a German Equatorial Mount (GEM) swaps sides of the pier to keep tracking a target that crossed the meridian (south line at culmination).\n\n'
        '* **On** (recommended): the mount flips automatically when the target reaches the configured meridian-limit (set per-mount by the §57 mount-safety policy). Exposure pauses, mount flips, plate-solve re-centers, guider re-calibrates, exposure resumes.\n'
        '* **Off**: the sequence pauses at the meridian-limit and waits for you to manually flip + resume.\n\n'
        'Fork-mounted scopes (CGEM-DX, alt-az without wedge) don\'t need a meridian flip — turn this off and the meridian-limit policy is ignored.',
    relatedSettings: ['safety.policies.meridian_pause_min', 'safety.policies.meridian_recenter', 'safety.policies.meridian_recal_guider'],
  ),
  'safety.policies.meridian_pause_min': Help(
    key: 'safety.policies.meridian_pause_min',
    title: 'Pause after meridian flip',
    body: 'Time the mount needs to settle mechanically after the pier-side swap before exposures resume. Faster mounts (Paramount, 10Micron) settle in <1 min; slower or heavy-payload setups need 3-5 min. '
        'Set this conservatively — a too-short pause produces motion-blurred first frames after the flip.',
    relatedSettings: ['safety.policies.meridian_flip_auto'],
  ),
  'safety.policies.on_altitude_limit': Help(
    key: 'safety.policies.on_altitude_limit',
    title: 'On altitude limit',
    body: 'What happens when a target drops below the minimum-altitude floor (set in §37.12 Site Preferences, default 20°).\n\n'
        '* **Skip target**: move to the next target in the sequence and continue. Recommended for multi-target sessions.\n'
        '* **Pause sequence**: pause and wait for the target to rise again (only useful for circumpolar targets).\n'
        '* **Abort sequence**: stop the whole session. Strict but predictable.',
    relatedSettings: ['safety.site.default_horizon_altitude_deg', 'safety.policies.park_if_no_more_targets'],
  ),
  'safety.policies.on_guider_lost': Help(
    key: 'safety.policies.on_guider_lost',
    title: 'On guider lost',
    body: 'Action when PHD2 reports lost lock — typically caused by clouds rolling in, a star drifting off the guide chip, or a calibration glitch.\n\n'
        '* **Pause + retry**: pause exposure, restart guider, retry until `Guider retry timeout` expires. Recommended for clear-but-occasional-cloud nights.\n'
        '* **Skip target**: skip this target immediately and move on.\n'
        '* **Abort sequence**: stop the whole session.',
    relatedSettings: ['safety.policies.guider_retry_timeout', 'safety.policies.skip_target_if_recovery_fails'],
  ),
  'safety.policies.on_disk_space_critical': Help(
    key: 'safety.policies.on_disk_space_critical',
    title: 'On critically-low disk',
    body: 'What the §29 disk-space monitor does when free space on your image save volume drops below the **critical** threshold (set under Settings → Session → Storage).\n\n'
        '* **Warn only** (default): raise a red diagnostic and, if enabled, a *Low disk space* notification — but keep capturing. You decide what to do.\n'
        '* **Abort the running sequence**: also halt any in-progress sequence, so you don\'t keep filling the disk with frames that may not even save. A critical notification records that it stopped the run.\n\n'
        'Either way the monitor never deletes anything. The warning thresholds and this action are independent — tune the levels in Storage, choose the consequence here.',
    relatedSettings: [
      'session.storage.min_free_disk_critical_gb',
      'session.notifications.on_disk_space_low',
    ],
  ),
  'safety.policies.guider_retry_timeout': Help(
    key: 'safety.policies.guider_retry_timeout',
    title: 'Guider retry timeout',
    body: 'How long to keep retrying guider re-acquisition before giving up. When the timeout expires, the §54 plate-solve-failed notification fires and the §35 `Skip target if recovery fails` policy decides next.\n\n'
        '60s is a good default — long enough to ride out a passing cloud but short enough to skip a target if guiding is genuinely broken.',
    relatedSettings: ['safety.policies.on_guider_lost', 'session.notifications.on_plate_solve_failed'],
  ),
  'diagnostics.mode': Help(
    key: 'diagnostics.mode',
    title: 'Diagnostics mode',
    body: 'Controls how Ara responds to §51 critical-severity diagnostic events (sensor temp out of range, mount drift > 30″, guider RMS triple, autofocus position lost, etc).\n\n'
        '* **Notify only** (default): events surface in the Diagnostic Panel + as §54 WS notifications, but sequence execution is never auto-paused by diagnostics alone.\n'
        '* **Pause on critical**: critical-severity events auto-pause the running sequence and ring the §35 alarm. You decide whether to resume.\n'
        '* **Abort on critical**: critical-severity events trigger §35 Abort + Park instead of pause. Use only for unattended observatory automation where you trust the safety policies to recover safely.\n\n'
        'Lower-severity diagnostic events (warnings, infos) never trigger automated action regardless of this setting.',
    relatedSettings: ['session.notifications.on_critical_diagnostic'],
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
  'session.storage.min_free_disk_warn_gb': Help(
    key: 'session.storage.min_free_disk_warn_gb',
    title: 'Low-disk warning threshold',
    body: 'When free space on your image save volume drops below this many GB, the daemon raises a **warning** (a yellow diagnostic and, if enabled, a *Low disk space* notification) so you can free space before a session stalls.\n\n'
        'It only warns — captures are never blocked and nothing is deleted — and the warning clears itself once space frees up. '
        'Set this comfortably above one night of frames (large OSC/mono subs add up fast). Must be above the critical threshold.',
    relatedSettings: [
      'session.storage.min_free_disk_warn_gb',
      'session.storage.min_free_disk_critical_gb',
    ],
  ),
  'session.storage.min_free_disk_critical_gb': Help(
    key: 'session.storage.min_free_disk_critical_gb',
    title: 'Critical-disk threshold',
    body: 'When free space drops below this many GB, the daemon escalates to a **critical** alert (a red diagnostic and, if enabled, a critical notification) — the disk is nearly full and the next frames may not fit.\n\n'
        'Like the warning, this is advisory: ARA never blocks a capture or deletes data. Must be below the warning threshold. '
        'If the warn/critical pair is left non-positive or inverted, the daemon falls back to its built-in 10 GB / 2 GB defaults.',
    relatedSettings: [
      'session.storage.min_free_disk_critical_gb',
      'session.storage.min_free_disk_warn_gb',
    ],
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

  // §29.2 Filenames — both fields are non-obvious so both get help.
  'session.filenames.date_separator': Help(
    key: 'session.filenames.date_separator',
    title: 'Date separator',
    body: 'Determines how `\$\$DATETIME\$\$` and `\$\$DATEMINUS12\$\$` tokens render in output paths.\n\n'
        '* **`/` forward slash**: dates like `2026-05-29` become actual subdirectories. Cleanest organization, plays well with file managers.\n'
        '* **`_` underscore**: dates stay inline (`2026-05-29_M31_L_60s.fits`). Flat output; good if you sort + organize externally later.\n'
        '* **`-` dash**: same as underscore, but uses `-` between date components. Maximally Windows-safe (no characters reserved by NTFS).',
    relatedSettings: ['session.storage.filename_template'],
  ),
  'session.filenames.compress_darks_and_bias': Help(
    key: 'session.filenames.compress_darks_and_bias',
    title: 'Compress bias + dark frames',
    body: 'Bias and dark frames are dominated by sensor noise (mostly zero in bias, slowly-varying in darks) and compress losslessly very well — typically 8-15x with RICE. '
        'When on, calibration frames get RICE compression regardless of the global compression setting in §29 Storage. '
        'When off, calibration frames respect the global compression setting.\n\n'
        'Recommended on — calibration frames are bulky (one library can take 5+ GB) and benefit far more from compression than light frames.',
    relatedSettings: ['session.storage.compression'],
  ),

  // §52.1 connection lifecycle — one shared help entry covers all 10 device-
  // type auto-connect toggles. Per-device side effects are listed in the body.
  'eq.auto_connect_on_boot': Help(
    key: 'eq.auto_connect_on_boot',
    title: 'Auto-connect on boot',
    body: 'Whether to automatically open the Alpaca connection to this device when the daemon starts.\n\n'
        '**Defaults split by side-effect risk:**\n\n'
        '*Connect-by-default* (minor or no actuation):\n'
        '* Camera — USB link power-up only\n'
        '* Mount — sidereal tracking comes on per §57\n'
        '* Focuser, rotator — position read on connect, no movement\n'
        '* Filter wheel — most drivers reposition to last-known slot on connect (driver-dependent). If it matters which filter is in beam at startup, leave this off and connect manually.\n'
        '* Flat panel (CoverCalibrator) — does not change cover position\n'
        '* Safety monitor — recommended on for unattended observatories\n\n'
        '*Manual-connect by default* (driver may actuate hardware on connect):\n'
        '* Guider — starts the PHD2 / openastro-phd2 client process\n'
        '* Dome — some drivers move shutter or rotate to home on connect\n'
        '* Weather station — keeps the polling loop quiet until you opt in\n\n'
        'Override per device based on your hardware\'s behaviour.',
  ),

  // §37.11 Autofocus — help on the genuinely non-obvious controls.
  'img.autofocus.method': Help(
    key: 'img.autofocus.method',
    title: 'Autofocus method',
    body: '* **HFR V-curve** (recommended): samples N positions across the focuser range, computes Half-Flux Radius at each, fits a V-shaped parabola, and picks the position at the V\'s minimum. Robust for CMOS + small refractors.\n'
        '* **Brightest-star HFR**: same algorithm but uses only the single brightest star in the frame (vs the median across all detected stars). Faster on sparse fields but noise-sensitive.\n'
        '* **FWHM (Gaussian fit)**: fits a 2D Gaussian to star profiles. More accurate at the focus point but slower; benefits from longer exposures.',
    relatedSettings: ['img.autofocus.steps', 'img.autofocus.step_size'],
  ),
  'img.autofocus.steps': Help(
    key: 'img.autofocus.steps',
    title: 'Number of AF steps',
    body: 'How many focuser positions to sample around the current position. The V-curve fit needs at least 3 points to be meaningful; 7-9 is the sweet spot for most setups (good fit + reasonable run time, ~5-10 min total).\n\n'
        'More steps catch a flatter HFR-vs-position curve more accurately but multiply the AF run time. Use 11-15 only if your CFZ is very small (long focal length + fast f-ratio) or you\'re tuning the routine.',
    relatedSettings: ['img.autofocus.step_size', 'img.autofocus.exposure_seconds'],
  ),
  'img.autofocus.step_size': Help(
    key: 'img.autofocus.step_size',
    title: 'AF step size',
    body: 'Distance between sample positions, in focuser native steps. Should span **3-5x the critical focus zone (CFZ)** total range — too small and the V-curve doesn\'t have enough vertical range to fit; too large and you sample outside the regime where the curve is parabolic.\n\n'
        'CFZ ≈ 2 × λ × N² where λ is wavelength (~0.55µm for green) and N is the f-ratio. f/4 → CFZ ~17µm; f/8 → CFZ ~70µm. Convert µm to focuser steps via your focuser\'s steps-per-µm.\n\n'
        'When in doubt: start with the default (50), run a focus, look at the V-curve. Flat curve → increase step size; sharp narrow V → decrease.',
    relatedSettings: ['img.autofocus.steps'],
  ),
  'img.autofocus.trigger_temp_delta_c': Help(
    key: 'img.autofocus.trigger_temp_delta_c',
    title: 'Temperature-trigger threshold',
    body: 'Most focuser tubes expand/contract with temperature — a 5°C overnight drop can move best-focus by 50-100 focuser steps. This setting triggers an AF run when the focuser-reported temperature has changed by this many °C since the last run.\n\n'
        '2.0°C is a sensible default for most aluminum/carbon-fiber tubes; tune lower (1.0-1.5°C) for very thermally sensitive setups (fast newts, big aperture refractors). 0 disables the temperature trigger.',
    relatedSettings: ['img.autofocus.trigger_hfr_drift_pct', 'img.autofocus.every_n_hours'],
  ),
  'img.autofocus.trigger_hfr_drift_pct': Help(
    key: 'img.autofocus.trigger_hfr_drift_pct',
    title: 'HFR-drift trigger',
    body: 'Triggers an AF run when the median HFR of recent light frames has worsened by this percentage compared to the post-AF baseline. Catches focus drift between scheduled AF runs — temperature changes are the usual cause but seeing degradation or mechanical shifts also bump HFR.\n\n'
        '15% is a balanced default — clear enough to detect real drift, loose enough to ignore single bad frames. Lower the threshold (5-10%) for narrowband / long exposures where bad frames are expensive.',
    relatedSettings: ['img.autofocus.trigger_temp_delta_c', 'img.autofocus.every_n_hours'],
  ),
  'img.autofocus.every_n_hours': Help(
    key: 'img.autofocus.every_n_hours',
    title: 'Periodic AF trigger',
    body: 'Force an AF run every N hours regardless of temperature or HFR. Catches slow drift that doesn\'t cross either of the other triggers (e.g. a gradual mechanical settling on first-night-out setups).\n\n'
        '2 hours is a safe interval for most sessions. Set to 0 to disable the time-based trigger and rely purely on temperature + HFR triggers.',
    relatedSettings: ['img.autofocus.trigger_temp_delta_c', 'img.autofocus.trigger_hfr_drift_pct'],
  ),

  // §37.10 Plate Solving — help on the non-obvious controls.
  'img.platesolve.engine': Help(
    key: 'img.platesolve.engine',
    title: 'Plate-solving engine',
    body: '* **ASTAP** (recommended): fast, accurate, local. Comes bundled with the §13 Debian package (`/usr/bin/astap`) plus a star index. Best default for unattended observatories.\n'
        '* **astrometry.net**: gold-standard accuracy. Can run online (nova.astrometry.net) or locally with downloaded index files. Slower than ASTAP but handles trickier fields.\n'
        '* **PlateSolve 2**: legacy Windows binary. Included for compatibility with NINA-imported sequences; not recommended for new setups.',
    relatedSettings: ['img.platesolve.path_or_endpoint', 'img.platesolve.search_radius_deg'],
  ),
  'img.platesolve.search_radius_deg': Help(
    key: 'img.platesolve.search_radius_deg',
    title: 'Search radius',
    body: 'How far from the hinted RA/Dec position the solver searches for a match.\n\n'
        '* **Small radius (5-15°)**: fast, but solve fails if your mount pointing is off or polar alignment is wrong.\n'
        '* **30° (default)**: tolerant of typical mount-pointing error.\n'
        '* **>90°**: effectively blind — slow but always finds a solution.\n\n'
        'Combine with `Use blind solve as fallback` for a fast-then-slow strategy.',
    relatedSettings: ['img.platesolve.use_blind_fallback'],
  ),
  'img.platesolve.downsample_factor': Help(
    key: 'img.platesolve.downsample_factor',
    title: 'Downsample factor',
    body: 'Plate solvers don\'t need full resolution to find a match — they only need enough pixels to detect stars. Downsampling 2x quarters the input area and is roughly 4x faster, with negligible accuracy hit on most setups.\n\n'
        'Bump to 3-4 for very large sensors (>30 MP). Drop to 1 if you have a small sensor (<5 MP) and solves are unreliable.',
    relatedSettings: ['img.platesolve.timeout_seconds'],
  ),
  'img.platesolve.use_blind_fallback': Help(
    key: 'img.platesolve.use_blind_fallback',
    title: 'Blind-solve fallback',
    body: 'If a hint-based solve times out (mount pointing way off, polar alignment wrong, hint coordinates stale), retry the same frame with no hint — let the solver search the entire sky.\n\n'
        'Blind solves are slower (often 30-60s) but rescue most bad-pointing situations. Recommended on except for very large sensors where blind solves can run out of timeout.',
    relatedSettings: ['img.platesolve.search_radius_deg'],
  ),
  'img.platesolve.max_iterations': Help(
    key: 'img.platesolve.max_iterations',
    title: 'Max centering iterations',
    body: 'Center-after-slew loops solve→slew→solve→slew until the target is within tolerance OR this many iterations have run.\n\n'
        '5 is enough for most mounts (each iteration typically halves the pointing error). Bump to 8-10 for cone-error setups where pointing improves slowly; drop to 2-3 for fast precision mounts that converge in one pass.',
    relatedSettings: ['img.platesolve.convergence_tolerance_arcsec'],
  ),
  'img.platesolve.convergence_tolerance_arcsec': Help(
    key: 'img.platesolve.convergence_tolerance_arcsec',
    title: 'Convergence tolerance',
    body: 'How close to dead-center the target must be before centering stops. 60″ (1 arc-minute) is a good default for typical setups — tighter than the §63 PHD2 sub-frame guiding can correct, looser than the human eye can notice.\n\n'
        'Tighten to 30″ for narrowband mosaics where panel alignment matters; loosen to 120″ for wide-field RGB where 2′ is well within frame.',
    relatedSettings: ['img.platesolve.max_iterations'],
  ),

  // §63 PHD2 — help on the genuinely non-obvious controls. Host/port/profile
  // are self-explanatory; settle-time + force-calibration get help because
  // their behaviour interacts with §35 + §38 in non-obvious ways.
  'eq.guider.dither_pixels': Help(
    key: 'eq.guider.dither_pixels',
    title: 'Dither amplitude',
    body: 'How many guide-camera pixels to shift the mount between exposures. Larger amplitudes randomize fixed-pattern noise more aggressively but mean longer settle times.\n\n'
        '* **3-5 px**: conservative. Settles fast on most mounts.\n'
        '* **5-10 px**: aggressive. Better noise reduction in stacks; needs a stable mount + good RMS.\n'
        '* **>10 px**: usually overkill; can push the guide star off the chip on small guide scopes.',
    relatedSettings: ['eq.guider.dither_every_n_frames', 'eq.guider.settle_pixels'],
  ),
  'eq.guider.settle_pixels': Help(
    key: 'eq.guider.settle_pixels',
    title: 'Settle threshold',
    body: 'Once a dither completes, PHD2 considers the guider re-settled when guide-RMS error stays below this many pixels for `settle_time` seconds.\n\n'
        'Tight thresholds (0.5-1.0 px) catch the last bit of motion but waste time on mounts that hover at 1 px RMS — they\'ll never converge.\n'
        '1.5 px (default) is a sensible middle ground. Loosen to 2-3 px for slower mounts; tighten only if your guide RMS routinely sits below 1 px.',
    relatedSettings: ['eq.guider.settle_time_sec', 'eq.guider.settle_timeout_sec'],
  ),
  'eq.guider.settle_timeout_sec': Help(
    key: 'eq.guider.settle_timeout_sec',
    title: 'Settle timeout',
    body: 'Hard maximum on settle wait. If the threshold isn\'t met by this point, exposure resumes anyway. The §54 plate-solve-failed notification (and §35 guider-lost retry budget) take over from here if guide quality stays bad.\n\n'
        '60s is the default. Bump to 120-180s on slow mounts; drop to 30s if you\'d rather skip frames than burn time on a stuck guider.',
    relatedSettings: ['eq.guider.settle_pixels', 'safety.policies.guider_retry_timeout'],
  ),
  'eq.guider.force_calibration_each_session': Help(
    key: 'eq.guider.force_calibration_each_session',
    title: 'Force calibration each session',
    body: 'PHD2 caches calibration data (guide-pulse direction, ratio, backlash) and reuses it across sessions by default. Forcing a fresh calibration each session is safer if your guide-scope orientation can shift overnight (loose dovetail, scope swap, etc) but adds 2-5 min to every startup.\n\n'
        'Recommended **off** for permanent setups (observatory rig); **on** for portable setups (grab-and-go scope, traveling kit).',
    relatedSettings: ['safety.policies.meridian_recal_guider'],
  ),
  // §63.5 guider-engine config — pushed to the guider daemon on connect.
  'eq.guider.guide_focal_length': Help(
    key: 'eq.guider.guide_focal_length',
    title: 'Guide focal length',
    body: 'Focal length of the guide scope (mm). Combined with the guide-camera pixel size it sets the guider\'s arcsec/pixel scale, which PHD2 uses for star-mass thresholds and the guiding graph.\n\n'
        'Leave **0** to keep whatever the PHD2 guide profile already has. Set it to push your value on connect.',
    relatedSettings: ['eq.guider.guide_pixel_size'],
  ),
  'eq.guider.guide_pixel_size': Help(
    key: 'eq.guider.guide_pixel_size',
    title: 'Guide pixel size',
    body: 'Pixel size of the guide camera (µm). With the guide focal length this gives the guider\'s arcsec/pixel scale.\n\n'
        'Leave **0** to keep the PHD2 guide profile default.',
    relatedSettings: ['eq.guider.guide_focal_length'],
  ),
  'eq.guider.ra_aggressiveness': Help(
    key: 'eq.guider.ra_aggressiveness',
    title: 'RA aggressiveness',
    body: 'Fraction (0–1) of each measured RA error that PHD2 corrects per cycle. Lower values guide more gently (less prone to oscillation / chasing seeing); higher values track real drift faster.\n\n'
        '**0.7** is a good default. Drop toward 0.5 if guiding oscillates; raise toward 0.9 only on a stiff, well-behaved mount.',
    relatedSettings: ['eq.guider.dec_aggressiveness', 'eq.guider.minimum_move'],
  ),
  'eq.guider.dec_aggressiveness': Help(
    key: 'eq.guider.dec_aggressiveness',
    title: 'Dec aggressiveness',
    body: 'Fraction (0–1) of each measured Dec error PHD2 corrects per cycle. Same idea as RA aggressiveness; Dec is often run a touch lower because of backlash near direction reversals.',
    relatedSettings: ['eq.guider.ra_aggressiveness', 'eq.guider.dec_guide_mode'],
  ),
  'eq.guider.minimum_move': Help(
    key: 'eq.guider.minimum_move',
    title: 'Minimum move',
    body: 'Smallest error (in guide pixels) PHD2 will react to. Errors below this are ignored, so the mount doesn\'t chase seeing noise.\n\n'
        '**~0.15 px** is typical. Raise it in poor seeing to calm the corrections; lower it only with a very stable mount + sky.',
    relatedSettings: ['eq.guider.ra_aggressiveness'],
  ),
  'eq.guider.dec_guide_mode': Help(
    key: 'eq.guider.dec_guide_mode',
    title: 'Dec guide mode',
    body: 'How PHD2 corrects declination:\n\n'
        '* **Auto**: correct in whichever direction the error appears (leaves PHD2\'s own setting alone — ARA won\'t push Auto).\n'
        '* **North / South**: only ever push that direction. Useful on mounts with bad Dec backlash — pick the uphill side so backlash is always taken up.\n'
        '* **Off**: no Dec guiding (RA only).',
    relatedSettings: ['eq.guider.dec_aggressiveness'],
  ),

  // §37.4 Filter Wheel slot labels.
  'eq.filterwheel.slot_labels': Help(
    key: 'eq.filterwheel.slot_labels',
    title: 'Filter wheel slot labels',
    body: 'Names you give each physical filter slot. They flow through to several places:\n\n'
        '* **`\$\$FILTER\$\$` filename token** — e.g. `M31_L_60s.fits` uses the active slot\'s label.\n'
        '* **FITS-header `FILTER` keyword** — read by downstream stacking tools (DSS, Siril, PixInsight) to group frames per filter.\n'
        '* **Sequence per-filter exposure blocks** — sequences reference filters by label, so labels here must match the labels used in your sequence templates.\n'
        '* **§29.2 calibration-set indexing** — matching darks/flats are looked up per filter label.\n\n'
        '**Conventions** (not enforced — use whatever you like):\n'
        '* Mono LRGB: `L`, `R`, `G`, `B`\n'
        '* Narrowband: `Hα` (or `Ha`), `OIII`, `SII`\n'
        '* Photometric: `U`, `B`, `V`, `R`, `I` (Johnson) or `u`, `g`, `r`, `i`, `z` (SDSS)\n\n'
        'Leave a slot blank if it\'s unused or unloaded.',
    relatedSettings: ['session.storage.filename_template'],
  ),
};
