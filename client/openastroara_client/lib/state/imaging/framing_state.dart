import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Framing state for the merged **Planning** tab (§25.5 + §36,
/// PORT_DECISIONS §36/§25.5):
///   - the framing parameters (rotation, mosaic panel grid)
///   - the Frame-toggle on/off flag
/// Target search now flows through the Planning tab's universal search
/// (`skyAtlasSearchProvider`). The profile-derived FOV (camera + scope) +
/// "Build Sequence" output land in the FOV slice.

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

/// Whether the Planning tab's **Frame** toggle is on — when true the framing
/// controls (and, in the FOV slice, the camera/scope FOV overlay) appear over
/// the atlas. Orthogonal to the Explore/Tonight's-Sky view mode
/// (`skyAtlasModeProvider`). See PORT_DECISIONS §36/§25.5.
class FrameModeNotifier extends Notifier<bool> {
  @override
  bool build() => false;
  void set(bool v) => state = v;
}

final frameModeEnabledProvider =
    NotifierProvider<FrameModeNotifier, bool>(FrameModeNotifier.new);
