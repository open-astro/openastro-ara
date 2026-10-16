/// Client mirror of the daemon's Bahtinov mask focus readout
/// (`GET /api/v1/bahtinov-focus/state`, `BahtinovFocusStatusDto`, #1299).
/// Wire format is snake_case.
library;

abstract final class BahtinovFocusStates {
  static const idle = 'idle';
  static const running = 'running';
  static const stopped = 'stopped';
  static const error = 'error';
}

/// Why a frame gave no measurement.
abstract final class BahtinovProblems {
  static const noStar = 'no_star';
  static const nearEdge = 'near_edge';
  static const noPattern = 'no_pattern';
}

double _d(Map<String, dynamic> json, String key) =>
    (json[key] as num?)?.toDouble() ?? 0;

int _i(Map<String, dynamic> json, String key) => (json[key] as num?)?.toInt() ?? 0;

/// One fitted spike over the star crop, in crop pixels.
class BahtinovLine {
  /// `outer` (the X) or `central`.
  final String role;
  final double x1;
  final double y1;
  final double x2;
  final double y2;

  const BahtinovLine({
    required this.role,
    required this.x1,
    required this.y1,
    required this.x2,
    required this.y2,
  });

  bool get isCentral => role == 'central';

  factory BahtinovLine.fromJson(Map<String, dynamic> json) => BahtinovLine(
        role: json['role'] is String ? json['role'] as String : 'outer',
        x1: _d(json, 'x1'),
        y1: _d(json, 'y1'),
        x2: _d(json, 'x2'),
        y2: _d(json, 'y2'),
      );
}

/// What to draw over the star crop: the three lines and the X's crossing.
class BahtinovOverlay {
  final int cropSize;
  final List<BahtinovLine> lines;
  final double intersectionX;
  final double intersectionY;

  const BahtinovOverlay({
    required this.cropSize,
    required this.lines,
    required this.intersectionX,
    required this.intersectionY,
  });

  static BahtinovOverlay? fromJson(Object? json) {
    if (json is! Map<String, dynamic>) return null;
    final raw = json['lines'];
    final lines = <BahtinovLine>[
      if (raw is List)
        for (final l in raw)
          if (l is Map<String, dynamic>) BahtinovLine.fromJson(l),
    ];
    final size = _i(json, 'crop_size');
    if (size <= 0 || lines.length != 3) return null;
    return BahtinovOverlay(
      cropSize: size,
      lines: lines,
      intersectionX: _d(json, 'intersection_x'),
      intersectionY: _d(json, 'intersection_y'),
    );
  }
}

/// One measured frame. [offsetPx] is null when nothing was measured
/// ([problem] says why).
class BahtinovSample {
  final int seq;
  final bool detected;
  final String? problem;
  final double? offsetPx;
  final double? defocusUm;
  final bool withinZone;
  final double peakAdu;
  final BahtinovOverlay? overlay;

  const BahtinovSample({
    required this.seq,
    required this.detected,
    this.problem,
    this.offsetPx,
    this.defocusUm,
    this.withinZone = false,
    this.peakAdu = 0,
    this.overlay,
  });

  factory BahtinovSample.fromJson(Map<String, dynamic> json) => BahtinovSample(
        seq: _i(json, 'seq'),
        detected: json['detected'] as bool? ?? false,
        problem: json['problem'] is String ? json['problem'] as String : null,
        offsetPx: (json['offset_px'] as num?)?.toDouble(),
        defocusUm: (json['defocus_um'] as num?)?.toDouble(),
        withinZone: json['within_zone'] as bool? ?? false,
        peakAdu: _d(json, 'peak_adu'),
        overlay: BahtinovOverlay.fromJson(json['overlay']),
      );
}

/// What the daemon's picture covers when it is a crop of the frame, in frame
/// pixels. Absent when the picture is the whole frame.
class BahtinovFrameCrop {
  final int left;
  final int top;
  final int size;
  const BahtinovFrameCrop({required this.left, required this.top, required this.size});

  static BahtinovFrameCrop? fromJson(Object? json) {
    if (json is! Map<String, dynamic>) return null;
    final size = _i(json, 'size');
    if (size <= 0) return null;
    return BahtinovFrameCrop(left: _i(json, 'left'), top: _i(json, 'top'), size: size);
  }
}

/// The readout's snapshot.
class BahtinovFocusStatus {
  final bool active;
  final String state;
  final double exposureSec;
  final int seq;
  final BahtinovSample? latest;
  final List<BahtinovSample> recent;
  final double? bestOffsetPx;
  /// The in-focus limit on the offset (px).
  final double zonePx;
  /// [zonePx] comes from the optics in the profile (else a fixed half pixel).
  final bool zoneFromOptics;
  /// Half the critical focus zone in µm, when the optics are known.
  final double? zoneUm;
  final bool withinZone;
  final String? error;
  final bool hasFrame;
  /// The sample the daemon's current frame belongs to. Behind [seq] when a
  /// frame failed to render, so the client compares against this, not [seq].
  final int frameSeq;
  /// The analysed frame's size in (binned) camera pixels; 0 before a frame.
  final int frameWidth;
  final int frameHeight;
  /// What the picture shows when it is a crop; null for the whole frame.
  final BahtinovFrameCrop? frameCrop;
  /// The star the user locked by tapping, in frame pixels.
  final int? pickX;
  final int? pickY;

  const BahtinovFocusStatus({
    this.active = false,
    this.state = BahtinovFocusStates.idle,
    this.exposureSec = 0,
    this.seq = 0,
    this.latest,
    this.recent = const [],
    this.bestOffsetPx,
    this.zonePx = 0.5,
    this.zoneFromOptics = false,
    this.zoneUm,
    this.withinZone = false,
    this.error,
    this.hasFrame = false,
    this.frameSeq = 0,
    this.frameWidth = 0,
    this.frameHeight = 0,
    this.frameCrop,
    this.pickX,
    this.pickY,
  });

  bool get hasPick => pickX != null && pickY != null;

  /// How many measured frames the readout averages. Seeing on a small image
  /// scale moves a single frame's offset by a pixel either way; the median of
  /// five holds still, while a real focuser move still shows within a few
  /// frames.
  static const steadyWindow = 5;

  /// [latest] with its offset replaced by the median of the last
  /// [steadyWindow] measured frames (and the zone verdict and µm estimate
  /// re-derived from it). The raw frame-by-frame value stays in [latest] and
  /// the trend. A latest frame without a measurement is returned as is (the
  /// live readout shows why). Pure.
  BahtinovSample? get steady {
    final l = latest;
    if (l == null || !l.detected || l.offsetPx == null) return l;
    return lastSteady;
  }

  /// The steadied reading at the LAST MEASURED frame, whatever the frames
  /// after it show: what Finish decides on, since by the time the user
  /// presses it the mask may be off and the newest frames have no spikes.
  /// Null before any measurement. Pure.
  BahtinovSample? get lastSteady {
    final history = [
      ...recent,
      if (latest case final l? when recent.isEmpty || recent.last.seq != l.seq) l,
    ];
    final measured = [
      for (final s in history.reversed)
        if (s.detected && s.offsetPx != null) s,
    ];
    if (measured.isEmpty) return null;
    // The overlay rides on `latest` only; keep it when latest is the anchor.
    final anchor = latest != null && latest!.seq == measured.first.seq ? latest! : measured.first;
    final offsets = [for (final s in measured.take(steadyWindow)) s.offsetPx!]..sort();
    if (offsets.length < 2) return anchor;
    final mid = offsets.length ~/ 2;
    final median = offsets.length.isOdd ? offsets[mid] : (offsets[mid - 1] + offsets[mid]) / 2;
    // µm per pixel from the zone (both come from the same optics), so an anchor
    // frame at exactly 0 px still yields a µm figure for a non-zero median.
    final umPerPx = zoneUm != null && zonePx > 0
        ? zoneUm! / zonePx
        : (anchor.defocusUm != null && anchor.offsetPx != 0 ? anchor.defocusUm! / anchor.offsetPx! : null);
    return BahtinovSample(
      seq: anchor.seq,
      detected: true,
      offsetPx: median,
      defocusUm: umPerPx == null ? null : median * umPerPx,
      withinZone: median.abs() <= zonePx,
      peakAdu: anchor.peakAdu,
      overlay: anchor.overlay,
    );
  }

  static const idle = BahtinovFocusStatus();

  /// At least one frame measured the pattern this session.
  bool get hasMeasurement => latest?.detected == true || recent.any((s) => s.detected);

  factory BahtinovFocusStatus.fromJson(Map<String, dynamic> json) {
    final raw = json['recent'];
    final latest = json['latest'];
    return BahtinovFocusStatus(
      active: json['active'] as bool? ?? false,
      state: json['state'] is String ? json['state'] as String : BahtinovFocusStates.idle,
      exposureSec: _d(json, 'exposure_sec'),
      seq: _i(json, 'seq'),
      latest: latest is Map<String, dynamic> ? BahtinovSample.fromJson(latest) : null,
      recent: [
        if (raw is List)
          for (final s in raw)
            if (s is Map<String, dynamic>) BahtinovSample.fromJson(s),
      ],
      bestOffsetPx: (json['best_offset_px'] as num?)?.toDouble(),
      zonePx: (json['zone_px'] as num?)?.toDouble() ?? 0.5,
      zoneFromOptics: json['zone_from_optics'] as bool? ?? false,
      zoneUm: (json['zone_um'] as num?)?.toDouble(),
      withinZone: json['within_zone'] as bool? ?? false,
      error: json['error'] is String ? json['error'] as String : null,
      hasFrame: json['has_frame'] as bool? ?? false,
      frameSeq: _i(json, 'frame_seq'),
      frameWidth: _i(json, 'frame_width'),
      frameHeight: _i(json, 'frame_height'),
      frameCrop: BahtinovFrameCrop.fromJson(json['frame_crop']),
      pickX: (json['pick_x'] as num?)?.toInt(),
      pickY: (json['pick_y'] as num?)?.toInt(),
    );
  }
}
