/// #1364 item 3 — the guider's settings beyond Tune Guiding, as Ara keeps them
/// in the profile (`/api/v1/profile/guider-advanced`). Every value is
/// optional: a missing (null) key keeps the guider's own value, so the map
/// only ever holds what the user set. Keys are the daemon's wire names.
class GuiderAdvancedSettings {
  final Map<String, Object?> values;
  const GuiderAdvancedSettings([this.values = const {}]);

  static const empty = GuiderAdvancedSettings();

  Object? operator [](String key) => values[key];

  /// A copy with [key] set to [value] (null clears it back to the guider's).
  GuiderAdvancedSettings withValue(String key, Object? value) {
    final next = Map<String, Object?>.of(values);
    if (value == null) {
      next.remove(key);
    } else {
      next[key] = value;
    }
    return GuiderAdvancedSettings(next);
  }

  factory GuiderAdvancedSettings.fromJson(Map<String, dynamic> json) {
    final out = <String, Object?>{};
    for (final spec in GuiderAdvancedField.all) {
      final v = json[spec.key];
      final parsed = spec.parse(v);
      if (parsed != null) out[spec.key] = parsed;
    }
    return GuiderAdvancedSettings(out);
  }

  /// Every known key, null where unset — the PUT body replaces the block.
  Map<String, dynamic> toJson() => {
    for (final spec in GuiderAdvancedField.all) spec.key: values[spec.key],
  };
}

enum GuiderAdvancedKind { toggle, integer, decimal, choice }

/// One editable setting: its wire key, label, group, kind, unit and help.
class GuiderAdvancedField {
  final String key;
  final String label;
  final String group;
  final GuiderAdvancedKind kind;
  final String? unit;
  final String help;

  /// For [GuiderAdvancedKind.choice]: fixed options; null means the options
  /// come from the guider (algorithms per axis).
  final List<String>? options;

  /// 0 is a real value to the guider (a max pulse of 0 = no correction on
  /// that axis); every other number must be positive.
  final bool allowZero;
  const GuiderAdvancedField(
    this.key,
    this.label,
    this.group,
    this.kind, {
    this.unit,
    required this.help,
    this.options,
    this.allowZero = false,
  });

  Object? parse(Object? v) => switch (kind) {
    GuiderAdvancedKind.toggle => v is bool ? v : null,
    GuiderAdvancedKind.integer => v is num ? v.round() : null,
    GuiderAdvancedKind.decimal => v is num ? v.toDouble() : null,
    GuiderAdvancedKind.choice => v is String && v.isNotEmpty ? v : null,
  };

  /// How a value reads in the UI: "On", "1500 ms", "8", "2x2 Mean".
  String format(Object? v) {
    if (v == null) return '—';
    return switch (kind) {
      GuiderAdvancedKind.toggle => v == true ? 'On' : 'Off',
      GuiderAdvancedKind.decimal =>
        '${_trim((v as num).toDouble())}${unit == null ? '' : ' $unit'}',
      GuiderAdvancedKind.integer => '$v${unit == null ? '' : ' $unit'}',
      GuiderAdvancedKind.choice => '$v',
    };
  }

  /// The value as an editable number, at full precision (the display format
  /// rounds decimals; editing must not save the rounded value). Pure.
  static String editText(Object v) {
    if (v is int) return '$v';
    if (v is double) return v == v.roundToDouble() ? v.toStringAsFixed(0) : '$v';
    return '$v';
  }

  static String _trim(double d) => d == d.roundToDouble()
      ? d.toStringAsFixed(0)
      : d.toStringAsFixed(2).replaceFirst(RegExp(r'0+$'), '');

  static const groups = [
    'Star tracking',
    'Star detection',
    'Exposure',
    'Algorithms & limits',
    'Backlash',
    'Mount',
    'Dither & delay',
  ];

  static const all = <GuiderAdvancedField>[
    GuiderAdvancedField(
      'multi_star_enabled',
      'Multi-star guiding',
      'Star tracking',
      GuiderAdvancedKind.toggle,
      help: 'Guide on several stars at once — steadier in poor seeing. Applies on the next guider connect.',
    ),
    GuiderAdvancedField(
      'fast_recenter',
      'Fast recenter after calibration',
      'Star tracking',
      GuiderAdvancedKind.toggle,
      help: 'Move the star back to its start quickly once calibration ends.',
    ),
    GuiderAdvancedField(
      'tolerate_jumps',
      'Tolerate star jumps',
      'Star tracking',
      GuiderAdvancedKind.toggle,
      help: 'Ignore a sudden large shift (a bump, wind) instead of chasing it.',
    ),
    GuiderAdvancedField(
      'tolerate_jumps_threshold_px',
      'Jump threshold',
      'Star tracking',
      GuiderAdvancedKind.decimal,
      unit: 'px',
      help: 'A shift bigger than this counts as a jump.',
    ),
    GuiderAdvancedField(
      'search_region_px',
      'Search region',
      'Star detection',
      GuiderAdvancedKind.integer,
      unit: 'px',
      help: 'Half-size of the box the guider looks for its star in each frame.',
    ),
    GuiderAdvancedField(
      'min_star_snr',
      'Minimum star SNR',
      'Star detection',
      GuiderAdvancedKind.decimal,
      help: 'Stars fainter than this are not picked or guided on.',
    ),
    GuiderAdvancedField(
      'min_star_hfd',
      'Minimum star HFD',
      'Star detection',
      GuiderAdvancedKind.decimal,
      unit: 'px',
      help: 'Smaller than this is a hot pixel, not a star.',
    ),
    GuiderAdvancedField(
      'max_star_hfd',
      'Maximum star HFD',
      'Star detection',
      GuiderAdvancedKind.decimal,
      unit: 'px',
      help: 'Bigger than this is out of focus or not a star.',
    ),
    GuiderAdvancedField(
      'mass_change_threshold_enabled',
      'Star mass detection',
      'Star detection',
      GuiderAdvancedKind.toggle,
      help: 'Treat a sudden change in the star\'s brightness as a lost star (a cloud, a different star).',
    ),
    GuiderAdvancedField(
      'mass_change_threshold',
      'Mass change threshold',
      'Star detection',
      GuiderAdvancedKind.decimal,
      help: 'How big a brightness change counts, in the guider\'s units.',
    ),
    GuiderAdvancedField(
      'auto_exposure_min_ms',
      'Auto exposure minimum',
      'Exposure',
      GuiderAdvancedKind.integer,
      unit: 'ms',
      help: 'Shortest guide exposure when the guider picks its own.',
    ),
    GuiderAdvancedField(
      'auto_exposure_max_ms',
      'Auto exposure maximum',
      'Exposure',
      GuiderAdvancedKind.integer,
      unit: 'ms',
      help: 'Longest guide exposure when the guider picks its own.',
    ),
    GuiderAdvancedField(
      'auto_exposure_target_snr',
      'Auto exposure target SNR',
      'Exposure',
      GuiderAdvancedKind.decimal,
      help: 'The star SNR auto exposure aims for.',
    ),
    GuiderAdvancedField(
      'noise_reduction',
      'Noise reduction',
      'Exposure',
      GuiderAdvancedKind.choice,
      help: 'Smooth each guide frame before finding the star.',
      options: ['None', '2x2 Mean', '3x3 Median'],
    ),
    GuiderAdvancedField(
      'use_subframes',
      'Use subframes',
      'Exposure',
      GuiderAdvancedKind.toggle,
      help: 'Download only the box around the star once guiding — faster frames on slow cameras.',
    ),
    GuiderAdvancedField(
      'ra_algorithm',
      'RA algorithm',
      'Algorithms & limits',
      GuiderAdvancedKind.choice,
      help: 'How RA corrections are computed. Predictive PEC learns the worm period.',
    ),
    GuiderAdvancedField(
      'dec_algorithm',
      'Dec algorithm',
      'Algorithms & limits',
      GuiderAdvancedKind.choice,
      help: 'How Dec corrections are computed.',
    ),
    GuiderAdvancedField(
      'max_ra_duration_ms',
      'Max RA pulse',
      'Algorithms & limits',
      GuiderAdvancedKind.integer,
      unit: 'ms',
      help: 'The longest single RA correction. 0 turns RA corrections off.',
      allowZero: true,
    ),
    GuiderAdvancedField(
      'max_dec_duration_ms',
      'Max Dec pulse',
      'Algorithms & limits',
      GuiderAdvancedKind.integer,
      unit: 'ms',
      help: 'The longest single Dec correction. 0 turns Dec corrections off.',
      allowZero: true,
    ),
    GuiderAdvancedField(
      'backlash_enabled',
      'Dec backlash compensation',
      'Backlash',
      GuiderAdvancedKind.toggle,
      help: 'Add a pulse when Dec reverses, to take up gear slack.',
    ),
    GuiderAdvancedField(
      'backlash_pulse_ms',
      'Backlash pulse',
      'Backlash',
      GuiderAdvancedKind.integer,
      unit: 'ms',
      help: 'How long the extra pulse is.',
    ),
    GuiderAdvancedField(
      'backlash_floor_ms',
      'Backlash floor',
      'Backlash',
      GuiderAdvancedKind.integer,
      unit: 'ms',
      help: 'The shortest the guider will adjust the pulse to.',
    ),
    GuiderAdvancedField(
      'backlash_ceiling_ms',
      'Backlash ceiling',
      'Backlash',
      GuiderAdvancedKind.integer,
      unit: 'ms',
      help: 'The longest the guider will adjust the pulse to.',
    ),
    GuiderAdvancedField(
      'assume_orthogonal',
      'Assume orthogonal axes',
      'Mount',
      GuiderAdvancedKind.toggle,
      help: 'Treat RA and Dec as exactly 90° apart after calibration.',
    ),
    GuiderAdvancedField(
      'stop_guiding_when_slewing',
      'Stop guiding when slewing',
      'Mount',
      GuiderAdvancedKind.toggle,
      help: 'Stop guiding when the mount reports a slew.',
    ),
    GuiderAdvancedField(
      'cal_flip_requires_dec_flip',
      'Meridian flip reverses Dec',
      'Mount',
      GuiderAdvancedKind.toggle,
      help: 'Flip the Dec output after a meridian flip (some mounts need it).',
    ),
    GuiderAdvancedField(
      'dither_scale_factor',
      'Dither scale',
      'Dither & delay',
      GuiderAdvancedKind.decimal,
      help: 'Multiplies every dither the run asks for.',
    ),
    GuiderAdvancedField(
      'dither_ra_only',
      'Dither in RA only',
      'Dither & delay',
      GuiderAdvancedKind.toggle,
      help: 'Dither along RA only (useful with Dec backlash).',
    ),
    GuiderAdvancedField(
      'variable_delay_enabled',
      'Variable exposure delay',
      'Dither & delay',
      GuiderAdvancedKind.toggle,
      help: 'Wait between guide frames while guiding is calm. Set all three delay values together.',
    ),
    GuiderAdvancedField(
      'variable_delay_short_s',
      'Short delay',
      'Dither & delay',
      GuiderAdvancedKind.decimal,
      unit: 's',
      help: 'Delay while the star is settling.',
    ),
    GuiderAdvancedField(
      'variable_delay_long_s',
      'Long delay',
      'Dither & delay',
      GuiderAdvancedKind.decimal,
      unit: 's',
      help: 'Delay once guiding is calm.',
    ),
  ];
}

/// What the guider has now, beside the profile's block.
class GuiderAdvancedView {
  final bool connected;
  final GuiderAdvancedSettings current;
  final List<String> raAlgorithms;
  final List<String> decAlgorithms;
  const GuiderAdvancedView({
    this.connected = false,
    this.current = GuiderAdvancedSettings.empty,
    this.raAlgorithms = const [],
    this.decAlgorithms = const [],
  });

  static const disconnected = GuiderAdvancedView();

  factory GuiderAdvancedView.fromJson(Map<String, dynamic> json) {
    List<String> names(Object? v) => [
      if (v is List)
        for (final s in v)
          if (s is String) s,
    ];
    final current = json['current'];
    return GuiderAdvancedView(
      connected: json['connected'] as bool? ?? false,
      current: current is Map<String, dynamic>
          ? GuiderAdvancedSettings.fromJson(current)
          : GuiderAdvancedSettings.empty,
      raAlgorithms: names(json['ra_algorithms']),
      decAlgorithms: names(json['dec_algorithms']),
    );
  }
}
