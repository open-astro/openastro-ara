/// Client model for §50.4 Focus & Temperature — the daemon's `StatsFocusTempDto`
/// from `GET /api/v1/stats/focus-temp`: one point per captured frame that
/// recorded a focuser position, pairing the sensor temperature against the
/// focuser step position, plus a Pearson r² over the pairs. Snake_case wire;
/// defensive parse.
library;

double _dbl(dynamic v) => v is num ? v.toDouble() : 0.0;
double? _dblOrNull(dynamic v) => v is num ? v.toDouble() : null;
int _int(dynamic v) => v is num ? v.toInt() : 0;

/// Parses a wire timestamp to UTC. A string with any timezone designator (`Z`
/// or a numeric offset like `±HH:MM`) is parsed by Dart with `isUtc == true`,
/// already converted to the correct UTC instant. A zone-LESS string parses as
/// local time, which would shift it by the client's offset — the daemon emits
/// UTC, so reinterpret its wall-clock fields as UTC instead of trusting the
/// local parse.
DateTime? _dt(dynamic v) {
  if (v is! String || v.isEmpty) return null;
  final parsed = DateTime.tryParse(v);
  if (parsed == null) return null;
  if (parsed.isUtc) return parsed;
  return DateTime.utc(parsed.year, parsed.month, parsed.day, parsed.hour,
      parsed.minute, parsed.second, parsed.millisecond, parsed.microsecond);
}

/// One focus-vs-temp sample: a frame's sensor temperature (°C) paired with the
/// focuser step position recorded at capture, plus the capture instant.
class FocusTempPoint {
  final double temperatureC;
  final int focuserPosition;
  final DateTime? timestamp;

  const FocusTempPoint({
    this.temperatureC = 0.0,
    this.focuserPosition = 0,
    this.timestamp,
  });

  factory FocusTempPoint.fromJson(Map<String, dynamic> json) => FocusTempPoint(
        temperatureC: _dbl(json['temperature_c']),
        focuserPosition: _int(json['focuser_position']),
        timestamp: _dt(json['timestamp']),
      );
}

/// The active profile's focus-vs-temperature scatter, with an optional Pearson
/// r² (`null` when fewer than two samples or either axis has zero variance).
class FocusTempSeries {
  final List<FocusTempPoint> samples;
  final double? correlationR2;

  const FocusTempSeries({
    this.samples = const [],
    this.correlationR2,
  });

  /// True when no positioned frame exists yet — the chart shows an empty state.
  bool get isEmpty => samples.isEmpty;

  factory FocusTempSeries.fromJson(Map<String, dynamic> json) {
    final raw = json['samples'];
    // Keep only rows with BOTH plotted axes present and numeric: a missing
    // temperature or focuser position can't be placed on the scatter, and
    // degrading either to 0 would plant a spurious point at the origin. The
    // daemon's query already filters `focuser_position IS NOT NULL`, so this
    // only guards schema drift.
    final samples = raw is List
        ? [
            for (final e in raw)
              if (e is Map<String, dynamic> &&
                  e['temperature_c'] is num &&
                  e['focuser_position'] is num)
                FocusTempPoint.fromJson(e),
          ]
        : const <FocusTempPoint>[];
    return FocusTempSeries(
      samples: samples,
      correlationR2: _dblOrNull(json['correlation_r2']),
    );
  }
}
