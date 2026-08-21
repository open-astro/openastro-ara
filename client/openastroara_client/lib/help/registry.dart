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

  /// §61/§68.4 — user-intent search words beyond the title ("equipment hub
  /// down" → the AlpacaBridge troubleshoot entry). Help entries are indexed
  /// into the command palette alongside settings; most entries need none
  /// (their titles already match), so this defaults empty.
  final List<String> keywords;

  /// §69 hardware-aware notes: vendor substring (matched case-insensitively
  /// against the CONNECTED device's name, e.g. "ZWO" in "ZWO ASI2600MM Pro")
  /// → an addendum the help sheet shows as "For your hardware". Keep the
  /// vendor keys in sync with the AlpacaBridge supported-drivers list
  /// (https://www.openastro.net/docs/supported-drivers) — the
  /// hardware-help-sync skill audits this on gear changes.
  final Map<String, String> driverNotes;

  const Help({
    required this.key,
    required this.title,
    required this.body,
    this.learnMoreUrl,
    this.relatedHelpKeys = const <String>[],
    this.relatedSettings = const <String>[],
    this.keywords = const <String>[],
    this.driverNotes = const <String, String>{},
  });

  /// The first driver note whose vendor key appears in [deviceName].
  String? noteFor(String? deviceName) {
    if (deviceName == null || deviceName.isEmpty) return null;
    final lower = deviceName.toLowerCase();
    for (final e in driverNotes.entries) {
      if (lower.contains(e.key.toLowerCase())) return e.value;
    }
    return null;
  }
}

const Map<String, Help> helpRegistry = {
  // §68.4 — the AlpacaBridge troubleshoot pointer (searchable via the palette;
  // the wizard's §68.2 Screen-2 panel carries the same guidance in-flow). The
  // companion `equipment.alpacabridge.version` entry from the playbook is MOOT:
  // the §68.1 version gate was removed (Alpaca has no version endpoint by
  // design — user decision 2026-06-21), so there is no version to explain.
  'session.notifications.alarm_delay': Help(
    key: 'session.notifications.alarm_delay',
    title: 'Safety alarm delay',
    body:
        'When Ara reports UNSAFE (safety monitor, weather threshold, or an '
        'emergency stop), the app pops the safety modal immediately but waits this many '
        'seconds before the tone starts looping at full volume — if you are at the screen '
        'you can silence it before it ever rings; if you are asleep, it rings. The server '
        'fires the event BEFORE its reaction runs, so the alarm and the park happen in '
        'parallel. Conditions clearing (safety.safe) auto-silences.',
    relatedSettings: ['session.notifications.alarm_tone'],
    keywords: ['alarm', 'delay', 'safety', 'unsafe', 'siren'],
  ),
  'session.notifications.alarm_tone': Help(
    key: 'session.notifications.alarm_tone',
    title: 'Safety alarm tone',
    body:
        'The bundled tone the safety alarm loops until silenced: a two-tone siren '
        '(default, hardest to sleep through), urgent triple beeps, or a gentler rising '
        'chime. Device-local — the desktop in your bedroom and the one in the observatory '
        'can each pick their own. Volume is forced to maximum while the alarm rings; a '
        'muted safety alarm defeats its purpose.',
    relatedSettings: ['session.notifications.alarm_delay'],
    keywords: ['alarm', 'tone', 'siren', 'beeps', 'chime'],
  ),
  'session.calibration.capture_default': Help(
    key: 'session.calibration.capture_default',
    title: 'End-of-night calibration capture',
    body:
        'When a sequence completes, Ara can generate matching flats from that night\'s own '
        'session — one flat set per filter, replaying the night\'s focus position, gain, and '
        'offset, so the flats actually match the lights they calibrate. "Ask at each sequence '
        'start" shows a prompt when you start a run (answering with "remember my choice" '
        'updates this setting); "Panel flats at end" starts the flats automatically when the '
        'run completes and sends a notification to light your panel; "Sky flats at twilight" '
        'generates the flats sequence ready to run — start it yourself at twilight; "Never" '
        'stays quiet. Aborted or stopped runs never trigger flats — only a run that completes '
        'on its own.',
    relatedSettings: [],
    keywords: [
      'calibration',
      'flats',
      'auto',
      'panel',
      'sky',
      'twilight',
      'prompt',
      'matching',
    ],
  ),
  'session.calibration.flat_target_adu': Help(
    key: 'session.calibration.flat_target_adu',
    title: 'Flat target brightness',
    body:
        'Generated flat sets expose themselves: each per-filter set probes short exposures, '
        'measures the frame\'s mean pixel value (ADU), and scales the exposure until it lands '
        'on this target before capturing the real flats. 30000 is a solid default for 16-bit '
        'cameras — roughly 45% of full scale, bright enough for clean signal and safely below '
        'saturation. If your camera is 12/14-bit behind a driver that scales to 16 bits, the '
        'default still applies; for unusual setups aim for 40-50% of your camera\'s full scale.',
    relatedSettings: [
      'session.calibration.flat_target_adu_tolerance_pct',
      'session.calibration.flat_frames_per_filter',
    ],
    keywords: ['flats', 'adu', 'target', 'brightness', 'auto exposure'],
  ),
  'session.calibration.flat_target_adu_tolerance_pct': Help(
    key: 'session.calibration.flat_target_adu_tolerance_pct',
    title: 'Flat target tolerance',
    body:
        'How close the probe\'s measured mean must be to the target before the set starts '
        'capturing, as a percentage of the target. 5% converges in one or two probes on a '
        'stable panel; tighten it if you want maximally consistent flats between filters, '
        'loosen it if your panel flickers and the probe struggles to settle.',
    relatedSettings: ['session.calibration.flat_target_adu'],
    keywords: ['flats', 'tolerance', 'percent', 'probe'],
  ),
  'session.calibration.flat_frames_per_filter': Help(
    key: 'session.calibration.flat_frames_per_filter',
    title: 'Flat frames per filter',
    body:
        'How many FLAT frames each generated per-filter set captures once its exposure has '
        'converged. Stacking 20-30 flats averages away photon noise in the master flat on '
        'most cameras; more helps narrowband filters with dim panels.',
    relatedSettings: ['session.calibration.flat_target_adu'],
    keywords: ['flats', 'frames', 'count', 'stack', 'master flat'],
  ),
  'session.calibration.post_flat_park_mount': Help(
    key: 'session.calibration.post_flat_park_mount',
    title: 'Park after flats',
    body:
        'Appends a park step to the end of the generated flats sequence, so when the last '
        'flat set finishes the mount parks itself and the rig ends the night in a safe, '
        'known position. Turn it off if something else manages parking (an observatory '
        'controller, or you run flats mid-evening).',
    relatedSettings: ['session.calibration.capture_default'],
    keywords: ['park', 'mount', 'flats', 'end of night'],
  ),
  'session.calibration.sky_flat_target_adu': Help(
    key: 'session.calibration.sky_flat_target_adu',
    title: 'Sky flat target brightness',
    body:
        'Twilight sky flats expose against the blank sky instead of a panel. Each set aims '
        'for this mean ADU, re-probing the exposure before every frame because the twilight '
        'sky brightens (or darkens) minute to minute. Keep the target comfortably between the '
        'stop-below and stop-above bounds so Ara has room to chase the changing sky.',
    relatedSettings: [
      'session.calibration.sky_flat_stop_at_max_adu',
      'session.calibration.sky_flat_stop_at_min_adu',
    ],
    keywords: ['sky flats', 'twilight', 'adu', 'target', 'brightness'],
  ),
  'session.calibration.sky_flat_frames_per_filter': Help(
    key: 'session.calibration.sky_flat_frames_per_filter',
    title: 'Sky flat frames per filter',
    body:
        'How many FLAT frames each twilight set captures per filter. Because the usable '
        'twilight window is short, keep this modest (15-25) so the whole filter set finishes '
        'before the sky leaves the brightness window — Ara stops early and honestly if '
        'it does.',
    relatedSettings: ['session.calibration.sky_flat_target_adu'],
    keywords: ['sky flats', 'twilight', 'frames', 'count'],
  ),
  'session.calibration.sky_flat_target_azimuth': Help(
    key: 'session.calibration.sky_flat_target_azimuth',
    title: 'Sky flat patch azimuth',
    body:
        'The compass bearing the mount slews to for twilight flats (0 = north, 90 = east, '
        '180 = south, 270 = west). The evenest twilight sky is the anti-solar patch — opposite '
        'the sunset at dusk, opposite the sunrise at dawn — so point away from where the sun '
        'sits on the horizon.',
    relatedSettings: ['session.calibration.sky_flat_target_altitude'],
    keywords: ['sky flats', 'twilight', 'azimuth', 'pointing', 'anti-solar'],
  ),
  'session.calibration.sky_flat_target_altitude': Help(
    key: 'session.calibration.sky_flat_target_altitude',
    title: 'Sky flat patch altitude',
    body:
        'The height above the horizon the mount slews to for twilight flats. Around 75 '
        'degrees keeps you clear of horizon brightness gradients and local obstructions while '
        'staying off the exact zenith (where a German mount can foul the pier).',
    relatedSettings: ['session.calibration.sky_flat_target_azimuth'],
    keywords: ['sky flats', 'twilight', 'altitude', 'elevation', 'pointing'],
  ),
  'session.calibration.sky_flat_stop_at_max_adu': Help(
    key: 'session.calibration.sky_flat_stop_at_max_adu',
    title: 'Sky flat stop-above',
    body:
        'The upper brightness guard. When the sky reads brighter than this even at the '
        'shortest exposure, dawn has grown too bright to flat against — Ara stops the '
        'set rather than saving blown frames. Keep it above the target with room to spare.',
    relatedSettings: [
      'session.calibration.sky_flat_stop_at_min_adu',
      'session.calibration.sky_flat_target_adu',
    ],
    keywords: ['sky flats', 'twilight', 'stop', 'too bright', 'ceiling'],
  ),
  'session.calibration.sky_flat_stop_at_min_adu': Help(
    key: 'session.calibration.sky_flat_stop_at_min_adu',
    title: 'Sky flat stop-below',
    body:
        'The lower brightness guard. When the sky reads darker than this even at the longest '
        'exposure, the sky has gone too dark to flat against — Ara stops the set. Keep '
        'it below the target with room to spare.',
    relatedSettings: [
      'session.calibration.sky_flat_stop_at_max_adu',
      'session.calibration.sky_flat_target_adu',
    ],
    keywords: ['sky flats', 'twilight', 'stop', 'too dark', 'floor'],
  ),
  'session.calibration.sky_flat_sun_altitude': Help(
    key: 'session.calibration.sky_flat_sun_altitude',
    title: 'Sky flat wait-for sun altitude',
    body:
        'The generated sky-flat sequence begins with a wait for the sun to reach this '
        'altitude below the horizon, so you can start the run early and let it park on the '
        'wait until twilight arrives. Around -9 degrees (nautical twilight) the sky is dim '
        'enough that stars are gone yet bright enough to reach the target within the exposure '
        'bounds. Dawn flats fire as the sun rises up through the same altitude.',
    relatedSettings: ['session.calibration.sky_flat_target_adu'],
    keywords: ['sky flats', 'twilight', 'sun', 'altitude', 'nautical', 'wait'],
  ),
  'session.storage.backup_stream': Help(
    key: 'session.storage.backup_stream',
    title: 'Real-time frame backup',
    body:
        'When enabled, this desktop pulls every newly-captured FITS from Ara as the '
        'night runs: it claims the single backup-stream slot, downloads each frame, verifies '
        'its SHA-256 checksum, stores it under your backup folder, and confirms back to the '
        'server. If your imaging drive dies overnight, everything already streamed is safe '
        'here — the worst case is losing the one frame being captured at the moment of '
        'failure. Only one desktop can stream from a server at a time; enabling it here '
        'while another desktop holds the slot shows who has it. Transfers pause while an '
        'exposure is downloading from the camera so they never compete for Ara\'s '
        'bandwidth at the wrong moment.',
    relatedSettings: [
      'session.storage.backup_stream_folder',
      'session.storage.backup_retention_count',
    ],
    keywords: [
      'backup',
      'stream',
      'mirror',
      'sync',
      'drive',
      'failure',
      'fits',
      'sha256',
    ],
  ),
  'session.storage.backup_stream_folder': Help(
    key: 'session.storage.backup_stream_folder',
    title: 'Backup folder',
    body:
        'Streamed frames land here, organized by server and imaging session: '
        '<folder>/<server>/<session-id>/<frame-id>.fits. Pick a drive with room for a full '
        'night of frames; if it fills, the stream surfaces the problem and stops pulling '
        '(Ara keeps the queue, so re-enabling resumes where it left off).',
    relatedSettings: ['session.storage.backup_stream'],
    keywords: ['backup', 'folder', 'path', 'destination', 'storage'],
  ),
  'session.storage.backup_stream_mbps': Help(
    key: 'session.storage.backup_stream_mbps',
    title: 'Backup bandwidth cap',
    body:
        'Caps the backup stream\'s AVERAGE download rate in megabits per second; 0 means '
        'unlimited. Each frame still transfers at full link speed — the puller then waits '
        'before fetching the next one until the session average is back under your cap. '
        'This rides on top of the capture-aware pause (transfers already hold while an '
        'exposure is downloading from the camera), so a cap is mainly useful on shared or '
        'metered links. The status line above shows the link speed measured on the '
        'session\'s first pull to help you pick a sensible number.',
    relatedSettings: ['session.storage.backup_stream'],
    keywords: ['bandwidth', 'cap', 'mbps', 'throttle', 'network'],
  ),
  'equipment.alpacabridge.troubleshoot': Help(
    key: 'equipment.alpacabridge.troubleshoot',
    title: 'AlpacaBridge not detected?',
    body:
        "AlpacaBridge is ARA's equipment hub — every camera, mount, focuser "
        "and other ASCOM Alpaca device connects through it. If equipment "
        "discovery finds nothing or the profile wizard reports the bridge "
        "missing:\n\n"
        "1. It should have been installed alongside ARA Core via apt. If it "
        "wasn't, install it on the daemon host:\n\n"
        "    sudo apt install alpaca-bridge\n\n"
        "2. Check the service is running on the daemon host:\n\n"
        "    systemctl status alpaca-bridge\n\n"
        "3. Devices are discovered over UDP port 32227 on the DAEMON's subnet "
        "— gear on a different network segment won't be found. A non-standard "
        "bridge, on a different host or port, can be entered as an address "
        "override on the wizard's AlpacaBridge screen.\n\n"
        "INDI/INDIGO users: connect through an Alpaca bridge such as AlpacaPi "
        "or INDIGO Sky's -A Alpaca server.",
    keywords: [
      'alpaca bridge missing',
      'equipment not found',
      'alpaca bridge not detected',
      'install alpaca bridge',
      'equipment hub down',
      'no devices',
      'discovery',
    ],
  ),
  // (Old `guider.dither_pixels` starter entry retired in 12h.4 — superseded
  // by the proper `eq.guider.*` namespace below.)
  'safety.policies.on_unsafe': Help(
    key: 'safety.policies.on_unsafe',
    title: 'Unsafe Weather Actions',
    body:
        'Determines what the system does when the safety monitor reports '
        'unsafe conditions (rain, high wind, clouds). "Pause + Park" is the '
        'safest default for unattended imaging.',
    relatedSettings: ['safety.policies.on_unsafe'],
  ),
  'safety.policies.weather_triggers': Help(
    key: 'safety.policies.weather_triggers',
    title: 'Weather-station thresholds',
    body:
        'With an ObservingConditions weather station connected, Ara can react to the '
        'numbers, not just a boolean safety monitor: wind (sustained or gust) over your '
        'km/h limit, humidity over your % limit, or ambient temperature closing to within '
        'your dew-delta of the dew point each make conditions UNSAFE — the same reaction '
        'as the safety monitor ("When conditions turn unsafe" runs, and the alert names '
        'exactly which threshold tripped). A sensor your station does not report simply '
        'skips its check. Auto-resume waits for the weather to clear too: conditions must '
        'read safe on every source before the countdown starts. Off by default.',
    relatedSettings: [
      'safety.policies.max_wind_kmh',
      'safety.policies.max_humidity_pct',
      'safety.policies.min_dew_delta_c',
      'safety.policies.on_unsafe',
    ],
    keywords: [
      'weather',
      'thresholds',
      'wind',
      'humidity',
      'dew',
      'station',
      'unsafe',
    ],
  ),
  'safety.policies.max_wind_kmh': Help(
    key: 'safety.policies.max_wind_kmh',
    title: 'Maximum wind',
    body:
        'Wind above this — sustained speed or gust, whichever reads worse — is a breach. '
        'Guiding usually degrades well before gear is at risk; 36 km/h (10 m/s) is a '
        'conservative default for a covered rig, lower it for long focal lengths.',
    relatedSettings: ['safety.policies.weather_triggers'],
    keywords: ['wind', 'gust', 'speed', 'threshold'],
  ),
  'safety.policies.max_humidity_pct': Help(
    key: 'safety.policies.max_humidity_pct',
    title: 'Maximum humidity',
    body:
        'Relative humidity above this is a breach — sustained high humidity is '
        'condensation territory for optics and electronics even before fog forms.',
    relatedSettings: [
      'safety.policies.weather_triggers',
      'safety.policies.min_dew_delta_c',
    ],
    keywords: ['humidity', 'moisture', 'condensation', 'threshold'],
  ),
  'safety.policies.min_dew_delta_c': Help(
    key: 'safety.policies.min_dew_delta_c',
    title: 'Minimum dew delta',
    body:
        'When the ambient temperature drops to within this many °C of the dew point, '
        'dew is about to form on your optics. 2 °C is a sensible floor; raise it if your '
        'corrector plate fogs early. Needs a station reporting both temperature and dew '
        'point — otherwise the check is skipped.',
    relatedSettings: ['safety.policies.weather_triggers'],
    keywords: ['dew', 'dew point', 'fog', 'delta', 'condensation'],
  ),
  'safety.policies.auto_resume': Help(
    key: 'safety.policies.auto_resume',
    title: 'Auto-resume',
    body:
        'If enabled, the sequence will automatically resume as soon as the '
        'safety monitor reports "Safe" again. If disabled, the sequence '
        'stays paused until you manually resume it.',
    relatedSettings: ['safety.policies.auto_resume'],
  ),
  'safety.policies.resume_delay': Help(
    key: 'safety.policies.resume_delay',
    title: 'Resume Delay',
    body:
        'The number of minutes to wait after a "Safe" signal before '
        'actually resuming. Useful to ensure that a passing cloud bank '
        'has fully cleared before starting the next exposure.',
    relatedSettings: ['safety.policies.auto_resume'],
  ),
  'safety.policies.meridian_flip_auto': Help(
    key: 'safety.policies.meridian_flip_auto',
    title: 'Auto meridian flip',
    body:
        'A meridian flip is when a German Equatorial Mount (GEM) swaps sides of the pier to keep tracking a target that crossed the meridian (south line at culmination).\n\n'
        '* **On** (recommended): the mount flips automatically when the target reaches the configured meridian-limit (set per-mount by the mount-safety policy). Exposure pauses, mount flips, plate-solve re-centers, guider re-calibrates, exposure resumes.\n'
        '* **Off**: the sequence pauses at the meridian-limit and waits for you to manually flip + resume.\n\n'
        'Fork-mounted scopes (CGEM-DX, alt-az without wedge) don\'t need a meridian flip — turn this off and the meridian-limit policy is ignored.',
    relatedSettings: [
      'safety.policies.meridian_pause_min',
      'safety.policies.meridian_recenter',
      'safety.policies.meridian_recal_guider',
    ],
  ),
  'safety.policies.meridian_pause_min': Help(
    key: 'safety.policies.meridian_pause_min',
    title: 'Pause after meridian flip',
    body:
        'Time the mount needs to settle mechanically after the pier-side swap before exposures resume. Faster mounts (Paramount, 10Micron) settle in <1 min; slower or heavy-payload setups need 3-5 min. '
        'Set this conservatively — a too-short pause produces motion-blurred first frames after the flip.',
    relatedSettings: ['safety.policies.meridian_flip_auto'],
  ),
  'safety.policies.on_altitude_limit': Help(
    key: 'safety.policies.on_altitude_limit',
    title: 'On altitude limit',
    body:
        'What happens when a target drops below the minimum-altitude floor (set in Site Preferences, default 20°).\n\n'
        '* **Skip target**: move to the next target in the sequence and continue. Recommended for multi-target sessions.\n'
        '* **Pause sequence**: pause and wait for the target to rise again (only useful for circumpolar targets).\n'
        '* **Abort sequence**: stop the whole session. Strict but predictable.',
    relatedSettings: [
      'safety.site.default_horizon_altitude_deg',
      'safety.policies.park_if_no_more_targets',
    ],
  ),
  'safety.policies.on_guider_lost': Help(
    key: 'safety.policies.on_guider_lost',
    title: 'On guider lost',
    body:
        'Action when OpenAstro Guider reports lost lock — typically caused by clouds rolling in, a star drifting off the guide chip, or a calibration glitch.\n\n'
        '* **Pause + retry**: pause exposure, restart guider, retry until `Guider retry timeout` expires. Recommended for clear-but-occasional-cloud nights.\n'
        '* **Skip target**: skip this target immediately and move on.\n'
        '* **Abort sequence**: stop the whole session.',
    relatedSettings: [
      'safety.policies.guider_retry_timeout',
      'safety.policies.skip_target_if_recovery_fails',
    ],
  ),
  'safety.policies.on_disk_space_critical': Help(
    key: 'safety.policies.on_disk_space_critical',
    title: 'On critically-low disk',
    body:
        'What the disk-space monitor does when free space on your image save volume **drops to** the **critical** threshold (set under Settings → Session → Storage).\n\n'
        '* **Warn only** (default): raise a red diagnostic and, if enabled, a *Low disk space* notification — but keep capturing. You decide what to do.\n'
        '* **Abort the running sequence**: also halt any in-progress sequence, so you don\'t keep filling the disk with frames that may not even save. A critical notification records that it stopped the run.\n\n'
        'This fires on the *transition* into critical (when free space crosses the threshold going down), so it halts a sequence that\'s already running. Starting a brand-new sequence while the disk is already critical isn\'t blocked yet — a pre-capture check is a planned follow-up. Either way the monitor never deletes anything.',
    relatedSettings: [
      'session.storage.min_free_disk_critical_gb',
      'session.notifications.on_disk_space_low',
    ],
  ),
  'safety.policies.guider_retry_timeout': Help(
    key: 'safety.policies.guider_retry_timeout',
    title: 'Guider retry timeout',
    body:
        'How long to keep retrying guider re-acquisition before giving up. When the timeout expires, the plate-solve-failed notification fires and the `Skip target if recovery fails` policy decides next.\n\n'
        '60s is a good default — long enough to ride out a passing cloud but short enough to skip a target if guiding is genuinely broken.',
    relatedSettings: [
      'safety.policies.on_guider_lost',
      'session.notifications.on_plate_solve_failed',
    ],
  ),
  'diagnostics.mode': Help(
    key: 'diagnostics.mode',
    title: 'Diagnostics mode',
    body:
        'Controls how Ara responds to critical-severity diagnostic events (sensor temp out of range, mount drift > 30″, guider RMS triple, autofocus position lost, etc).\n\n'
        '* **Notify only** (default): events surface in the Diagnostic Panel + as notifications, but sequence execution is never auto-paused by diagnostics alone.\n'
        '* **Pause on critical**: critical-severity events auto-pause the running sequence and ring the alarm. You decide whether to resume.\n'
        '* **Abort on critical**: critical-severity events trigger Abort + Park instead of pause. Use only for unattended observatory automation where you trust the safety policies to recover safely.\n\n'
        'Lower-severity diagnostic events (warnings, infos) never trigger automated action regardless of this setting.',
    relatedSettings: ['session.notifications.on_critical_diagnostic'],
  ),
  // §37.9 Imaging Defaults — help only on the non-obvious controls (per
  // §69.1 default-is-no-tooltip). Exposure / target temp / frame type are
  // self-explanatory by their labels.
  'imaging.defaults.gain': Help(
    key: 'imaging.defaults.gain',
    title: 'Default gain',
    body:
        'CMOS sensor gain (amplification before the ADC). Higher gain = more sensitivity per photon, but also more read noise floor. '
        'For deep-sky targets most CMOS cameras have a "unity gain" sweet spot listed in their datasheet — start there.',
    relatedSettings: ['imaging.defaults.gain'],
  ),
  'imaging.defaults.offset': Help(
    key: 'imaging.defaults.offset',
    title: 'Default offset',
    body:
        'A small DC pedestal added to every pixel before readout. Prevents the black level from clipping at zero, which would '
        'break dark-frame and bias subtraction. Camera-specific — your camera\'s manual usually recommends a value.',
    relatedSettings: ['imaging.defaults.offset'],
  ),
  'imaging.defaults.bin': Help(
    key: 'imaging.defaults.bin',
    title: 'Default binning',
    body:
        'Pixel binning combines a NxN grid of pixels into one larger virtual pixel. 2x2 quadruples sensitivity per pixel but halves resolution. '
        'On CMOS cameras binning is typically done in software (post-readout) and equivalent to downsampling — the gain in SNR is smaller than on CCD.',
    relatedSettings: ['imaging.defaults.bin'],
  ),
  'imaging.defaults.cooler_ramp_c_per_min': Help(
    key: 'imaging.defaults.cooler_ramp_c_per_min',
    title: 'Cooler ramp rate',
    body:
        'How fast the sensor cools toward the target temperature. Faster ramps stress the TEC and risk condensation on the sensor cover '
        'as the temperature crosses the dew point. 1°C/min is a safe default; some sensors handle 2-3°C/min fine.',
    relatedSettings: ['imaging.defaults.cooler_ramp_c_per_min'],
  ),
  'imaging.defaults.warmup_at_session_end': Help(
    key: 'imaging.defaults.warmup_at_session_end',
    title: 'Warm up at session end',
    body:
        'CCD sensors can crack under repeated thermal shock if disconnected cold. CMOS sensors are tolerant — most users leave this off. '
        'When enabled, the cooler ramps the sensor back to within ~5°C of ambient before disconnecting at session end.',
    relatedSettings: ['imaging.defaults.warmup_at_session_end'],
  ),

  // §29 Storage — help on the non-obvious controls (format, compression,
  // filename template, plus a brief save-directory note because the default
  // `/media/openastroara` mount point isn't obvious to novices).
  'session.storage.save_directory': Help(
    key: 'session.storage.save_directory',
    title: 'Save directory',
    body:
        'Base path where captured frames are written. Must be a mounted writable directory. '
        'By default this is the external drive you chose in Settings → Session → Storage.'
        'Capturing to the SD card is fine for testing but will wear the card out over a single all-night session — use external storage for real sessions.',
    relatedSettings: ['session.storage.save_directory'],
  ),
  'session.storage.preview_cache': Help(
    key: 'session.storage.preview_cache',
    title: 'Preview cache',
    body:
        'To show thumbnails and previews instantly, the server keeps small JPEG copies of every frame '
        'next to the FITS files on the capture drive (named *.thumb.jpg and *.preview.*.jpg). '
        'Cleaning the cache deletes those JPEGs and frees the space — your FITS data is never touched. '
        'It is always safe: the server re-creates thumbnails and default previews automatically in the '
        'background after its next restart, and anything else the moment you view it. The library will '
        'just load at its slower, uncached pace until the rebuild finishes.',
    relatedSettings: ['session.storage.save_directory'],
  ),
  'session.storage.file_format': Help(
    key: 'session.storage.file_format',
    title: 'File format',
    body:
        'FITS is the historical standard for astronomy and the safest choice for downstream tools (DSS, Siril, PixInsight, AstroPixelProcessor — all read FITS). '
        'XISF is PixInsight\'s native format with richer metadata (per-frame statistics, processing history) but smaller tool support. '
        'RICE-compressed FITS halves file size on light frames with minimal CPU cost and is widely supported. '
        'Gzipped FITS is universal but slower to write and read.',
    relatedSettings: ['session.storage.file_format'],
  ),
  'session.storage.compression': Help(
    key: 'session.storage.compression',
    title: 'Compression',
    body:
        'Optional lossless compression applied as each frame is written to disk.\n\n'
        '* **Off**: No compression — fastest, biggest files.\n'
        '* **RICE**: Astronomy-tuned algorithm. ~2x compression on lights, ~10x on darks/bias. Fast both ways. Recommended.\n'
        '* **gzip**: General-purpose. Smaller files than RICE but ~5x slower to write. Use only if a downstream tool requires it.',
    relatedSettings: ['session.storage.compression'],
  ),
  'session.storage.filename_template': Help(
    key: 'session.storage.filename_template',
    title: 'Naming template',
    body:
        'Plain words in curly braces, `/` between folders — for example '
        '`{night}/{type}/{datetime}_{filter}_{exposure}s`. Folders are '
        'created automatically, and a part with no value (no filter wheel, '
        'no target) simply vanishes from the name.\n\n'
        '**Words you can use:**\n'
        '* `{night}` — the night the frame belongs to (a 2 AM frame files under yesterday evening)\n'
        '* `{datetime}`, `{date}`, `{time}` — when the exposure started\n'
        '* `{type}` — Light / Dark / Bias / Flat\n'
        '* `{target}` — what you were shooting\n'
        '* `{filter}`, `{exposure}`, `{gain}`, `{offset}`, `{binning}`\n'
        '* `{temp}` — sensor temperature, `{camera}` — camera name\n'
        '* `{n}` — frame number\n\n'
        'Templates imported from NINA (the `\$\$TOKEN\$\$` style) keep working as-is.',
    relatedSettings: ['session.storage.filename_template'],
  ),
  'session.storage.backup_retention_count': Help(
    key: 'session.storage.backup_retention_count',
    title: 'Backup snapshot retention',
    body:
        'How many configuration backups (profile + sequences) Ara keeps under its backups folder. After every new backup, the oldest snapshots beyond this count are deleted automatically — so routine backups can\u2019t slowly fill the disk.\n\n'
        'Backups are small (kilobytes), so the default of 20 costs almost nothing while keeping weeks of history. Set **0** to keep every backup forever and manage the folder yourself.',
    relatedSettings: [
      'session.storage.backup_retention_count',
      'session.storage.min_free_disk_warn_gb',
    ],
  ),
  'session.storage.min_free_disk_warn_gb': Help(
    key: 'session.storage.min_free_disk_warn_gb',
    title: 'Low-disk warning threshold',
    body:
        'When free space on your image save volume drops below this many GB, Ara raises a **warning** (a yellow diagnostic and, if enabled, a *Low disk space* notification) so you can free space before a session stalls.\n\n'
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
    body:
        'When free space drops below this many GB, Ara escalates to a **critical** alert (a red diagnostic and, if enabled, a critical notification) — the disk is nearly full and the next frames may not fit.\n\n'
        'Like the warning, this is advisory: ARA never blocks a capture or deletes data. Must be below the warning threshold. '
        'If the warn/critical pair is left non-positive or inverted, Ara falls back to its built-in 10 GB / 2 GB defaults.',
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
    body:
        'Pushover is a paid (one-time \$5) push-notification service that delivers messages to your phone or desktop. '
        'To use: sign up at pushover.net, then copy the User Key from your dashboard into this field.\n\n'
        'Leave empty to disable Pushover delivery entirely. Other channels (in-app banner, OS notification, sound) work independently.',
    learnMoreUrl: 'https://pushover.net/',
    relatedSettings: ['session.notifications.telegram_bot_token'],
  ),
  'session.notifications.telegram_bot_token': Help(
    key: 'session.notifications.telegram_bot_token',
    title: 'Telegram bot token',
    body:
        'Telegram bots are free and deliver messages to a Telegram chat you control. '
        'To use: message @BotFather in Telegram, send `/newbot`, follow the prompts, then paste the bot token here.\n\n'
        'You\'ll also need to send `/start` to your new bot once so it can DM you. Leave empty to disable Telegram delivery.',
    learnMoreUrl: 'https://core.telegram.org/bots#how-do-i-create-a-bot',
    relatedSettings: ['session.notifications.pushover_token'],
  ),
  'session.notifications.on_critical_diagnostic': Help(
    key: 'session.notifications.on_critical_diagnostic',
    title: 'Critical diagnostic events',
    body:
        '"Critical" is the most serious level — events that indicate something is actively wrong inside Ara and may require intervention.'
        'Examples: sensor cooler runaway, mount tracking deviation > 30″, guider RMS suddenly tripled, autofocus position drifted past the backlash budget.\n\n'
        'Distinct from "Safety event" — safety events are environmental conditions (weather, altitude limits, guider loss) that already trigger automated park/pause actions.'
        'Critical diagnostics surface in-app problems that don\'t themselves trigger safety actions.',
    relatedSettings: [
      'session.notifications.on_safety_event',
      'diagnostics.mode',
    ],
  ),
  'session.notifications.on_safety_event': Help(
    key: 'session.notifications.on_safety_event',
    title: 'Safety event',
    body:
        'Fires when the safety monitor reports a condition that triggers a safety action. Three classes of events qualify:\n\n'
        '* **Unsafe weather** — rain, clouds, high wind, humidity past dew point\n'
        '* **Altitude limit** — target is below the minimum-altitude policy\n'
        '* **Guider lost** — guider stops reporting valid frames\n\n'
        'These events trigger pause/park/abort actions configured per the safety policies. This toggle controls only whether you also get a notification when one fires; the underlying action runs regardless.',
    relatedSettings: [
      'session.notifications.on_critical_diagnostic',
      'safety.policies.on_unsafe',
    ],
  ),
  'session.notifications.on_plate_solve_failed': Help(
    key: 'session.notifications.on_plate_solve_failed',
    title: 'Plate solve failed (×N)',
    body:
        'Fires after N consecutive plate-solve failures — single-try failures are common (clouds, blooming, framing issue) and not worth alerting on. '
        'The retry count N is set by the guider-retry-timeout in Safety Policies; the default is 3 tries before giving up.',
    relatedSettings: [
      'session.notifications.on_plate_solve_failed',
      'safety.policies.guider_retry_timeout',
    ],
  ),
  'session.notifications.on_disk_space_low': Help(
    key: 'session.notifications.on_disk_space_low',
    title: 'Disk space low',
    body:
        'Fires when free space on the save directory drops below ~10 GB — about one hour of LRGB capture at 4096x4096 16-bit FITS.'
        'Threshold is fixed in v0.0.1; making it configurable is a v0.1.0 enhancement.',
    relatedSettings: ['session.storage.save_directory'],
  ),

  // §37.12 Site — help on the genuinely non-obvious controls. Site name,
  // lat/lon, elevation, time zone are self-explanatory by label.
  'safety.site.use_custom_horizon': Help(
    key: 'safety.site.use_custom_horizon',
    title: 'Custom horizon polygon',
    body:
        'A measured azimuth/altitude polygon describing actual obstructions at your site (trees, roof line, neighbor\'s house). '
        'When on, target visibility checks use this polygon instead of the flat default-altitude floor — much more accurate for low-altitude targets.\n\n'
        'The polygon import + measurement workflow lives in Sky Atlas → "Capture horizon mask".'
        'Until you\'ve imported one, leave this off and the flat horizon will be used.',
    relatedSettings: [
      'safety.site.use_custom_horizon',
      'safety.site.default_horizon_altitude_deg',
    ],
  ),
  'safety.site.default_horizon_altitude_deg': Help(
    key: 'safety.site.default_horizon_altitude_deg',
    title: 'Default horizon altitude',
    body:
        'Flat altitude floor used for visibility checks when no custom horizon polygon is loaded. '
        'Targets transiting below this altitude are flagged as below-horizon by the framing assistant and skipped by the altitude-limit safety policy.\n\n'
        '20° is a sensible default for backyard sites (covers most trees + suburban roof lines); 0° turns the floor off; 30° is conservative for low-precision tracking.',
    relatedSettings: ['safety.site.use_custom_horizon'],
  ),
  'safety.site.bortle_class': Help(
    key: 'safety.site.bortle_class',
    title: 'Bortle dark-sky class',
    body:
        'A 1-9 scale rating your site\'s sky darkness, where 1 is an excellent dark site (SQM ≥21.99 mag/arcsec²) and 9 is inner-city light pollution (Milky Way invisible).\n\n'
        '* **1-2**: Excellent / true dark site\n'
        '* **3-4**: Rural / rural-suburban transition\n'
        '* **5-6**: Suburban / bright suburban (most backyard astrophotographers)\n'
        '* **7-8**: Suburban-urban transition / urban\n'
        '* **9**: Inner city — narrowband filters required\n\n'
        'Used by quality-score estimation + suggested exposure ranges in Don\'t know your class? lightpollutionmap.info or darkskies.org.',
    learnMoreUrl: 'https://en.wikipedia.org/wiki/Bortle_scale',
    relatedSettings: ['safety.site.typical_seeing_arcsec'],
  ),
  'safety.site.sqm_mag_per_arcsec2': Help(
    key: 'safety.site.sqm_mag_per_arcsec2',
    title: 'Sky brightness (SQM)',
    body:
        'A MEASURED zenith sky brightness in mag/arcsec² — from an SQM meter, '
        'an all-sky sensor, or lightpollutionmap.info for your location.\n\n'
        'When set, exposure planning (the Optimal Sub advisor and target plans) '
        'uses this instead of the coarse Bortle estimate. One Bortle class '
        'spans about half a magnitude and sky flux is exponential in '
        'magnitude, so a real reading tightens suggested exposures by up to '
        '±30%.\n\n'
        '* **~16-18**: city / inner suburbs\n'
        '* **~19-20**: suburban\n'
        '* **~21-21.7**: rural (Bortle 3-4)\n'
        '* **~21.8-22.2**: pristine dark site\n\n'
        'Leave at 0 to keep deriving the sky from the Bortle class.',
    learnMoreUrl: 'https://www.lightpollutionmap.info',
    relatedSettings: ['safety.site.bortle_class'],
  ),
  'safety.site.typical_seeing_arcsec': Help(
    key: 'safety.site.typical_seeing_arcsec',
    title: 'Typical seeing',
    body:
        'The median FWHM of star images at your site, in arcseconds — a measure of atmospheric turbulence.\n\n'
        '* **<1.0″**: Excellent (high-altitude observatory class)\n'
        '* **1.0-2.0″**: Very good\n'
        '* **2.0-3.0″**: Typical backyard\n'
        '* **3.0-4.0″**: Poor / windy / heat-cell turbulence\n'
        '* **>4.0″**: Severe — usually rules out planetary or short-FL imaging\n\n'
        'Used as the baseline for quality scoring (frames worse than 2x typical seeing get auto-rated down) and for autofocus convergence thresholds.',
    relatedSettings: ['safety.site.bortle_class'],
  ),
  'safety.site.twilight_definition': Help(
    key: 'safety.site.twilight_definition',
    title: 'Twilight definition',
    body:
        'Determines when "night" begins/ends for sequence scheduling.\n\n'
        '* **Civil (−6°)**: Sun 6° below horizon — sky is still bright; brightest planets visible. Used by sequence-start with skip-twilight off.\n'
        '* **Nautical (−12°)**: Sun 12° below — horizon visible to the eye, bright stars + globulars OK for testing or wide-field.\n'
        '* **Astronomical (−18°)**: Sun 18° below — sky is fully dark, deep-sky imaging window. Recommended default.',
  ),
  'safety.site.soft_warning_altitude_deg': Help(
    key: 'safety.site.soft_warning_altitude_deg',
    title: 'Soft warning altitude',
    body:
        'An advisory mark, distinct from the hard horizon floor. Targets below the '
        'hard floor are never scheduled; targets that never CLIMB above this soft '
        'mark tonight are still listed in Tonight\'s Sky, but tagged — at low '
        'elevation you look through more atmosphere, so seeing and extinction '
        'soften detail and colour.\n\n'
        '**Default 30°; 0 disables the advisory.** It never hides or down-ranks a '
        'target (advise, don\'t dictate).',
  ),
  'safety.site.max_sequence_runtime_min': Help(
    key: 'safety.site.max_sequence_runtime_min',
    title: 'Max sequence runtime',
    body:
        'A safety ceiling on how long one sequence run may execute, in minutes. '
        'A run still going past the ceiling is stopped gracefully — same as pressing Stop — '
        'and a notification explains why.\n\n'
        '**0 (the default) means no limit.** Useful as unattended-rig insurance: a stuck loop '
        'or an over-ambitious plan can\'t keep the mount tracking into the morning.',
  ),

  // §29.2 Filenames — both fields are non-obvious so both get help.
  'session.filenames.date_separator': Help(
    key: 'session.filenames.date_separator',
    title: 'Date separator',
    body:
        'Determines how `\$\$DATETIME\$\$` and `\$\$DATEMINUS12\$\$` tokens render in output paths.\n\n'
        '* **`/` forward slash**: dates like `2026-05-29` become actual subdirectories. Cleanest organization, plays well with file managers.\n'
        '* **`_` underscore**: dates stay inline (`2026-05-29_M31_L_60s.fits`). Flat output; good if you sort + organize externally later.\n'
        '* **`-` dash**: same as underscore, but uses `-` between date components. Maximally Windows-safe (no characters reserved by NTFS).',
    relatedSettings: ['session.storage.filename_template'],
  ),
  'session.filenames.observer': Help(
    key: 'session.filenames.observer',
    title: 'Observer',
    body:
        'Your name, written into every frame as the OBSERVER header. '
        'Archives, stacking reports and shared data keep the credit with the '
        'image. Leave it empty and the header is simply omitted.',
    relatedSettings: ['session.filenames.telescope'],
  ),
  'session.filenames.telescope': Help(
    key: 'session.filenames.telescope',
    title: 'Telescope',
    body:
        'What you shoot through — "RedCat 51", "EdgeHD 8" — written into '
        'every frame as the TELESCOP header. The focal length and aperture '
        'numbers come from Optics; this is the human name. Leave it empty '
        'and the header is simply omitted.',
    relatedSettings: ['imaging.optics.focal_length_mm'],
  ),
  'session.filenames.folders': Help(
    key: 'session.filenames.folders',
    title: 'Folders',
    body:
        'How frames are organized on the disk.\n\n'
        '* **By night, then frame type** — one folder per night, lights and '
        'calibration separated. The default.\n'
        '* **By night, then target** — nights first, then what you shot.\n'
        '* **By target, then night** — a folder per project, nights inside.\n'
        '* **No folders** — everything flat in the save folder.\n\n'
        'A frame taken after midnight files under the evening its night '
        'started, so one session never splits across two folders.',
    relatedSettings: ['session.storage.filename_template'],
  ),
  'session.filenames.header_identity': Help(
    key: 'session.filenames.header_identity',
    title: 'Header: who took it',
    body:
        'Writes the observer and telescope names into every frame '
        '(OBSERVER, TELESCOP). Off, and shared frames carry no name.',
    relatedSettings: [
      'session.filenames.observer',
      'session.filenames.telescope',
    ],
  ),
  'session.filenames.header_site': Help(
    key: 'session.filenames.header_site',
    title: 'Header: your site',
    body:
        'Writes your observing location into every frame (SITELAT, '
        'SITELONG, SITEELEV). Useful for archives and airmass math — but a '
        'frame you post publicly then carries your exact coordinates. Turn '
        'this off if that matters to you; everything else keeps working.',
    relatedSettings: ['safety.site.latitude_deg'],
    keywords: ['privacy', 'location', 'coordinates', 'share'],
  ),
  'session.filenames.header_optics': Help(
    key: 'session.filenames.header_optics',
    title: 'Header: optics',
    body:
        'Writes focal length, aperture and pixel size into every frame '
        '(FOCALLEN, APTDIA, XPIXSZ/YPIXSZ). Plate solvers and stacking '
        'reports read these — leave it on unless you have a reason not to.',
    relatedSettings: ['session.filenames.telescope'],
  ),
  'session.filenames.header_temperature': Help(
    key: 'session.filenames.header_temperature',
    title: 'Header: sensor temperature',
    body:
        'Writes the sensor temperature and cooler set point into every '
        'frame (CCD-TEMP, SET-TEMP) — what lets calibration match darks to '
        'lights by temperature.',
    relatedSettings: [],
  ),
  'session.filenames.header_weather': Help(
    key: 'session.filenames.header_weather',
    title: 'Header: sky & weather',
    body:
        'Writes the sky the frame was taken under (SQM sky quality, '
        'ambient temperature, humidity, dew point) whenever a weather '
        'station is connected. Great for judging nights against each other '
        'later.',
    relatedSettings: [],
  ),
  'session.filenames.header_ephemeris': Help(
    key: 'session.filenames.header_ephemeris',
    title: 'Header: sun & moon',
    body:
        'Writes where the sun and moon were at the moment of capture '
        '(SUNALT, MOONALT, MOONILL, MOONPHSE) — the two numbers that explain '
        'a bright background or a gradient better than any note. Computed '
        'from your site coordinates, so if you turn off the Site header for '
        'privacy, consider this one too: celestial geometry at a timestamp '
        'narrows down where a frame was taken.',
    relatedSettings: ['session.filenames.header_site'],
    keywords: ['moon', 'sun', 'phase', 'illumination', 'altitude', 'gradient'],
  ),
  'session.filenames.compress_darks_and_bias': Help(
    key: 'session.filenames.compress_darks_and_bias',
    title: 'Compress bias + dark frames',
    body:
        'Bias and dark frames are dominated by sensor noise (mostly zero in bias, slowly-varying in darks) and compress losslessly very well — typically 8-15x with RICE. '
        'When on, calibration frames get RICE compression regardless of the global compression setting in Storage.'
        'When off, calibration frames respect the global compression setting.\n\n'
        'Recommended on — calibration frames are bulky (one library can take 5+ GB) and benefit far more from compression than light frames.',
    relatedSettings: ['session.storage.compression'],
  ),

  // §52.1 connection lifecycle — one shared help entry covers all 10 device-
  // type auto-connect toggles. Per-device side effects are listed in the body.
  'eq.auto_connect_on_boot': Help(
    key: 'eq.auto_connect_on_boot',
    title: 'Auto-connect on boot',
    body:
        'Whether to automatically open the Alpaca connection to this device when Ara starts.\n\n'
        '**Defaults split by side-effect risk:**\n\n'
        '*Connect-by-default* (minor or no actuation):\n'
        '* Camera — USB link power-up only\n'
        '* Mount — sidereal tracking comes on\n'
        '* Focuser, rotator — position read on connect, no movement\n'
        '* Filter wheel — most drivers reposition to last-known slot on connect (driver-dependent). If it matters which filter is in beam at startup, leave this off and connect manually.\n'
        '* Flat panel (CoverCalibrator) — does not change cover position\n'
        '* Safety monitor — recommended on for unattended observatories\n\n'
        '*Manual-connect by default* (driver may actuate hardware on connect):\n'
        '* Guider — starts the OpenAstro Guider client process\n'
        '* Dome — some drivers move shutter or rotate to home on connect\n'
        '* Weather station — keeps the polling loop quiet until you opt in\n\n'
        'Override per device based on your hardware\'s behaviour.',
  ),

  // §37.11 Autofocus — help on the genuinely non-obvious controls.
  'img.autofocus.method': Help(
    key: 'img.autofocus.method',
    title: 'Autofocus method',
    body:
        '* **HFR V-curve** (recommended): samples N positions across the focuser range, computes Half-Flux Radius at each, fits a V-shaped parabola, and picks the position at the V\'s minimum. Robust for CMOS + small refractors.\n'
        '* **Brightest-star HFR**: same algorithm but uses only the single brightest star in the frame (vs the median across all detected stars). Faster on sparse fields but noise-sensitive.\n'
        '* **FWHM (Gaussian fit)**: fits a 2D Gaussian to star profiles. More accurate at the focus point but slower; benefits from longer exposures.',
    relatedSettings: ['img.autofocus.steps', 'img.autofocus.step_size'],
  ),
  'img.autofocus.telescope_type': Help(
    key: 'img.autofocus.telescope_type',
    title: 'Telescope type',
    body:
        'Out-of-focus stars look different per optical design, and Smart Focus exploits that:\n'
        '* **Refractor** — no central obstruction; defocus broadens the star (FWHM, peak brightness).\n'
        '* **SCT / Maksutov / RC** — defocus makes donuts: a bright ring around the secondary-mirror shadow. The donut\'s diameter grows linearly with defocus — a very direct distance ruler.\n'
        '* **Newtonian** — donuts too (plus spider-vane spikes).\n'
        '* **Other / unknown** — assume nothing: the universal HFR behaviour, no per-design upgrades.\n\n'
        'With a declared design, autofocus can also learn which *side* of focus you\'re on from a single frame (your rig\'s aberration signature, learned during calibration) — often saving an exposure per run. A wrong or missing choice never breaks focusing; it only skips these upgrades.',
    relatedSettings: ['img.autofocus.method', 'img.autofocus.steps'],
  ),
  'img.autofocus.steps': Help(
    key: 'img.autofocus.steps',
    title: 'Number of AF steps',
    body:
        'How many focuser positions to sample around the current position. The V-curve fit needs at least 3 points to be meaningful; 7-9 is the sweet spot for most setups (good fit + reasonable run time, ~5-10 min total).\n\n'
        'More steps catch a flatter HFR-vs-position curve more accurately but multiply the AF run time. Use 11-15 only if your CFZ is very small (long focal length + fast f-ratio) or you\'re tuning the routine.',
    relatedSettings: [
      'img.autofocus.step_size',
      'img.autofocus.exposure_seconds',
    ],
  ),
  'img.autofocus.step_size': Help(
    key: 'img.autofocus.step_size',
    title: 'AF step size',
    body:
        'Distance between sample positions, in focuser native steps. Should span **3-5x the critical focus zone (CFZ)** total range — too small and the V-curve doesn\'t have enough vertical range to fit; too large and you sample outside the regime where the curve is parabolic.\n\n'
        'CFZ ≈ 2 × λ × N² where λ is wavelength (~0.55µm for green) and N is the f-ratio. f/4 → CFZ ~17µm; f/8 → CFZ ~70µm. Convert µm to focuser steps via your focuser\'s steps-per-µm.\n\n'
        'When in doubt: start with the default (50), run a focus, look at the V-curve. Flat curve → increase step size; sharp narrow V → decrease.',
    relatedSettings: ['img.autofocus.steps'],
  ),
  'img.autofocus.trigger_temp_delta_c': Help(
    key: 'img.autofocus.trigger_temp_delta_c',
    title: 'Temperature-trigger threshold',
    body:
        'Most focuser tubes expand/contract with temperature — a 5°C overnight drop can move best-focus by 50-100 focuser steps. This setting triggers an AF run when the focuser-reported temperature has changed by this many °C since the last run.\n\n'
        '2.0°C is a sensible default for most aluminum/carbon-fiber tubes; tune lower (1.0-1.5°C) for very thermally sensitive setups (fast newts, big aperture refractors). 0 disables the temperature trigger.',
    relatedSettings: [
      'img.autofocus.trigger_hfr_drift_pct',
      'img.autofocus.every_n_hours',
    ],
  ),
  'img.autofocus.trigger_hfr_drift_pct': Help(
    key: 'img.autofocus.trigger_hfr_drift_pct',
    title: 'HFR-drift trigger',
    body:
        'Triggers an AF run when the median HFR of recent light frames has worsened by this percentage compared to the post-AF baseline. Catches focus drift between scheduled AF runs — temperature changes are the usual cause but seeing degradation or mechanical shifts also bump HFR.\n\n'
        '15% is a balanced default — clear enough to detect real drift, loose enough to ignore single bad frames. Lower the threshold (5-10%) for narrowband / long exposures where bad frames are expensive.',
    relatedSettings: [
      'img.autofocus.trigger_temp_delta_c',
      'img.autofocus.every_n_hours',
    ],
  ),
  'img.autofocus.every_n_hours': Help(
    key: 'img.autofocus.every_n_hours',
    title: 'Periodic AF trigger',
    body:
        'Force an AF run every N hours regardless of temperature or HFR. Catches slow drift that doesn\'t cross either of the other triggers (e.g. a gradual mechanical settling on first-night-out setups).\n\n'
        '2 hours is a safe interval for most sessions. Set to 0 to disable the time-based trigger and rely purely on temperature + HFR triggers.',
    relatedSettings: [
      'img.autofocus.trigger_temp_delta_c',
      'img.autofocus.trigger_hfr_drift_pct',
    ],
  ),

  // §37.10 Plate Solving — help on the non-obvious controls.
  'img.platesolve.engine': Help(
    key: 'img.platesolve.engine',
    title: 'Plate-solving engine',
    body:
        '* **ASTAP** — the only supported engine: fast, accurate, local, installed alongside Ara plus a star index.\n'
        '* A profile still carrying **astrometry.net** or **PlateSolve 2** (e.g. imported from NINA) solves with ASTAP — Ara ships local solvers only and logs the substitution once per run. Switch the setting to ASTAP to clear the notice.',
    relatedSettings: [
      'img.platesolve.path_or_endpoint',
      'img.platesolve.search_radius_deg',
    ],
  ),
  'img.platesolve.search_radius_deg': Help(
    key: 'img.platesolve.search_radius_deg',
    title: 'Search radius',
    body:
        'How far from the hinted RA/Dec position the solver searches for a match.\n\n'
        '* **Small radius (5-15°)**: fast, but solve fails if your mount pointing is off or polar alignment is wrong.\n'
        '* **30° (default)**: tolerant of typical mount-pointing error.\n'
        '* **>90°**: effectively blind — slow but always finds a solution.\n\n'
        'Combine with `Use blind solve as fallback` for a fast-then-slow strategy.',
    relatedSettings: ['img.platesolve.use_blind_fallback'],
  ),
  'img.platesolve.downsample_factor': Help(
    key: 'img.platesolve.downsample_factor',
    title: 'Downsample factor',
    body:
        'Plate solvers don\'t need full resolution to find a match — they only need enough pixels to detect stars. Downsampling 2x quarters the input area and is roughly 4x faster, with negligible accuracy hit on most setups.\n\n'
        'Bump to 3-4 for very large sensors (>30 MP). Drop to 1 if you have a small sensor (<5 MP) and solves are unreliable.',
    relatedSettings: ['img.platesolve.timeout_seconds'],
  ),
  'img.platesolve.use_blind_fallback': Help(
    key: 'img.platesolve.use_blind_fallback',
    title: 'Blind-solve fallback',
    body:
        'If a hint-based solve times out (mount pointing way off, polar alignment wrong, hint coordinates stale), retry the same frame with no hint — let the solver search the entire sky.\n\n'
        'Blind solves are slower (often 30-60s) but rescue most bad-pointing situations. Recommended on except for very large sensors where blind solves can run out of timeout.',
    relatedSettings: ['img.platesolve.search_radius_deg'],
  ),
  'img.platesolve.max_iterations': Help(
    key: 'img.platesolve.max_iterations',
    title: 'Max centering iterations',
    body:
        'Center-after-slew loops solve→slew→solve→slew until the target is within tolerance OR this many iterations have run.\n\n'
        '5 is enough for most mounts (each iteration typically halves the pointing error). Bump to 8-10 for cone-error setups where pointing improves slowly; drop to 2-3 for fast precision mounts that converge in one pass.',
    relatedSettings: ['img.platesolve.convergence_tolerance_arcsec'],
  ),
  'img.platesolve.convergence_tolerance_arcsec': Help(
    key: 'img.platesolve.convergence_tolerance_arcsec',
    title: 'Convergence tolerance',
    body:
        'How close to dead-center the target must be before centering stops. 60″ (1 arc-minute) is a good default for typical setups — tighter than the guider can correct, looser than the human eye can notice.\n\n'
        'Tighten to 30″ for narrowband mosaics where panel alignment matters; loosen to 120″ for wide-field RGB where 2′ is well within frame.',
    relatedSettings: ['img.platesolve.max_iterations'],
  ),

  // §63 OpenAstro Guider — help on the genuinely non-obvious controls. Host/port/profile
  // are self-explanatory; settle-time + force-calibration get help because
  // their behaviour interacts with §35 + §38 in non-obvious ways.
  'eq.guider.dither_pixels': Help(
    key: 'eq.guider.dither_pixels',
    title: 'Dither amplitude',
    body:
        'How many guide-camera pixels to shift the mount between exposures. Larger amplitudes randomize fixed-pattern noise more aggressively but mean longer settle times.\n\n'
        '* **3-5 px**: conservative. Settles fast on most mounts.\n'
        '* **5-10 px**: aggressive. Better noise reduction in stacks; needs a stable mount + good RMS.\n'
        '* **>10 px**: usually overkill; can push the guide star off the chip on small guide scopes.',
    relatedSettings: [
      'eq.guider.dither_every_n_frames',
      'eq.guider.settle_pixels',
    ],
  ),
  'eq.guider.settle_pixels': Help(
    key: 'eq.guider.settle_pixels',
    title: 'Settle threshold',
    body:
        'Once a dither completes, OpenAstro Guider considers the guider re-settled when guide-RMS error stays below this many pixels for `settle_time` seconds.\n\n'
        'Tight thresholds (0.5-1.0 px) catch the last bit of motion but waste time on mounts that hover at 1 px RMS — they\'ll never converge.\n'
        '1.5 px (default) is a sensible middle ground. Loosen to 2-3 px for slower mounts; tighten only if your guide RMS routinely sits below 1 px.',
    relatedSettings: [
      'eq.guider.settle_time_sec',
      'eq.guider.settle_timeout_sec',
    ],
  ),
  'eq.guider.settle_timeout_sec': Help(
    key: 'eq.guider.settle_timeout_sec',
    title: 'Settle timeout',
    body:
        'Hard maximum on settle wait. If the threshold isn\'t met by this point, exposure resumes anyway. The plate-solve-failed notification (and guider-lost retry budget) take over from here if guide quality stays bad.\n\n'
        '60s is the default. Bump to 120-180s on slow mounts; drop to 30s if you\'d rather skip frames than burn time on a stuck guider.',
    relatedSettings: [
      'eq.guider.settle_pixels',
      'safety.policies.guider_retry_timeout',
    ],
  ),
  'eq.guider.force_calibration_each_session': Help(
    key: 'eq.guider.force_calibration_each_session',
    title: 'Force calibration each session',
    body:
        'OpenAstro Guider caches calibration data (guide-pulse direction, ratio, backlash) and reuses it across sessions by default. Forcing a fresh calibration each session is safer if your guide-scope orientation can shift overnight (loose dovetail, scope swap, etc) but adds 2-5 min to every startup.\n\n'
        'Recommended **off** for permanent setups (observatory rig); **on** for portable setups (grab-and-go scope, traveling kit).',
    relatedSettings: ['safety.policies.meridian_recal_guider'],
  ),
  // §63.5 guider-engine config — pushed to the guider daemon on connect.
  'eq.guider.guide_focal_length': Help(
    key: 'eq.guider.guide_focal_length',
    title: 'Guide focal length',
    body:
        'Focal length of the guide scope (mm). Combined with the guide-camera pixel size it sets the guider\'s arcsec/pixel scale, which OpenAstro Guider uses for star-mass thresholds and the guiding graph.\n\n'
        'Leave **0** to keep whatever the OpenAstro Guider guide profile already has. Set it to push your value on connect.',
    relatedSettings: ['eq.guider.guide_pixel_size'],
  ),
  'eq.guider.guide_pixel_size': Help(
    key: 'eq.guider.guide_pixel_size',
    title: 'Guide pixel size',
    body:
        'Pixel size of the guide camera (µm). With the guide focal length this gives the guider\'s arcsec/pixel scale.\n\n'
        'Leave **0** to keep the OpenAstro Guider guide profile default.',
    relatedSettings: ['eq.guider.guide_focal_length'],
  ),
  'eq.guider.ra_aggressiveness': Help(
    key: 'eq.guider.ra_aggressiveness',
    title: 'RA aggressiveness',
    body:
        'Fraction (0–1) of each measured RA error that OpenAstro Guider corrects per cycle. Lower values guide more gently (less prone to oscillation / chasing seeing); higher values track real drift faster.\n\n'
        '**0.7** is a good default. Drop toward 0.5 if guiding oscillates; raise toward 0.9 only on a stiff, well-behaved mount.',
    relatedSettings: ['eq.guider.dec_aggressiveness', 'eq.guider.minimum_move'],
  ),
  'eq.guider.dec_aggressiveness': Help(
    key: 'eq.guider.dec_aggressiveness',
    title: 'Dec aggressiveness',
    body:
        'Fraction (0–1) of each measured Dec error OpenAstro Guider corrects per cycle. Same idea as RA aggressiveness; Dec is often run a touch lower because of backlash near direction reversals.',
    relatedSettings: [
      'eq.guider.ra_aggressiveness',
      'eq.guider.dec_guide_mode',
    ],
  ),
  'eq.guider.minimum_move': Help(
    key: 'eq.guider.minimum_move',
    title: 'Minimum move',
    body:
        'Smallest error (in guide pixels) OpenAstro Guider will react to. Errors below this are ignored, so the mount doesn\'t chase seeing noise.\n\n'
        '**~0.15 px** is typical. Raise it in poor seeing to calm the corrections; lower it only with a very stable mount + sky.',
    relatedSettings: ['eq.guider.ra_aggressiveness'],
  ),
  'eq.guider.dec_guide_mode': Help(
    key: 'eq.guider.dec_guide_mode',
    title: 'Dec guide mode',
    body:
        'How OpenAstro Guider corrects declination:\n\n'
        '* **Auto**: correct in whichever direction the error appears (leaves OpenAstro Guider\'s own setting alone — ARA won\'t push Auto).\n'
        '* **North / South**: only ever push that direction. Useful on mounts with bad Dec backlash — pick the uphill side so backlash is always taken up.\n'
        '* **Off**: no Dec guiding (RA only).',
    relatedSettings: ['eq.guider.dec_aggressiveness'],
  ),

  // §37.4 Filter Wheel slot labels.
  'eq.filterwheel.slot_labels': Help(
    key: 'eq.filterwheel.slot_labels',
    title: 'Filter wheel slot labels',
    body:
        'Names you give each physical filter slot. They flow through to several places:\n\n'
        '* **`\$\$FILTER\$\$` filename token** — e.g. `M31_L_60s.fits` uses the active slot\'s label.\n'
        '* **FITS-header `FILTER` keyword** — read by downstream stacking tools (DSS, Siril, PixInsight) to group frames per filter.\n'
        '* **Sequence per-filter exposure blocks** — sequences reference filters by label, so labels here must match the labels used in your sequence templates.\n'
        '* **calibration-set indexing** — matching darks and flats are found by filter.\n\n'
        '**Conventions** (not enforced — use whatever you like):\n'
        '* Mono LRGB: `L`, `R`, `G`, `B`\n'
        '* Narrowband: `Hα` (or `Ha`), `OIII`, `SII`\n'
        '* Photometric: `U`, `B`, `V`, `R`, `I` (Johnson) or `u`, `g`, `r`, `i`, `z` (SDSS)\n\n'
        'Leave a slot blank if it\'s unused or unloaded.',
    relatedSettings: ['session.storage.filename_template'],
  ),

  // ── Camera electronics (img.electronics) — §50 sensor model ─────────────
  'img.electronics.sensor_name': Help(
    key: 'img.electronics.sensor_name',
    title: 'Sensor name',
    body:
        'The sensor model inside your camera (e.g. IMX571, IMX533). Purely a label — it does not change behavior — '
        'but it helps you confirm the electronic numbers below match the sensor they were measured for, '
        'since read noise and full-well figures published by manufacturers are always quoted per sensor model.',
  ),
  'img.electronics.read_noise': Help(
    key: 'img.electronics.read_noise',
    title: 'Read noise',
    body:
        'How much random noise (in electrons RMS) the camera adds every time it reads out a frame, at the gain below. '
        'Used by the exposure planner: with high read noise you need longer subs so sky background swamps the readout noise; '
        'with low read noise (modern CMOS at high gain) short subs cost almost nothing. '
        'Find it on the manufacturer\'s gain chart — read it off at the gain you actually image with.',
  ),
  'img.electronics.full_well': Help(
    key: 'img.electronics.full_well',
    title: 'Full-well capacity',
    body:
        'How many electrons one pixel can hold before it saturates (clips to pure white), at the gain below. '
        'Determines how bright a star can get before it burns out: higher full-well = more dynamic range in one sub. '
        'From the manufacturer\'s chart — it drops as gain rises, which is the trade-off against read noise.',
  ),
  'img.electronics.conversion_gain': Help(
    key: 'img.electronics.conversion_gain',
    title: 'Conversion gain',
    body:
        'How many electrons one ADU (one count in the image file) represents, at the gain setting below. '
        'This is the bridge between pixel values in your FITS files and real photon counts — the planner needs it '
        'to convert measured sky background into electrons per second. On the manufacturer\'s chart it\'s usually labeled "gain (e-/ADU)".',
  ),
  'img.electronics.gain_reference': Help(
    key: 'img.electronics.gain_reference',
    title: 'Gain these values apply at',
    body:
        'The camera gain setting the three numbers above were measured at. Sensor electronics change with gain — '
        'read noise falls, full-well shrinks, conversion gain scales — so the numbers are only valid as a set. '
        'Enter the gain you normally image with, and read all three values off the manufacturer\'s charts at that same gain.',
  ),
  'img.electronics.peak_qe': Help(
    key: 'img.electronics.peak_qe',
    title: 'Peak quantum efficiency',
    body:
        'The fraction of photons hitting the sensor that actually get detected, at the sensor\'s best wavelength (0–1, so 0.8 = 80%). '
        'Used when estimating how much signal a target will deliver in a given exposure. '
        'Manufacturers quote it as a percentage — a modern back-illuminated CMOS is typically 0.8–0.9.',
  ),

  // ── Flat panel (eq.flat) ────────────────────────────────────────────────
  'eq.flat.auto_brightness_target': Help(
    key: 'eq.flat.auto_brightness_target',
    title: 'Auto-brightness target',
    body:
        'The average pixel level (in ADU) the flat-capture routine aims for when it auto-adjusts the panel brightness '
        'or exposure time. Roughly half of the camera\'s full range (e.g. ~32000 for a 16-bit camera) keeps flats '
        'well-exposed: bright enough to have low noise, far enough from saturation to stay linear. '
        'This value is shared with the calibration system — edit it under Settings → Safety → Policies.',
  ),
  'eq.flat.target_tolerance': Help(
    key: 'eq.flat.target_tolerance',
    title: 'Target tolerance',
    body:
        'How far (in percent) a flat\'s average level may drift from the target before the routine re-adjusts brightness '
        'or exposure. Tighter tolerance = more consistent flats but more adjustment iterations before capture starts. '
        'Shared with the calibration system — edit under Settings → Safety → Policies.',
  ),
  'eq.flat.frames_per_filter': Help(
    key: 'eq.flat.frames_per_filter',
    title: 'Frames per filter',
    body:
        'How many flat frames to capture for each filter. Flats are averaged (or median-stacked) into a master flat, '
        'and more frames means less noise injected by calibration — 20–50 is typical. '
        'Shared with the calibration system — edit under Settings → Safety → Policies.',
  ),

  // ── Guider (eq.guider) additions ────────────────────────────────────────
  'eq.guider.host': Help(
    key: 'eq.guider.host',
    title: 'Guider host',
    body:
        'Where the guiding software (PHD2 or the built-in OpenAstro Guider) is running. '
        '"localhost" means it runs on the rig computer itself — the normal setup. '
        'Only change this if you deliberately run the guider on a different machine on your network.',
  ),
  'eq.guider.port': Help(
    key: 'eq.guider.port',
    title: 'Guider port',
    body:
        'The TCP port of the guider\'s control interface. PHD2 and OpenAstro Guider both listen on 4400 by default. '
        'Only change it if you changed the port in the guider itself.',
  ),
  'eq.guider.profile': Help(
    key: 'eq.guider.profile',
    title: 'Guider profile',
    body:
        'Which equipment profile the guider should load when Ara connects it. Leave empty to use the guider\'s '
        'current/default profile. Profiles in the guider hold its camera + mount selection and calibration data, '
        'so pointing at the wrong one connects the wrong hardware.',
  ),
  'eq.guider.dither_every_n': Help(
    key: 'eq.guider.dither_every_n',
    title: 'Dither every N frames',
    body:
        'After every N captured light frames, the guider nudges the telescope a few pixels in a random direction '
        'before the next exposure. When you later stack the frames, hot pixels and fixed-pattern noise land in different '
        'places on every sub and average away — dithering is the single cheapest quality upgrade in stacking. '
        '1 dithers after every frame (best quality, slowest); 3–5 is a good balance; 0 disables dithering.',
  ),
  'eq.guider.settle_time': Help(
    key: 'eq.guider.settle_time',
    title: 'Settle time',
    body:
        'After a dither or a new guide start, guiding must stay below the settle threshold for this many seconds '
        'before the next exposure begins. This stops a frame from starting while the mount is still recovering from the nudge. '
        'Too short risks trailed stars at the top of a frame; too long wastes imaging time.',
  ),
  'eq.guider.recal_on_flip': Help(
    key: 'eq.guider.recal_on_flip',
    title: 'Re-calibrate on meridian flip',
    body:
        'After a meridian flip the telescope is on the other side of the mount, so the guide camera\'s sense of '
        '"which way is west" is mirrored. Most modern setups can just flip the calibration data automatically, but some '
        'mount/guider combinations guide badly after a flip unless the guider re-runs a full calibration. '
        'Enable this if your first post-flip frames show guiding running away in Dec.',
  ),
  'eq.guider.setup_type': Help(
    key: 'eq.guider.setup_type',
    title: 'Guide setup',
    body:
        'How your guide camera sees the sky: through a separate guide scope riding on the main telescope, '
        'or through an off-axis guider (OAG) that picks off a corner of the main telescope\'s light path. '
        'This determines which focal length applies to guide pixels: a guide scope uses its own short focal length, '
        'an OAG uses the main telescope\'s. Getting it wrong makes every guiding error number meaningless.',
  ),
  'eq.guider.guide_camera': Help(
    key: 'eq.guider.guide_camera',
    title: 'Guide camera',
    body:
        'Which camera the guider uses to watch a guide star. This list comes from the guider\'s own equipment '
        'detection — pick your small guide camera here, never the main imaging camera.',
  ),
  'eq.guider.guide_camera_id': Help(
    key: 'eq.guider.guide_camera_id',
    title: 'Guide camera ID',
    body:
        'The device identifier the guider uses to tell apart multiple cameras of the same model (e.g. two ZWO cameras '
        'on one rig). Usually filled automatically when you pick the camera; only edit it by hand if the guider keeps '
        'grabbing the wrong one of two identical cameras.',
  ),
  'eq.guider.guide_mount': Help(
    key: 'eq.guider.guide_mount',
    title: 'Guide mount',
    body:
        'The connection the guider uses to send correction pulses to the mount. "On-camera" means pulses travel through '
        'the ST-4 cable from guide camera to mount; an ASCOM/INDI mount entry means pulses go over the mount\'s own '
        'driver connection (generally more reliable and lets the guider know the pointing position).',
  ),
  'eq.guider.aux_mount': Help(
    key: 'eq.guider.aux_mount',
    title: 'Aux mount',
    body:
        'An optional second mount connection used only to READ pointing information (where the telescope is aimed), '
        'while guide pulses still go through the guide-mount connection above. Useful with ST-4 guiding, where the pulse '
        'cable carries no position data: the aux connection lets the guider auto-flip calibration after a meridian flip.',
  ),
  'eq.guider.rotator': Help(
    key: 'eq.guider.rotator',
    title: 'Rotator',
    body:
        'If your imaging train has a camera rotator, telling the guider about it lets guiding survive rotation: '
        'when the camera angle changes, the guider rotates its calibration data instead of guiding in the wrong direction. '
        'Leave unset if you have no rotator.',
  ),
  'eq.guider.alpaca_host': Help(
    key: 'eq.guider.alpaca_host',
    title: 'Alpaca host',
    body:
        'The address of the Alpaca bridge the guider uses to reach cameras and mounts over the network. '
        'On a standard rig this is the rig computer itself (localhost). Only change it if your devices are served '
        'by an Alpaca server on a different machine.',
  ),
  'eq.guider.alpaca_port': Help(
    key: 'eq.guider.alpaca_port',
    title: 'Alpaca port',
    body:
        'The port of the Alpaca bridge (default 6800 on an OpenAstro rig, 11111 for a stock ASCOM Remote server). '
        'Must match what the bridge actually listens on — see its own web setup page.',
  ),

  // ── Safety monitor (eq.safety) ──────────────────────────────────────────
  'eq.safety.on_unsafe': Help(
    key: 'eq.safety.on_unsafe',
    title: 'On unsafe',
    body:
        'What happens the moment the safety monitor reports UNSAFE (rain, clouds, power problem — whatever the device '
        'watches): typically pause the sequence, stop exposures, park the mount and close covers. This is the reaction '
        'chain that protects the equipment when you\'re asleep — configure the details under Settings → Safety → Policies.',
  ),
  'eq.safety.auto_resume': Help(
    key: 'eq.safety.auto_resume',
    title: 'Auto-resume when safe',
    body:
        'Whether the sequence starts back up on its own after the safety monitor returns to SAFE (e.g. the clouds pass). '
        'With this off, an unsafe event ends your night until you intervene. With it on, the resume delay below is honored '
        'first so a flickering sensor can\'t bounce the roof open and closed.',
  ),
  'eq.safety.resume_delay': Help(
    key: 'eq.safety.resume_delay',
    title: 'Resume delay',
    body:
        'How many minutes of continuous SAFE the monitor must report before an auto-resume actually happens. '
        'Weather rarely clears instantly — a passing gap in clouds can read SAFE for a minute and then rain again. '
        '10–15 minutes is a sensible floor for rain sensors.',
  ),

  // ── Weather (eq.weather) ────────────────────────────────────────────────
  'eq.weather.triggers_safety': Help(
    key: 'eq.weather.triggers_safety',
    title: 'Weather triggers safety',
    body:
        'When enabled, bad weather readings (wind, humidity, dew point below) are treated exactly like a safety-monitor '
        'UNSAFE: the sequence pauses and the protective reactions run. When disabled, weather is displayed for your '
        'information only and never interrupts a session.',
  ),
  'eq.weather.wind_max': Help(
    key: 'eq.weather.wind_max',
    title: 'Wind speed max',
    body:
        'Above this wind speed the conditions count as unsafe (if weather triggers safety). Wind shakes the telescope '
        'and ruins subexposures long before it endangers equipment — typical limits are 20–40 km/h depending on how '
        'sheltered the site is and how big a sail your telescope tube is.',
  ),
  'eq.weather.humidity_max': Help(
    key: 'eq.weather.humidity_max',
    title: 'Humidity max',
    body:
        'Above this relative humidity the conditions count as unsafe (if weather triggers safety). Very high humidity '
        'means imminent dew on optics and electronics; 85–95% is a common threshold, lower if you have no dew heaters.',
  ),
  'eq.weather.dewpoint_margin': Help(
    key: 'eq.weather.dewpoint_margin',
    title: 'Dew-point margin',
    body:
        'The minimum gap between air temperature and dew point before conditions count as unsafe. When the two meet, '
        'water condenses on everything — corrector plates first. A 2–3 °C margin gives dew heaters time to keep up; '
        'increase it if your optics dew over regularly.',
  ),

  // ── Filter set (eq.filterset) ───────────────────────────────────────────
  'eq.filterset.name': Help(
    key: 'eq.filterset.name',
    title: 'Filter name',
    body:
        'The display name for this filter wheel slot (L, R, G, B, Ha, OIII, SII…). Used everywhere a frame is labeled: '
        'file names, the library, planning. Keep it short and consistent — the name is written into every FITS header, '
        'and stacking software groups frames by it.',
  ),
  'eq.filterset.kind': Help(
    key: 'eq.filterset.kind',
    title: 'Filter kind',
    body:
        'What type of filter this is: broadband (L/R/G/B — passes wide swaths of the spectrum) or narrowband '
        '(Ha/OIII/SII — passes a few nanometers around one emission line). The planner uses the kind to model how '
        'moonlight affects each filter: narrowband shrugs off moonlight that would wash out broadband completely.',
  ),
  'eq.filterset.bandwidth': Help(
    key: 'eq.filterset.bandwidth',
    title: 'Bandwidth',
    body:
        'For narrowband filters: how wide the passband is, in nanometers (a "7nm Ha" filter → 7). Tighter bandwidth '
        'means darker sky background and better moon resistance, at the cost of needing longer exposures. '
        'Leave 0 to use a sensible default for the filter\'s kind.',
  ),

  // ── Autofocus additions (img.autofocus) ─────────────────────────────────
  'img.autofocus.exposure': Help(
    key: 'img.autofocus.exposure',
    title: 'AF exposure time',
    body:
        'The exposure length for each autofocus test frame. It needs enough stars for a reliable HFR measurement but '
        'no longer — the whole focus run takes number-of-steps × this. 2–5 s works for most broadband setups; '
        'narrowband filters may need 5–15 s to show enough stars.',
  ),
  'img.autofocus.binning': Help(
    key: 'img.autofocus.binning',
    title: 'AF binning',
    body:
        'Pixel binning for autofocus frames. Binning 2 combines each 2×2 block of pixels, quadrupling sensitivity and '
        'quartering download time — focus doesn\'t need full resolution, it needs star brightness, so 2 is a good default.',
  ),
  'img.autofocus.filter': Help(
    key: 'img.autofocus.filter',
    title: 'Filter for AF',
    body:
        'Which filter to autofocus through. Focusing through a bright broadband filter (typically L) is faster and more '
        'reliable than through narrowband, and per-filter focus offsets then adjust for the others. '
        'Leave empty to focus through whatever filter is currently in place.',
  ),

  // ── Imaging defaults additions (imaging.defaults) ───────────────────────
  'imaging.defaults.exposure': Help(
    key: 'imaging.defaults.exposure',
    title: 'Default exposure',
    body:
        'The exposure length pre-filled for new captures and sequence rows. Just a starting point — every sequence can '
        'override it. The exposure planner can compute an optimal length from your sky brightness and camera electronics.',
  ),
  'imaging.defaults.frame_type': Help(
    key: 'imaging.defaults.frame_type',
    title: 'Default frame type',
    body:
        'What kind of frame a plain capture takes by default: LIGHT (the actual image of the sky), DARK (shutter closed, '
        'for calibrating sensor noise), FLAT (evenly lit, for calibrating dust and vignetting) or BIAS (shortest possible '
        'exposure, for readout signature). The type is stamped into the FITS header and drives how the library and '
        'stacking software treat the frame.',
  ),
  'imaging.defaults.cooling_target': Help(
    key: 'imaging.defaults.cooling_target',
    title: 'Cooling target temperature',
    body:
        'The sensor temperature the camera cooler drives to at session start. Colder = less thermal noise, but the '
        'cooler must be able to HOLD it all night — pick a target ~25–30 °C below your warmest ambient, and keep it '
        'consistent across nights so your dark library matches your lights.',
  ),

  // ── Plate solving additions (img.platesolve) ────────────────────────────
  'img.platesolve.solver_path': Help(
    key: 'img.platesolve.solver_path',
    title: 'Where to find the solver',
    body:
        'The path to the astrometry solver executable on the rig computer. The default is where the OpenAstro Ara '
        'installer puts it — only change this if you installed a different solver (e.g. a system astrometry.net) '
        'and want Ara to use that instead.',
  ),
  'img.platesolve.index_path': Help(
    key: 'img.platesolve.index_path',
    title: 'Index download path',
    body:
        'Where the solver\'s star index files live. Indexes are the star catalogs the solver matches your image against '
        '— without the right index scale for your field of view, solving fails. They are downloaded once and can be '
        'several GB, so on a Pi keep them on the external drive.',
  ),
  'img.platesolve.timeout': Help(
    key: 'img.platesolve.timeout',
    title: 'Solver timeout',
    body:
        'How long one solve attempt may run before it\'s abandoned. A healthy solve with a good position hint finishes '
        'in seconds; a blind solve of an unknown field can legitimately take much longer. If centering keeps timing out, '
        'check the search radius and that the right index files are installed before raising this.',
  ),

  // ── Optics (imaging.optics) ─────────────────────────────────────────────
  'imaging.optics.focal_length': Help(
    key: 'imaging.optics.focal_length',
    title: 'Focal length',
    body:
        'Your telescope\'s native focal length in millimeters (before any reducer or barlow — that factor is entered '
        'separately below). Together with the pixel size this sets the image scale (arcsec/pixel) that framing, '
        'plate solving and guiding math all depend on. It\'s printed on the telescope or in its manual.',
  ),
  'imaging.optics.reducer': Help(
    key: 'imaging.optics.reducer',
    title: 'Reducer / barlow factor',
    body:
        'The magnification factor of anything between telescope and camera: 0.8 for a 0.8× focal reducer, 2 for a 2× '
        'barlow, 1 for nothing. Effective focal length = native focal length × this factor — get it wrong and every '
        'field-of-view preview and image-scale number is wrong with it.',
  ),
  'imaging.optics.aperture': Help(
    key: 'imaging.optics.aperture',
    title: 'Aperture',
    body:
        'The diameter of your telescope\'s objective in millimeters. With focal length it gives the focal ratio (f/number), '
        'which drives exposure planning: an f/5 system collects light 4× faster than f/10. '
        'Also printed on the telescope (e.g. "80/480" = 80 mm aperture, 480 mm focal length).',
  ),
  'imaging.optics.sensor_width': Help(
    key: 'imaging.optics.sensor_width',
    title: 'Sensor width',
    body:
        'Your camera sensor\'s width in pixels (e.g. 6248 for an IMX571 camera). Used with pixel size and focal length '
        'to compute your true field of view for framing and mosaic planning. Usually filled automatically when the '
        'camera connects; enter it manually only for planning without the camera attached.',
  ),
  'imaging.optics.sensor_height': Help(
    key: 'imaging.optics.sensor_height',
    title: 'Sensor height',
    body:
        'Your camera sensor\'s height in pixels (e.g. 4176 for an IMX571 camera). Used with pixel size and focal length '
        'to compute your true field of view for framing and mosaic planning.',
  ),
  'imaging.optics.pixel_size': Help(
    key: 'imaging.optics.pixel_size',
    title: 'Pixel size',
    body:
        'The physical size of one sensor pixel in micrometers (µm) — e.g. 3.76 for an IMX571. With focal length this '
        'sets the image scale: scale(″/px) = 206.265 × pixel size ÷ focal length. Aim for roughly 1–2″/px for deep-sky '
        'work; the number is in your camera\'s spec sheet.',
  ),

  // ── Active profile (profile.active) ─────────────────────────────────────
  'profile.active.name': Help(
    key: 'profile.active.name',
    title: 'Profile name',
    body:
        'The name of the equipment profile currently active on the server. A profile bundles everything about one rig '
        'configuration — equipment selection, optics, storage, safety policies — so you can keep separate profiles for '
        'different telescopes or cameras and switch between them.',
  ),
  'profile.active.available': Help(
    key: 'profile.active.available',
    title: 'Profiles on this server',
    body:
        'All equipment profiles stored on the connected server. Only one is active at a time; switching profiles '
        'reconfigures the whole rig (equipment, optics, storage paths) to that profile\'s settings. '
        'Create a new profile per distinct hardware combination rather than editing one back and forth.',
  ),
  'profile.active.metadata': Help(
    key: 'profile.active.metadata',
    title: 'Profile details',
    body:
        'Bookkeeping for the active profile: when it was created, when any setting in it last changed, and its unique ID. '
        'The ID is what backups and logs reference — useful when reporting a problem or restoring a snapshot.',
  ),

  // ── Site additions (safety.site) ────────────────────────────────────────
  'safety.site.site_name': Help(
    key: 'safety.site.site_name',
    title: 'Site name',
    body:
        'A label for this observing location ("Backyard", "Dark-site field"). Written into FITS headers and shown in '
        'session logs, so frames from different locations stay distinguishable in your archive.',
  ),
  'safety.site.latitude': Help(
    key: 'safety.site.latitude',
    title: 'Latitude',
    body:
        'Your observing site\'s latitude in decimal degrees (north positive, e.g. 33.45; south negative). Everything '
        'that computes where objects are in YOUR sky flows from this: target altitude curves, meridian flip timing, '
        'polar alignment. A phone GPS reading is plenty accurate.',
  ),
  'safety.site.longitude': Help(
    key: 'safety.site.longitude',
    title: 'Longitude',
    body:
        'Your observing site\'s longitude in decimal degrees (east positive, west negative — most of the Americas is '
        'negative). With latitude it anchors all sky calculations; errors shift the whole night\'s timing (transit times, '
        'twilight) rather than breaking anything obvious, so double-check the sign.',
  ),
  'safety.site.elevation': Help(
    key: 'safety.site.elevation',
    title: 'Elevation',
    body:
        'Height above sea level in meters. Used in atmospheric refraction corrections near the horizon — a small '
        'effect, so an estimate from a map is fine.',
  ),
  'safety.site.time_zone': Help(
    key: 'safety.site.time_zone',
    title: 'Time zone',
    body:
        'The IANA time zone of the site (e.g. America/Denver). Determines how "tonight" is defined and how local clock '
        'times are shown; using the region name (not a fixed UTC offset) keeps daylight-saving transitions correct.',
  ),
  'safety.site.horizon_azimuth': Help(
    key: 'safety.site.horizon_azimuth',
    title: 'Horizon point — azimuth',
    body:
        'The compass direction of one point on your custom horizon (0° = north, 90° = east, 180° = south, 270° = west). '
        'Add a point wherever a tree, roof or hill blocks the sky; the planner will not schedule targets behind it.',
  ),
  'safety.site.horizon_altitude': Help(
    key: 'safety.site.horizon_altitude',
    title: 'Horizon point — altitude',
    body:
        'How high (in degrees above the ideal horizon) the obstruction reaches at this azimuth. The planner treats '
        'everything below this line as unobservable from your site — e.g. a 25° tall tree line to the east means eastern '
        'targets only become schedulable once they climb above 25°.',
  ),

  // ── Notifications additions (session.notifications) ─────────────────────
  'session.notifications.pushover_user_key': Help(
    key: 'session.notifications.pushover_user_key',
    title: 'Pushover user key',
    body:
        'Your personal user key from pushover.net (shown on the dashboard after you log in) — it identifies which '
        'phone/account receives the notifications. Pair it with an application API token created for OpenAstro Ara. '
        'The key stays on your rig server and is only ever sent to Pushover itself.',
  ),
  'session.notifications.telegram_chat_id': Help(
    key: 'session.notifications.telegram_chat_id',
    title: 'Telegram chat ID',
    body:
        'The numeric ID of the Telegram chat that should receive alerts. Easiest way to find it: message your bot, '
        'then open https://api.telegram.org/bot<token>/getUpdates in a browser and read "chat":{"id":…} from the reply. '
        'Group chats have negative IDs — include the minus sign.',
  ),

  'safety.policies.expected_flip_slew_s': Help(
    key: 'safety.policies.expected_flip_slew_s',
    title: 'Expected flip-slew duration',
    body:
        'How long your mount typically takes to perform the meridian-flip slew itself. The scheduler blocks out this '
        'window when planning exposures around the flip, so a frame never starts that the flip would interrupt. '
        'Time one real flip with a stopwatch and add a few seconds of margin.',
  ),
  'safety.policies.unattended_shutdown_wait_min': Help(
    key: 'safety.policies.unattended_shutdown_wait_min',
    title: 'Unattended shutdown wait',
    body:
        'After the last sequence finishes (or fails) with nobody connected, the rig waits this many minutes before '
        'running the end-of-night shutdown (park, warm the camera, close covers). The wait is your window to reconnect '
        'and keep going; once it expires the night is considered over and the equipment protects itself.',
  ),
  'session.filenames.datetime_token': Help(
    key: 'session.filenames.datetime_token',
    title: 'Date & time in filenames',
    body:
        'Every filename always includes the capture timestamp. It\'s what keeps names unique (two frames can never '
        'collide) and makes an alphabetical file listing also a chronological one — which stacking tools and your own '
        'sanity both rely on. That\'s why it can\'t be removed from the template.',
  ),
  'session.filenames.always_written': Help(
    key: 'session.filenames.always_written',
    title: 'Always written',
    body:
        'Regardless of your filename template, the essentials are always recorded WITH each frame: frame type, '
        'exposure, gain, filter, binning, capture time and temperature all go into the FITS header itself. '
        'The filename is for humans browsing folders; the header is what software actually reads — so a minimal '
        'filename template never loses data.',
  ),

  // ── Live gear controls (§25.5.5 device panels) ──────────────────────────
  'eq.camera.cooler': Help(
    key: 'eq.camera.cooler',
    title: 'Camera cooler',
    body:
        'Cooled astronomy cameras have a thermoelectric cooler (TEC) behind the sensor. Turning it on drives the '
        'sensor down to the target temperature, which dramatically reduces thermal noise — hot pixels and background '
        'glow roughly halve for every 5–6 °C of cooling. Turn it on well before imaging (it takes a few minutes to '
        'reach target), and let the camera WARM UP gradually before powering off on humid nights so dew doesn\'t '
        'condense on the cold sensor window.',

    driverNotes: {
      'ZWO':
          'Cooled ZWO models ("Pro" suffix, e.g. ASI2600MC/MM Pro) regulate to a set-point and also '
          'report cooler power. The Mini-series guide cameras (120/174/290 Mini) have no cooler — '
          'this switch will not appear for them.',
      'ToupTek':
          'The ATR2600M is a cooled deep-sky camera; the GPCMOS guide camera is uncooled. ToupTek '
          'coolers report power draw — a cooler pinned at 100% cannot hold the target; raise it.',
      'QHY':
          'The QHY268C is cooled with set-point regulation. The miniCam8M runs a compact cooler — '
          'expect a smaller delta below ambient than a full-size cooled camera.',
      'Player One':
          'The Ceres 462M and Uranus-C PRO are planetary/guide-class cameras without TEC cooling — '
          'cooling controls will not appear.',
      'SVBONY': 'The SV905C2 has no cooler — cooling controls will not appear.',
    },
  ),
  'eq.camera.cooler_target': Help(
    key: 'eq.camera.cooler_target',
    title: 'Cooler target temperature',
    body:
        'The sensor temperature the cooler drives to and holds. Colder is quieter, but the cooler can only remove '
        '~30–35 °C below ambient — set a target it can hold ALL night at your warmest expected temperature, or the '
        'noise level will drift between frames. Just as important: use the SAME target every night, so your dark '
        'calibration frames match your lights. −10 °C is a common year-round choice in temperate climates.',

    driverNotes: {
      'ZWO':
          'ZWO Pro coolers manage ~30–35 °C below ambient. A year-round −10 °C target is the common '
          'choice; the driver ramps gently on its own, so no manual stepping is needed.',
      'ToupTek':
          'ToupTek cooled cameras hold ~30 °C below ambient. Watch the reported cooler power: '
          'sustained 100% means the target is too ambitious for tonight\'s ambient temperature.',
      'QHY':
          'QHY recommends conservative set-points and gradual warm-up; the driver handles ramping. '
          'Keep the same target across nights so your dark library stays valid.',
    },
  ),
  'eq.camera.readout_mode': Help(
    key: 'eq.camera.readout_mode',
    title: 'Readout mode',
    body:
        'Many cameras offer several ways to read the sensor out, trading noise against dynamic range or speed. '
        'The mode names in this list come straight from your camera\'s driver, so they differ by brand — ZWO, '
        'ToupTek, QHY and others each use their own labels ("Normal", "Low noise", "High DR", "HCG", …). '
        'Whatever the name, the right choice for deep-sky imaging is usually the lowest-read-noise mode; '
        'planetary/lucky imaging favors a fast mode. IMPORTANT: the mode changes the sensor\'s noise signature, so '
        'darks, flats and lights must all be captured in the SAME readout mode.',

    driverNotes: {
      'ZWO':
          'ZWO cameras typically expose "Normal" plus low-noise/high-DR variants; the 2600-class '
          'sensors are effectively zero-glow in any mode. Deep-sky: pick the lowest-read-noise mode '
          'and keep gain at the HCG threshold (100 on the 2600 series).',
      'ToupTek':
          'ToupTek drivers commonly list modes like "CMS" (low conversion noise) and "HDR". '
          'For deep-sky the low-noise mode is the usual choice; note ToupTek mode changes can also '
          'alter the usable gain range shown below.',
      'QHY':
          'QHY exposes readout modes prominently (e.g. "PhotoGraphic", "High Gain", "Extend Full Well" '
          'on the 268-class). These change read noise AND full-well substantially — re-check your '
          'camera-electronics numbers after switching, and rebuild darks.',
      'Player One':
          'Player One planetary/guide cameras usually run a single fast readout path — if only one '
          'mode is listed, there is nothing to choose.',
      'SVBONY':
          'The SV905C2 is a guide camera with a single readout path — if only one mode is listed, '
          'there is nothing to choose.',
    },
  ),
  'eq.mount.tracking': Help(
    key: 'eq.mount.tracking',
    title: 'Tracking',
    body:
        'When on, the mount continuously turns at sidereal rate to counteract Earth\'s rotation, keeping stars '
        'stationary in the frame — without it every exposure longer than a second trails. Tracking is normally '
        'managed automatically (on after a slew, off when parked); this switch is the manual override. '
        'A mount left tracking unattended will eventually point at the ground or hit the pier — that\'s what the '
        'safety-policy limits are for.',

    driverNotes: {
      'ZWO':
          'The AM-series are harmonic-drive mounts: no counterweights and high torque, but harmonic '
          'gears have larger fast-moving periodic error than worm drives — guiding is effectively '
          'mandatory for imaging, with short (0.5–1 s) guide exposures working best.',
      'iOptron':
          'iOptron HEM/HAE harmonic mounts behave like the AM series: no counterweight, guide with '
          'short exposures. The HAE29C\'s encoders (EC models) largely remove periodic error.',
      'Celestron':
          'The CGX-L is a worm-drive mount: smooth long-period error that guides out easily, but it '
          'needs proper balancing (slightly east-heavy) for best tracking.',
      'Sky-Watcher':
          'The HEQ5 PRO tracks well once belt-modded and balanced; SynScan connections can be over '
          'USB/serial or Wi-Fi — the wired path is more reliable for all-night sessions.',
    },
  ),
  'eq.mount.goto': Help(
    key: 'eq.mount.goto',
    title: 'GoTo coordinates',
    body:
        'Slews the mount to the entered right ascension (in hours, 0–24) and declination (in degrees, −90 to +90). '
        'This is a raw coordinate slew — no plate-solve verification afterward, so the target lands only as accurately '
        'as your alignment. For framing a target for imaging, prefer the Planning flow, which slews AND centers with '
        'plate solving. Never GoTo blindly with the telescope near obstructions.',

    driverNotes: {
      'ZWO':
          'AM-series harmonic mounts slew fast and hard — make especially sure the imaging train '
          'clears the tripod/pier before a large GoTo, as there is no counterweight to remind you '
          'of the swing envelope.',
      'Sky-Watcher':
          'Over SynScan the GoTo accuracy depends on the hand-controller/app alignment model; a '
          'plate-solved centering pass after the slew is strongly recommended.',
    },
  ),
  'eq.mount.manual_move': Help(
    key: 'eq.mount.manual_move',
    title: 'Manual movement',
    body:
        'Nudges the mount in the chosen direction at the selected rate while you hold the button — for centering a '
        'star by eye or testing that the mount responds. Rates are in multiples of sidereal speed: low rates (0.5–1×) '
        'for fine centering, high rates for slewing across the sky. The center button is an immediate all-stop.',
  ),
  'eq.dome.shutter': Help(
    key: 'eq.dome.shutter',
    title: 'Dome shutter',
    body:
        'Opens or closes the dome\'s shutter (or the roof on a roll-off). The shutter is the weather barrier: '
        'the safety system closes it automatically on an unsafe condition, and end-of-night shutdown closes it too. '
        'Opening manually is fine for setup — just remember an open shutter with safety monitoring disabled means '
        'nothing will protect the telescope if weather turns.',
  ),
  'eq.rotator.reverse': Help(
    key: 'eq.rotator.reverse',
    title: 'Reverse direction',
    body:
        'Flips which way the rotator turns for a positive angle change. Needed when the rotator is mounted mirrored '
        '(e.g. on the far side of an off-axis guider) so that "rotate to 30°" turns the camera the way the software '
        'expects. If plate-solved rotation angles keep moving AWAY from the requested angle, this is the fix.',
  ),
  'eq.rotator.move': Help(
    key: 'eq.rotator.move',
    title: 'Move / Sync',
    body:
        'Move turns the rotator to the entered angle. Sync does NOT move anything — it tells the rotator "your '
        'current position IS this angle", re-zeroing its scale, typically after a plate solve measured the true sky '
        'angle. Use Sync to calibrate, Move to actually rotate the camera for framing.',

    driverNotes: {
      'ZWO':
          'The ZWO CAA reports mechanical angle only — always Sync after a plate solve so sky-angle '
          'moves land correctly.',
      'WandererAstro':
          'The WandererRotator Mini is gear-driven with some backlash; approaching the target angle '
          'from the same direction each time gives the most repeatable framing.',
    },
  ),
  'eq.rotator.sky_angle': Help(
    key: 'eq.rotator.sky_angle',
    title: 'Sky angle vs mechanical',
    body:
        'With this checked, the angle you enter is a SKY position angle (0° = north up in the image, as used by '
        'framing tools and plate solvers) and the software translates it to the right mechanical position. Unchecked, '
        'the angle goes to the rotator hardware raw. Use sky angle for framing targets; mechanical only for testing '
        'the device itself.',
  ),
  'eq.focuser.move': Help(
    key: 'eq.focuser.move',
    title: 'Move focuser',
    body:
        'Drives the focuser to the entered position (absolute focusers, in motor steps) or by a signed step count '
        '(relative focusers, negative = inward). Moving changes focus — mid-sequence this ruins the current frame, so '
        'it\'s a setup/testing control; during sessions the autofocus routine owns the focuser. If you don\'t know '
        'your rough focus position, move in big steps toward smaller star donuts, then let autofocus finish the job.',

    driverNotes: {
      'ZWO':
          'The ZWO EAF is an absolute focuser (positions 0–60000 by default). If positions seem '
          'mirrored, reverse direction in the driver rather than remembering "backwards" offsets.',
      'ToupTek':
          'The ToupTek AAF is absolute with a configurable max step; check its backlash setting — '
          'a few tens of steps of backlash compensation noticeably improves autofocus V-curves.',
      'Gemini':
          'The Gemini Automatic Astro Focuser Pro is absolute; its high step resolution means '
          'autofocus step sizes need to be larger than you might expect.',
      'Astroasis':
          'The Oasis Focuser reports temperature from its probe — useful for temperature-triggered '
          'refocus without a separate sensor.',
    },
  ),

  // ─── Display ───
  'app.night_mode': Help(
    key: 'app.night_mode',
    title: 'Night mode',
    body:
        'Turns the whole app red so it doesn\'t wreck your dark adaptation at the eyepiece.\n\n'
        'Your eyes take 20–30 minutes to fully dark-adapt, and one look at a white screen '
        'resets that. Deep red light is the one colour your night vision barely responds to, '
        'so a red display lets you drive the rig between subs without starting the clock over.\n\n'
        '**Toggling it:** this switch, the moon button beside the equipment chips in the top '
        'bar, or the **N** key from anywhere in the app (except while you\'re typing in a '
        'field). The choice is remembered for next launch.\n\n'
        'The sky map is a native browser view that Ara can\'t paint over, so it gets a matching '
        'red tint applied inside the map itself.\n\n'
        'For the deepest red, also turn your monitor\'s brightness right down — a dim red screen '
        'beats a bright one, and no software filter can undo a backlight at full blast.',
  ),
};
