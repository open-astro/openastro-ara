import '../state/settings/optics_settings_state.dart';
import '../state/settings/site_settings_state.dart';
import '../state/sky_atlas/site_location_state.dart';

/// What the planetarium page is seeded with — pure helpers so the offline
/// fallback rules are unit-tested without a webview.
///
/// The page used to take its observer ONLY from the daemon's site endpoint:
/// with no server it self-initialised at latitude 0, longitude 0 and drew the
/// sky for the Gulf of Guinea, so a target Tonight's Sky put at 70° landed on
/// the treeline in the atlas ("it's finding objects below the horizon").

/// The observer to seed: the daemon's site when it answered, else the
/// client's own site settings (hydrated from the cached profile offline),
/// else null when neither knows a location — the (0, 0) sentinel is never
/// treated as a real site.
SiteLocation? planetariumSiteFor(SiteLocation? fromServer, SiteSettings local) {
  if (fromServer != null) return fromServer;
  if (local.latitudeDeg == 0 && local.longitudeDeg == 0) return null;
  return SiteLocation(
    latitudeDeg: local.latitudeDeg,
    longitudeDeg: local.longitudeDeg,
    elevationM: local.elevationM,
  );
}

/// The optics the framing overlay draws its box from, as the page's
/// `applyOptics` wire fields — the same shape `GET /api/v1/profile/optics`
/// returns, so the page uses one code path online and off. Null when the
/// train isn't configured enough to know a FOV.
Map<String, num>? planetariumOpticsFor(OpticsSettings o) {
  if (o.focalLengthMm <= 0 ||
      o.sensorWidthPx <= 0 ||
      o.sensorHeightPx <= 0 ||
      o.pixelSizeUm <= 0) {
    return null;
  }
  return {
    'focal_length_mm': o.focalLengthMm,
    'reducer_factor': o.reducerFactor,
    'sensor_width_px': o.sensorWidthPx,
    'sensor_height_px': o.sensorHeightPx,
    'pixel_size_um': o.pixelSizeUm,
  };
}
