import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Framing Assistant state (§25.5.2). Phase 12c.1 scopes to:
///   - the search query string
///   - the framing parameters (FOV, rotation, mosaic panel grid)
/// Real target catalog lookups (Messier/NGC/IC/by name or coords) + sky-chart
/// rendering land in Phase 12c.2 alongside the bundled-catalog service.

class FramingParams {
  final double rotationDeg;
  final int mosaicCols;
  final int mosaicRows;
  final double mosaicOverlapPct;

  const FramingParams({
    this.rotationDeg = 0,
    this.mosaicCols = 1,
    this.mosaicRows = 1,
    this.mosaicOverlapPct = 10,
  });

  FramingParams copyWith({
    double? rotationDeg,
    int? mosaicCols,
    int? mosaicRows,
    double? mosaicOverlapPct,
  }) =>
      FramingParams(
        rotationDeg: rotationDeg ?? this.rotationDeg,
        mosaicCols: mosaicCols ?? this.mosaicCols,
        mosaicRows: mosaicRows ?? this.mosaicRows,
        mosaicOverlapPct: mosaicOverlapPct ?? this.mosaicOverlapPct,
      );
}

class FramingController extends Notifier<FramingParams> {
  @override
  FramingParams build() => const FramingParams();

  void setRotationDeg(double v) => state = state.copyWith(rotationDeg: v);
  void setMosaicCols(int v) => state = state.copyWith(mosaicCols: v);
  void setMosaicRows(int v) => state = state.copyWith(mosaicRows: v);
  void setOverlapPct(double v) => state = state.copyWith(mosaicOverlapPct: v);
}

final framingControllerProvider =
    NotifierProvider<FramingController, FramingParams>(
        FramingController.new);

/// Riverpod 3.x removed `StateProvider`; use a thin Notifier for the search
/// box's debounced query.
class TargetSearchQueryNotifier extends Notifier<String> {
  @override
  String build() => '';
  void set(String s) => state = s;
}

final targetSearchQueryProvider =
    NotifierProvider<TargetSearchQueryNotifier, String>(
        TargetSearchQueryNotifier.new);
