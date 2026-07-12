import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'settings_sync_mixin.dart';

import '../../services/profile_api.dart';

/// §37.10 Plate Solving settings. Phase 12h.6i wires the daemon round-
/// trip via [ProfileApi] (`/api/v1/profile/plate-solve`). Local state is
/// the source of truth between syncs.

enum PlateSolveEngine { astap, astrometryNet, platesolve2 }

class PlateSolveSettings {
  final PlateSolveEngine engine;
  final String pathOrEndpoint;
  final String indexDownloadPath;
  final double searchRadiusDeg;
  final int downsampleFactor;
  final int timeoutSeconds;
  final bool useBlindFallback;
  final bool centerAfterSlew;
  final bool syncToCoordinates;
  final int maxIterations;
  final double convergenceToleranceArcsec;

  const PlateSolveSettings({
    this.engine = PlateSolveEngine.astap,
    this.pathOrEndpoint = '/usr/bin/astap',
    this.indexDownloadPath = '/var/lib/astap',
    this.searchRadiusDeg = 30,
    this.downsampleFactor = 2,
    this.timeoutSeconds = 60,
    this.useBlindFallback = true,
    this.centerAfterSlew = true,
    this.syncToCoordinates = true,
    this.maxIterations = 5,
    this.convergenceToleranceArcsec = 60,
  });

  PlateSolveSettings copyWith({
    PlateSolveEngine? engine,
    String? pathOrEndpoint,
    String? indexDownloadPath,
    double? searchRadiusDeg,
    int? downsampleFactor,
    int? timeoutSeconds,
    bool? useBlindFallback,
    bool? centerAfterSlew,
    bool? syncToCoordinates,
    int? maxIterations,
    double? convergenceToleranceArcsec,
  }) =>
      PlateSolveSettings(
        engine: engine ?? this.engine,
        pathOrEndpoint: pathOrEndpoint ?? this.pathOrEndpoint,
        indexDownloadPath: indexDownloadPath ?? this.indexDownloadPath,
        searchRadiusDeg: searchRadiusDeg ?? this.searchRadiusDeg,
        downsampleFactor: downsampleFactor ?? this.downsampleFactor,
        timeoutSeconds: timeoutSeconds ?? this.timeoutSeconds,
        useBlindFallback: useBlindFallback ?? this.useBlindFallback,
        centerAfterSlew: centerAfterSlew ?? this.centerAfterSlew,
        syncToCoordinates: syncToCoordinates ?? this.syncToCoordinates,
        maxIterations: maxIterations ?? this.maxIterations,
        convergenceToleranceArcsec:
            convergenceToleranceArcsec ?? this.convergenceToleranceArcsec,
      );
}

class PlateSolveSettingsNotifier extends Notifier<PlateSolveSettings>
    with SettingsSyncMixin<PlateSolveSettings> {
  @override
  PlateSolveSettings build() => const PlateSolveSettings();

  void setEngine(PlateSolveEngine e) => state = state.copyWith(engine: e);

  void setPathOrEndpoint(String s) {
    final v = s.trim();
    if (v.isEmpty) return;
    state = state.copyWith(pathOrEndpoint: v);
  }

  void setIndexDownloadPath(String s) {
    final v = s.trim();
    if (v.isEmpty) return;
    state = state.copyWith(indexDownloadPath: v);
  }

  void setSearchRadiusDeg(double v) {
    // 0° wouldn't search anything; 180° is the full sphere from any
    // pointing. Reject out-of-range (per the convention used by every
    // other settings notifier — display-sync snaps the field back).
    if (v <= 0 || v > 180) return;
    state = state.copyWith(searchRadiusDeg: v);
  }

  void setDownsampleFactor(int v) {
    // 1 = no downsampling; 8 is the practical upper limit before the
    // image is too coarse for star detection. Reject out-of-range.
    if (v < 1 || v > 8) return;
    state = state.copyWith(downsampleFactor: v);
  }

  void setTimeoutSeconds(int v) {
    if (v <= 0) return;
    state = state.copyWith(timeoutSeconds: v);
  }

  void setUseBlindFallback(bool v) =>
      state = state.copyWith(useBlindFallback: v);
  void setCenterAfterSlew(bool v) =>
      state = state.copyWith(centerAfterSlew: v);
  void setSyncToCoordinates(bool v) =>
      state = state.copyWith(syncToCoordinates: v);

  void setMaxIterations(int v) {
    if (v < 1) return;
    state = state.copyWith(maxIterations: v);
  }

  void setConvergenceToleranceArcsec(double v) {
    if (v <= 0) return;
    state = state.copyWith(convergenceToleranceArcsec: v);
  }

  Future<void> hydrateFromServer(ProfileApi api) =>
      hydrateGuarded(() => api.getPlateSolveSettings());

  Future<PlateSolveSettings> persistToServer(ProfileApi api) =>
      persistGuarded((sent) => api.putPlateSolveSettings(sent));
}

final plateSolveSettingsProvider =
    NotifierProvider<PlateSolveSettingsNotifier, PlateSolveSettings>(
        PlateSolveSettingsNotifier.new);
