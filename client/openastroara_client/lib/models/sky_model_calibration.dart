/// #1140 P2 — the Integration Budget's calibration store as the daemon fits it
/// (`GET /api/v1/planning/sky-model`): one scalar k per filter kind and
/// binning that scales the spec-sheet sky flux to what this rig's frames
/// measured. Null on the wire means "never fitted".
class SkyModelFilterCalibration {
  final String kind; // the daemon's wire token: l, r, g, b, osc, ha, oiii, sii, duo, tri
  final int bin;
  final double? k;
  final double? scatterPct;
  final double? pedestalAdu;
  final double? measuredRateEPerSecPerPx;
  final double? predictedRateEPerSecPerPx;
  final int frameCount;

  /// The Phase-V gate: |k| in 0.4–1.5 and scatter under 30 % over at least
  /// three frames. Only a passed entry may be applied to the planner.
  final bool passed;
  final String? note;

  const SkyModelFilterCalibration({
    required this.kind,
    required this.bin,
    this.k,
    this.scatterPct,
    this.pedestalAdu,
    this.measuredRateEPerSecPerPx,
    this.predictedRateEPerSecPerPx,
    required this.frameCount,
    required this.passed,
    this.note,
  });

  static SkyModelFilterCalibration? fromJson(Object? json) {
    if (json is! Map<String, dynamic>) return null;
    final kind = json['kind'];
    if (kind is! String) return null;
    return SkyModelFilterCalibration(
      kind: kind,
      bin: (json['bin'] as num?)?.toInt() ?? 1,
      k: (json['k'] as num?)?.toDouble(),
      scatterPct: (json['scatter_pct'] as num?)?.toDouble(),
      pedestalAdu: (json['pedestal_adu'] as num?)?.toDouble(),
      measuredRateEPerSecPerPx: (json['measured_rate_e_per_sec_per_px'] as num?)
          ?.toDouble(),
      predictedRateEPerSecPerPx:
          (json['predicted_rate_e_per_sec_per_px'] as num?)?.toDouble(),
      frameCount: (json['frame_count'] as num?)?.toInt() ?? 0,
      passed: json['passed'] == true,
      note: json['note'] as String?,
    );
  }

  /// The row's one-line standing for the settings panel.
  String get standing {
    if (k == null) return note ?? 'Not fitted.';
    final kText = 'k ${k!.toStringAsFixed(2)}';
    final scatter = scatterPct == null
        ? ''
        : ' · scatter ${scatterPct!.toStringAsFixed(0)} %';
    final frames = ' · $frameCount frames';
    return passed
        ? '$kText$scatter$frames · applies'
        : '$kText$scatter$frames · not applied: ${note ?? 'outside the gate'}';
  }
}

class SkyModelCalibration {
  final DateTime fittedUtc;
  final int framesConsidered;
  final List<SkyModelFilterCalibration> filters;

  const SkyModelCalibration({
    required this.fittedUtc,
    required this.framesConsidered,
    required this.filters,
  });

  static SkyModelCalibration? fromJson(Object? json) {
    if (json is! Map<String, dynamic>) return null;
    final fitted = json['fitted_utc'];
    final filters = json['filters'];
    if (fitted is! String || filters is! List) return null;
    final at = DateTime.tryParse(fitted);
    if (at == null) return null;
    return SkyModelCalibration(
      fittedUtc: at.toUtc(),
      framesConsidered: (json['frames_considered'] as num?)?.toInt() ?? 0,
      filters: filters
          .map(SkyModelFilterCalibration.fromJson)
          .whereType<SkyModelFilterCalibration>()
          .toList(),
    );
  }

  /// k by filter kind (bin 1 preferred, else the smallest binning) for the
  /// kinds that passed the gate — the map the planner will multiply the sky
  /// flux by. Plain data, so it can cross into the Tonight's Sky isolate.
  Map<String, double> get appliedK {
    final out = <String, double>{};
    final byKind = <String, SkyModelFilterCalibration>{};
    for (final f in filters.where((f) => f.passed && f.k != null)) {
      final current = byKind[f.kind];
      if (current == null || f.bin < current.bin) byKind[f.kind] = f;
    }
    for (final e in byKind.entries) {
      out[e.key] = e.value.k!;
    }
    return out;
  }
}
