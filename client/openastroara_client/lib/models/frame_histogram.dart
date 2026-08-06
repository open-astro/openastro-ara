/// §12c.2 — the frame's RAW 16-bit luminance histogram from
/// `GET /frames/{id}/histogram`: 128 bins over the ADU range plus the
/// numbers a pixel-peeper wants without opening the FITS.
class FrameHistogram {
  final List<int> bins;
  final int minAdu;
  final int maxAdu;
  final double meanAdu;

  /// Share of pixels in the bottom/top bin — the "you're clipping" signals.
  final double lowClipFraction;
  final double highClipFraction;

  const FrameHistogram({
    required this.bins,
    required this.minAdu,
    required this.maxAdu,
    required this.meanAdu,
    required this.lowClipFraction,
    required this.highClipFraction,
  });

  factory FrameHistogram.fromJson(Map<String, dynamic> json) => FrameHistogram(
        bins: (json['bins'] as List<dynamic>? ?? const [])
            .map((b) => (b as num).toInt())
            .toList(growable: false),
        minAdu: (json['min_adu'] as num?)?.toInt() ?? 0,
        maxAdu: (json['max_adu'] as num?)?.toInt() ?? 0,
        meanAdu: (json['mean_adu'] as num?)?.toDouble() ?? 0,
        lowClipFraction: (json['low_clip_fraction'] as num?)?.toDouble() ?? 0,
        highClipFraction: (json['high_clip_fraction'] as num?)?.toDouble() ?? 0,
      );
}
