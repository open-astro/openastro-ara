import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Units the Live-tab guide graph plots in — PHD2's "arc-sec / pixels"
/// toggle. [auto] draws arcsec whenever a pixel scale is known.
enum GuideGraphUnit { auto, arcsec, px }

/// The PHD2 graph window controls: how many frames are in view, the y
/// half-range (null = auto-scale to the data, PHD2's "Auto"), the units and
/// whether the correction pulses are drawn. Session state — PHD2 keeps these
/// per profile, but a night rarely needs more than a quick flip.
class GuideGraphSettings {
  /// Frames in view. PHD2 offers 50 / 100 / 200 / 400.
  final int xRange;
  /// Half-range of the y axis in the graph's unit; null = auto.
  final double? yHalfRange;
  final GuideGraphUnit unit;
  final bool showCorrections;

  const GuideGraphSettings({
    this.xRange = 100,
    this.yHalfRange,
    this.unit = GuideGraphUnit.auto,
    this.showCorrections = true,
  });

  static const List<int> xRanges = [50, 100, 200, 400];
  /// PHD2's y choices in arcsec; the same ladder serves px.
  static const List<double> yRanges = [0.5, 1, 2, 4, 8, 16];

  GuideGraphSettings copyWith({
    int? xRange,
    double? yHalfRange,
    bool clearY = false,
    GuideGraphUnit? unit,
    bool? showCorrections,
  }) =>
      GuideGraphSettings(
        xRange: xRange ?? this.xRange,
        yHalfRange: clearY ? null : (yHalfRange ?? this.yHalfRange),
        unit: unit ?? this.unit,
        showCorrections: showCorrections ?? this.showCorrections,
      );

  @override
  bool operator ==(Object other) =>
      other is GuideGraphSettings &&
      other.xRange == xRange &&
      other.yHalfRange == yHalfRange &&
      other.unit == unit &&
      other.showCorrections == showCorrections;

  @override
  int get hashCode => Object.hash(xRange, yHalfRange, unit, showCorrections);
}

class GuideGraphSettingsNotifier extends Notifier<GuideGraphSettings> {
  @override
  GuideGraphSettings build() => const GuideGraphSettings();

  void setXRange(int frames) => state = state.copyWith(xRange: frames);
  void setYHalfRange(double? half) =>
      state = half == null ? state.copyWith(clearY: true) : state.copyWith(yHalfRange: half);
  void setUnit(GuideGraphUnit unit) => state = state.copyWith(unit: unit);
  void setShowCorrections(bool on) => state = state.copyWith(showCorrections: on);
}

final guideGraphSettingsProvider =
    NotifierProvider<GuideGraphSettingsNotifier, GuideGraphSettings>(
        GuideGraphSettingsNotifier.new);
