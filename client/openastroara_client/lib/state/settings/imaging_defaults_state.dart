import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'loaded_section.dart';

import '../../services/profile_api.dart';
import '../imaging/exposure_state.dart' show FrameKind;

/// Profile-level imaging defaults (§37.11 + §29). These seed the
/// `ExposureController` on first build of the Imaging tab; changes from the
/// Settings panel persist back here, through the section module
/// (loaded_section.dart): the rig's copy, loaded before it is saved.

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
  }) => ImagingDefaults(
    defaultExposure: defaultExposure ?? this.defaultExposure,
    defaultGain: defaultGain ?? this.defaultGain,
    defaultOffset: defaultOffset ?? this.defaultOffset,
    defaultBin: defaultBin ?? this.defaultBin,
    defaultFrameKind: defaultFrameKind ?? this.defaultFrameKind,
    coolerTargetC: coolerTargetC ?? this.coolerTargetC,
    coolerRampRatePerMin: coolerRampRatePerMin ?? this.coolerRampRatePerMin,
    warmupAtSessionEnd: warmupAtSessionEnd ?? this.warmupAtSessionEnd,
  );
}

/// ADR 0015 — a value only once the rig has answered. Loads itself: Simple
/// Mode's camera window, plans and calibration read it with no Settings panel
/// open first.
class ImagingDefaultsNotifier extends SelfLoadingSection<ImagingDefaults> {
  // Setters validate at the boundary so the daemon never sees physically
  // impossible defaults. Matches the round-2 ExposureController contract.
  void setExposure(Duration d) {
    if (d <= Duration.zero) return;
    edit((c) => c.copyWith(defaultExposure: d));
  }

  void setGain(int v) {
    if (v < 0) return;
    edit((c) => c.copyWith(defaultGain: v));
  }

  void setOffset(int v) {
    if (v < 0) return;
    edit((c) => c.copyWith(defaultOffset: v));
  }

  void setBin(int v) {
    if (v < 1) return;
    edit((c) => c.copyWith(defaultBin: v));
  }

  void setFrameKind(FrameKind k) =>
      edit((c) => c.copyWith(defaultFrameKind: k));

  /// Sensor cooler practical floor — even high-end mono cameras stop around
  /// -45°C (delta from ambient).
  static bool validCoolerTargetC(double v) => v >= -60 && v <= 30;

  void setCoolerTargetC(double v) {
    if (!validCoolerTargetC(v)) return;
    edit((c) => c.copyWith(coolerTargetC: v));
  }

  void setCoolerRampRate(double v) {
    if (v <= 0 || v > 10) return;
    edit((c) => c.copyWith(coolerRampRatePerMin: v));
  }

  void setWarmupAtSessionEnd(bool v) =>
      edit((c) => c.copyWith(warmupAtSessionEnd: v));

  @override
  Future<Tagged<ImagingDefaults>> fetch(ProfileApi api) =>
      api.getImagingDefaultsSection();

  @override
  Future<Tagged<ImagingDefaults>> send(
    ProfileApi api,
    ImagingDefaults value,
    String? etag,
  ) => api.putImagingDefaultsSection(value, ifMatch: etag);
}

final imagingDefaultsProvider =
    NotifierProvider<ImagingDefaultsNotifier, LoadedSection<ImagingDefaults>>(
      ImagingDefaultsNotifier.new,
    );
