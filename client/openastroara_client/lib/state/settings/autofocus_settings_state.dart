import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';

/// §37.11 Autofocus settings. Phase 12h.6h wires the daemon round-trip
/// via [ProfileApi] (`/api/v1/profile/autofocus`). Local state is the
/// source of truth between syncs.

enum AutofocusMethod { hfrVCurve, brightestStarHfr, fwhm }

class AutofocusSettings {
  final AutofocusMethod method;
  final int steps;
  final int stepSize;
  final int exposureSeconds;
  final int binning;
  final String afFilter;
  final bool runAfterFilterChange;
  final double triggerTempDeltaC;
  final double triggerHfrDriftPct;
  final int everyNHours;
  final bool abortSequenceOnAfFailure;
  final bool restorePositionOnFailure;

  const AutofocusSettings({
    this.method = AutofocusMethod.hfrVCurve,
    this.steps = 7,
    this.stepSize = 50,
    this.exposureSeconds = 5,
    this.binning = 1,
    this.afFilter = 'L',
    this.runAfterFilterChange = true,
    this.triggerTempDeltaC = 2.0,
    this.triggerHfrDriftPct = 15,
    this.everyNHours = 2,
    this.abortSequenceOnAfFailure = true,
    this.restorePositionOnFailure = true,
  });

  AutofocusSettings copyWith({
    AutofocusMethod? method,
    int? steps,
    int? stepSize,
    int? exposureSeconds,
    int? binning,
    String? afFilter,
    bool? runAfterFilterChange,
    double? triggerTempDeltaC,
    double? triggerHfrDriftPct,
    int? everyNHours,
    bool? abortSequenceOnAfFailure,
    bool? restorePositionOnFailure,
  }) =>
      AutofocusSettings(
        method: method ?? this.method,
        steps: steps ?? this.steps,
        stepSize: stepSize ?? this.stepSize,
        exposureSeconds: exposureSeconds ?? this.exposureSeconds,
        binning: binning ?? this.binning,
        afFilter: afFilter ?? this.afFilter,
        runAfterFilterChange:
            runAfterFilterChange ?? this.runAfterFilterChange,
        triggerTempDeltaC: triggerTempDeltaC ?? this.triggerTempDeltaC,
        triggerHfrDriftPct: triggerHfrDriftPct ?? this.triggerHfrDriftPct,
        everyNHours: everyNHours ?? this.everyNHours,
        abortSequenceOnAfFailure:
            abortSequenceOnAfFailure ?? this.abortSequenceOnAfFailure,
        restorePositionOnFailure:
            restorePositionOnFailure ?? this.restorePositionOnFailure,
      );
}

class AutofocusSettingsNotifier extends Notifier<AutofocusSettings> {
  @override
  AutofocusSettings build() => const AutofocusSettings();

  void setMethod(AutofocusMethod m) => state = state.copyWith(method: m);

  void setSteps(int v) {
    // V-curve needs at least 3 points to fit; 31 is the practical upper
    // bound to keep AF runs under a few minutes.
    if (v < 3 || v > 31) return;
    state = state.copyWith(steps: v);
  }

  void setStepSize(int v) {
    if (v <= 0) return;
    state = state.copyWith(stepSize: v);
  }

  void setExposureSeconds(int v) {
    if (v <= 0) return;
    state = state.copyWith(exposureSeconds: v);
  }

  void setBinning(int v) {
    if (v < 1) return;
    state = state.copyWith(binning: v);
  }

  void setAfFilter(String s) {
    final v = s.trim();
    if (v.isEmpty) return;
    state = state.copyWith(afFilter: v);
  }

  void setRunAfterFilterChange(bool v) =>
      state = state.copyWith(runAfterFilterChange: v);

  void setTriggerTempDeltaC(double v) {
    if (v < 0) return;
    state = state.copyWith(triggerTempDeltaC: v);
  }

  void setTriggerHfrDriftPct(double v) {
    if (v < 0 || v > 100) return;
    state = state.copyWith(triggerHfrDriftPct: v);
  }

  void setEveryNHours(int v) {
    if (v < 0) return;
    state = state.copyWith(everyNHours: v);
  }

  void setAbortSequenceOnAfFailure(bool v) =>
      state = state.copyWith(abortSequenceOnAfFailure: v);
  void setRestorePositionOnFailure(bool v) =>
      state = state.copyWith(restorePositionOnFailure: v);

  Future<void> hydrateFromServer(ProfileApi api) async {
    state = await api.getAutofocusSettings();
  }

  Future<AutofocusSettings> persistToServer(ProfileApi api) async {
    final echoed = await api.putAutofocusSettings(state);
    state = echoed;
    return echoed;
  }
}

final autofocusSettingsProvider =
    NotifierProvider<AutofocusSettingsNotifier, AutofocusSettings>(
        AutofocusSettingsNotifier.new);
