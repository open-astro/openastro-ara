/// §12c.2 — the frame's RAW 16-bit statistics from
/// `GET /frames/{id}/histogram`: 128 bins for the plot plus the NINA-style
/// Statistics readout — exact mean/SD/median/MAD, min/max with their pixel
/// counts, and the capture/analysis columns from the catalog.
class FrameHistogram {
  final List<int> bins;
  final int minAdu;
  final int minCount;
  final int maxAdu;
  final int maxCount;
  final double meanAdu;
  final double stdDev;
  final double median;
  final double mad;

  /// Share of pixels at the true rails (0 / 65535).
  final double lowClipFraction;
  final double highClipFraction;

  final int width;
  final int height;
  final int bitDepth;

  /// Null until the frame's star analysis lands (or for non-light frames).
  final int? stars;
  final double? hfr;
  final int? gain;
  final int? offset;

  const FrameHistogram({
    required this.bins,
    required this.minAdu,
    required this.minCount,
    required this.maxAdu,
    required this.maxCount,
    required this.meanAdu,
    required this.stdDev,
    required this.median,
    required this.mad,
    required this.lowClipFraction,
    required this.highClipFraction,
    required this.width,
    required this.height,
    required this.bitDepth,
    this.stars,
    this.hfr,
    this.gain,
    this.offset,
  });

  factory FrameHistogram.fromJson(Map<String, dynamic> json) => FrameHistogram(
        bins: (json['bins'] as List<dynamic>? ?? const [])
            .map((b) => (b as num).toInt())
            .toList(growable: false),
        minAdu: (json['min_adu'] as num?)?.toInt() ?? 0,
        minCount: (json['min_count'] as num?)?.toInt() ?? 0,
        maxAdu: (json['max_adu'] as num?)?.toInt() ?? 0,
        maxCount: (json['max_count'] as num?)?.toInt() ?? 0,
        meanAdu: (json['mean_adu'] as num?)?.toDouble() ?? 0,
        stdDev: (json['std_dev'] as num?)?.toDouble() ?? 0,
        median: (json['median'] as num?)?.toDouble() ?? 0,
        mad: (json['mad'] as num?)?.toDouble() ?? 0,
        lowClipFraction: (json['low_clip_fraction'] as num?)?.toDouble() ?? 0,
        highClipFraction: (json['high_clip_fraction'] as num?)?.toDouble() ?? 0,
        width: (json['width'] as num?)?.toInt() ?? 0,
        height: (json['height'] as num?)?.toInt() ?? 0,
        bitDepth: (json['bit_depth'] as num?)?.toInt() ?? 0,
        stars: (json['stars'] as num?)?.toInt(),
        hfr: (json['hfr'] as num?)?.toDouble(),
        gain: (json['gain'] as num?)?.toInt(),
        offset: (json['offset'] as num?)?.toInt(),
      );
}
