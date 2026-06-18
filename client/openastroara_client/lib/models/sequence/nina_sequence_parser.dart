import 'sequence_node.dart';

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

  SequenceNode? node(dynamic raw) {
    if (raw is! Map) return null;
    // A Json.NET back-reference ({ $ref: "N" }) is a pointer to an already-seen
    // object (e.g. Parent / shared filter), not a real child — skip it.
    if (raw[r'$ref'] != null && raw[r'$type'] == null) return null;

    final type = shortType(raw[r'$type']);
    final children = <SequenceNode>[];
    for (final c in values(raw['Items'])) {
      final n = node(c);
      if (n != null) children.add(n);
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

    return SequenceNode(
      id: raw[r'$id'] is String ? 'nina-${raw[r'$id']}' : nextId(),
      kind: kindFor(type, children.isNotEmpty),
      displayName: trimmedName(raw['Name']) ??
          (type.isEmpty ? 'Item' : humanize(type)),
      instructionType: type.isEmpty ? null : type,
      params: params,
      children: children,
    );
  }

  // The top level is always the root; its children come from the body's Items.
  final rootChildren = <SequenceNode>[];
  for (final c in values(body['Items'])) {
    final n = node(c);
    if (n != null) rootChildren.add(n);
  }
  return SequenceNode(
    id: trimmedName(body[r'$id']) != null ? 'nina-${body[r'$id']}' : 'root',
    kind: SequenceNodeKind.root,
    displayName: trimmedName(body['Name']) ??
        trimmedName(body['name']) ??
        trimmedName(body['target']) ??
        'Sequence',
    children: rootChildren,
  );
}
