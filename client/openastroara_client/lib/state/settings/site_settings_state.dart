import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'loaded_section.dart';

import '../../services/profile_api.dart';
import '../../util/optimal_sub.dart';

/// §37.12 Site preferences — location + horizon + observing conditions.
/// Backed by the daemon's `GET`/`PUT /api/v1/profile/site` section through the
/// section module (loaded_section.dart): the rig's copy, loaded before it is
/// saved.

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

  /// Measured sky brightness in mag/arcsec2 (SQM meter / lightpollutionmap).
  /// 0 = Bortle mode (the sky derives from [bortleClass]); a valid reading
  /// (16-22.5) = SQM mode, and [bortleClass] then holds the class derived from
  /// it. One input, never both (see `SkyState`).
  final double sqmMagPerArcsec2;
  final double typicalSeeingArcsec;
  final TwilightDefinition twilightDefinition;

  // §37.5 — whole-run ceiling in minutes; 0 = no limit (the default). The
  // daemon's runtime-cap watchdog stops a run gracefully past this.
  final int maxSequenceRuntimeMin;

  // §37.5 — the ADVISORY altitude (0 disables). Distinct from the hard
  // horizon floor: Tonight's Sky tags targets that never clear this mark.
  final double softWarningAltitudeDeg;

  // §29.2 — goes in every frame's OBSERVER header. Empty = header omitted.
  final String observerName;

  // #1565 — the mount stays polar aligned (a permanent pier): Simple Mode's
  // Night Setup skips polar alignment. Saved in the profile, so every device
  // agrees.
  final bool permanentPier;

  // No defaults: the daemon's normalizer is the one owner (ADR 0012, ADR
  // 0015 rule 3), so a value is the rig's copy or the offline cache's.
  const SiteSettings({
    required this.siteName,
    required this.latitudeDeg,
    required this.longitudeDeg,
    required this.elevationM,
    required this.timeZone,
    required this.useCustomHorizon,
    required this.defaultHorizonAltitudeDeg,
    required this.bortleClass,
    required this.sqmMagPerArcsec2,
    required this.typicalSeeingArcsec,
    required this.twilightDefinition,
    required this.maxSequenceRuntimeMin,
    required this.softWarningAltitudeDeg,
    required this.observerName,
    required this.permanentPier,
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
    double? sqmMagPerArcsec2,
    double? typicalSeeingArcsec,
    TwilightDefinition? twilightDefinition,
    int? maxSequenceRuntimeMin,
    double? softWarningAltitudeDeg,
    String? observerName,
    bool? permanentPier,
  }) => SiteSettings(
    siteName: siteName ?? this.siteName,
    latitudeDeg: latitudeDeg ?? this.latitudeDeg,
    longitudeDeg: longitudeDeg ?? this.longitudeDeg,
    elevationM: elevationM ?? this.elevationM,
    timeZone: timeZone ?? this.timeZone,
    useCustomHorizon: useCustomHorizon ?? this.useCustomHorizon,
    defaultHorizonAltitudeDeg:
        defaultHorizonAltitudeDeg ?? this.defaultHorizonAltitudeDeg,
    bortleClass: bortleClass ?? this.bortleClass,
    sqmMagPerArcsec2: sqmMagPerArcsec2 ?? this.sqmMagPerArcsec2,
    typicalSeeingArcsec: typicalSeeingArcsec ?? this.typicalSeeingArcsec,
    twilightDefinition: twilightDefinition ?? this.twilightDefinition,
    maxSequenceRuntimeMin: maxSequenceRuntimeMin ?? this.maxSequenceRuntimeMin,
    softWarningAltitudeDeg:
        softWarningAltitudeDeg ?? this.softWarningAltitudeDeg,
    observerName: observerName ?? this.observerName,
    permanentPier: permanentPier ?? this.permanentPier,
  );
}

/// ADR 0015 — a value only once the rig has answered. Loads itself: planning,
/// Tonight's Sky, Simple Mode's Rig page and the polar alignment hemisphere
/// read it with no Settings panel open first. The daemon's own writes (a time
/// sync that fills the site from GPS) reach it as `profile.updated`.
class SiteSettingsNotifier extends SelfLoadingSection<SiteSettings> {
  void setSiteName(String s) {
    final v = s.trim();
    if (v.isEmpty) return;
    edit((c) => c.copyWith(siteName: v));
  }

  /// Empty is allowed — it means "omit the OBSERVER header".
  void setObserverName(String s) =>
      edit((c) => c.copyWith(observerName: s.trim()));

  void setPermanentPier(bool v) => edit((c) => c.copyWith(permanentPier: v));

  void setLatitudeDeg(double v) => edit((c) => withLatitude(c, v));

  void setLongitudeDeg(double v) => edit((c) => withLongitude(c, v));

  void setElevationM(double v) => edit((c) => withElevation(c, v));

  void setTimeZone(String s) => edit((c) => withTimeZone(c, s));

  /// [c] with latitude [v]; [c] itself when [v] is out of range.
  static SiteSettings withLatitude(SiteSettings c, double v) =>
      v < -90 || v > 90 ? c : c.copyWith(latitudeDeg: v);

  /// [c] with longitude [v]; [c] itself when [v] is out of range.
  static SiteSettings withLongitude(SiteSettings c, double v) =>
      v < -180 || v > 180 ? c : c.copyWith(longitudeDeg: v);

  /// [c] with elevation [v] in whole metres (a GPS fix's centimetres are
  /// noise); [c] itself below the Dead Sea (-500 m) or above 9000 m, which no
  /// tripod-mounted scope reaches.
  static SiteSettings withElevation(SiteSettings c, double v) =>
      v < -500 || v > 9000 ? c : c.copyWith(elevationM: v.roundToDouble());

  /// [c] with time zone [s] trimmed; [c] itself when that is empty.
  static SiteSettings withTimeZone(SiteSettings c, String s) {
    final v = s.trim();
    return v.isEmpty ? c : c.copyWith(timeZone: v);
  }

  /// [c] with a location fix: the coordinates, the elevation when there is
  /// one, and [timeZone] (the zone those coordinates fall in).
  static SiteSettings withFix(
    SiteSettings c, {
    required double lat,
    required double lng,
    double? alt,
    required String timeZone,
  }) {
    var next = withLongitude(withLatitude(c, lat), lng);
    if (alt != null) next = withElevation(next, alt);
    return withTimeZone(next, timeZone);
  }

  void setUseCustomHorizon(bool v) =>
      edit((c) => c.copyWith(useCustomHorizon: v));

  void setDefaultHorizonAltitudeDeg(double v) {
    if (v < 0 || v > 90) return;
    edit((c) => c.copyWith(defaultHorizonAltitudeDeg: v));
  }

  static bool isValidBortle(int v) => v >= 1 && v <= 9;

  /// Bortle mode: the class is the one input, so any SQM reading is cleared
  /// (no hidden value may still affect planning, #1529).
  void setSkyBortle(int v) {
    if (!isValidBortle(v)) return;
    edit((c) => c.copyWith(bortleClass: v, sqmMagPerArcsec2: 0));
  }

  /// SQM mode: [v] (16..22.5) is the one input and the Bortle class becomes
  /// the class derived from it. 0 or out-of-range is rejected; switch to
  /// Bortle mode with [setSkyBortle] instead.
  void setSkySqm(double v) {
    if (!isSqmReading(v)) return;
    edit(
      (c) => c.copyWith(sqmMagPerArcsec2: v, bortleClass: bortleFromSkyMag(v)),
    );
  }

  void setTypicalSeeingArcsec(double v) {
    if (v <= 0 || v > 20) return;
    edit((c) => c.copyWith(typicalSeeingArcsec: v));
  }

  void setTwilightDefinition(TwilightDefinition d) =>
      edit((c) => c.copyWith(twilightDefinition: d));

  void setMaxSequenceRuntimeMin(int v) {
    // 0 = no limit; a week (10080 min) is a generous ceiling for one run.
    if (v < 0 || v > 10080) return;
    edit((c) => c.copyWith(maxSequenceRuntimeMin: v));
  }

  void setSoftWarningAltitudeDeg(double v) {
    // 0 disables the advisory.
    if (v < 0 || v > 90) return;
    edit((c) => c.copyWith(softWarningAltitudeDeg: v));
  }

  @override
  Future<Tagged<SiteSettings>> fetch(ProfileApi api) => api.getSiteSection();

  @override
  Future<Tagged<SiteSettings>> send(
    ProfileApi api,
    SiteSettings value,
    String? etag,
  ) => api.putSiteSection(value, ifMatch: etag);
}

final siteSettingsProvider =
    NotifierProvider<SiteSettingsNotifier, LoadedSection<SiteSettings>>(
      SiteSettingsNotifier.new,
    );
