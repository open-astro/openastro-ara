import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/util/tonight_sky_local.dart';

void main() {
  const optics = OpticsSettings(
    focalLengthMm: 250, reducerFactor: 1.0, sensorWidthPx: 6248,
    sensorHeightPx: 4176, pixelSizeUm: 3.76, apertureMm: 51);
  test('full-precision GPS coordinates rank identically to rounded', () {
    const full = SiteSettings(
      siteName: 't', latitudeDeg: 34.12345678901, longitudeDeg: -84.98765432109,
      bortleClass: 6, defaultHorizonAltitudeDeg: 20,
      twilightDefinition: TwilightDefinition.astronomical, softWarningAltitudeDeg: 30);
    final list = computeTonightSkyLocal(
        site: full, optics: optics, atUtc: DateTime.utc(2026, 1, 15, 3), limit: 10);
    expect(list, isNotEmpty);
  });
}
