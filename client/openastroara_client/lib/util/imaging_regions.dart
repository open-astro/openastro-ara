import '../services/dso_catalog_service.dart';

/// §36.8 curated imaging regions — the "take me somewhere amazing" layer over
/// the raw OpenNGC catalog.
///
/// OpenNGC records the size of the CATALOGED object, which for famous emission
/// complexes is often just the bright core (M17 "Checkmark" = 12.6′; the
/// nebula an imager actually frames runs ~45′ — and NGC 6604 is a 9.6′ open
/// cluster sitting inside the degrees-wide Sh2-54 field). Ranked on those
/// numbers, exactly the targets a wide-field narrowband rig is FOR read as
/// "Small open cluster" and vanish. This table fixes the data, not the math:
///
///  * [overrides] — keyed by OpenNGC id: the imaging-region name, realistic
///    imaging extent, and (where the region is emission though the cataloged
///    object isn't) the region's type, replacing the catalog row's fields.
///  * [standaloneRegions] — famous imaging fields with no single catalog
///    object to hang them on (Rho Ophiuchi, Simeis 147). Appended to the
///    ranked set.
///
/// Sizes are conventional imaging extents (rounded), not survey isophotes —
/// they exist so the §36.8 framing tiers judge the region the imager frames.
class ImagingRegion {
  final String name;
  final String type; // OpenNGC-style code — drives emission class + tier logic
  final double sizeMajArcmin;
  final double? sizeMinArcmin;
  const ImagingRegion(this.name, this.type, this.sizeMajArcmin,
      [this.sizeMinArcmin]);
}

const Map<String, ImagingRegion> overrides = {
  // Sagittarius / Serpens — the summer emission corridor.
  'NGC6618': ImagingRegion('Swan Nebula (M17)', 'HII', 45, 35),
  'NGC6611': ImagingRegion('Eagle Nebula (M16)', 'HII', 70, 55),
  'NGC6604': ImagingRegion('Sh2-54 region (NGC 6604)', 'HII', 150, 100),
  'NGC6523': ImagingRegion('Lagoon Nebula (M8)', 'HII', 90, 40),
  'NGC6514': ImagingRegion('Trifid Nebula (M20)', 'HII', 28, 28),
  // Cygnus / Cepheus.
  'IC1396': ImagingRegion("Elephant's Trunk region (IC 1396)", 'HII', 170, 140),
  'NGC7023': ImagingRegion('Iris Nebula + dust (NGC 7023)', 'RfN', 60, 60),
  // Cassiopeia / Auriga / Monoceros — the winter corridor.
  'IC1805': ImagingRegion('Heart Nebula (IC 1805)', 'HII', 150, 150),
  'IC1848': ImagingRegion('Soul Nebula (IC 1848)', 'HII', 150, 75),
  'NGC281': ImagingRegion('Pacman Nebula (NGC 281)', 'HII', 35, 30),
  'IC443': ImagingRegion('Jellyfish Nebula (IC 443)', 'SNR', 50, 40),
  'NGC2264': ImagingRegion('Cone / Christmas Tree region (NGC 2264)', 'HII', 60, 30),
  'NGC2244': ImagingRegion('Rosette Nebula (NGC 2244)', 'HII', 80, 60),
  'NGC1499': ImagingRegion('California Nebula (NGC 1499)', 'HII', 145, 40),
  // Orion.
  'NGC1976': ImagingRegion('Orion Nebula + Running Man (M42)', 'HII', 85, 60),
  'IC434': ImagingRegion('Horsehead + Flame region (IC 434)', 'HII', 60, 30),
  // Wolf-Rayet shells — the blown-off envelopes of the most massive stars;
  // spectacular OIII/Ha narrowband targets most imagers have never framed.
  'NGC2359': ImagingRegion("Thor's Helmet — WR 7 shell (NGC 2359)", 'EmN', 22, 16),
  'NGC6888': ImagingRegion('Crescent Nebula — WR 136 shell (NGC 6888)', 'EmN', 20, 12),
  'NGC3199': ImagingRegion('Dragon-head — WR 18 shell (NGC 3199)', 'EmN', 22, 15),
  // Named favourites whose catalog rows undersell or anonymise them.
  'NGC246': ImagingRegion('Skull Nebula (NGC 246)', 'PN', 4.5, 4),
  'NGC7822': ImagingRegion('Question Mark region (NGC 7822 / Ced 214)', 'HII', 100, 60),
  'NGC6820': ImagingRegion('Sh2-86 region (NGC 6820)', 'HII', 40, 30),
  'NGC7380': ImagingRegion('Wizard Nebula (NGC 7380)', 'HII', 25, 25),
};

/// Photogenic tier for catalog rows that carry NO photometry (the Sharpless
/// package has 314 rows with zero magnitudes and zero surface brightness —
/// the ranker literally cannot tell Sh2-110 from the Tulip). 3 = showpiece,
/// 2 = a good field, 1 = faint/specialist but worth knowing about. A row not
/// listed here is "no photometry and not a known imaging field" and the
/// ranker discounts it hard: most Sharpless entries are HII regions that
/// image as a star field with a faint glow — not what a wide-field rig is
/// for. This is a stopgap until sky-data carries the Sharpless brightness
/// class; keep the ids in catalog spelling. Sharpless ids a curated region
/// stands for ([sharplessAnchors]: Tulip, Crescent, Heart, Spaghetti …) are
/// not listed here — they are tier 3 by membership, and their raw row is
/// replaced by the region wherever the region is present.
const Map<String, int> photogenicTier = {
  // Scorpius / Sagittarius / Serpens.
  'Sh2-8': 3, // Cat's Paw (NGC 6334)
  'Sh2-11': 3, // Lobster (NGC 6357)
  'Sh2-64': 2, // W40
  // Vulpecula / Cygnus.
  'Sh2-88': 2,
  'Sh2-91': 1, // Cygnus SNR filament — faint
  'Sh2-104': 2,
  'Sh2-106': 2,
  'Sh2-108': 3, // Sadr / Butterfly (IC 1318)
  'Sh2-112': 2,
  'Sh2-115': 2,
  'Sh2-117': 3, // North America + Pelican
  'Sh2-119': 2, // Clamshell
  'Sh2-124': 2,
  'Sh2-126': 1, // Lacerta — huge, faint
  // Cepheus / Cassiopeia.
  'Sh2-140': 2,
  'Sh2-162': 3, // Bubble (NGC 7635)
  'Sh2-170': 2, // Little Rosette
  'Sh2-173': 2, // Phantom
  'Sh2-185': 3, // Ghost of Cassiopeia (IC 59/63)
  'Sh2-188': 1, // Dolphin — faint
  // Perseus / Auriga / Taurus.
  'Sh2-216': 1, // giant faint PN
  'Sh2-224': 1, // faint SNR
  'Sh2-229': 3, // Flaming Star (IC 405)
  'Sh2-236': 3, // Tadpoles (IC 410)
  // Gemini / Orion / Monoceros.
  'Sh2-252': 3, // Monkey Head (NGC 2174)
  'Sh2-261': 2, // Lower's
  'Sh2-264': 3, // Lambda Orionis ring
  'Sh2-276': 3, // Barnard's Loop
  'Sh2-284': 2,
  'Sh2-292': 3, // Seagull head
  'Sh2-296': 3, // Seagull (IC 2177)
  'Sh2-302': 2,
  'Sh2-311': 2, // NGC 2467
};

/// The tier a photometry-less row earns, or null when it isn't a known
/// imaging field. Membership in the curated layer itself — an [overrides]
/// key, a standalone region, or a Sharpless id a curated region stands for
/// ([sharplessAnchors]) — IS the showpiece tier: that table exists to
/// promote exactly those fields (review #1104: a magnitude-less OpenNGC
/// override like NGC 7822 fell to the unknown-field discount).
int? photogenicTierOf(String id) {
  if (overrides.containsKey(id) ||
      _standaloneIds.contains(id) ||
      _anchoredSharpless.contains(id)) {
    return 3;
  }
  return photogenicTier[id];
}

final Set<String> _standaloneIds = {for (final r in standaloneRegions) r.id};
final Set<String> _anchoredSharpless = {for (final a in sharplessAnchors) a.$2};

/// Curated region → the Sharpless row that is the SAME nebula, so the
/// Sharpless package's own row doesn't list beside the region (the Tulip as
/// "Tulip Nebula" and again as "Sh2-101"; the Lagoon as M8 and as Sh2-25).
/// A standalone region always stands in; an NGC/IC-keyed override only when
/// its catalog row is actually present — otherwise the Sharpless row is the
/// only listing and stays (tier 3 by membership). Pairs, not a map: one
/// override can stand for two Sharpless rows (M42's field is Sh2-281 and
/// the Running Man's Sh2-279). Review #1104, rounds 1–3.
const List<(String region, String sharpless)> sharplessAnchors = [
  // Standalone regions.
  ('REGION-SH2-101', 'Sh2-101'),
  ('REGION-SH2-129', 'Sh2-129'),
  ('REGION-SH2-132', 'Sh2-132'),
  ('REGION-SH2-155', 'Sh2-155'),
  ('REGION-SH2-157', 'Sh2-157'),
  ('REGION-SH2-308', 'Sh2-308'),
  ('REGION-SIMEIS-147', 'Sh2-240'),
  // NGC/IC-keyed overrides.
  ('NGC6523', 'Sh2-25'), // Lagoon
  ('NGC6514', 'Sh2-30'), // Trifid
  ('NGC6618', 'Sh2-45'), // Omega
  ('NGC6611', 'Sh2-49'), // Eagle
  ('NGC6604', 'Sh2-54'),
  ('NGC6820', 'Sh2-86'),
  ('NGC6888', 'Sh2-105'), // Crescent
  ('IC1396', 'Sh2-131'),
  ('NGC7380', 'Sh2-142'), // Wizard
  ('NGC7822', 'Sh2-171'),
  ('IC1805', 'Sh2-190'), // Heart
  ('IC1848', 'Sh2-199'), // Soul
  ('NGC1499', 'Sh2-220'), // California
  ('IC443', 'Sh2-248'), // Jellyfish
  ('NGC2264', 'Sh2-273'), // Cone
  ('NGC2244', 'Sh2-275'), // Rosette
  ('IC434', 'Sh2-277'), // Flame / Horsehead
  ('NGC1976', 'Sh2-279'), // Running Man
  ('NGC1976', 'Sh2-281'), // Orion Nebula
  ('NGC2359', 'Sh2-298'), // Thor's Helmet
];

/// Region-scale fields with no single catalog anchor. Ids are stable and
/// namespaced so they can never collide with an OpenNGC name.
final List<PlanningDso> standaloneRegions = [
  PlanningDso(
    id: 'REGION-RHO-OPH',
    name: 'Rho Ophiuchi cloud complex',
    type: 'RfN', // colorful reflection + dark dust — a broadband showpiece
    magnitude: 4.6,
    raDeg: 246.4,
    decDeg: -24.4,
    sizeMajArcmin: 270,
    sizeMinArcmin: 210,
  ),
  PlanningDso(
    id: 'REGION-SIMEIS-147',
    name: 'Spaghetti Nebula (Simeis 147)',
    type: 'SNR',
    magnitude: 7.0,
    raDeg: 84.75,
    decDeg: 27.83,
    sizeMajArcmin: 200,
    sizeMinArcmin: 180,
  ),
  // Wolf-Rayet shells with no NGC/IC anchor.
  PlanningDso(
    id: 'REGION-SH2-308',
    name: 'Dolphin-Head Nebula — WR 6 shell (Sh2-308)',
    type: 'EmN', // ghost-blue OIII bubble
    magnitude: 8.0,
    raDeg: 103.4,
    decDeg: -23.93,
    sizeMajArcmin: 40,
    sizeMinArcmin: 40,
  ),
  PlanningDso(
    id: 'REGION-WR134',
    name: 'WR 134 ring (Cygnus OIII shell)',
    type: 'EmN',
    magnitude: 8.1,
    raDeg: 302.28,
    decDeg: 36.18,
    sizeMajArcmin: 25,
    sizeMinArcmin: 25,
  ),
  // Sharpless "cloudy wonderland" — big narrowband fields with great names
  // that most imagers have never pointed at. Cepheus/Cygnus circumpolar-ish
  // from mid-northern sites, so they show up night after night.
  PlanningDso(
    id: 'REGION-SH2-132',
    name: 'Lion Nebula (Sh2-132)',
    type: 'EmN',
    magnitude: 9.0,
    raDeg: 334.8,
    decDeg: 56.1,
    sizeMajArcmin: 80,
    sizeMinArcmin: 60,
  ),
  PlanningDso(
    id: 'REGION-SH2-129',
    name: 'Flying Bat + Squid (Sh2-129 / OU4)',
    type: 'EmN',
    magnitude: 9.5,
    raDeg: 317.9,
    decDeg: 59.95,
    sizeMajArcmin: 140,
    sizeMinArcmin: 100,
  ),
  PlanningDso(
    id: 'REGION-SH2-101',
    name: 'Tulip Nebula (Sh2-101)',
    type: 'EmN',
    magnitude: 9.0,
    raDeg: 300.0,
    decDeg: 35.27,
    sizeMajArcmin: 20,
    sizeMinArcmin: 12,
  ),
  PlanningDso(
    id: 'REGION-SH2-155',
    name: 'Cave Nebula (Sh2-155)',
    type: 'EmN',
    magnitude: 7.7,
    raDeg: 344.2,
    decDeg: 62.62,
    sizeMajArcmin: 50,
    sizeMinArcmin: 30,
  ),
  PlanningDso(
    id: 'REGION-SH2-157',
    name: 'Lobster Claw Nebula (Sh2-157)',
    type: 'EmN',
    magnitude: 8.5,
    raDeg: 349.0,
    decDeg: 60.3,
    sizeMajArcmin: 60,
    sizeMinArcmin: 50,
  ),
];

/// Apply the curated layer: replace matching rows' display name / size / type
/// and append the standalone regions. Pure + cheap — runs inside the ranking
/// isolate on every recompute.
List<PlanningDso> applyImagingRegions(List<PlanningDso> catalog) {
  final present = {for (final o in catalog) o.id};
  final covered = {
    for (final a in sharplessAnchors)
      if (_standaloneIds.contains(a.$1) || present.contains(a.$1)) a.$2,
  };
  final merged = [
    for (final o in catalog)
      if (!covered.contains(o.id))
      switch (overrides[o.id]) {
        null => o,
        final r => PlanningDso(
            id: o.id,
            name: r.name,
            type: r.type,
            magnitude: o.magnitude,
            raDeg: o.raDeg,
            decDeg: o.decDeg,
            sizeMajArcmin: r.sizeMajArcmin,
            sizeMinArcmin: r.sizeMinArcmin,
            // The override replaces the NAME, TYPE and imaging extent; the
            // catalog's measured photometry stays (dropping the surface
            // brightness silently cost the 12-point SB term and the
            // integration-budget line, review #1104).
            posAngleDeg: o.posAngleDeg,
            surfaceBrightness: o.surfaceBrightness,
          ),
      },
    ...standaloneRegions,
  ];
  return merged;
}
