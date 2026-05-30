import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';

/// §35 Safety policies. Phase 12h.6g wires the daemon round-trip via
/// [ProfileApi] (`/api/v1/profile/safety`). Local state is still source
/// of truth between syncs.

enum UnsafeAction { pauseAndPark, parkOnly, abortAndPark, ignore }
enum AltitudeLimitAction { skipTarget, pauseSequence, abortSequence }
enum GuiderLostAction { pauseAndRetry, skipTarget, abortSequence }

class SafetyPolicies {
  // On unsafe weather.
  final UnsafeAction onUnsafe;
  final bool autoResumeWhenSafe;
  final int resumeDelayMin;

  // On meridian flip.
  final bool meridianFlipAuto;
  final int meridianPauseMin;
  final bool meridianRecenter;
  final bool meridianRecalGuider;

  // On altitude limit.
  final AltitudeLimitAction onAltitudeLimit;
  final bool parkIfNoMoreTargets;

  // On guider lost.
  final GuiderLostAction onGuiderLost;
  final int guiderRetryTimeoutSec;
  final bool skipTargetIfRecoveryFails;

  const SafetyPolicies({
    this.onUnsafe = UnsafeAction.pauseAndPark,
    this.autoResumeWhenSafe = true,
    this.resumeDelayMin = 10,
    this.meridianFlipAuto = true,
    this.meridianPauseMin = 5,
    this.meridianRecenter = true,
    this.meridianRecalGuider = true,
    this.onAltitudeLimit = AltitudeLimitAction.skipTarget,
    this.parkIfNoMoreTargets = true,
    this.onGuiderLost = GuiderLostAction.pauseAndRetry,
    this.guiderRetryTimeoutSec = 60,
    this.skipTargetIfRecoveryFails = true,
  });

  SafetyPolicies copyWith({
    UnsafeAction? onUnsafe,
    bool? autoResumeWhenSafe,
    int? resumeDelayMin,
    bool? meridianFlipAuto,
    int? meridianPauseMin,
    bool? meridianRecenter,
    bool? meridianRecalGuider,
    AltitudeLimitAction? onAltitudeLimit,
    bool? parkIfNoMoreTargets,
    GuiderLostAction? onGuiderLost,
    int? guiderRetryTimeoutSec,
    bool? skipTargetIfRecoveryFails,
  }) =>
      SafetyPolicies(
        onUnsafe: onUnsafe ?? this.onUnsafe,
        autoResumeWhenSafe: autoResumeWhenSafe ?? this.autoResumeWhenSafe,
        resumeDelayMin: resumeDelayMin ?? this.resumeDelayMin,
        meridianFlipAuto: meridianFlipAuto ?? this.meridianFlipAuto,
        meridianPauseMin: meridianPauseMin ?? this.meridianPauseMin,
        meridianRecenter: meridianRecenter ?? this.meridianRecenter,
        meridianRecalGuider: meridianRecalGuider ?? this.meridianRecalGuider,
        onAltitudeLimit: onAltitudeLimit ?? this.onAltitudeLimit,
        parkIfNoMoreTargets:
            parkIfNoMoreTargets ?? this.parkIfNoMoreTargets,
        onGuiderLost: onGuiderLost ?? this.onGuiderLost,
        guiderRetryTimeoutSec:
            guiderRetryTimeoutSec ?? this.guiderRetryTimeoutSec,
        skipTargetIfRecoveryFails:
            skipTargetIfRecoveryFails ?? this.skipTargetIfRecoveryFails,
      );
}

class SafetyPoliciesNotifier extends Notifier<SafetyPolicies> {
  @override
  SafetyPolicies build() => const SafetyPolicies();

  void setOnUnsafe(UnsafeAction a) => state = state.copyWith(onUnsafe: a);
  void setAutoResumeWhenSafe(bool v) =>
      state = state.copyWith(autoResumeWhenSafe: v);

  void setResumeDelayMin(int v) {
    if (v < 0) return;
    state = state.copyWith(resumeDelayMin: v);
  }

  void setMeridianFlipAuto(bool v) =>
      state = state.copyWith(meridianFlipAuto: v);

  void setMeridianPauseMin(int v) {
    if (v < 0) return;
    state = state.copyWith(meridianPauseMin: v);
  }

  void setMeridianRecenter(bool v) =>
      state = state.copyWith(meridianRecenter: v);
  void setMeridianRecalGuider(bool v) =>
      state = state.copyWith(meridianRecalGuider: v);
  void setOnAltitudeLimit(AltitudeLimitAction a) =>
      state = state.copyWith(onAltitudeLimit: a);
  void setParkIfNoMoreTargets(bool v) =>
      state = state.copyWith(parkIfNoMoreTargets: v);
  void setOnGuiderLost(GuiderLostAction a) =>
      state = state.copyWith(onGuiderLost: a);

  void setGuiderRetryTimeoutSec(int v) {
    if (v < 0) return;
    state = state.copyWith(guiderRetryTimeoutSec: v);
  }

  void setSkipTargetIfRecoveryFails(bool v) =>
      state = state.copyWith(skipTargetIfRecoveryFails: v);

  Future<void> hydrateFromServer(ProfileApi api) async {
    state = await api.getSafetyPolicies();
  }

  Future<SafetyPolicies> persistToServer(ProfileApi api) async {
    final echoed = await api.putSafetyPolicies(state);
    state = echoed;
    return echoed;
  }
}

final safetyPoliciesProvider =
    NotifierProvider<SafetyPoliciesNotifier, SafetyPolicies>(
        SafetyPoliciesNotifier.new);
