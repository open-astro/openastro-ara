import '../services/dso_catalog_service.dart';

/// #1720 curated common names, laid over the bundled catalogs the way
/// imaging_regions.dart lays its regions: a Dart const table, applied once
/// after the merge (a bundled asset would need a second parser for a list this
/// short). A name is ADDED as an alias, so search finds the object by its
/// nickname; it never replaces a name the catalog already carries. A row with
/// no common name of its own (its display name is its id) shows the first one.
///
/// Only names with a source: an entry cites the SIMBAD `NAME` identifier or
/// the issue that asked for it. OpenNGC's Common names column already names the
/// other Messier and Caldwell objects that have a well-known name, and the
/// rest are left out until a name with a source is found.
///
/// Keyed by the catalog row's id (OpenNGC spelling, zero padded).
const Map<String, List<String>> commonNames = {
  // The reported case, C 56 (#1720); the imaging-regions table calls it the same.
  'NGC0246': ['Skull Nebula'],
  // M 101; SIMBAD NAME identifier "Pinwheel" (simbad.cds.unistra.fr, ident of NGC 5457).
  'NGC5457': ['Pinwheel'],
};

/// [catalog] with [names] laid over it. Pure.
List<PlanningDso> applyCommonNames(
  List<PlanningDso> catalog, {
  Map<String, List<String>> names = commonNames,
}) => [
  for (final d in catalog)
    switch (names[d.id]) {
      null => d,
      final extra => _withNames(d, extra),
    },
];

PlanningDso _withNames(PlanningDso d, List<String> extra) {
  final held = {
    designationKey(d.id),
    designationKey(d.name),
    for (final a in d.aliases) designationKey(a),
  };
  final added = [
    for (final n in extra)
      if (held.add(designationKey(n))) n,
  ];
  // Nothing new, and the catalog's own name stays: the row is unchanged.
  if (added.isEmpty) return d;
  return PlanningDso(
    id: d.id,
    name: designationKey(d.name) == designationKey(d.id) ? added.first : d.name,
    type: d.type,
    magnitude: d.magnitude,
    raDeg: d.raDeg,
    decDeg: d.decDeg,
    sizeMajArcmin: d.sizeMajArcmin,
    sizeMinArcmin: d.sizeMinArcmin,
    posAngleDeg: d.posAngleDeg,
    surfaceBrightness: d.surfaceBrightness,
    messierNum: d.messierNum,
    caldwellNum: d.caldwellNum,
    constellation: d.constellation,
    aliases: [...d.aliases, ...added],
  );
}
