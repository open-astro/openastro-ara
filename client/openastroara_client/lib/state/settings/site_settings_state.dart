import 'package:flutter_riverpod/flutter_riverpod.dart';

/// §37.12 Site preferences — location + horizon + observing conditions.
/// Phase 12h.2-site holds the values in memory; 12h.2b wires
/// `/api/v1/profile/site` for daemon round-trip.

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
      );
}

class SiteSettingsNotifier extends Notifier<SiteSettings> {
  @override
  SiteSettings build() => const SiteSettings();

  void setSiteName(String s) {
    if (s.isEmpty) return;
    state = state.copyWith(siteName: s);
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
    if (s.isEmpty) return;
    state = state.copyWith(timeZone: s);
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
}

final siteSettingsProvider =
    NotifierProvider<SiteSettingsNotifier, SiteSettings>(
        SiteSettingsNotifier.new);
