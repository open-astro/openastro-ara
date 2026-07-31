import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'settings_sync_mixin.dart';

import '../../services/profile_api.dart';
import '../saved_server_state.dart';

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

  // §63.19 guide setup type: 'guide_scope' (separate guide scope, focal
  // length user-entered) or 'oag' (off-axis guider — the guide focal length
  // is DERIVED from the main optics: focal_length_mm × reducer_factor).
  final String guiderSetupType;

  // §63.5 guider-engine config (pushed to the guider daemon on connect).
  final int guideFocalLength; // mm, 0 = unset
  final double guidePixelSize; // µm, 0 = unset
  final double raAggressiveness; // 0..1
  final double decAggressiveness; // 0..1
  final double minimumMove; // px
  final String decGuideMode; // auto | north | south | off

  // §63.17 guider equipment selection — pushed to the daemon inside the §63.5
  // disconnected window. Values are the daemon's own choice strings, verbatim
  // from GET /equipment/guider/choices; "" / 0 = unset (not pushed, the daemon
  // keeps its own selection).
  final String guiderCamera;
  final String guiderCameraId;
  final String guiderMount;
  final String guiderAuxMount;
  final String guiderRotator;
  final String guiderAlpacaHost;
  final int guiderAlpacaPort; // 0 = unset

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
    this.guiderSetupType = 'guide_scope',
    this.guideFocalLength = 0,
    this.guidePixelSize = 0,
    this.raAggressiveness = 0.7,
    this.decAggressiveness = 0.7,
    this.minimumMove = 0.15,
    this.decGuideMode = 'auto',
    this.guiderCamera = '',
    this.guiderCameraId = '',
    this.guiderMount = '',
    this.guiderAuxMount = '',
    this.guiderRotator = '',
    this.guiderAlpacaHost = '',
    this.guiderAlpacaPort = 0,
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
    String? guiderSetupType,
    int? guideFocalLength,
    double? guidePixelSize,
    double? raAggressiveness,
    double? decAggressiveness,
    double? minimumMove,
    String? decGuideMode,
    String? guiderCamera,
    String? guiderCameraId,
    String? guiderMount,
    String? guiderAuxMount,
    String? guiderRotator,
    String? guiderAlpacaHost,
    int? guiderAlpacaPort,
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
        guiderSetupType: guiderSetupType ?? this.guiderSetupType,
        guideFocalLength: guideFocalLength ?? this.guideFocalLength,
        guidePixelSize: guidePixelSize ?? this.guidePixelSize,
        raAggressiveness: raAggressiveness ?? this.raAggressiveness,
        decAggressiveness: decAggressiveness ?? this.decAggressiveness,
        minimumMove: minimumMove ?? this.minimumMove,
        decGuideMode: decGuideMode ?? this.decGuideMode,
        guiderCamera: guiderCamera ?? this.guiderCamera,
        guiderCameraId: guiderCameraId ?? this.guiderCameraId,
        guiderMount: guiderMount ?? this.guiderMount,
        guiderAuxMount: guiderAuxMount ?? this.guiderAuxMount,
        guiderRotator: guiderRotator ?? this.guiderRotator,
        guiderAlpacaHost: guiderAlpacaHost ?? this.guiderAlpacaHost,
        guiderAlpacaPort: guiderAlpacaPort ?? this.guiderAlpacaPort,
      );
}

class Phd2SettingsNotifier extends Notifier<Phd2Settings>
    with SettingsSyncMixin<Phd2Settings> {
  @override
  Phd2Settings build() {
    // The hydrate memo is per-SERVER, not per-process: switching the active
    // server must force the next transient surface (tune dialog) to re-hydrate,
    // or Apply could full-object-PUT server A's stale settings onto server B.
    ref.listen(activeServerProvider, (prev, next) {
      if (prev != next) _hydratedOnce = false;
    });
    return const Phd2Settings();
  }

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

  // §63.19 guide setup type. Unknown tokens (older daemons, hand-edited
  // profiles) coerce to 'guide_scope' — the historical behavior where the
  // guide focal length is user-entered.
  static const guiderSetupTypes = ['guide_scope', 'oag'];

  static String normalizeGuiderSetupType(String s) {
    final v = s.trim().toLowerCase();
    return guiderSetupTypes.contains(v) ? v : 'guide_scope';
  }

  void setGuiderSetupType(String v) =>
      state = state.copyWith(guiderSetupType: normalizeGuiderSetupType(v));

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

  // §63.17 — guider equipment selection. "" / 0 mean unset (the daemon keeps
  // its own selection), so empty values are ACCEPTED here — trimming only.

  void setGuiderCamera(String s) =>
      state = state.copyWith(guiderCamera: s.trim());

  void setGuiderCameraId(String s) =>
      state = state.copyWith(guiderCameraId: s.trim());

  void setGuiderMount(String s) => state = state.copyWith(guiderMount: s.trim());

  void setGuiderAuxMount(String s) =>
      state = state.copyWith(guiderAuxMount: s.trim());

  void setGuiderRotator(String s) =>
      state = state.copyWith(guiderRotator: s.trim());

  void setGuiderAlpacaHost(String s) =>
      state = state.copyWith(guiderAlpacaHost: s.trim());

  void setGuiderAlpacaPort(int v) {
    // 0 = unset; otherwise any valid TCP port (Alpaca commonly uses 11111 but
    // simulators bind wherever).
    if (v < 0 || v > 65535) return;
    state = state.copyWith(guiderAlpacaPort: v);
  }

  // Session-scoped hydrate memo: the tune dialog is a fresh widget per open,
  // and an unconditional re-hydrate there would discard unapplied local edits
  // (hydrateGuarded replaces state by design). The notifier is the object that
  // actually lives for the app session, so it remembers.
  bool _hydratedOnce = false;

  /// True once any successful [hydrateFromServer] has landed this session —
  /// transient surfaces (the tune dialog) use this to skip their hydrate wait
  /// and enable Apply immediately. Cleared on a server switch.
  bool get hydratedOnce => _hydratedOnce;

  /// Hydrate at most once per server session. The memo lives INSIDE the
  /// hydrate (not at call sites) because several surfaces share this provider
  /// — the tune dialog, Settings → Guider, the sequencer's planning hydrate —
  /// and any ungated caller would silently clobber unapplied dialog edits
  /// with the daemon's saved copy. A server switch clears the memo (see
  /// [build]), which restores hydrate-on-next-use for the new server.
  Future<void> hydrateFromServer(ProfileApi api) async {
    if (_hydratedOnce) return;
    await hydrateGuarded(() => api.getPhd2Settings());
    _hydratedOnce = true;
  }

  Future<Phd2Settings> persistToServer(ProfileApi api) =>
      persistGuarded((sent) => api.putPhd2Settings(sent));
}

final phd2SettingsProvider =
    NotifierProvider<Phd2SettingsNotifier, Phd2Settings>(
        Phd2SettingsNotifier.new);
