import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';

/// §35 Safety policies. Phase 12h.6g wires the daemon round-trip via
/// [ProfileApi] (`/api/v1/profile/safety`). Local state is still source
/// of truth between syncs.

enum UnsafeAction { pauseAndPark, parkOnly, abortAndPark, ignore }
enum AltitudeLimitAction { skipTarget, pauseSequence, abortSequence }
enum GuiderLostAction { pauseAndRetry, skipTarget, abortSequence }
enum DiskSpaceCriticalAction { warn, abort }

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

  // §29 — on critically-low disk space.
  final DiskSpaceCriticalAction onDiskSpaceCritical;

  // §58.9 — unattended flip safety: the pre-flip flight check + in-slew
  // watchdog + hard pier-side gate. Default on; rigs whose driver misreports
  // pier side can turn it off.
  final bool flipSafetyEnabled;

  // §58.9 — expected flip-slew duration (s). Alpaca has no slew-estimate API,
  // so this stands in for the "mount estimate": the watchdog's hard timeout is
  // min(3 × this, 5 min).
  final int expectedFlipSlewSeconds;

  // §58.8 — whether this profile's one-time first-flip announce has already
  // run (true = later flips are silent). The daemon sets it after the first
  // announced flip and clears it on an optics change; the panel shows the
  // state and offers a manual re-arm (set back to false).
  final bool firstFlipConfirmed;

  // §58.10 — bump equipment-impacting notification severities one level while
  // the site sits in astronomical darkness. Default on.
  final bool unattendedEscalation;

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
    this.onDiskSpaceCritical = DiskSpaceCriticalAction.warn,
    this.flipSafetyEnabled = true,
    this.expectedFlipSlewSeconds = 90,
    this.firstFlipConfirmed = false,
    this.unattendedEscalation = true,
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
    DiskSpaceCriticalAction? onDiskSpaceCritical,
    bool? flipSafetyEnabled,
    int? expectedFlipSlewSeconds,
    bool? firstFlipConfirmed,
    bool? unattendedEscalation,
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
        onDiskSpaceCritical:
            onDiskSpaceCritical ?? this.onDiskSpaceCritical,
        flipSafetyEnabled: flipSafetyEnabled ?? this.flipSafetyEnabled,
        expectedFlipSlewSeconds:
            expectedFlipSlewSeconds ?? this.expectedFlipSlewSeconds,
        firstFlipConfirmed: firstFlipConfirmed ?? this.firstFlipConfirmed,
        unattendedEscalation:
            unattendedEscalation ?? this.unattendedEscalation,
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

  void setFlipSafetyEnabled(bool v) =>
      state = state.copyWith(flipSafetyEnabled: v);

  void setUnattendedEscalation(bool v) =>
      state = state.copyWith(unattendedEscalation: v);

  /// §58.8 — re-arm the one-time first-flip announce (e.g. after re-balancing
  /// or any rig change the optics-based auto-reset can't see).
  void rearmFirstFlipAnnounce() =>
      state = state.copyWith(firstFlipConfirmed: false);

  void setExpectedFlipSlewSeconds(int v) {
    if (v <= 0) return; // the daemon's watchdog needs a positive expectation
    state = state.copyWith(expectedFlipSlewSeconds: v);
  }

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
  void setOnDiskSpaceCritical(DiskSpaceCriticalAction a) =>
      state = state.copyWith(onDiskSpaceCritical: a);

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
