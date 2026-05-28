import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../imaging/exposure_state.dart' show FrameKind;

/// Profile-level imaging defaults (§37.11 + §29). These seed the
/// `ExposureController` on first build of the Imaging tab; changes from the
/// Settings panel persist back here. Phase 12h.2-imaging holds the values
/// in memory; 12h.2b will wire `/api/v1/profile/imaging-defaults` for
/// daemon round-trip persistence.

class ImagingDefaults {
  final Duration defaultExposure;
  final int defaultGain;
  final int defaultOffset;
  final int defaultBin;
  final FrameKind defaultFrameKind;
  final double coolerTargetC;
  final double coolerRampRatePerMin;
  final bool warmupAtSessionEnd;

  const ImagingDefaults({
    this.defaultExposure = const Duration(seconds: 5),
    this.defaultGain = 100,
    this.defaultOffset = 50,
    this.defaultBin = 1,
    this.defaultFrameKind = FrameKind.light,
    this.coolerTargetC = -10,
    this.coolerRampRatePerMin = 1.0,
    this.warmupAtSessionEnd = false,
  });

  ImagingDefaults copyWith({
    Duration? defaultExposure,
    int? defaultGain,
    int? defaultOffset,
    int? defaultBin,
    FrameKind? defaultFrameKind,
    double? coolerTargetC,
    double? coolerRampRatePerMin,
    bool? warmupAtSessionEnd,
  }) =>
      ImagingDefaults(
        defaultExposure: defaultExposure ?? this.defaultExposure,
        defaultGain: defaultGain ?? this.defaultGain,
        defaultOffset: defaultOffset ?? this.defaultOffset,
        defaultBin: defaultBin ?? this.defaultBin,
        defaultFrameKind: defaultFrameKind ?? this.defaultFrameKind,
        coolerTargetC: coolerTargetC ?? this.coolerTargetC,
        coolerRampRatePerMin:
            coolerRampRatePerMin ?? this.coolerRampRatePerMin,
        warmupAtSessionEnd: warmupAtSessionEnd ?? this.warmupAtSessionEnd,
      );
}

class ImagingDefaultsNotifier extends Notifier<ImagingDefaults> {
  @override
  ImagingDefaults build() => const ImagingDefaults();

  // Setters validate at the boundary so the daemon never sees physically
  // impossible defaults. Matches the round-2 ExposureController contract.
  void setExposure(Duration d) {
    if (d <= Duration.zero) return;
    state = state.copyWith(defaultExposure: d);
  }

  void setGain(int v) {
    if (v < 0) return;
    state = state.copyWith(defaultGain: v);
  }

  void setOffset(int v) {
    if (v < 0) return;
    state = state.copyWith(defaultOffset: v);
  }

  void setBin(int v) {
    if (v < 1) return;
    state = state.copyWith(defaultBin: v);
  }

  void setFrameKind(FrameKind k) =>
      state = state.copyWith(defaultFrameKind: k);

  void setCoolerTargetC(double v) {
    // Sensor cooler practical floor — even high-end mono cameras stop
    // around -45°C (delta from ambient).
    if (v < -60 || v > 30) return;
    state = state.copyWith(coolerTargetC: v);
  }

  void setCoolerRampRate(double v) {
    if (v <= 0 || v > 10) return;
    state = state.copyWith(coolerRampRatePerMin: v);
  }

  void setWarmupAtSessionEnd(bool v) =>
      state = state.copyWith(warmupAtSessionEnd: v);
}

final imagingDefaultsProvider =
    NotifierProvider<ImagingDefaultsNotifier, ImagingDefaults>(
        ImagingDefaultsNotifier.new);
