import 'dart:math' as math;

import '../services/optimal_sub_api.dart' as api show OptimalSubResult;
import '../state/settings/camera_electronics_state.dart';
import '../state/settings/filter_set_state.dart';
import '../state/settings/optics_settings_state.dart';
import '../state/settings/site_settings_state.dart';
import 'optimal_sub.dart';
import 'star_model.dart' as star_model;

/// NEXTGEN §2/§3/§5 — the CLIENT-side Optimal-Sub resolver, replacing
/// `GET /planning/optimal-sub` per the 2026-07-15 PORT_DECISIONS call. Same
/// merge semantics as the daemon's `OptimalSubOverrides`, sourced from the
/// live (or offline-seeded) settings notifiers instead of the profile store:
///
/// - Filter passband: the named planning filter (case-insensitive) → its
///   bandwidth or kind default; unknown name fails with the SAME actionable
///   message the daemon used; no name → broadband default, tagged assumed.
/// - Electronics: profile value → Tier-0 default, tagged assumed.
/// - Imaging-train geometry: NO honest default — fails with setup guidance.
/// - Sky: the site's Bortle through the one shared sky model.
/// - §3.1 star context (when a target position is supplied): sensor dims
///   (advisory-degrading) + profile seeing (Tier-0 2.5″, tagged).
sealed class LocalOptimalSubOutcome {
  const LocalOptimalSubOutcome();
}

/// A fixable-configuration story — mirrors the daemon's 400 detail strings so
/// the advisor's "Open optics / Open filter set" routing keeps working.
class LocalOptimalSubUnavailable extends LocalOptimalSubOutcome {
  final String message;
  const LocalOptimalSubUnavailable(this.message);
}

class LocalOptimalSubSuccess extends LocalOptimalSubOutcome {
  final api.OptimalSubResult result;
  const LocalOptimalSubSuccess(this.result);
}

const double _maxReducerFactor = 10.0; // TonightSkyOverrides.MaxReducerFactor

LocalOptimalSubOutcome resolveOptimalSubLocal({
  required OpticsSettings optics,
  required CameraElectronics electronics,
  required SiteSettings site,
  required FilterSetSettings filterSet,
  String? filterName,
  double? raDeg,
  double? decDeg,
}) {
  final assumed = <String>[];

  // ── Filter passband: named profile filter → broadband default ────────────
  double bandwidth;
  final name = filterName?.trim();
  if (name != null && name.isNotEmpty) {
    final match = filterSet.filters
        .where((f) => f.name.trim().toLowerCase() == name.toLowerCase())
        .firstOrNull;
    if (match == null) {
      final known = filterSet.filters.isNotEmpty
          ? filterSet.filters.map((f) => "'${f.name}'").join(', ')
          : '(none configured)';
      return LocalOptimalSubUnavailable(
          "Unknown filter '$name' — configured planning filters: $known. "
          'Add it in Settings → Filter set.');
    }
    bandwidth = match.bandwidthNm > 0
        ? match.bandwidthNm
        : match.kind.defaultBandwidthNm;
  } else {
    bandwidth = defaultBroadbandBandwidthNm;
    assumed.add('filter_bandwidth_nm');
  }

  // ── Electronics: profile (0/-1 = unset) → Tier-0 default, tagged ─────────
  double readNoise;
  if (electronics.readNoiseE > 0) {
    readNoise = electronics.readNoiseE;
  } else {
    readNoise = defaultReadNoiseE;
    assumed.add('read_noise_e');
  }
  double fullWell;
  if (electronics.fullWellE > 0) {
    fullWell = electronics.fullWellE;
  } else {
    fullWell = defaultFullWellE;
    assumed.add('full_well_e');
  }
  final ePerAdu = math.max(0.0, electronics.electronsPerAdu);
  double qe;
  if (electronics.quantumEfficiencyPeak > 0) {
    qe = electronics.quantumEfficiencyPeak;
  } else {
    qe = defaultQuantumEfficiency;
    assumed.add('quantum_efficiency');
  }

  // ── Imaging-train geometry: NO honest default ─────────────────────────────
  if (!(optics.apertureMm.isFinite &&
      optics.apertureMm > 0 &&
      optics.focalLengthMm.isFinite &&
      optics.focalLengthMm > 0 &&
      optics.pixelSizeUm.isFinite &&
      optics.pixelSizeUm > 0)) {
    return const LocalOptimalSubUnavailable(
        'The imaging train is incomplete — aperture, focal length and pixel '
        'size have no honest defaults. Set them up in Settings → Optics '
        '(including the aperture field).');
  }
  if (!(optics.reducerFactor.isFinite &&
      optics.reducerFactor > 0 &&
      optics.reducerFactor <= _maxReducerFactor)) {
    return LocalOptimalSubUnavailable(
        'The reducer factor (${optics.reducerFactor}) is out of range — set '
        'it in Settings → Optics to a value in (0, $_maxReducerFactor].');
  }

  final input = OptimalSubInput(
    readNoiseE: readNoise,
    fullWellE: fullWell,
    electronsPerAdu: ePerAdu,
    pixelSizeUm: optics.pixelSizeUm,
    apertureMm: optics.apertureMm,
    focalLengthMm: optics.focalLengthMm,
    reducerFactor: optics.reducerFactor,
    quantumEfficiency: qe,
    skyMagPerArcsec2: effectiveSkyMag(
      bortleClass: site.bortleClass,
      sqmMagPerArcsec2: site.sqmMagPerArcsec2,
    ),
    filterBandwidthNm: bandwidth,
  );

  var window = computeOptimalSub(input);
  String? starReason;

  // ── §3.1 star-field context (only when a target was supplied) ────────────
  if (raDeg != null && decDeg != null) {
    if (optics.sensorWidthPx < 1 || optics.sensorHeightPx < 1) {
      // Advisory rider degrades; the Glover window still flows.
      starReason = 'Star-count advice needs your sensor dimensions (no '
          'honest default) — set them in Settings → Optics.';
    } else {
      double seeing;
      if (site.typicalSeeingArcsec > 0 && site.typicalSeeingArcsec.isFinite) {
        seeing = site.typicalSeeingArcsec;
      } else {
        seeing = 2.5;
        assumed.add('seeing_arcsec');
      }
      final scale = 206.265 *
          optics.pixelSizeUm /
          (optics.focalLengthMm * optics.reducerFactor);
      final fovDeg2 = (optics.sensorWidthPx * scale / 3600.0) *
          (optics.sensorHeightPx * scale / 3600.0);
      window = star_model.augmentWithStarFloor(
          window, input, seeing, raDeg, decDeg, fovDeg2);
      starReason = window.starReason;
    }
  }

  return LocalOptimalSubSuccess(api.OptimalSubResult(
    skyFluxEPerSecPerPx: window.skyFluxEPerSecPerPx,
    floorSec: window.floorSec,
    ceilingSec: window.ceilingSec,
    viable: window.viable,
    limitingBound: switch (window.limitingBound) {
      OptimalSubBound.readNoiseFloor => 'readnoisefloor',
      OptimalSubBound.saturationCeiling => 'saturationceiling',
      OptimalSubBound.starFloor => 'starfloor',
    },
    recommendedSec: window.recommendedSec,
    assumedDefaults: assumed.isEmpty ? null : assumed,
    starFloorSec: window.starFloorSec,
    starsDetectedPerSub: window.starsDetectedPerSub,
    starsRegistrationPerSub: window.starsRegistrationPerSub,
    starReason: starReason,
  ));
}
