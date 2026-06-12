import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';

/// §63 PHD2 / guider settings. Phase 12h.6k wires the daemon round-trip
/// via [ProfileApi] (`/api/v1/profile/phd2`). The §35 meridian-flip
/// re-cal-guider policy lives in `safetyPoliciesProvider` (crosses the
/// §35/§63 boundary, belongs with the rest of meridian behavior).

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

  // §63.5 guider-engine config (pushed to the guider daemon on connect).
  final int guideFocalLength; // mm, 0 = unset
  final double guidePixelSize; // µm, 0 = unset
  final double raAggressiveness; // 0..1
  final double decAggressiveness; // 0..1
  final double minimumMove; // px
  final String decGuideMode; // auto | north | south | off

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
    this.guideFocalLength = 0,
    this.guidePixelSize = 0,
    this.raAggressiveness = 0.7,
    this.decAggressiveness = 0.7,
    this.minimumMove = 0.15,
    this.decGuideMode = 'auto',
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
    int? guideFocalLength,
    double? guidePixelSize,
    double? raAggressiveness,
    double? decAggressiveness,
    double? minimumMove,
    String? decGuideMode,
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
        guideFocalLength: guideFocalLength ?? this.guideFocalLength,
        guidePixelSize: guidePixelSize ?? this.guidePixelSize,
        raAggressiveness: raAggressiveness ?? this.raAggressiveness,
        decAggressiveness: decAggressiveness ?? this.decAggressiveness,
        minimumMove: minimumMove ?? this.minimumMove,
        decGuideMode: decGuideMode ?? this.decGuideMode,
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

  // §63.5 — guider-engine config. Ranges mirror the server's ApplyPhd2 normalization
  // (aggressiveness ∈ [0,1], non-negative focal/pixel/min-move, dec-mode in the known set).
  static const decGuideModes = ['auto', 'north', 'south', 'off'];

  void setGuideFocalLength(int v) {
    if (v < 0) return;
    state = state.copyWith(guideFocalLength: v);
  }

  void setGuidePixelSize(double v) {
    if (v < 0) return;
    state = state.copyWith(guidePixelSize: v);
  }

  void setRaAggressiveness(double v) {
    if (v < 0 || v > 1) return;
    state = state.copyWith(raAggressiveness: v);
  }

  void setDecAggressiveness(double v) {
    if (v < 0 || v > 1) return;
    state = state.copyWith(decAggressiveness: v);
  }

  void setMinimumMove(double v) {
    if (v < 0) return;
    state = state.copyWith(minimumMove: v);
  }

  void setDecGuideMode(String v) {
    final m = v.trim().toLowerCase();
    if (!decGuideModes.contains(m)) return;
    state = state.copyWith(decGuideMode: m);
  }

  Future<void> hydrateFromServer(ProfileApi api) async {
    state = await api.getPhd2Settings();
  }

  Future<Phd2Settings> persistToServer(ProfileApi api) async {
    final echoed = await api.putPhd2Settings(state);
    state = echoed;
    return echoed;
  }
}

final phd2SettingsProvider =
    NotifierProvider<Phd2SettingsNotifier, Phd2Settings>(
        Phd2SettingsNotifier.new);
