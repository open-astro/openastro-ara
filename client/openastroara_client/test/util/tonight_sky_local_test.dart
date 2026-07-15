import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/util/tonight_sky_local.dart';

void main() {
  // A mid-northern winter night: Atlanta-ish site, 03:00 UTC on Jan 15
  // (≈ 22:00 local the evening of Jan 14) — Orion territory.
  const site = SiteSettings(
    siteName: 'test',
    latitudeDeg: 34.0,
    longitudeDeg: -84.0,
    bortleClass: 6,
    defaultHorizonAltitudeDeg: 20,
    twilightDefinition: TwilightDefinition.astronomical,
    softWarningAltitudeDeg: 30,
  );
  // RedCat-51-ish train on an APS-C-ish sensor.
  const optics = OpticsSettings(
    focalLengthMm: 250,
    reducerFactor: 1.0,
    sensorWidthPx: 6248,
    sensorHeightPx: 4176,
    pixelSizeUm: 3.76,
    apertureMm: 51,
  );
  final winterNight = DateTime.utc(2026, 1, 15, 3);

  List<TonightSkyObject> rank({DateTime? at, SiteSettings? s, int limit = 10}) =>
      computeTonightSkyLocal(
          site: s ?? site, optics: optics, atUtc: at ?? winterNight, limit: limit);

  test('returns ranked objects on a winter night, scores descending in [0,100]',
      () {
    final list = rank();
    expect(list, isNotEmpty);
    for (var i = 0; i < list.length; i++) {
      expect(list[i].score, isNotNull);
      expect(list[i].score!, inInclusiveRange(0, 100));
      if (i > 0) {
        expect(list[i - 1].score! >= list[i].score!, isTrue,
            reason: 'list must be score-descending');
      }
    }
  });

  test('Orion Nebula gets a real dark window on a January night', () {
    final list = rank(limit: 20);
    final m42 = list.where((o) => o.id == 'M42').singleOrNull;
    expect(m42, isNotNull, reason: 'M42 must be listed on a winter night');
    expect(m42!.integrationHours, greaterThan(1));
    expect(m42.windowStartUtc, isNotNull);
    expect(m42.windowEndUtc!.isAfter(m42.windowStartUtc!), isTrue);
    expect(m42.remainingHours, lessThanOrEqualTo(m42.integrationHours));
    // Advisory fields ride along.
    expect(m42.moonIlluminationPct, isNotNull);
    expect(m42.moonUpFraction, inInclusiveRange(0, 1));
    expect(m42.scoreReasons, isNotNull);
    expect(m42.scoreReasons!.join(' '), contains('offline ranking'));
  });

  test('far-northern targets are excluded from a deep-southern site', () {
    // From lat −35°, M81 (dec +69°) culminates at 90 − |−35−69| = −14° — it
    // can never clear a 20° horizon.
    final southern = rank(
        s: const SiteSettings(
          siteName: 'south',
          latitudeDeg: -35.0,
          longitudeDeg: 149.0,
          bortleClass: 4,
          defaultHorizonAltitudeDeg: 20,
          twilightDefinition: TwilightDefinition.astronomical,
        ),
        limit: 20);
    expect(southern.where((o) => o.id == 'M81'), isEmpty);
    expect(southern, isNotEmpty); // southern-sky staples still rank
  });

  test('limit caps the list', () {
    expect(rank(limit: 3).length, 3);
  });

  test('nothing is listed as up during local daytime-only spans', () {
    // ±12h always spans a night, so the gate is about DARK windows: every
    // listed window must be bounded and non-empty.
    for (final o in rank(limit: 20)) {
      expect(o.integrationHours, greaterThan(0));
      expect(o.integrationHours, lessThanOrEqualTo(24));
    }
  });
}
