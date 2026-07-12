import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'settings_sync_mixin.dart';

import '../../services/profile_api.dart';
import '../imaging/exposure_state.dart' show FrameKind;

/// Profile-level imaging defaults (§37.11 + §29). These seed the
/// `ExposureController` on first build of the Imaging tab; changes from the
/// Settings panel persist back here. Phase 12h.6b wires the daemon round-
/// trip via [ProfileApi]: [ImagingDefaultsNotifier.hydrateFromServer] runs
/// on panel mount, [ImagingDefaultsNotifier.persistToServer] runs from the
/// Save button. Local state is still the source of truth between syncs.

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

class ImagingDefaultsNotifier extends Notifier<ImagingDefaults>
    with SettingsSyncMixin<ImagingDefaults> {
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

  /// Replace local state with what the daemon currently holds. Called on
  /// Settings panel mount so the user sees the persisted values, not the
  /// in-process defaults. Errors bubble up to the caller for snackbar UI.
  Future<void> hydrateFromServer(ProfileApi api) =>
      hydrateGuarded(() => api.getImagingDefaults());

  /// Send the current local state to the daemon. Returns the daemon's echo
  /// so the panel can confirm what was persisted (and update state if the
  /// daemon normalized any field).
  Future<ImagingDefaults> persistToServer(ProfileApi api) =>
      persistGuarded((sent) => api.putImagingDefaults(sent));
}

final imagingDefaultsProvider =
    NotifierProvider<ImagingDefaultsNotifier, ImagingDefaults>(
        ImagingDefaultsNotifier.new);
