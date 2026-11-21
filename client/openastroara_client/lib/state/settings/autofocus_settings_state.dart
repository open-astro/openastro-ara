import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../focus/focus_presets.dart';
import 'loaded_section.dart';

import '../../services/profile_api.dart';

/// §37.11 Autofocus settings. Phase 12h.6h wires the daemon round-trip
/// via [ProfileApi] (`/api/v1/profile/autofocus`), through the section
/// module (loaded_section.dart): the rig's copy, loaded before it is saved.

enum AutofocusMethod { hfrVCurve, brightestStarHfr, fwhm }

/// §59.4 — the optical design, declared once (wizard §59.14 or this panel).
/// Smart Focus picks its defocus features from it; [other] assumes nothing
/// (HFR-only, no side classification) and is the safe default.
enum TelescopeType { refractor, sct, mak, rc, newtonian, other }

/// §59.13 wire strings (exact; the daemon treats anything else as `other`).
TelescopeType telescopeTypeFromWire(String? s) => switch (s) {
      'refractor' => TelescopeType.refractor,
      'sct' => TelescopeType.sct,
      'mak' => TelescopeType.mak,
      'rc' => TelescopeType.rc,
      'newtonian' => TelescopeType.newtonian,
      _ => TelescopeType.other,
    };

String telescopeTypeToWire(TelescopeType t) => switch (t) {
      TelescopeType.refractor => 'refractor',
      TelescopeType.sct => 'sct',
      TelescopeType.mak => 'mak',
      TelescopeType.rc => 'rc',
      TelescopeType.newtonian => 'newtonian',
      TelescopeType.other => 'other',
    };

class AutofocusSettings {
  final AutofocusMethod method;
  final int steps;
  final int stepSize;

  /// §59.8 — size the sweep automatically: from the V half-width the last
  /// sweep measured, else half the CFZ when the focuser reports µm/step, else
  /// [stepSize] as the deliberately wide first sweep. Off, [stepSize] is used
  /// as typed.
  final bool stepSizeAuto;
  final int exposureSeconds;
  final int binning;
  final String afFilter;
  final bool runAfterFilterChange;
  final double triggerTempDeltaC;
  final double triggerHfrDriftPct;
  final int everyNHours;
  final bool abortSequenceOnAfFailure;
  final bool restorePositionOnFailure;
  final TelescopeType telescopeType;

  /// #1541 — focuser backlash (steps) from the setup wizard; the daemon
  /// overshoots by the one for the new direction on a reversal. 0 = none.
  final int backlashInSteps;
  final int backlashOutSteps;

  const AutofocusSettings({
    this.method = AutofocusMethod.hfrVCurve,
    this.steps = 7,
    this.stepSize = 50,
    this.stepSizeAuto = true,
    this.exposureSeconds = 5,
    this.binning = 1,
    this.afFilter = 'L',
    this.runAfterFilterChange = true,
    this.triggerTempDeltaC = 2.0,
    this.triggerHfrDriftPct = 15,
    this.everyNHours = 2,
    this.abortSequenceOnAfFailure = true,
    this.restorePositionOnFailure = true,
    this.telescopeType = TelescopeType.other,
    this.backlashInSteps = 0,
    this.backlashOutSteps = 0,
  });

  AutofocusSettings copyWith({
    AutofocusMethod? method,
    int? steps,
    int? stepSize,
    bool? stepSizeAuto,
    int? exposureSeconds,
    int? binning,
    String? afFilter,
    bool? runAfterFilterChange,
    double? triggerTempDeltaC,
    double? triggerHfrDriftPct,
    int? everyNHours,
    bool? abortSequenceOnAfFailure,
    bool? restorePositionOnFailure,
    TelescopeType? telescopeType,
    int? backlashInSteps,
    int? backlashOutSteps,
  }) =>
      AutofocusSettings(
        method: method ?? this.method,
        steps: steps ?? this.steps,
        stepSize: stepSize ?? this.stepSize,
        stepSizeAuto: stepSizeAuto ?? this.stepSizeAuto,
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
        telescopeType: telescopeType ?? this.telescopeType,
        backlashInSteps: backlashInSteps ?? this.backlashInSteps,
        backlashOutSteps: backlashOutSteps ?? this.backlashOutSteps,
      );
}

/// ADR 0015 — a value only once the rig has answered. Loads itself: Simple
/// Mode's Focuser window and Focus screen, the focus cards and plans read it
/// with no Settings panel open first.
class AutofocusSettingsNotifier extends SelfLoadingSection<AutofocusSettings> {
  void setMethod(AutofocusMethod m) => edit((c) => c.copyWith(method: m));

  void setSteps(int v) {
    // V-curve needs at least 3 points to fit; 31 is the practical upper
    // bound to keep AF runs under a few minutes.
    if (v < 3 || v > 31) return;
    edit((c) => c.copyWith(steps: v));
  }

  void setStepSize(int v) {
    if (v <= 0) return;
    edit((c) => c.copyWith(stepSize: v));
  }

  void setStepSizeAuto(bool v) => edit((c) => c.copyWith(stepSizeAuto: v));

  void setExposureSeconds(int v) {
    if (v <= 0) return;
    edit((c) => c.copyWith(exposureSeconds: v));
  }

  void setBinning(int v) {
    if (v < 1) return;
    edit((c) => c.copyWith(binning: v));
  }

  void setAfFilter(String s) {
    final v = s.trim();
    if (v.isEmpty) return;
    edit((c) => c.copyWith(afFilter: v));
  }

  void setRunAfterFilterChange(bool v) =>
      edit((c) => c.copyWith(runAfterFilterChange: v));

  void setTriggerTempDeltaC(double v) {
    if (v < 0) return;
    edit((c) => c.copyWith(triggerTempDeltaC: v));
  }

  void setTriggerHfrDriftPct(double v) {
    if (v < 0 || v > 100) return;
    edit((c) => c.copyWith(triggerHfrDriftPct: v));
  }

  void setEveryNHours(int v) {
    if (v < 0) return;
    edit((c) => c.copyWith(everyNHours: v));
  }

  void setAbortSequenceOnAfFailure(bool v) =>
      edit((c) => c.copyWith(abortSequenceOnAfFailure: v));
  void setRestorePositionOnFailure(bool v) =>
      edit((c) => c.copyWith(restorePositionOnFailure: v));
  void setTelescopeType(TelescopeType v) =>
      edit((c) => c.copyWith(telescopeType: v));

  /// #1544 — Simple Mode's "Keep focus sharp": all four triggers at once.
  void applyKeepFocusSharp(KeepFocusSharp p) =>
      edit((c) => withKeepFocusSharp(c, p));

  /// #1544 — Simple Mode's "Autofocus during plans". On from off applies the
  /// Normal preset (today's defaults); off clears all four triggers.
  void setAutofocusDuringPlans(bool on) =>
      edit((c) => withAutofocusDuringPlans(c, on));

  @override
  Future<Tagged<AutofocusSettings>> fetch(ProfileApi api) =>
      api.getAutofocusSection();

  @override
  Future<Tagged<AutofocusSettings>> send(
    ProfileApi api,
    AutofocusSettings value,
    String? etag,
  ) => api.putAutofocusSection(value, ifMatch: etag);
}

final autofocusSettingsProvider =
    NotifierProvider<
      AutofocusSettingsNotifier,
      LoadedSection<AutofocusSettings>
    >(AutofocusSettingsNotifier.new);
