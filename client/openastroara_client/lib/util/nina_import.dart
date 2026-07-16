import '../models/sequence/instruction_catalog.dart'
    show instructionCatalog;

/// §38.4 — CLIENT-side NINA import translation, ported from the daemon's
/// `NinaImportTypeNormalizer` + the import path of `ImportAsync` under the
/// 2026-07-15 PORT_DECISIONS call (the file lives on THIS machine; uploading
/// it purely so the Pi could run a JSON→JSON translation was the last
/// planning flow that died without a server). The daemon's authoritative
/// schema validator still gates the create/push — only the translation moved.
///
/// **Lossless by construction:** only `$type` strings are rewritten, and only
/// via the mechanical NINA→OpenAstroAra namespace rename (the port renamed
/// namespaces 1:1, so every NINA type name maps to its OpenAstroAra twin
/// string). Nothing is deserialized; every other property and child is
/// preserved verbatim. Types the daemon build doesn't actually have resolve
/// identically under either spelling at daemon load time — its
/// `JsonCreationConverter.ResolveType` tries both forms — so the rename never
/// changes a node's effective identity.
///
/// The "unsupported instruction" warning is computed against the CLIENT's
/// instruction catalog — the honest user-facing meaning: these nodes will
/// render as a generic fallback in the editor (they still execute if the
/// daemon knows them).
class NinaImportTranslation {
  final Map<String, dynamic> body;
  final List<String> warnings;
  const NinaImportTranslation({required this.body, required this.warnings});
}

const String _schemaVersionField = 'schemaVersion';

/// Mirrors the daemon's `SequenceSchemaValidator.SchemaVersion` — the value
/// backfilled when a raw NINA export (which never carries the field) is
/// imported, so the daemon's persist-time validator accepts it.
const String schemaVersion = 'openastroara-sequence-v1';

/// The editor-known canonical types (lazy — the catalog is a const list).
final Set<String> _editorTypes = {
  for (final def in instructionCatalog) def.type,
};

/// Translate a decoded NINA sequence export into an ARA-canonical body +
/// user-facing warnings. Pure; never throws on content (a malformed shape
/// just translates to itself).
NinaImportTranslation translateNinaSequence(Map<String, dynamic> nina) {
  final warnings = <String>[];

  // §38.4 step 3: NINA exports carry no schemaVersion; ARA-saved exports do.
  // Backfill so the daemon's persist-time validator accepts the first save.
  Map<String, dynamic> body;
  if (nina.containsKey(_schemaVersionField)) {
    body = _deepCopy(nina);
  } else {
    body = <String, dynamic>{_schemaVersionField: schemaVersion, ..._deepCopy(nina)};
    warnings.add(
        "schemaVersion was missing; backfilled to '$schemaVersion'.");
  }

  // §38.4 — normalize $type names. Collect editor-unknown sequencer types for
  // the warning (sorted, deduped — matching the daemon's SortedSet).
  final unsupported = <String>{};
  _normalizeNode(body, unsupported);
  if (unsupported.isNotEmpty) {
    final sorted = unsupported.toList()..sort();
    warnings.add(
        '${sorted.length} instruction type(s) are not yet supported and were '
        'kept as-is: ${sorted.join(', ')}.');
  }

  return NinaImportTranslation(body: body, warnings: warnings);
}

void _normalizeNode(Object? node, Set<String> unsupported) {
  if (node is Map) {
    final type = node[r'$type'];
    if (type is String) {
      final canonical = _remapNamespace(type);
      if (canonical != type) node[r'$type'] = canonical;
      if (canonical.startsWith('OpenAstroAra.Sequencer.') &&
          !_editorTypes.contains(canonical)) {
        unsupported.add(_shortName(canonical));
      } else if (canonical.startsWith('NINA.Sequencer.')) {
        unsupported.add(_shortName(canonical));
      }
    }
    for (final value in node.values) {
      _normalizeNode(value, unsupported);
    }
  } else if (node is List) {
    for (final item in node) {
      _normalizeNode(item, unsupported);
    }
  }
}

/// The three §0.5g/h/l rename pairs (class AND assembly sides), plus the
/// pre-module-split single-"NINA"-assembly fallback — mirrors the daemon's
/// `NinaTypeRemapper.RemapNamespace` + its `, NINA` special case.
String _remapNamespace(String typeString) {
  var s = typeString
      .replaceAll('NINA.Sequencer', 'OpenAstroAra.Sequencer')
      .replaceAll('NINA.Astrometry', 'OpenAstroAra.Astrometry')
      .replaceAll('NINA.Core', 'OpenAstroAra.Core');
  // Legacy single-assembly bodies: "..., NINA" → the sequencer assembly (the
  // daemon tries Sequencer/Core/Astrometry in turn; sequencer types dominate
  // sequence bodies, and either spelling resolves at daemon load time).
  if (s.endsWith(', NINA')) {
    s = '${s.substring(0, s.length - ', NINA'.length)}, OpenAstroAra.Sequencer';
  }
  return s;
}

/// Short class name for warnings — strips generic args ('[' onward) before
/// the assembly comma so a comma inside type params can't truncate the name.
String _shortName(String typeString) {
  final classSide = typeString.split('[').first.split(',').first;
  final lastDot = classSide.lastIndexOf('.');
  return lastDot >= 0 ? classSide.substring(lastDot + 1) : classSide;
}

Map<String, dynamic> _deepCopy(Map<String, dynamic> source) {
  Object? copy(Object? v) => switch (v) {
        Map<String, dynamic> m => {for (final e in m.entries) e.key: copy(e.value)},
        Map m => {for (final e in m.entries) e.key.toString(): copy(e.value)},
        List l => [for (final e in l) copy(e)],
        _ => v,
      };
  return copy(source) as Map<String, dynamic>;
}
