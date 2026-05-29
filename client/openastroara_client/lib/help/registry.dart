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
};
