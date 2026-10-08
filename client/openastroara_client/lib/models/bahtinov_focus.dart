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
  });

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
    );
  }
}
