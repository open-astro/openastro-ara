/// #1364 — the live guide-camera view (`GET /api/v1/equipment/guider/live`):
/// what the guider is doing, the frame its picture covers, the star it holds
/// and the search box around it, the last guide frame's SNR and mass, whether
/// multi-star is on, and the other stars it found. Coordinates are
/// guide-camera pixels.
class GuiderLive {
  final bool connected;

  /// Stopped | Looping | Selected | Calibrating | Guiding | LostLock | Paused.
  final String? appState;
  final int? frameWidth;
  final int? frameHeight;
  final double? lockX;
  final double? lockY;

  /// Half-size of the star search box, px.
  final int? searchRegion;
  final double? snr;
  final double? starMass;
  final bool? multiStarEnabled;
  final List<GuiderLiveStar> stars;
  final bool hasFrame;

  const GuiderLive({
    this.connected = false,
    this.appState,
    this.frameWidth,
    this.frameHeight,
    this.lockX,
    this.lockY,
    this.searchRegion,
    this.snr,
    this.starMass,
    this.multiStarEnabled,
    this.stars = const [],
    this.hasFrame = false,
  });

  static const disconnected = GuiderLive();

  bool get hasLock => lockX != null && lockY != null;
  bool get hasFrameSize => (frameWidth ?? 0) > 0 && (frameHeight ?? 0) > 0;
  bool get isLooping => appState == 'Looping' || appState == 'Selected';
  /// Something is running: Loop would abandon it and Stop ends it. A paused
  /// guide counts — Loop there would throw away the paused session.
  bool get isBusy => appState != null && appState != 'Stopped';

  bool get isActive =>
      appState != null && appState != 'Stopped' && appState != 'Paused';

  /// One line for the panel header: "Guiding · SNR 42 · mass 12.3k".
  String get summary {
    if (!connected) return 'Guider not connected';
    final parts = <String>[
      switch (appState) {
        'Stopped' || null => 'Stopped',
        'Looping' => 'Looping',
        'Selected' => 'Star selected',
        'Calibrating' => 'Calibrating',
        'Guiding' => 'Guiding',
        'LostLock' => 'Star lost',
        'Paused' => 'Paused',
        final other => other,
      },
      if (snr != null) 'SNR ${snr!.toStringAsFixed(0)}',
      if (starMass != null) 'mass ${_k(starMass!)}',
    ];
    return parts.join(' · ');
  }

  static String _k(double v) =>
      v >= 1000 ? '${(v / 1000).toStringAsFixed(1)}k' : v.toStringAsFixed(0);

  factory GuiderLive.fromJson(Map<String, dynamic> json) {
    final raw = json['stars'];
    return GuiderLive(
      connected: json['connected'] as bool? ?? false,
      appState: json['app_state'] is String
          ? json['app_state'] as String
          : null,
      frameWidth: (json['frame_width'] as num?)?.toInt(),
      frameHeight: (json['frame_height'] as num?)?.toInt(),
      lockX: (json['lock_x'] as num?)?.toDouble(),
      lockY: (json['lock_y'] as num?)?.toDouble(),
      searchRegion: (json['search_region'] as num?)?.toInt(),
      snr: (json['snr'] as num?)?.toDouble(),
      starMass: (json['star_mass'] as num?)?.toDouble(),
      multiStarEnabled: json['multi_star_enabled'] as bool?,
      stars: [
        if (raw is List)
          for (final s in raw)
            if (s is Map<String, dynamic>) GuiderLiveStar.fromJson(s),
      ],
      hasFrame: json['has_frame'] as bool? ?? false,
    );
  }
}

/// One star the guider sees, guide-camera pixels.
class GuiderLiveStar {
  final double x;
  final double y;
  final double? snr;
  final double? mass;
  final double? hfd;
  const GuiderLiveStar({
    required this.x,
    required this.y,
    this.snr,
    this.mass,
    this.hfd,
  });

  factory GuiderLiveStar.fromJson(Map<String, dynamic> json) => GuiderLiveStar(
    x: (json['x'] as num?)?.toDouble() ?? 0,
    y: (json['y'] as num?)?.toDouble() ?? 0,
    snr: (json['snr'] as num?)?.toDouble(),
    mass: (json['mass'] as num?)?.toDouble(),
    hfd: (json['hfd'] as num?)?.toDouble(),
  );
}
