import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';
import 'loaded_section.dart';

/// #1264 — Imaging → Unguided (profile section `unguided`): the knobs behind
/// the Tracking Probe, the Unguided Exposure Guard and Center After Drift on a
/// rig without a guider. The run builder reads it when it cooks the nodes; the
/// daemon reads the tolerance basis when it turns the mount's spec sheet into
/// a prior. The daemon's section defaults are the shipped behaviour.
class UnguidedSettings {
  final bool guardEnabled;
  final double eccentricityTolerance;
  final double goodEccentricity;
  final int stepDownAfter;
  final int stepUpAfter;

  /// Ascending ladder before the planned-sub / 2 × prior cap; null (or
  /// empty) is the daemon's own ladder, see [effectiveProbeRungsSeconds].
  final List<double>? probeRungsSeconds;
  final double recentreDriftArcmin;
  final int recentreSolveEveryN;

  /// `max` (1 px or half the seeing, whichever is larger), `pixel`, `seeing`.
  final String priorToleranceBasis;

  // No defaults: the daemon's normalizer is the one owner (ADR 0012, ADR
  // 0015 rule 3), so a value is the rig's copy or the offline cache's.
  const UnguidedSettings({
    required this.guardEnabled,
    required this.eccentricityTolerance,
    required this.goodEccentricity,
    required this.stepDownAfter,
    required this.stepUpAfter,
    required this.probeRungsSeconds,
    required this.recentreDriftArcmin,
    required this.recentreSolveEveryN,
    required this.priorToleranceBasis,
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

  /// The ladder the probe runs: the daemon's default when the section holds
  /// none (`UnguidedSettingsDto.EffectiveProbeRungsSeconds`).
  List<double> get effectiveProbeRungsSeconds {
    final r = probeRungsSeconds;
    return r == null || r.isEmpty ? defaultProbeRungsSeconds : r;
  }

  /// The ladder as "10, 30, 60, 120" for the settings row.
  String get probeRungsText => effectiveProbeRungsSeconds
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

class UnguidedSettingsNotifier extends SectionNotifier<UnguidedSettings> {
  void setGuardEnabled(bool v) => edit((c) => c.copyWith(guardEnabled: v));

  /// (0, 1]; the good bar is kept strictly below it.
  void setEccentricityTolerance(double v) {
    if (!v.isFinite || v <= 0 || v > 1) return;
    edit((c) {
      final good = c.goodEccentricity < v ? c.goodEccentricity : v / 2;
      return c.copyWith(eccentricityTolerance: v, goodEccentricity: good);
    });
  }

  /// (0, tolerance).
  void setGoodEccentricity(double v) {
    edit(
      (c) => !v.isFinite || v <= 0 || v >= c.eccentricityTolerance
          ? c
          : c.copyWith(goodEccentricity: v),
    );
  }

  void setStepDownAfter(int v) {
    if (v < 1) return;
    edit((c) => c.copyWith(stepDownAfter: v));
  }

  void setStepUpAfter(int v) {
    if (v < 1) return;
    edit((c) => c.copyWith(stepUpAfter: v));
  }

  void setProbeRungs(List<double> rungs) {
    if (rungs.isEmpty) return;
    edit((c) => c.copyWith(probeRungsSeconds: List.unmodifiable(rungs)));
  }

  void setRecentreDriftArcmin(double v) {
    if (!v.isFinite || v <= 0) return;
    edit((c) => c.copyWith(recentreDriftArcmin: v));
  }

  void setRecentreSolveEveryN(int v) {
    if (v < 1) return;
    edit((c) => c.copyWith(recentreSolveEveryN: v));
  }

  void setPriorToleranceBasis(String v) {
    if (!UnguidedSettings.toleranceBases.contains(v)) return;
    edit((c) => c.copyWith(priorToleranceBasis: v));
  }

  @override
  Future<Tagged<UnguidedSettings>> fetch(ProfileApi api) =>
      api.getUnguidedSection();

  @override
  Future<Tagged<UnguidedSettings>> send(
    ProfileApi api,
    UnguidedSettings value,
    String? etag,
  ) => api.putUnguidedSection(value, ifMatch: etag);
}

final unguidedSettingsProvider =
    NotifierProvider<UnguidedSettingsNotifier, LoadedSection<UnguidedSettings>>(
      UnguidedSettingsNotifier.new,
    );
