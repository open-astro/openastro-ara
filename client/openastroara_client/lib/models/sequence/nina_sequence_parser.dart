import 'sequence_node.dart';

/// Recursion cap for [parseNinaSequenceBody] — a corrupted/adversarial deep body
/// can't overflow the stack. Real NINA sequences nest ~5 levels. Exposed so the
/// test can assert the cap mechanically rather than against a hand-picked number.
const int ninaParseMaxDepth = 64;

/// Parses a §38.1 sequence body (`openastroara-sequence-v1`) into the client's
/// display [SequenceNode] tree.
///
/// The real tree bodies are NINA sequence files (the daemon stores an import's
/// body as-is, only backfilling `schemaVersion`), so this targets NINA's Json.NET
/// shape: `$type`-discriminated nodes, `$id`/`$ref` reference handling, and
/// `Items`/`Conditions`/`Triggers` collections wrapped as `{ $type, $values: [] }`.
///
/// Best-effort + defensive: unknown node types degrade to a generic container
/// (if they have children) or instruction (if they don't), `$ref` placeholders
/// are skipped, and a body with no recognizable tree yields an empty root rather
/// than throwing. Display-oriented — it captures scalar params for the editor
/// but does not interpret per-instruction semantics.
SequenceNode parseNinaSequenceBody(Map<String, dynamic> body) {
  var counter = 0;
  String nextId() => 'n${counter++}';

  // ObservableCollection serializes as { $type, $values: [...] }; a plain array
  // is also accepted. Anything else → no children.
  List<dynamic> values(dynamic v) {
    if (v is List) return v;
    if (v is Map && v[r'$values'] is List) return v[r'$values'] as List;
    return const [];
  }

  // "NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer" → "TakeExposure"
  String shortType(dynamic t) {
    if (t is! String || t.isEmpty) return '';
    final beforeComma = t.split(',').first;
    final lastDot = beforeComma.lastIndexOf('.');
    return lastDot >= 0 ? beforeComma.substring(lastDot + 1) : beforeComma;
  }

  // "CoolCamera" → "Cool Camera"; leaves already-spaced names untouched.
  String humanize(String s) =>
      s.replaceAllMapped(RegExp(r'(?<=[a-z0-9])(?=[A-Z])'), (_) => ' ');

  String? trimmedName(dynamic v) =>
      (v is String && v.trim().isNotEmpty) ? v.trim() : null;

  const structuralKeys = {
    'Items',
    'Conditions',
    'Triggers',
    'Parent',
    'Name',
    'Strategy',
    'IsExpanded',
  };

  SequenceNodeKind kindFor(String type, bool hasChildren) {
    switch (type) {
      case 'SequenceRootContainer':
        return SequenceNodeKind.root;
      case 'StartAreaContainer':
      case 'TargetAreaContainer':
      case 'EndAreaContainer':
        return SequenceNodeKind.area;
      case 'DeepSkyObjectContainer':
        return SequenceNodeKind.target;
      case 'ParallelContainer':
        return SequenceNodeKind.parallelContainer;
      case 'SequentialContainer':
        return SequenceNodeKind.sequentialContainer;
      default:
        // A node with children but no known container type (e.g. SmartExposure)
        // is shown as a generic container; a leaf is an instruction.
        return hasChildren
            ? SequenceNodeKind.sequentialContainer
            : SequenceNodeKind.instruction;
    }
  }

  // Short $type names of a Conditions/Triggers collection — kept so a container's
  // loop conditions ("run 5×") / dither triggers aren't lost (the wiring slice
  // renders them as badges). Stored in params rather than on the model to avoid
  // a SequenceNode schema change. Returns List<Object?> deliberately: the model's
  // deep-freeze turns any stored List into List<Object?>, so consumers must read
  // `params['conditionTypes'] as List` and `.cast<String>()` (never `as
  // List<String>`, which would throw on the frozen value).
  List<Object?> typeSummaries(dynamic collection) => values(collection)
      .whereType<Map>()
      .map((e) => shortType(e[r'$type']))
      .where((s) => s.isNotEmpty)
      .toList(growable: false);

  // Parses one node. [forceRoot] makes the top object a root regardless of its
  // $type, with the sequence-name fallbacks. Returns null for a $ref placeholder
  // or a non-object.
  SequenceNode? node(dynamic raw, int depth, {bool forceRoot = false}) {
    if (raw is! Map) return null;
    // A Json.NET back-reference is a pure { "$ref": "N" } object that points at an
    // already-seen node (e.g. Parent / a shared filter) — it never carries $type,
    // so this dual condition skips refs while keeping real nodes.
    if (raw[r'$ref'] != null && raw[r'$type'] == null) return null;

    final type = shortType(raw[r'$type']);
    final children = <SequenceNode>[];
    if (depth < ninaParseMaxDepth) {
      for (final c in values(raw['Items'])) {
        final n = node(c, depth + 1);
        if (n != null) children.add(n);
      }
    }

    // Scalar fields → display params (skip structural/metadata + $-prefixed keys).
    final params = <String, Object?>{};
    raw.forEach((key, value) {
      if (key is String &&
          !key.startsWith(r'$') &&
          !structuralKeys.contains(key) &&
          (value is num || value is String || value is bool)) {
        params[key] = value;
      }
    });
    final conditions = typeSummaries(raw['Conditions']);
    final triggers = typeSummaries(raw['Triggers']);
    if (conditions.isNotEmpty) params['conditionTypes'] = conditions;
    if (triggers.isNotEmpty) params['triggerTypes'] = triggers;

    return SequenceNode(
      id: raw[r'$id'] is String ? 'nina-${raw[r'$id']}' : nextId(),
      kind: forceRoot
          ? SequenceNodeKind.root
          : kindFor(type, children.isNotEmpty),
      displayName: forceRoot
          ? (trimmedName(raw['Name']) ??
              trimmedName(raw['name']) ??
              trimmedName(raw['target']) ??
              'Sequence')
          : (trimmedName(raw['Name']) ??
              (type.isEmpty ? 'Item' : humanize(type))),
      // The root is a display container, not an instruction.
      instructionType: forceRoot || type.isEmpty ? null : type,
      params: params,
      children: children,
    );
  }

  return node(body, 0, forceRoot: true) ??
      SequenceNode(
          id: 'root',
          kind: SequenceNodeKind.root,
          displayName: 'Sequence');
}
