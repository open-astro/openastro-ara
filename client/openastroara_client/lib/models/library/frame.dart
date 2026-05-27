// In-memory model for a captured frame per playbook §40. Phase 12f.1
// uses an immutable value class with the per-frame metadata shown by
// §40.5 (filename, exposure, gain, offset, HFR, star count, etc).
// Phase 12f.2 wires this against `/api/v1/frames/{id}` and the bundled
// stretch picker (§65).

class CapturedFrame {
  final String id;
  final String filename;
  final Duration exposure;
  final int gain;
  final int offset;
  final String filter;
  final int bin;
  final String frameType;
  final double hfr;
  final int starCount;
  final int medianAdu;
  final int backgroundAdu;
  final double sensorTempC;
  final int focusSteps;
  final DateTime capturedAt;
  final int rating;
  final List<String> tags;
  final String notes;

  CapturedFrame({
    required this.id,
    required this.filename,
    required this.exposure,
    required this.gain,
    required this.offset,
    required this.filter,
    required this.bin,
    required this.frameType,
    required this.hfr,
    required this.starCount,
    required this.medianAdu,
    required this.backgroundAdu,
    required this.sensorTempC,
    required this.focusSteps,
    required this.capturedAt,
    this.rating = 0,
    List<String> tags = const <String>[],
    this.notes = '',
  }) : tags = List<String>.unmodifiable(tags);
}

class CaptureSession {
  final String id;
  final DateTime date;
  final String targetName;
  final String siteName;
  final List<CapturedFrame> frames;

  CaptureSession({
    required this.id,
    required this.date,
    required this.targetName,
    required this.siteName,
    required List<CapturedFrame> frames,
  }) : frames = List<CapturedFrame>.unmodifiable(frames);

  /// Total integration time across all light frames in the session.
  Duration get totalIntegration {
    Duration sum = Duration.zero;
    for (final f in frames) {
      if (f.frameType.toLowerCase() == 'light') sum += f.exposure;
    }
    return sum;
  }

  // Canonical filter order — used by [framesByFilter] so the result honors
  // the documented L/R/G/B/Hα/OIII/SII contract regardless of capture order.
  static const _filterOrder = <String>['L', 'R', 'G', 'B', 'Hα', 'OIII', 'SII'];

  /// Frame counts grouped by filter, in canonical L/R/G/B/Hα/OIII/SII order.
  /// Unknown filters land at the end in the order they were first seen.
  Map<String, int> get framesByFilter {
    final counts = <String, int>{};
    for (final f in frames) {
      counts[f.filter] = (counts[f.filter] ?? 0) + 1;
    }
    final ordered = <String, int>{};
    for (final f in _filterOrder) {
      if (counts.containsKey(f)) ordered[f] = counts[f]!;
    }
    for (final entry in counts.entries) {
      ordered.putIfAbsent(entry.key, () => entry.value);
    }
    return ordered;
  }
}
