import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
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

  test('mosaic tiles enlarge the framing FOV: overflow becomes good', () {
    // NGC 7000 (~120' major axis) on a 1000 mm train with a small sensor:
    // single-frame min dimension ≈ 4176·(206.265·3.76/1000)/60 ≈ 54' →
    // ratio ≈ 2.2 → overflows. A 3×3 mosaic triples it → ratio ≈ 0.74 → good.
    const longFl = OpticsSettings(
      focalLengthMm: 1000,
      reducerFactor: 1.0,
      sensorWidthPx: 6248,
      sensorHeightPx: 4176,
      pixelSizeUm: 3.76,
      apertureMm: 100,
    );
    final ngc7000 = PlanningDso(
        id: 'NGC7000',
        name: 'North America Nebula',
        type: 'HII',
        magnitude: 4.0,
        raDeg: 314.75,
        decDeg: 44.33,
        sizeMajArcmin: 120,
        sizeMinArcmin: 100);
    final autumnNight = DateTime.utc(2026, 10, 15, 3);

    final single = computeTonightSkyLocal(
        site: site, optics: longFl, atUtc: autumnNight, catalog: [ngc7000]);
    expect(single.single.framing, TonightFraming.tooBig);

    final mosaic = computeTonightSkyLocal(
        site: site,
        optics: longFl,
        atUtc: autumnNight,
        catalog: [ngc7000],
        mosaicTilesX: 3,
        mosaicTilesY: 3);
    expect(mosaic.single.framing, TonightFraming.good);
    // Framing is the dominant score term — the mosaic plan must outrank.
    expect(mosaic.single.score!, greaterThan(single.single.score!));
  });

  PlanningDso dso(String id, double sizeMajArcmin,
          {double raDeg = 314.75, double decDeg = 44.33}) =>
      PlanningDso(
          id: id,
          name: id,
          type: 'OCl',
          magnitude: 5.0,
          raDeg: raDeg,
          decDeg: decDeg,
          sizeMajArcmin: sizeMajArcmin,
          sizeMinArcmin: sizeMajArcmin);

  test('framing tiers: fills ≥40%, good fit 15–40%, small <15% of short side', () {
    // The 250 mm train's short FOV side ≈ 4176·(206.265·3.76/250)/60 ≈ 216'.
    final night = DateTime.utc(2026, 10, 15, 3);
    List<TonightSkyObject> rank(double size) => computeTonightSkyLocal(
        site: site, optics: optics, atUtc: night, catalog: [dso('x', size)]);

    expect(rank(120).single.framing, TonightFraming.good); // 56% → fills
    expect(rank(60).single.framing, TonightFraming.goodFit); // 28% → good fit
    expect(rank(14).single.framing, TonightFraming.tooSmall); // 6.5% → small
    // A genuine frame-filler must outrank a good-fit which outranks a small.
    final fills = rank(120).single.score!;
    final goodFit = rank(60).single.score!;
    final small = rank(14).single.score!;
    expect(fills, greaterThan(goodFit));
    expect(goodFit, greaterThan(small));
  });

  test('type + filter-capability adjust the score, advisory-sized', () {
    final night = DateTime.utc(2026, 10, 15, 3);
    PlanningDso typed(String id, String type) => PlanningDso(
        id: id, name: id, type: type, magnitude: 5.0,
        raDeg: 314.75, decDeg: 44.33,
        sizeMajArcmin: 60, sizeMinArcmin: 60);
    const nb = FilterSetSettings(filters: [
      PlanningFilter(name: 'Ha', kind: FilterKind.ha),
      PlanningFilter(name: 'L', kind: FilterKind.l),
    ]);
    const broadOnly = FilterSetSettings(filters: [
      PlanningFilter(name: 'L', kind: FilterKind.l),
    ]);
    double scoreOf(String type, FilterSetSettings fs) => computeTonightSkyLocal(
            site: site,
            optics: optics,
            atUtc: night,
            filterSet: fs,
            catalog: [typed('x', type)])
        .single
        .score!;

    // Same geometry/brightness: an open cluster ranks below an HII region.
    expect(scoreOf('OCl', nb), lessThan(scoreOf('HII', nb)));
    // An emission target scores higher WITH narrowband glass than without.
    expect(scoreOf('HII', broadOnly), lessThan(scoreOf('HII', nb)));
    // Continuum targets are untouched by the filter factor.
    expect(scoreOf('G', broadOnly), scoreOf('G', nb));
  });

  test('custom horizon skyline gates per azimuth', () {
    // A target culminating high in the SOUTH. A skyline with a 60° wall in
    // the south must drop it even though the flat default (20°) would keep
    // it; a low southern skyline must keep it.
    final night = DateTime.utc(2026, 10, 15, 5);
    final southern = dso('south', 60, raDeg: 340.0, decDeg: 0.0);
    const siteWithSkyline = SiteSettings(
      siteName: 'test',
      latitudeDeg: 34.0,
      longitudeDeg: -84.0,
      bortleClass: 6,
      defaultHorizonAltitudeDeg: 20,
      useCustomHorizon: true,
      twilightDefinition: TwilightDefinition.astronomical,
      softWarningAltitudeDeg: 30,
    );
    // 60° terrain wall from az 90° to 270° (the whole southern sky), open north.
    final walled = computeTonightSkyLocal(
        site: siteWithSkyline,
        optics: optics,
        atUtc: night,
        catalog: [southern],
        customHorizon: [(0, 5), (90, 60), (270, 60), (359, 5)]);
    expect(walled, isEmpty, reason: 'a 60° southern wall hides a dec-0 target');

    // Same site, low skyline everywhere — the target is found again (and the
    // 5° southern horizon finds it EARLIER than the flat 20° default would).
    final open = computeTonightSkyLocal(
        site: siteWithSkyline,
        optics: optics,
        atUtc: night,
        catalog: [southern],
        customHorizon: [(0, 5), (90, 5), (270, 5), (359, 5)]);
    expect(open, hasLength(1));

    // useCustomHorizon off → the polygon is ignored, flat default gates.
    final toggleOff = computeTonightSkyLocal(
        site: site,
        optics: optics,
        atUtc: night,
        catalog: [southern],
        customHorizon: [(90, 60), (270, 60)]);
    expect(toggleOff, hasLength(1));
  });
}
