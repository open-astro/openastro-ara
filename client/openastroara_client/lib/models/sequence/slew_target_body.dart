import '../../util/input_coordinates.dart';
import 'instruction_catalog.dart';
import 'nina_dom.dart';

/// Builds a minimal, runnable `openastroara-sequence-v1` body for "go observe
/// this target": a [sequentialContainerType] root holding a single
/// `SlewScopeToRaDec` pointed at the given coordinates. Used by the Planning
/// tab's "Add to sequence" action (§36/§25.5).
///
/// The root and the slew node are produced by the catalog's own `build()` (the
/// same shapes the palette emits), so the result round-trips through the editor
/// and the daemon identically to a hand-built sequence. The only field set
/// outside the catalog is the top-level `schemaVersion` envelope the server's
/// §38.5 validator requires.
///
/// The schema version string must match the server's
/// `SequenceSchemaValidator.SchemaVersion`.
const String sequenceSchemaVersion = 'openastroara-sequence-v1';

/// Build the sequence body that slews to [raDeg]/[decDeg] (decimal degrees).
///
/// [targetName], when non-blank, names the root container so the sequence reads
/// as the target's name in the editor and in NINA; otherwise the catalog's
/// default container name is kept.
Map<String, dynamic> buildSlewTargetBody({
  required double raDeg,
  required double decDeg,
  String? targetName,
}) {
  final slewDef = instructionForType(slewScopeToRaDecType);
  if (slewDef == null) {
    // Both consts live in instruction_catalog.dart, so this can only fire if the
    // catalog entry is removed — fail loudly rather than emit a body the daemon
    // would reject for missing its one instruction.
    throw StateError('SlewScopeToRaDec missing from the instruction catalog');
  }
  final slew = slewDef.build()
    ..['Coordinates'] = inputCoordinatesFromDeg(raDeg, decDeg);

  final containerDef = instructionForType(sequentialContainerType);
  if (containerDef == null) {
    throw StateError('SequentialContainer missing from the instruction catalog');
  }
  var root = containerDef.build();
  final name = targetName?.trim();
  if (name != null && name.isNotEmpty) {
    root = Map<String, dynamic>.of(root)..['Name'] = name;
  }
  root = withChildren(root, [slew]);
  root['schemaVersion'] = sequenceSchemaVersion;
  return root;
}
