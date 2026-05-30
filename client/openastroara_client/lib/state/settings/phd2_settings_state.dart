import 'package:flutter_riverpod/flutter_riverpod.dart';

/// §63 PHD2 / guider settings. Phase 12h.4 holds state in memory; 12h.2b
/// will wire `/api/v1/profile/phd2` for daemon round-trip. The §35
/// meridian-flip re-cal-guider policy lives in `safetyPoliciesProvider`
/// (it crosses the §35/§63 boundary and belongs with the rest of meridian
/// behavior) — only PHD2-internal settings live here.

class Phd2Settings {
  // Connection.
  final String host;
  final int port;
  final String phd2Profile;

  // Dithering.
  final bool ditherEnabled;
  final int ditherEveryNFrames;
  final double ditherPixels;
  final double settlePixels;
  final int settleTimeSec;
  final int settleTimeoutSec;

  // Calibration.
  final bool forceCalibrationEachSession;

  const Phd2Settings({
    this.host = 'localhost',
    this.port = 4400,
    this.phd2Profile = 'Default',
    this.ditherEnabled = true,
    this.ditherEveryNFrames = 1,
    this.ditherPixels = 5.0,
    this.settlePixels = 1.5,
    this.settleTimeSec = 10,
    this.settleTimeoutSec = 60,
    this.forceCalibrationEachSession = false,
  });

  Phd2Settings copyWith({
    String? host,
    int? port,
    String? phd2Profile,
    bool? ditherEnabled,
    int? ditherEveryNFrames,
    double? ditherPixels,
    double? settlePixels,
    int? settleTimeSec,
    int? settleTimeoutSec,
    bool? forceCalibrationEachSession,
  }) =>
      Phd2Settings(
        host: host ?? this.host,
        port: port ?? this.port,
        phd2Profile: phd2Profile ?? this.phd2Profile,
        ditherEnabled: ditherEnabled ?? this.ditherEnabled,
        ditherEveryNFrames: ditherEveryNFrames ?? this.ditherEveryNFrames,
        ditherPixels: ditherPixels ?? this.ditherPixels,
        settlePixels: settlePixels ?? this.settlePixels,
        settleTimeSec: settleTimeSec ?? this.settleTimeSec,
        settleTimeoutSec: settleTimeoutSec ?? this.settleTimeoutSec,
        forceCalibrationEachSession:
            forceCalibrationEachSession ?? this.forceCalibrationEachSession,
      );
}

class Phd2SettingsNotifier extends Notifier<Phd2Settings> {
  @override
  Phd2Settings build() => const Phd2Settings();

  void setHost(String s) {
    final v = s.trim();
    if (v.isEmpty) return;
    state = state.copyWith(host: v);
  }

  void setPort(int v) {
    // Privileged ports (<1024) and dynamic (>65535) rejected. PHD2 default
    // is 4400; non-default deployments may rebind to other unprivileged
    // ports.
    if (v < 1024 || v > 65535) return;
    state = state.copyWith(port: v);
  }

  void setPhd2Profile(String s) {
    final v = s.trim();
    if (v.isEmpty) return;
    state = state.copyWith(phd2Profile: v);
  }

  void setDitherEnabled(bool v) => state = state.copyWith(ditherEnabled: v);

  void setDitherEveryNFrames(int v) {
    if (v < 1) return;
    state = state.copyWith(ditherEveryNFrames: v);
  }

  void setDitherPixels(double v) {
    if (v < 0) return;
    state = state.copyWith(ditherPixels: v);
  }

  void setSettlePixels(double v) {
    if (v < 0) return;
    state = state.copyWith(settlePixels: v);
  }

  void setSettleTimeSec(int v) {
    if (v < 0) return;
    state = state.copyWith(settleTimeSec: v);
  }

  void setSettleTimeoutSec(int v) {
    if (v < 1) return;
    state = state.copyWith(settleTimeoutSec: v);
  }

  void setForceCalibrationEachSession(bool v) =>
      state = state.copyWith(forceCalibrationEachSession: v);
}

final phd2SettingsProvider =
    NotifierProvider<Phd2SettingsNotifier, Phd2Settings>(
        Phd2SettingsNotifier.new);
