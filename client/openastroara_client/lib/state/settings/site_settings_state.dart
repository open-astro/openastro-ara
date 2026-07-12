import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'settings_sync_mixin.dart';

import '../../services/profile_api.dart';

/// §37.12 Site preferences — location + horizon + observing conditions.
/// Phase 12h.6e wires the daemon round-trip via [ProfileApi]; local state
/// is still the source of truth between syncs.

enum TwilightDefinition { civil, nautical, astronomical }

class SiteSettings {
  // Location.
  final String siteName;
  final double latitudeDeg;
  final double longitudeDeg;
  final double elevationM;
  final String timeZone;

  // Horizon (custom horizon-polygon file lands in 12h.2b alongside §36.8
  // — for now we just track whether the user wants the custom version or
  // the default 20° flat horizon).
  final bool useCustomHorizon;
  final double defaultHorizonAltitudeDeg;

  // Observing conditions.
  final int bortleClass; // 1..9
  final double typicalSeeingArcsec;
  final TwilightDefinition twilightDefinition;

  // §37.5 — whole-run ceiling in minutes; 0 = no limit (the default). The
  // daemon's runtime-cap watchdog stops a run gracefully past this.
  final int maxSequenceRuntimeMin;

  // §37.5 — the ADVISORY altitude (0 disables). Distinct from the hard
  // horizon floor: Tonight's Sky tags targets that never clear this mark.
  final double softWarningAltitudeDeg;

  const SiteSettings({
    this.siteName = 'Backyard',
    this.latitudeDeg = 0,
    this.longitudeDeg = 0,
    this.elevationM = 0,
    this.timeZone = 'UTC',
    this.useCustomHorizon = false,
    this.defaultHorizonAltitudeDeg = 20,
    this.bortleClass = 6,
    this.typicalSeeingArcsec = 2.5,
    this.twilightDefinition = TwilightDefinition.astronomical,
    this.maxSequenceRuntimeMin = 0,
    this.softWarningAltitudeDeg = 30,
  });

  SiteSettings copyWith({
    String? siteName,
    double? latitudeDeg,
    double? longitudeDeg,
    double? elevationM,
    String? timeZone,
    bool? useCustomHorizon,
    double? defaultHorizonAltitudeDeg,
    int? bortleClass,
    double? typicalSeeingArcsec,
    TwilightDefinition? twilightDefinition,
    int? maxSequenceRuntimeMin,
    double? softWarningAltitudeDeg,
  }) =>
      SiteSettings(
        siteName: siteName ?? this.siteName,
        latitudeDeg: latitudeDeg ?? this.latitudeDeg,
        longitudeDeg: longitudeDeg ?? this.longitudeDeg,
        elevationM: elevationM ?? this.elevationM,
        timeZone: timeZone ?? this.timeZone,
        useCustomHorizon: useCustomHorizon ?? this.useCustomHorizon,
        defaultHorizonAltitudeDeg:
            defaultHorizonAltitudeDeg ?? this.defaultHorizonAltitudeDeg,
        bortleClass: bortleClass ?? this.bortleClass,
        typicalSeeingArcsec:
            typicalSeeingArcsec ?? this.typicalSeeingArcsec,
        twilightDefinition: twilightDefinition ?? this.twilightDefinition,
        maxSequenceRuntimeMin:
            maxSequenceRuntimeMin ?? this.maxSequenceRuntimeMin,
        softWarningAltitudeDeg:
            softWarningAltitudeDeg ?? this.softWarningAltitudeDeg,
      );
}

class SiteSettingsNotifier extends Notifier<SiteSettings>
    with SettingsSyncMixin<SiteSettings> {
  @override
  SiteSettings build() => const SiteSettings();

  void setSiteName(String s) {
    final v = s.trim();
    if (v.isEmpty) return;
    state = state.copyWith(siteName: v);
  }

  void setLatitudeDeg(double v) {
    if (v < -90 || v > 90) return;
    state = state.copyWith(latitudeDeg: v);
  }

  void setLongitudeDeg(double v) {
    if (v < -180 || v > 180) return;
    state = state.copyWith(longitudeDeg: v);
  }

  void setElevationM(double v) {
    // Below sea level → Dead Sea (-430m) is the practical floor. Above
    // 9000m is no longer reachable by a tripod-mounted scope.
    if (v < -500 || v > 9000) return;
    state = state.copyWith(elevationM: v);
  }

  void setTimeZone(String s) {
    final v = s.trim();
    if (v.isEmpty) return;
    state = state.copyWith(timeZone: v);
  }

  void setUseCustomHorizon(bool v) =>
      state = state.copyWith(useCustomHorizon: v);

  void setDefaultHorizonAltitudeDeg(double v) {
    if (v < 0 || v > 90) return;
    state = state.copyWith(defaultHorizonAltitudeDeg: v);
  }

  void setBortleClass(int v) {
    if (v < 1 || v > 9) return;
    state = state.copyWith(bortleClass: v);
  }

  void setTypicalSeeingArcsec(double v) {
    if (v <= 0 || v > 20) return;
    state = state.copyWith(typicalSeeingArcsec: v);
  }

  void setTwilightDefinition(TwilightDefinition d) =>
      state = state.copyWith(twilightDefinition: d);

  void setMaxSequenceRuntimeMin(int v) {
    // 0 = no limit; a week (10080 min) is a generous ceiling for one run.
    if (v < 0 || v > 10080) return;
    state = state.copyWith(maxSequenceRuntimeMin: v);
  }

  void setSoftWarningAltitudeDeg(double v) {
    // 0 disables the advisory.
    if (v < 0 || v > 90) return;
    state = state.copyWith(softWarningAltitudeDeg: v);
  }

  Future<void> hydrateFromServer(ProfileApi api) =>
      hydrateGuarded(() => api.getSiteSettings());

  Future<SiteSettings> persistToServer(ProfileApi api) =>
      persistGuarded((sent) => api.putSiteSettings(sent));
}

final siteSettingsProvider =
    NotifierProvider<SiteSettingsNotifier, SiteSettings>(
        SiteSettingsNotifier.new);
