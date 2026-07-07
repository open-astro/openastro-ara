import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';

/// §35 Safety policies. Phase 12h.6g wires the daemon round-trip via
/// [ProfileApi] (`/api/v1/profile/safety`). Local state is still source
/// of truth between syncs.

enum UnsafeAction { pauseAndPark, parkOnly, abortAndPark, ignore }
enum AltitudeLimitAction { skipTarget, pauseSequence, abortSequence }
enum GuiderLostAction { pauseAndRetry, skipTarget, abortSequence }
enum DiskSpaceCriticalAction { warn, abort }

/// §48.2 — the sequence-start "capture calibration tonight?" behaviour.
enum CalibrationCaptureDefault { ask, panelAtEnd, skyAtTwilight, never }

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

  // §58.12 — when a run pauses awaiting the user and nobody responds within
  // the wait window, the daemon gracefully shuts the rig down (guider stopped,
  // mount parked, equipment disconnected, cooler warmed). Default on; the spec
  // allows disabling but flags it as not recommended.
  final bool unattendedShutdownEnabled;

  // §58.12 — minutes of unattended silence before that shutdown executes.
  // 10 is the Goldilocks default; 5 for battery rigs, 30 for wall-powered
  // observatories.
  final int unattendedShutdownWaitMinutes;

  // §48.2 — whether starting a sequence prompts for end-of-night calibration
  // capture ("ask", the default), silently auto-decides (panel/sky), or stays
  // quiet ("never"). Rides the same profile safety-policies document.
  final CalibrationCaptureDefault calibrationCaptureDefault;

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
    this.unattendedShutdownEnabled = true,
    this.unattendedShutdownWaitMinutes = 10,
    this.calibrationCaptureDefault = CalibrationCaptureDefault.ask,
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
    bool? unattendedShutdownEnabled,
    int? unattendedShutdownWaitMinutes,
    CalibrationCaptureDefault? calibrationCaptureDefault,
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
        unattendedShutdownEnabled:
            unattendedShutdownEnabled ?? this.unattendedShutdownEnabled,
        unattendedShutdownWaitMinutes:
            unattendedShutdownWaitMinutes ?? this.unattendedShutdownWaitMinutes,
        calibrationCaptureDefault:
            calibrationCaptureDefault ?? this.calibrationCaptureDefault,
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

  void setUnattendedShutdownEnabled(bool v) =>
      state = state.copyWith(unattendedShutdownEnabled: v);

  void setUnattendedShutdownWaitMinutes(int v) {
    if (v < 1) return; // the daemon clamps to >= 1 minute; mirror it here
    state = state.copyWith(unattendedShutdownWaitMinutes: v);
  }

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
  void setCalibrationCaptureDefault(CalibrationCaptureDefault v) =>
      state = state.copyWith(calibrationCaptureDefault: v);
  void setOnDiskSpaceCritical(DiskSpaceCriticalAction a) =>
      state = state.copyWith(onDiskSpaceCritical: a);

  /// §58.8 — re-arm the one-time first-flip announce (e.g. after re-balancing
  /// or any rig change the optics-based auto-reset can't see). Goes straight
  /// to the daemon's dedicated endpoint — the flag is DAEMON-owned (the flip
  /// executor sets it out-of-band, and the general safety PUT ignores it), so
  /// a local-only mutation deferred to Save could clobber or be clobbered by
  /// an overnight flip. State refreshes from the daemon's echoed policies.
  Future<void> rearmFirstFlip(ProfileApi api) async {
    final echoed = await api.rearmFirstFlip();
    // Patch ONLY the daemon-owned flag into local state: the echo carries the
    // PERSISTED policies, and replacing the whole object would silently
    // discard any unsaved edits staged in the panel — the same stale-snapshot
    // clobber class the server-side merge closes for the daemon-owned field,
    // mirrored here for the thirteen user-owned ones.
    state = state.copyWith(firstFlipConfirmed: echoed.firstFlipConfirmed);
  }

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
