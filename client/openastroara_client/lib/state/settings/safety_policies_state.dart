import 'package:flutter/foundation.dart' show mapEquals;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'loaded_section.dart';

import '../../services/profile_api.dart';

/// §35 Safety policies. Backed by the daemon's `GET`/`PUT
/// /api/v1/profile/safety` section through the section module
/// (loaded_section.dart): the rig's copy, loaded before it is saved.

enum UnsafeAction { pauseAndPark, parkOnly, abortAndPark, ignore }
enum AltitudeLimitAction { skipTarget, pauseSequence, abortSequence }
enum GuiderLostAction { pauseAndRetry, skipTarget, abortSequence }
/// #1185 — `pause` suspends the running sequence (resumable once space is freed).
enum DiskSpaceCriticalAction { warn, pause, abort }

/// §58.4 step 3 / §58.6 (#1138) — re-focus after a meridian flip.
enum MeridianRefocusMode { always, ifTempDrifted, never }

/// §58.4 step 4 / §58.6 — how guiding comes back after a meridian flip.
enum GuiderRecalMode { autoRestore, full, skip }

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
  // Legacy bool the wire still carries (true ≡ meridianGuiderRecal != skip).
  final bool meridianRecalGuider;

  // §58.6 (#1138) — re-focus after the flip, and the drift that triggers it in
  // if_temp_drifted mode (focuser temperature vs the session's last autofocus).
  final MeridianRefocusMode meridianRefocus;
  final double meridianRefocusTempThresholdC;

  // §58.6 — auto_restore (the guider's own restore, ~30 s), full (5-10 min
  // recalibration) or skip (do not resume guiding). Null = the profile predates
  // the mode: the daemon keeps its old behaviour (guiding restored when a guider
  // is connected; the legacy bool alone says whether one is required) and the
  // client leaves the field untouched until the dropdown is used.
  final GuiderRecalMode? meridianGuiderRecal;

  // §58.10 (#1138) — an explicit unattended window, site-local HH:mm start and
  // end (wrapping past midnight). Both blank = computed from the sun.
  final String unattendedWindowStart;
  final String unattendedWindowEnd;

  // Unused (#1589): no daemon code reads on_altitude_limit and the panel no
  // longer shows it. Kept only so the field round-trips on the wire.
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

  // §58.8 — whether the user confirmed a completed meridian flip on this
  // profile (true = later flips are silent). Until then every flip alerts and
  // waits 60 s. The daemon owns it: the user confirms through
  // [ProfileApi.confirmFirstFlip] (#1589) after a flip completed, and re-arm
  // or an optics change clears it.
  final bool firstFlipConfirmed;

  // #1589 — the last flip that ran to the end while the profile was not
  // confirmed (null = none), and whether the mount reported its pier side
  // changing (false = the mount did not report its pier side). Daemon-owned.
  final DateTime? firstFlipCompletedUtc;
  final bool firstFlipPierSideChanged;

  // #1589 — the clearance limit: the highest altitude this rig may point at
  // before the camera or rotator can reach the pier or tripod. While it is on,
  // the rig refuses a GoTo (and a meridian flip) to a target at or above it,
  // and stops tracking and guiding and pauses the run when the mount tracks up
  // to it. The rig owns the value; it is off until the user turns it on.
  final bool clearanceLimitEnabled;
  final double clearanceMaxAltitudeDeg;

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

  // §35.1 — granular weather thresholds over the connected weather station.
  // Default OFF: an upgraded rig must not surprise-park because its station
  // reads breezy. A breach makes conditions unsafe through the same on-unsafe
  // reaction as the safety monitor.
  final bool weatherTriggersEnabled;
  final int maxWindKmh;
  final int maxHumidityPct;
  final double minDewDeltaC;

  // §48.7 flat_panel — the auto-exposure flat sets the daemon generates
  // (target mean ADU, tolerance, saved frames per filter, park after).
  final int flatTargetAdu;
  // Panel flats aim at this share of the camera's own full scale (supersedes
  // flatTargetAdu, which stays only so older daemons round-trip).
  final double flatTargetPercent;
  final double flatTargetAduTolerancePct;
  final int flatFramesPerFilter;
  final bool postFlatParkMount;

  // §48.4 sky_flat — the twilight sky-flat sets the daemon generates (target mean ADU, saved
  // frames per filter, the sky patch to point at, the ADU window it stops outside of, and the
  // sun altitude the sequence waits for before it starts).
  final int skyFlatTargetAdu;
  final int skyFlatFramesPerFilter;
  final double skyFlatTargetAzimuth;
  final double skyFlatTargetAltitude;
  final double skyFlatStopAtMaxAdu;
  final double skyFlatStopAtMinAdu;
  final double skyFlatSunAltitude;

  // The rest of the rig's copy, kept as served and sent back on every Save:
  // the daemon's fault policies (hot reconnect, camera, mount and tracking
  // lost, switch tolerance) have no field here, and the daemon fills a key a
  // PUT leaves out with its own default (#1807).
  final Map<String, Object?> unmodeled;

  const SafetyPolicies({
    this.onUnsafe = UnsafeAction.pauseAndPark,
    this.autoResumeWhenSafe = true,
    this.resumeDelayMin = 10,
    this.meridianFlipAuto = true,
    this.meridianPauseMin = 5,
    this.meridianRecenter = true,
    this.meridianRecalGuider = true,
    this.meridianRefocus = MeridianRefocusMode.ifTempDrifted,
    this.meridianRefocusTempThresholdC = 2.0,
    this.meridianGuiderRecal = GuiderRecalMode.autoRestore,
    this.unattendedWindowStart = '',
    this.unattendedWindowEnd = '',
    this.onAltitudeLimit = AltitudeLimitAction.skipTarget,
    this.parkIfNoMoreTargets = true,
    this.onGuiderLost = GuiderLostAction.pauseAndRetry,
    this.guiderRetryTimeoutSec = 60,
    this.skipTargetIfRecoveryFails = true,
    this.onDiskSpaceCritical = DiskSpaceCriticalAction.warn,
    this.flipSafetyEnabled = true,
    this.expectedFlipSlewSeconds = 90,
    this.firstFlipConfirmed = false,
    this.firstFlipCompletedUtc,
    this.firstFlipPierSideChanged = false,
    this.clearanceLimitEnabled = false,
    this.clearanceMaxAltitudeDeg = 80.0,
    this.unattendedEscalation = true,
    this.unattendedShutdownEnabled = true,
    this.unattendedShutdownWaitMinutes = 30,
    this.calibrationCaptureDefault = CalibrationCaptureDefault.ask,
    this.weatherTriggersEnabled = false,
    this.maxWindKmh = 36,
    this.maxHumidityPct = 85,
    this.minDewDeltaC = 2.0,
    this.flatTargetAdu = 30000,
    this.flatTargetPercent = 50,
    this.flatTargetAduTolerancePct = 5.0,
    this.flatFramesPerFilter = 30,
    this.postFlatParkMount = false,
    this.skyFlatTargetAdu = 25000,
    this.skyFlatFramesPerFilter = 20,
    this.skyFlatTargetAzimuth = 90.0,
    this.skyFlatTargetAltitude = 75.0,
    this.skyFlatStopAtMaxAdu = 50000.0,
    this.skyFlatStopAtMinAdu = 5000.0,
    this.skyFlatSunAltitude = -9.0,
    this.unmodeled = const {},
  });

  SafetyPolicies copyWith({
    UnsafeAction? onUnsafe,
    bool? autoResumeWhenSafe,
    int? resumeDelayMin,
    bool? meridianFlipAuto,
    int? meridianPauseMin,
    bool? meridianRecenter,
    bool? meridianRecalGuider,
    MeridianRefocusMode? meridianRefocus,
    double? meridianRefocusTempThresholdC,
    GuiderRecalMode? meridianGuiderRecal,
    String? unattendedWindowStart,
    String? unattendedWindowEnd,
    AltitudeLimitAction? onAltitudeLimit,
    bool? parkIfNoMoreTargets,
    GuiderLostAction? onGuiderLost,
    int? guiderRetryTimeoutSec,
    bool? skipTargetIfRecoveryFails,
    DiskSpaceCriticalAction? onDiskSpaceCritical,
    bool? flipSafetyEnabled,
    int? expectedFlipSlewSeconds,
    bool? firstFlipConfirmed,
    DateTime? firstFlipCompletedUtc,
    bool clearFirstFlipCompletedUtc = false,
    bool? firstFlipPierSideChanged,
    bool? clearanceLimitEnabled,
    double? clearanceMaxAltitudeDeg,
    bool? unattendedEscalation,
    bool? unattendedShutdownEnabled,
    int? unattendedShutdownWaitMinutes,
    CalibrationCaptureDefault? calibrationCaptureDefault,
    bool? weatherTriggersEnabled,
    int? maxWindKmh,
    int? maxHumidityPct,
    double? minDewDeltaC,
    int? flatTargetAdu,
    double? flatTargetPercent,
    double? flatTargetAduTolerancePct,
    int? flatFramesPerFilter,
    bool? postFlatParkMount,
    int? skyFlatTargetAdu,
    int? skyFlatFramesPerFilter,
    double? skyFlatTargetAzimuth,
    double? skyFlatTargetAltitude,
    double? skyFlatStopAtMaxAdu,
    double? skyFlatStopAtMinAdu,
    double? skyFlatSunAltitude,
    Map<String, Object?>? unmodeled,
  }) =>
      SafetyPolicies(
        onUnsafe: onUnsafe ?? this.onUnsafe,
        autoResumeWhenSafe: autoResumeWhenSafe ?? this.autoResumeWhenSafe,
        resumeDelayMin: resumeDelayMin ?? this.resumeDelayMin,
        meridianFlipAuto: meridianFlipAuto ?? this.meridianFlipAuto,
        meridianPauseMin: meridianPauseMin ?? this.meridianPauseMin,
        meridianRecenter: meridianRecenter ?? this.meridianRecenter,
        meridianRecalGuider: meridianRecalGuider ?? this.meridianRecalGuider,
        meridianRefocus: meridianRefocus ?? this.meridianRefocus,
        meridianRefocusTempThresholdC:
            meridianRefocusTempThresholdC ?? this.meridianRefocusTempThresholdC,
        meridianGuiderRecal: meridianGuiderRecal ?? this.meridianGuiderRecal,
        unattendedWindowStart:
            unattendedWindowStart ?? this.unattendedWindowStart,
        unattendedWindowEnd: unattendedWindowEnd ?? this.unattendedWindowEnd,
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
        firstFlipCompletedUtc: clearFirstFlipCompletedUtc
            ? null
            : firstFlipCompletedUtc ?? this.firstFlipCompletedUtc,
        firstFlipPierSideChanged:
            firstFlipPierSideChanged ?? this.firstFlipPierSideChanged,
        clearanceLimitEnabled:
            clearanceLimitEnabled ?? this.clearanceLimitEnabled,
        clearanceMaxAltitudeDeg:
            clearanceMaxAltitudeDeg ?? this.clearanceMaxAltitudeDeg,
        unattendedEscalation:
            unattendedEscalation ?? this.unattendedEscalation,
        unattendedShutdownEnabled:
            unattendedShutdownEnabled ?? this.unattendedShutdownEnabled,
        unattendedShutdownWaitMinutes:
            unattendedShutdownWaitMinutes ?? this.unattendedShutdownWaitMinutes,
        calibrationCaptureDefault:
            calibrationCaptureDefault ?? this.calibrationCaptureDefault,
        weatherTriggersEnabled:
            weatherTriggersEnabled ?? this.weatherTriggersEnabled,
        maxWindKmh: maxWindKmh ?? this.maxWindKmh,
        maxHumidityPct: maxHumidityPct ?? this.maxHumidityPct,
        minDewDeltaC: minDewDeltaC ?? this.minDewDeltaC,
        flatTargetAdu: flatTargetAdu ?? this.flatTargetAdu,
        flatTargetPercent: flatTargetPercent ?? this.flatTargetPercent,
        flatTargetAduTolerancePct:
            flatTargetAduTolerancePct ?? this.flatTargetAduTolerancePct,
        flatFramesPerFilter: flatFramesPerFilter ?? this.flatFramesPerFilter,
        postFlatParkMount: postFlatParkMount ?? this.postFlatParkMount,
        skyFlatTargetAdu: skyFlatTargetAdu ?? this.skyFlatTargetAdu,
        skyFlatFramesPerFilter:
            skyFlatFramesPerFilter ?? this.skyFlatFramesPerFilter,
        skyFlatTargetAzimuth:
            skyFlatTargetAzimuth ?? this.skyFlatTargetAzimuth,
        skyFlatTargetAltitude:
            skyFlatTargetAltitude ?? this.skyFlatTargetAltitude,
        skyFlatStopAtMaxAdu: skyFlatStopAtMaxAdu ?? this.skyFlatStopAtMaxAdu,
        skyFlatStopAtMinAdu: skyFlatStopAtMinAdu ?? this.skyFlatStopAtMinAdu,
        skyFlatSunAltitude: skyFlatSunAltitude ?? this.skyFlatSunAltitude,
        unmodeled: unmodeled ?? this.unmodeled,
      );

  /// This state with the daemon-owned first-flip fields taken from [echoed]
  /// (the daemon's answer to a confirm or a re-arm); every other field keeps
  /// its local value, so unsaved panel edits survive.
  SafetyPolicies withFirstFlipFrom(SafetyPolicies echoed) => copyWith(
        firstFlipConfirmed: echoed.firstFlipConfirmed,
        firstFlipCompletedUtc: echoed.firstFlipCompletedUtc,
        clearFirstFlipCompletedUtc: echoed.firstFlipCompletedUtc == null,
        firstFlipPierSideChanged: echoed.firstFlipPierSideChanged,
      );
}

/// ADR 0015 — a value only once the rig has answered. Loads itself: Simple
/// Mode's mount, weather and safety windows and the monitor read it with no
/// Settings panel open first. The daemon's own writes (the flip executor's
/// first-flip confirmation, auto flats) reach it as `profile.updated`.
class SafetyPoliciesNotifier extends SelfLoadingSection<SafetyPolicies> {
  void setOnUnsafe(UnsafeAction a) => edit((c) => c.copyWith(onUnsafe: a));
  void setAutoResumeWhenSafe(bool v) =>
      edit((c) => c.copyWith(autoResumeWhenSafe: v));

  void setResumeDelayMin(int v) {
    if (v < 0) return;
    edit((c) => c.copyWith(resumeDelayMin: v));
  }

  void setMeridianFlipAuto(bool v) =>
      edit((c) => c.copyWith(meridianFlipAuto: v));

  void setMeridianPauseMin(int v) {
    if (v < 0) return;
    edit((c) => c.copyWith(meridianPauseMin: v));
  }

  void setMeridianRecenter(bool v) =>
      edit((c) => c.copyWith(meridianRecenter: v));

  void setMeridianRefocus(MeridianRefocusMode v) =>
      edit((c) => c.copyWith(meridianRefocus: v));

  /// Clamped to the registry's 0.5–10 °C range.
  void setMeridianRefocusTempThresholdC(double v) {
    if (v <= 0 || v.isNaN) return;
    edit((c) => c.copyWith(meridianRefocusTempThresholdC: v.clamp(0.5, 10.0)));
  }

  /// The mode drives the legacy bool too, so an older daemon still sees
  /// "recalibrate" for anything but skip. Until the user picks one, a legacy
  /// profile's null mode (and its bool) are left exactly as stored.
  void setMeridianGuiderRecal(GuiderRecalMode v) => edit((c) => c.copyWith(
        meridianGuiderRecal: v,
        meridianRecalGuider: v != GuiderRecalMode.skip,
      ));

  /// Blank clears (back to the sun-computed window); otherwise HH:mm only.
  void setUnattendedWindowStart(String v) {
    final t = v.trim();
    if (t.isNotEmpty && !isClockText(t)) return;
    edit((c) => c.copyWith(unattendedWindowStart: t));
  }

  void setUnattendedWindowEnd(String v) {
    final t = v.trim();
    if (t.isNotEmpty && !isClockText(t)) return;
    edit((c) => c.copyWith(unattendedWindowEnd: t));
  }

  void setFlipSafetyEnabled(bool v) =>
      edit((c) => c.copyWith(flipSafetyEnabled: v));

  void setClearanceLimitEnabled(bool v) =>
      edit((c) => c.copyWith(clearanceLimitEnabled: v));

  /// Above 0° and under 90°, as the rig accepts it (#1589); anything else is
  /// dropped, so the shown value stays the rig's.
  void setClearanceMaxAltitudeDeg(double v) {
    if (v.isNaN || v <= 0 || v >= 90) return;
    edit((c) => c.copyWith(clearanceMaxAltitudeDeg: v));
  }

  void setUnattendedEscalation(bool v) =>
      edit((c) => c.copyWith(unattendedEscalation: v));

  void setUnattendedShutdownEnabled(bool v) =>
      edit((c) => c.copyWith(unattendedShutdownEnabled: v));

  void setUnattendedShutdownWaitMinutes(int v) {
    if (v < 1) return; // the daemon clamps to >= 1 minute; mirror it here
    edit((c) => c.copyWith(unattendedShutdownWaitMinutes: v));
  }

  void setExpectedFlipSlewSeconds(int v) {
    if (v <= 0) return; // the daemon's watchdog needs a positive expectation
    edit((c) => c.copyWith(expectedFlipSlewSeconds: v));
  }

  void setParkIfNoMoreTargets(bool v) =>
      edit((c) => c.copyWith(parkIfNoMoreTargets: v));
  void setOnGuiderLost(GuiderLostAction a) =>
      edit((c) => c.copyWith(onGuiderLost: a));

  void setGuiderRetryTimeoutSec(int v) {
    if (v < 0) return;
    edit((c) => c.copyWith(guiderRetryTimeoutSec: v));
  }

  void setSkipTargetIfRecoveryFails(bool v) =>
      edit((c) => c.copyWith(skipTargetIfRecoveryFails: v));
  void setCalibrationCaptureDefault(CalibrationCaptureDefault v) =>
      edit((c) => c.copyWith(calibrationCaptureDefault: v));
  void setWeatherTriggersEnabled(bool v) =>
      edit((c) => c.copyWith(weatherTriggersEnabled: v));

  void setMaxWindKmh(int v) {
    if (v <= 0) return;
    edit((c) => c.copyWith(maxWindKmh: v));
  }

  void setMaxHumidityPct(int v) {
    if (v <= 0 || v > 100) return;
    edit((c) => c.copyWith(maxHumidityPct: v));
  }

  void setMinDewDeltaC(double v) {
    if (v < 0) return;
    edit((c) => c.copyWith(minDewDeltaC: v));
  }

  void setFlatTargetAdu(int v) {
    if (v <= 0) return;
    edit((c) => c.copyWith(flatTargetAdu: v));
  }

  void setFlatTargetPercent(double v) {
    if (v <= 0 || v >= 100) return;
    edit((c) => c.copyWith(flatTargetPercent: v));
  }

  void setFlatTargetAduTolerancePct(double v) {
    if (v <= 0) return;
    edit((c) => c.copyWith(flatTargetAduTolerancePct: v));
  }

  void setFlatFramesPerFilter(int v) {
    if (v <= 0) return;
    edit((c) => c.copyWith(flatFramesPerFilter: v));
  }

  void setPostFlatParkMount(bool v) =>
      edit((c) => c.copyWith(postFlatParkMount: v));

  void setSkyFlatTargetAdu(int v) {
    if (v <= 0) return;
    edit((c) => c.copyWith(skyFlatTargetAdu: v));
  }

  void setSkyFlatFramesPerFilter(int v) {
    if (v <= 0) return;
    edit((c) => c.copyWith(skyFlatFramesPerFilter: v));
  }

  void setSkyFlatTargetAzimuth(double v) {
    if (v < 0 || v > 360) return;
    edit((c) => c.copyWith(skyFlatTargetAzimuth: v));
  }

  void setSkyFlatTargetAltitude(double v) {
    if (v < 0 || v > 90) return;
    edit((c) => c.copyWith(skyFlatTargetAltitude: v));
  }

  void setSkyFlatStopAtMaxAdu(double v) {
    if (v <= 0) return;
    edit((c) => c.copyWith(skyFlatStopAtMaxAdu: v));
  }

  void setSkyFlatStopAtMinAdu(double v) {
    if (v < 0) return;
    edit((c) => c.copyWith(skyFlatStopAtMinAdu: v));
  }

  void setSkyFlatSunAltitude(double v) {
    if (v < -90 || v > 90) return;
    edit((c) => c.copyWith(skyFlatSunAltitude: v));
  }

  void setOnDiskSpaceCritical(DiskSpaceCriticalAction a) =>
      edit((c) => c.copyWith(onDiskSpaceCritical: a));

  /// §58.8 — re-arm the unconfirmed-flip announce (e.g. after re-balancing or any
  /// rig change the optics-based auto-reset can't see). Goes straight to the
  /// daemon's dedicated endpoint — the fields are DAEMON-owned (the flip
  /// executor records a completed flip out-of-band, and the general safety PUT
  /// ignores them), so a local-only mutation deferred to Save could clobber or
  /// be clobbered by an overnight flip.
  ///
  /// Only the first-flip fields are patched into the shown copy: replacing it
  /// with the rig's would discard unsaved edits staged in the panel. The rig
  /// answers its new tag (#1807), kept so the next Save is not refused as
  /// stale; when the rig's copy differs by more than those fields, another
  /// device saved in between and the panel offers Reload instead (ADR 0015).
  Future<void> rearmFirstFlip() =>
      _firstFlipAction((api) => api.rearmFirstFlip());

  /// #1589 — confirm the profile's completed meridian flip (the user watched
  /// it). Straight to the daemon's endpoint, like [rearmFirstFlip]: the
  /// confirmation is daemon-owned. Patches only the first-flip fields from
  /// the rig's answer, so unsaved panel edits survive.
  Future<void> confirmFirstFlip() =>
      _firstFlipAction((api) => api.confirmFirstFlip());

  Future<void> _firstFlipAction(
    Future<Tagged<SafetyPolicies>> Function(ProfileApi api) action,
  ) => actOnRig(
    action,
    apply: (shown, rig) => shown.withFirstFlipFrom(rig),
    onlyAction: (loaded, rig) => mapEquals(
      ProfileApi.safetyPoliciesToJson(loaded.withFirstFlipFrom(rig)),
      ProfileApi.safetyPoliciesToJson(rig),
    ),
  );

  @override
  Future<Tagged<SafetyPolicies>> fetch(ProfileApi api) =>
      api.getSafetySection();

  @override
  Future<Tagged<SafetyPolicies>> send(
    ProfileApi api,
    SafetyPolicies value,
    String? etag,
  ) => api.putSafetySection(value, ifMatch: etag);
}

final safetyPoliciesProvider =
    NotifierProvider<SafetyPoliciesNotifier, LoadedSection<SafetyPolicies>>(
      SafetyPoliciesNotifier.new,
    );

/// `H:mm` / `HH:mm`, 24-hour — what the daemon's unattended window parses.
bool isClockText(String s) {
  final m = RegExp(r'^(\d{1,2}):(\d{2})$').firstMatch(s.trim());
  if (m == null) return false;
  final h = int.parse(m.group(1)!);
  final mi = int.parse(m.group(2)!);
  return h >= 0 && h < 24 && mi >= 0 && mi < 60;
}
