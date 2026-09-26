import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/sky_atlas/site_location_state.dart';
import 'package:openastroara/util/planetarium_seed.dart';

void main() {
  test('the daemon site wins; offline the cached site seeds the observer', () {
    const server = SiteLocation(latitudeDeg: 1, longitudeDeg: 2, elevationM: 3);
    const belen = SiteSettings(
        latitudeDeg: 34.67, longitudeDeg: -106.79, elevationM: 1483.7);
    expect(planetariumSiteFor(server, belen), server);
    final offline = planetariumSiteFor(null, belen)!;
    expect(offline.latitudeDeg, 34.67);
    expect(offline.longitudeDeg, -106.79);
    expect(offline.elevationM, 1483.7);
    // The (0, 0) "not set" sentinel never becomes a Gulf-of-Guinea observer.
    expect(planetariumSiteFor(null, const SiteSettings()), isNull);
  });

  test('optics seed matches the profile endpoint shape, null when unconfigured', () {
    const redcat = OpticsSettings(
        focalLengthMm: 250,
        reducerFactor: 1,
        sensorWidthPx: 6248,
        sensorHeightPx: 4176,
        pixelSizeUm: 3.76,
        apertureMm: 51);
    expect(planetariumOpticsFor(redcat), {
      'focal_length_mm': 250.0,
      'reducer_factor': 1.0,
      'sensor_width_px': 6248,
      'sensor_height_px': 4176,
      'pixel_size_um': 3.76,
    });
    expect(planetariumOpticsFor(const OpticsSettings()), isNull);
  });
}
