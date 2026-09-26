import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart' show clockProvider, isDarkNow, skyClockProvider, tonightSkyUpNowProvider;
import 'package:openastroara/util/imaging_regions.dart';
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

    TonightSkyObject only(List<TonightSkyObject> l) =>
        l.where((o) => o.id == 'NGC7000').single;
    final single = computeTonightSkyLocal(
        site: site, optics: longFl, atUtc: autumnNight, catalog: [ngc7000]);
    expect(only(single).framing, TonightFraming.tooBig);

    final mosaic = computeTonightSkyLocal(
        site: site,
        optics: longFl,
        atUtc: autumnNight,
        catalog: [ngc7000],
        mosaicTilesX: 3,
        mosaicTilesY: 3);
    expect(only(mosaic).framing, TonightFraming.good);
    // Framing is the dominant score term — the mosaic plan must outrank.
    expect(only(mosaic).score!, greaterThan(only(single).score!));
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

    TonightSkyObject one(double size) =>
        rank(size).where((o) => o.id == 'x').single;
    expect(one(120).framing, TonightFraming.good); // 56% → fills
    expect(one(60).framing, TonightFraming.goodFit); // 28% → good fit
    expect(one(14).framing, TonightFraming.tooSmall); // 6.5% → small
    // A genuine frame-filler must outrank a good-fit which outranks a small.
    final fills = one(120).score!;
    final goodFit = one(60).score!;
    final small = one(14).score!;
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
        .where((o) => o.id == 'x')
        .single
        .score!;

    // Same geometry/brightness: an open cluster ranks below an HII region.
    expect(scoreOf('OCl', nb), lessThan(scoreOf('HII', nb)));
    // An emission target scores higher WITH narrowband glass than without.
    expect(scoreOf('HII', broadOnly), lessThan(scoreOf('HII', nb)));
    // Continuum targets are untouched by the filter factor.
    expect(scoreOf('G', broadOnly), scoreOf('G', nb));
  });

  test('photometry-less emission rows: curated fields keep their score, the rest drop', () {
    final night = DateTime.utc(2026, 10, 15, 3);
    const nb = FilterSetSettings(filters: [
      PlanningFilter(name: 'Ha', kind: FilterKind.ha),
    ]);
    PlanningDso sh2(String id) => PlanningDso(
        id: id, name: id, type: 'HII', magnitude: null,
        raDeg: 314.75, decDeg: 44.33, sizeMajArcmin: 60);
    const galaxy = PlanningDso(
        id: 'NGC7331', name: 'NGC7331', type: 'G', magnitude: 9.5,
        raDeg: 339.267, decDeg: 34.416,
        sizeMajArcmin: 10.5, sizeMinArcmin: 3.7, surfaceBrightness: 22.5);
    final list = computeTonightSkyLocal(
        site: site, optics: optics, atUtc: night, filterSet: nb,
        catalog: [sh2('Sh2-110'), sh2('Sh2-105'), sh2('Sh2-126'), galaxy],
        limit: 50);
    double score(String id) => list.firstWhere((o) => o.id == id).score!;
    // Same geometry, same (absent) photometry: the Crescent is a showpiece,
    // Sh2-126 a faint specialist field, Sh2-110 an unknown that's mostly stars.
    expect(score('Sh2-105'), greaterThan(score('Sh2-126')));
    expect(score('Sh2-126'), greaterThan(score('Sh2-110')));
    expect(score('Sh2-110'), lessThan(score('NGC7331')),
        reason: 'a galaxy with real photometry beats an unknown Sharpless field');
    expect(list.firstWhere((o) => o.id == 'Sh2-110').scoreReasons!.join(' '),
        contains('not a known imaging field'));
    // A photometry-less cluster+nebula stub (IC 1310) is the same story.
    const stub = PlanningDso(
        id: 'IC1310', name: 'IC1310', type: 'Cl+N', magnitude: null,
        raDeg: 314.75, decDeg: 44.33, sizeMajArcmin: 60);
    final withStub = computeTonightSkyLocal(
        site: site, optics: optics, atUtc: night, filterSet: nb,
        catalog: const [stub, galaxy], limit: 50);
    expect(withStub.firstWhere((o) => o.id == 'IC1310').score!,
        lessThan(withStub.firstWhere((o) => o.id == 'NGC7331').score!));
  });

  test('a curated override keeps its photometry and is a showpiece, never an unknown field', () {
    // Review #1104: OpenNGC rows with neither V- nor B-Mag exist; the
    // override rebuilt the row WITHOUT surface brightness, so NGC 7822
    // (renamed "Question Mark region") fell to the unknown-field ×0.5.
    final night = DateTime.utc(2026, 10, 15, 3);
    const nb = FilterSetSettings(filters: [
      PlanningFilter(name: 'Ha', kind: FilterKind.ha),
    ]);
    const ngc7822 = PlanningDso(
        id: 'NGC7822', name: 'NGC7822', type: 'HII', magnitude: null,
        raDeg: 0.9, decDeg: 68.6, sizeMajArcmin: 30, surfaceBrightness: 22.0,
        posAngleDeg: 65.0);
    final list = computeTonightSkyLocal(
        site: site, optics: optics, atUtc: night, filterSet: nb,
        catalog: const [ngc7822], limit: 50);
    final row = list.firstWhere((o) => o.id == 'NGC7822');
    expect(row.name, contains('Question Mark'));
    expect(row.surfaceBrightness, 22.0, reason: 'the override keeps the SB');
    expect(row.posAngleDeg, 65.0, reason: 'and the position angle');
    final why = row.scoreReasons!.join(' ');
    expect(why, isNot(contains('not a known imaging field')));
    expect(why, contains('for Bortle'), reason: 'the SB term scored, not "unknown"');
    expect(why, isNot(contains('showpiece')),
        reason: 'a row WITH photometry is scored on it, no tier consulted');
    // An override with no photometry at all is a showpiece by membership.
    const california = PlanningDso(
        id: 'NGC1499', name: 'NGC1499', type: 'HII', magnitude: null,
        raDeg: 60.0, decDeg: 36.6, sizeMajArcmin: 145);
    final bare = computeTonightSkyLocal(
            site: site, optics: optics, atUtc: night, filterSet: nb,
            catalog: const [california], limit: 50)
        .firstWhere((o) => o.id == 'NGC1499');
    expect(bare.scoreReasons!.join(' '), contains('showpiece imaging field (+0)'));
    // Standalone regions are tier 3 by membership, no table entry needed.
    expect(photogenicTierOf('REGION-SH2-101'), 3);
    expect(photogenicTierOf('NGC1499'), 3);
    expect(photogenicTierOf('Sh2-110'), isNull);
  });

  test('a bare OSC (empty filter set) is scored as broadband, harder under bright skies', () {
    final night = DateTime.utc(2026, 10, 15, 3);
    const hii = PlanningDso(
        id: 'X', name: 'X', type: 'HII', magnitude: 5.0,
        raDeg: 314.75, decDeg: 44.33, sizeMajArcmin: 60, sizeMinArcmin: 60);
    const galaxy = PlanningDso(
        id: 'G', name: 'G', type: 'G', magnitude: 5.0,
        raDeg: 314.75, decDeg: 44.33, sizeMajArcmin: 60, sizeMinArcmin: 60);
    const nb = FilterSetSettings(filters: [
      PlanningFilter(name: 'Ha', kind: FilterKind.ha),
    ]);
    double scoreOf(String id, FilterSetSettings fs, {int bortle = 4}) =>
        computeTonightSkyLocal(
                site: site.copyWith(bortleClass: bortle),
                optics: optics,
                atUtc: night,
                filterSet: fs,
                catalog: const [hii, galaxy])
            .firstWhere((o) => o.id == id)
            .score!;
    const osc = FilterSetSettings(filters: []);
    // Before: an empty set skipped the factor entirely — same as having Hα.
    expect(scoreOf('X', osc), lessThan(scoreOf('X', nb)));
    // Bortle 6 with no narrowband is penalised more than Bortle 3.
    expect(scoreOf('X', osc, bortle: 6), lessThan(scoreOf('X', osc, bortle: 3)));
    // Continuum targets are untouched by any of it.
    expect(scoreOf('G', osc), scoreOf('G', nb));
    // The split applies to a declared broadband-only set the same way.
    const broadOnly = FilterSetSettings(filters: [
      PlanningFilter(name: 'L', kind: FilterKind.l),
    ]);
    expect(scoreOf('X', broadOnly, bortle: 6), lessThan(scoreOf('X', broadOnly, bortle: 3)));
    expect(scoreOf('X', broadOnly, bortle: 3), closeTo(scoreOf('X', osc, bortle: 3), 1e-9),
        reason: 'no narrowband is no narrowband, declared or not');
  });

  test('a standalone curated region replaces the raw Sharpless row', () {
    final night = DateTime.utc(2026, 10, 15, 3);
    const raw = PlanningDso(
        id: 'Sh2-101', name: 'Sh2-101', type: 'HII', magnitude: null,
        raDeg: 300.0, decDeg: 35.3, sizeMajArcmin: 20);
    final list = computeTonightSkyLocal(
        site: site, optics: optics, atUtc: night, catalog: const [raw]);
    expect(list.where((o) => o.id == 'Sh2-101'), isEmpty,
        reason: 'REGION-SH2-101 (Tulip) stands in for it');
    expect(list.where((o) => o.id == 'REGION-SH2-101'), hasLength(1));
  });

  test('dark nebulae rank below real photometry, never flood the list', () {
    // The LDN/Barnard packages carry ONLY a major axis: no magnitude, no
    // surface brightness. Scored neutral on both they hit a flat 90 whenever
    // they transit high, and ~2,100 of them filled every slot of the
    // 30-item list — no NGC, no Messier, nothing else.
    final night = DateTime.utc(2026, 10, 15, 3);
    PlanningDso ldn(int n) => PlanningDso(
        id: 'LDN $n', name: 'LDN $n', type: 'DrkN', magnitude: null,
        raDeg: 314.75 + n * 0.01, decDeg: 44.33,
        sizeMajArcmin: 120);
    // A modest galaxy, small in a 250 mm frame, with honest photometry.
    const galaxy = PlanningDso(
        id: 'NGC7331', name: 'NGC7331', type: 'G', magnitude: 9.5,
        raDeg: 339.267, decDeg: 34.416,
        sizeMajArcmin: 10.5, sizeMinArcmin: 3.7, surfaceBrightness: 22.5);
    // An emission region from Sharpless: also magnitude-less, size only.
    const sh2 = PlanningDso(
        id: 'Sh2-119', name: 'Sh2-119', type: 'HII', magnitude: null,
        raDeg: 319.6, decDeg: 43.9, sizeMajArcmin: 160);
    final list = computeTonightSkyLocal(
        site: site, optics: optics, atUtc: night,
        catalog: [for (var i = 1; i <= 60; i++) ldn(i), galaxy, sh2],
        limit: 30);
    final ids = list.map((o) => o.id).toList();
    expect(ids, contains('NGC7331'));
    expect(ids, contains('Sh2-119'));
    final ldnBest = list.firstWhere((o) => o.type == 'DrkN');
    expect(ldnBest.score!, lessThan(list.firstWhere((o) => o.id == 'NGC7331').score!));
    expect(ldnBest.score!, lessThan(list.firstWhere((o) => o.id == 'Sh2-119').score!));
    // "Never flood" = never crowd a real target out: with 400 dark nebulae
    // in a 30-slot list, both real targets are still listed and every dark
    // nebula that made the list sits below them. (A share-of-list assertion
    // is meaningless on a two-target synthetic catalog — the remaining
    // slots have nothing else to hold.)
    final flood = computeTonightSkyLocal(
        site: site, optics: optics, atUtc: night,
        catalog: [for (var i = 1; i <= 400; i++) ldn(i), galaxy, sh2],
        limit: 30);
    final ids400 = flood.map((o) => o.id).toList();
    expect(ids400, containsAll(['NGC7331', 'Sh2-119']));
    final firstDark = flood.indexWhere((o) => o.type == 'DrkN');
    expect(firstDark, greaterThan(ids400.indexOf('NGC7331')));
    expect(firstDark, greaterThan(ids400.indexOf('Sh2-119')));
    // Still listed (advise, don't dictate), with the why spelled out — BOTH
    // halves of the rule: the ×0.6 factor and the SB floor (review #1104:
    // deleting the floor left every assertion green).
    final why = ldnBest.scoreReasons!.join(' ');
    expect(why, contains('dark nebula'));
    expect(why, contains('silhouette on the sky (+2)'),
        reason: '12 × 0.15 floor, not the 0.5 neutral (+6)');
    // Same geometry, same missing photometry: a DrkN scores below a Neb
    // before the type factor even applies — the floor alone is worth 4 pts.
    const neb = PlanningDso(
        id: 'NEB', name: 'NEB', type: 'Neb', magnitude: null,
        raDeg: 314.75, decDeg: 44.33, sizeMajArcmin: 120);
    const drk = PlanningDso(
        id: 'DRK', name: 'DRK', type: 'DrkN', magnitude: null,
        raDeg: 314.75, decDeg: 44.33, sizeMajArcmin: 120);
    final pair = computeTonightSkyLocal(
        site: site, optics: optics, atUtc: night, catalog: const [neb, drk], limit: 50);
    final nebWhy = pair.firstWhere((o) => o.id == 'NEB').scoreReasons!.join(' ');
    expect(nebWhy, contains('surface brightness unknown (+6)'));
    expect(pair.firstWhere((o) => o.id == 'DRK').scoreReasons!.join(' '),
        contains('(+2)'));
  });

  test('a curated region replaces the Sharpless row it stands for — only when present', () {
    final night = DateTime.utc(2026, 10, 15, 3);
    const sh2105 = PlanningDso(
        id: 'Sh2-105', name: 'Sh2-105', type: 'HII', magnitude: null,
        raDeg: 303.05, decDeg: 38.35, sizeMajArcmin: 20);
    const ngc6888 = PlanningDso(
        id: 'NGC6888', name: 'NGC6888', type: 'EmN', magnitude: 7.4,
        raDeg: 303.05, decDeg: 38.35, sizeMajArcmin: 18);
    const sh2240 = PlanningDso(
        id: 'Sh2-240', name: 'Sh2-240', type: 'HII', magnitude: null,
        raDeg: 85.25, decDeg: 28.1, sizeMajArcmin: 180);
    // Sharpless installed, OpenNGC too: the Crescent lists once, as NGC 6888.
    final both = computeTonightSkyLocal(
        site: site, optics: optics, atUtc: night,
        catalog: const [sh2105, ngc6888, sh2240], limit: 60);
    expect(both.where((o) => o.id == 'Sh2-105'), isEmpty);
    expect(both.where((o) => o.id == 'NGC6888'), hasLength(1));
    // Simeis 147 is a STANDALONE region: Sh2-240 is always replaced by it.
    expect(both.where((o) => o.id == 'Sh2-240'), isEmpty);
    expect(both.where((o) => o.id == 'REGION-SIMEIS-147'), hasLength(1));
    // Sharpless only (no NGC row): Sh2-105 is the only Crescent and stays,
    // as a showpiece by membership.
    final only = computeTonightSkyLocal(
        site: site, optics: optics, atUtc: night, catalog: const [sh2105], limit: 60);
    final kept = only.firstWhere((o) => o.id == 'Sh2-105');
    expect(kept.scoreReasons!.join(' '), contains('showpiece'));
    expect(photogenicTierOf('Sh2-240'), 3);
    // Round 3: the remaining override/Sharpless pairs are anchored too.
    const sh225 = PlanningDso(
        id: 'Sh2-25', name: 'Sh2-25', type: 'HII', magnitude: null,
        raDeg: 270.9, decDeg: -24.4, sizeMajArcmin: 90);
    const m8 = PlanningDso(
        id: 'NGC6523', name: 'NGC6523', type: 'HII', magnitude: 6.0,
        raDeg: 270.9, decDeg: -24.4, sizeMajArcmin: 90);
    final summer = DateTime.utc(2026, 7, 17, 6);
    final lagoon = computeTonightSkyLocal(
        site: site, optics: optics, atUtc: summer, catalog: const [sh225, m8], limit: 60);
    expect(lagoon.where((o) => o.id == 'Sh2-25'), isEmpty);
    expect(lagoon.firstWhere((o) => o.id == 'NGC6523').name, contains('Lagoon'));
    expect(photogenicTierOf('Sh2-279'), 3, reason: 'M42 stands for two Sharpless rows');
    expect(photogenicTierOf('Sh2-281'), 3);
  });

  test('curated imaging regions override catalog core-sizes and add fields', () {
    // OpenNGC undersells the famous complexes: NGC 6618 is a 12.6' "Checkmark"
    // core but the imaged Swan runs ~45'; NGC 6604 is a 9.6' OCl inside the
    // degrees-wide Sh2-54 field. The curated layer must rename + resize them
    // so the framing tiers judge what the imager actually frames.
    final night = DateTime.utc(2026, 7, 17, 6); // summer night, Sagittarius up
    final catalog = [
      PlanningDso(
          id: 'NGC6618', name: 'Checkmark Nebula', type: 'Neb',
          magnitude: 7.0, raDeg: 275.196, decDeg: -16.17,
          sizeMajArcmin: 12.6),
      PlanningDso(
          id: 'NGC6604', name: 'NGC6604', type: 'OCl',
          magnitude: 6.5, raDeg: 274.512, decDeg: -12.24,
          sizeMajArcmin: 9.6),
    ];
    final list = computeTonightSkyLocal(
        site: site, optics: optics, atUtc: night, catalog: catalog, limit: 30);
    final swan = list.where((o) => o.id == 'NGC6618').single;
    expect(swan.name, contains('Swan'));
    expect(swan.framing, TonightFraming.goodFit,
        reason: "45' against a 216' short side is a good fit, not Small");
    final sh254 = list.where((o) => o.id == 'NGC6604').single;
    expect(sh254.name, contains('Sh2-54'));
    expect(sh254.framing, TonightFraming.good,
        reason: "150' fills the frame — and no OCl discount as an HII region");
    // Region-scale standalone fields ride along (Rho Oph is up in July).
    expect(list.where((o) => o.id == 'REGION-RHO-OPH'), hasLength(1));
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
    expect(walled.where((o) => o.id == 'south'), isEmpty,
        reason: 'a 60° southern wall hides a dec-0 target');

    // Same site, low skyline everywhere — the target is found again (and the
    // 5° southern horizon finds it EARLIER than the flat 20° default would).
    final open = computeTonightSkyLocal(
        site: siteWithSkyline,
        optics: optics,
        atUtc: night,
        catalog: [southern],
        customHorizon: [(0, 5), (90, 5), (270, 5), (359, 5)]);
    expect(open.where((o) => o.id == 'south'), hasLength(1));

    // useCustomHorizon off → the polygon is ignored, flat default gates.
    final toggleOff = computeTonightSkyLocal(
        site: site,
        optics: optics,
        atUtc: night,
        catalog: [southern],
        customHorizon: [(90, 60), (270, 60)]);
    expect(toggleOff.where((o) => o.id == 'south'), hasLength(1));
  });

  test('daytime scores are anchored to the coming night, not the wall clock',
      () {
    // Live repro of the drifting-score bug: the same site asked at mid-morning
    // vs early afternoon must describe the SAME upcoming night — identical
    // dark windows and identical scores. (The old ±12h-of-now grid clipped
    // the previous night's dusk minute by minute across the day.)
    final morning = DateTime.utc(2026, 1, 14, 16); // ≈ 11:00 local
    final afternoon = DateTime.utc(2026, 1, 14, 19); // ≈ 14:00 local
    final a = rank(at: morning, limit: 20);
    final b = rank(at: afternoon, limit: 20);
    expect(a.map((o) => o.id), b.map((o) => o.id));
    for (var i = 0; i < a.length; i++) {
      expect(a[i].score, b[i].score,
          reason: '${a[i].id} score must not drift across the day');
      expect(a[i].windowStartUtc, b[i].windowStartUtc,
          reason: '${a[i].id} window must be tonight\'s in both asks');
      expect(a[i].integrationHours, b[i].integrationHours);
    }
    // And the daytime window is the COMING night: it opens after "now".
    expect(a.first.windowStartUtc!.isAfter(morning), isTrue);

    // #861 review round 2 — the day/night branch boundary and the small
    // hours. A mid-night ask (02:00 local, inside the window: sun-down
    // branch, anchor = now) and an ask just before dusk (sun-up branch,
    // anchor = coming midnight) must both describe the SAME night as the
    // daytime asks: the 5-min snap phase-aligns every grid, so windows are
    // bit-identical while the night fits the ±12 h span.
    final midNight = DateTime.utc(2026, 1, 15, 7); // ≈ 02:00 local Jan 15
    final nearDusk = DateTime.utc(2026, 1, 14, 22); // ≈ 17:00 local, sun up
    final c = rank(at: midNight, limit: 20);
    final d = rank(at: nearDusk, limit: 20);
    expect(c.map((o) => o.id), a.map((o) => o.id));
    expect(d.map((o) => o.id), a.map((o) => o.id));
    for (var i = 0; i < a.length; i++) {
      expect(c[i].windowStartUtc, a[i].windowStartUtc,
          reason: '${a[i].id}: a small-hours ask must score the SAME night');
      expect(d[i].windowStartUtc, a[i].windowStartUtc,
          reason: '${a[i].id}: a near-dusk ask must score the SAME night');
      expect(c[i].score, a[i].score);
      expect(d[i].score, a[i].score);
    }
  });

  test('stars in the mirror (WR package) are searchable, never ranked', () {
    final night = DateTime.utc(2026, 10, 15, 3);
    const wr = PlanningDso(
        id: 'WR 134', name: 'WR 134', type: 'WR*', magnitude: 8.1,
        raDeg: 302.28, decDeg: 36.18);
    // OpenNGC's own star rows (bright doubles used to rank on mag ≤ 12).
    const star = PlanningDso(
        id: 'NGC0017', name: 'NGC0017', type: '*', magnitude: 9.0,
        raDeg: 302.0, decDeg: 40.0);
    const dbl = PlanningDso(
        id: 'NGC0018', name: 'NGC0018', type: '**', magnitude: 6.0,
        raDeg: 303.0, decDeg: 41.0);
    final list = computeTonightSkyLocal(
        site: site, optics: optics, atUtc: night, catalog: const [wr, star, dbl], limit: 50);
    expect(list.where((o) => o.id == 'WR 134'), isEmpty);
    expect(list.where((o) => o.type == '*' || o.type == '**'), isEmpty);
    // …but the curated WR 134 ring (a nebula) still ranks.
    expect(list.where((o) => o.id == 'REGION-WR134'), hasLength(1));
  });

  test('isDarkNow follows the sun at the site, never for an unset site', () {
    const belen = SiteSettings(latitudeDeg: 34.67, longitudeDeg: -106.79);
    // 22:43 MDT on 2026-09-25 = 04:43 UTC on the 26th: well after dusk.
    expect(isDarkNow(belen, nowUtc: DateTime.utc(2026, 9, 26, 4, 43)), isTrue);
    // 15:00 MDT = 21:00 UTC: broad daylight.
    expect(isDarkNow(belen, nowUtc: DateTime.utc(2026, 9, 25, 21, 0)), isFalse);
    // The (0, 0) "not set" sentinel is never dark.
    expect(isDarkNow(const SiteSettings(), nowUtc: DateTime.utc(2026, 9, 26, 4, 43)),
        isFalse);
  });

  test('a tapped Up-now chip stays pinned when the site changes underneath it', () {
    final c = ProviderContainer();
    addTearDown(c.dispose);
    // Unset site → auto = off.
    expect(c.read(tonightSkyUpNowProvider), isFalse);
    c.read(tonightSkyUpNowProvider.notifier).set(true);
    expect(c.read(tonightSkyUpNowProvider), isTrue);
    // A site edit re-runs build(); the pin must win over the auto rule
    // (daylight at this site would otherwise flip it off).
    c.read(siteSettingsProvider.notifier).setLatitudeDeg(34.0);
    c.read(siteSettingsProvider.notifier).setLongitudeDeg(-106.0);
    expect(c.read(tonightSkyUpNowProvider), isTrue);
  });

  test('Up now switches itself on when the clock crosses into dark, until pinned', () {
    const belen = SiteSettings(latitudeDeg: 34.67, longitudeDeg: -106.79);
    var now = DateTime.utc(2026, 9, 25, 21, 0); // 15:00 MDT, daylight
    final ticks = StreamController<int>.broadcast();
    addTearDown(ticks.close);
    final c = ProviderContainer(overrides: [
      clockProvider.overrideWithValue(() => now),
      skyClockProvider.overrideWith((ref) => ticks.stream),
      siteSettingsProvider.overrideWith(() => _SeededSite(belen)),
    ]);
    addTearDown(c.dispose);
    final keep = c.listen(tonightSkyUpNowProvider, (_, _) {});
    addTearDown(keep.close);
    expect(c.read(tonightSkyUpNowProvider), isFalse);
    // The evening passes; the next tick re-evaluates against the new clock.
    now = DateTime.utc(2026, 9, 26, 4, 43); // 22:43 MDT
    ticks.add(1);
    return Future<void>.delayed(Duration.zero).then((_) {
      expect(c.read(tonightSkyUpNowProvider), isTrue, reason: 'dark now');
      // A tap pins it; later ticks leave it alone.
      c.read(tonightSkyUpNowProvider.notifier).set(false);
      now = DateTime.utc(2026, 9, 26, 5, 30);
      ticks.add(2);
      return Future<void>.delayed(Duration.zero);
    }).then((_) {
      expect(c.read(tonightSkyUpNowProvider), isFalse, reason: 'pinned');
    });
  });
  test('a Wolf-Rayet star never enters the ranked list, however bright', () {
    // Review #1105: a WR* row has no size, so the framing score is the
    // NEUTRAL 0.5 rather than the too-small floor — a mag-8 star with no
    // photometry outranked real galaxies on a wide-field rig. Orion-ish
    // coordinates so it is well up on the winter night.
    const catalog = [
      PlanningDso(id: 'WR 1', name: 'WR 1', type: 'WR*', magnitude: 8,
          raDeg: 85, decDeg: -5),
      PlanningDso(id: 'NGC 1', name: 'small galaxy', type: 'G', magnitude: 11,
          raDeg: 86, decDeg: -4, sizeMajArcmin: 3, surfaceBrightness: 22.5),
    ];
    final list = computeTonightSkyLocal(
        site: site, optics: optics, atUtc: winterNight, catalog: catalog, limit: 30);
    expect(list.map((o) => o.id), contains('NGC 1'));
    expect(list.map((o) => o.id), isNot(contains('WR 1')));
  });
}

class _SeededSite extends SiteSettingsNotifier {
  _SeededSite(this._seed);
  final SiteSettings _seed;
  @override
  SiteSettings build() => _seed;
}
