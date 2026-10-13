import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';
import 'settings_sync_mixin.dart';

/// #1264 — Imaging → Unguided (profile section `unguided`): the knobs behind
/// the Tracking Probe, the Unguided Exposure Guard and Center After Drift on a
/// rig without a guider. The run builder reads it when it cooks the nodes; the
/// daemon reads the tolerance basis when it turns the mount's spec sheet into
/// a prior. Defaults ARE the shipped behaviour.
class UnguidedSettings {
  final bool guardEnabled;
  final double eccentricityTolerance;
  final double goodEccentricity;
  final int stepDownAfter;
  final int stepUpAfter;

  /// Ascending ladder before the planned-sub / 2 × prior cap.
  final List<double> probeRungsSeconds;
  final double recentreDriftArcmin;
  final int recentreSolveEveryN;

  /// `max` (1 px or half the seeing, whichever is larger), `pixel`, `seeing`.
  final String priorToleranceBasis;

  const UnguidedSettings({
    this.guardEnabled = true,
    this.eccentricityTolerance = 0.5,
    this.goodEccentricity = 0.3,
    this.stepDownAfter = 2,
    this.stepUpAfter = 10,
    this.probeRungsSeconds = defaultProbeRungsSeconds,
    this.recentreDriftArcmin = 10,
    this.recentreSolveEveryN = 1,
    this.priorToleranceBasis = 'max',
  });

  static const List<double> defaultProbeRungsSeconds = [10, 30, 60, 120];
  static const List<String> toleranceBases = ['max', 'pixel', 'seeing'];

  UnguidedSettings copyWith({
    bool? guardEnabled,
    double? eccentricityTolerance,
    double? goodEccentricity,
    int? stepDownAfter,
    int? stepUpAfter,
    List<double>? probeRungsSeconds,
    double? recentreDriftArcmin,
    int? recentreSolveEveryN,
    String? priorToleranceBasis,
  }) => UnguidedSettings(
    guardEnabled: guardEnabled ?? this.guardEnabled,
    eccentricityTolerance: eccentricityTolerance ?? this.eccentricityTolerance,
    goodEccentricity: goodEccentricity ?? this.goodEccentricity,
    stepDownAfter: stepDownAfter ?? this.stepDownAfter,
    stepUpAfter: stepUpAfter ?? this.stepUpAfter,
    probeRungsSeconds: probeRungsSeconds ?? this.probeRungsSeconds,
    recentreDriftArcmin: recentreDriftArcmin ?? this.recentreDriftArcmin,
    recentreSolveEveryN: recentreSolveEveryN ?? this.recentreSolveEveryN,
    priorToleranceBasis: priorToleranceBasis ?? this.priorToleranceBasis,
  );

  /// The ladder as "10, 30, 60, 120" for the settings row.
  String get probeRungsText => probeRungsSeconds
      .map((r) => r == r.roundToDouble() ? r.toStringAsFixed(0) : r.toStringAsFixed(1))
      .join(', ');

  /// Parse "10, 30, 60" into an ascending positive ladder; null when it is
  /// not one (the row then keeps its canonical value).
  static List<double>? parseRungs(String text) {
    final parts = text.split(RegExp(r'[,\s]+')).where((p) => p.isNotEmpty);
    final out = <double>[];
    for (final p in parts) {
      final v = double.tryParse(p);
      if (v == null || !v.isFinite || v <= 0) return null;
      if (out.isNotEmpty && v <= out.last) return null;
      out.add(v);
    }
    return out.isEmpty || out.length > 10 ? null : out;
  }
}

class UnguidedSettingsNotifier extends Notifier<UnguidedSettings>
    with SettingsSyncMixin<UnguidedSettings> {
  @override
  UnguidedSettings build() => const UnguidedSettings();

  void setGuardEnabled(bool v) => state = state.copyWith(guardEnabled: v);

  /// (0, 1]; the good bar is kept strictly below it.
  void setEccentricityTolerance(double v) {
    if (!v.isFinite || v <= 0 || v > 1) return;
    final good = state.goodEccentricity < v ? state.goodEccentricity : v / 2;
    state = state.copyWith(eccentricityTolerance: v, goodEccentricity: good);
  }

  /// (0, tolerance).
  void setGoodEccentricity(double v) {
    if (!v.isFinite || v <= 0 || v >= state.eccentricityTolerance) return;
    state = state.copyWith(goodEccentricity: v);
  }

  void setStepDownAfter(int v) {
    if (v < 1) return;
    state = state.copyWith(stepDownAfter: v);
  }

  void setStepUpAfter(int v) {
    if (v < 1) return;
    state = state.copyWith(stepUpAfter: v);
  }

  void setProbeRungs(List<double> rungs) {
    if (rungs.isEmpty) return;
    state = state.copyWith(probeRungsSeconds: List.unmodifiable(rungs));
  }

  void setRecentreDriftArcmin(double v) {
    if (!v.isFinite || v <= 0) return;
    state = state.copyWith(recentreDriftArcmin: v);
  }

  void setRecentreSolveEveryN(int v) {
    if (v < 1) return;
    state = state.copyWith(recentreSolveEveryN: v);
  }

  void setPriorToleranceBasis(String v) {
    if (!UnguidedSettings.toleranceBases.contains(v)) return;
    state = state.copyWith(priorToleranceBasis: v);
  }

  Future<void> hydrateFromServer(ProfileApi api) =>
      hydrateGuarded(() => api.getUnguidedSettings());

  Future<UnguidedSettings> persistToServer(ProfileApi api) =>
      persistGuarded((sent) => api.putUnguidedSettings(sent));
}

final unguidedSettingsProvider =
    NotifierProvider<UnguidedSettingsNotifier, UnguidedSettings>(
      UnguidedSettingsNotifier.new,
    );
