/// §38 sequence-editor mutation engine — pure, structural operations over the
/// RAW NINA Json.NET DOM (the `$type`/`$values` map the daemon stores verbatim),
/// NOT the lossy display tree. Every op returns a NEW body; the input is never
/// mutated (the source `SequenceDetail.body` is deeply unmodifiable), so the
/// result round-trips faithfully on Save (`PATCH /{id}`).
///
/// A node is addressed by a [NodePath]: the list of child indices from the root
/// (root = `[]`, its first child = `[0]`, that child's second child = `[0,1]`).
library;

/// Child-index path from the root to a node. Empty = the root itself.
typedef NodePath = List<int>;

/// The ObservableCollection wrapper `$type` used for a container's `Items` when
/// the node being edited doesn't already carry one (e.g. a freshly-built
/// container). Matches the daemon's own templates so constructed bodies run.
const String itemsWrapperType =
    'System.Collections.ObjectModel.ObservableCollection`1[[OpenAstroAra.Sequencer.SequenceItem.ISequenceItem, OpenAstroAra.Sequencer]], System.ObjectModel';

/// The child nodes of [node] — the daemon wraps `Items` as
/// `{ $type: ObservableCollection, $values: [...] }`, but tolerate a plain array
/// or a missing/!map `Items` (a leaf instruction) → empty list. Non-map entries
/// are dropped. The returned list is a fresh, growable copy.
List<Map<String, dynamic>> childrenOf(Map<String, dynamic> node) {
  final items = node['Items'];
  final List raw;
  if (items is Map && items[r'$values'] is List) {
    raw = items[r'$values'] as List;
  } else if (items is List) {
    raw = items;
  } else {
    return <Map<String, dynamic>>[];
  }
  return raw.whereType<Map<String, dynamic>>().toList();
}

/// A copy of [node] whose `Items` holds [children] in the ObservableCollection
/// wrapper shape. Preserves the node's existing wrapper `$type` (so a NINA-
/// namespaced imported body keeps its namespace) and falls back to
/// [itemsWrapperType] for a node that had no wrapper.
Map<String, dynamic> withChildren(
    Map<String, dynamic> node, List<Map<String, dynamic>> children) {
  final existing = node['Items'];
  final wrapperType = existing is Map && existing[r'$type'] is String
      ? existing[r'$type'] as String
      : itemsWrapperType;
  return Map<String, dynamic>.of(node)
    ..['Items'] = <String, dynamic>{
      r'$type': wrapperType,
      r'$values': children,
    };
}

/// The node at [path], or null if any index is out of range. [path] `[]` → root.
Map<String, dynamic>? nodeAt(Map<String, dynamic> root, NodePath path) {
  var node = root;
  for (final i in path) {
    final kids = childrenOf(node);
    if (i < 0 || i >= kids.length) return null;
    node = kids[i];
  }
  return node;
}

/// Rebuild the spine from [node] to [path], applying [op] to the addressed node
/// and re-threading new parent maps back up. Untouched subtrees are shared (not
/// copied). Throws [RangeError] if [path] addresses a missing child.
Map<String, dynamic> _rebuild(Map<String, dynamic> node, NodePath path,
    Map<String, dynamic> Function(Map<String, dynamic>) op) {
  if (path.isEmpty) return op(node);
  final kids = childrenOf(node);
  final i = path.first;
  if (i < 0 || i >= kids.length) {
    throw RangeError('node path index $i out of range (${kids.length} children)');
  }
  kids[i] = _rebuild(kids[i], path.sublist(1), op);
  return withChildren(node, kids);
}

/// Set scalar field [key] to [value] on the node at [path]; returns a new root.
Map<String, dynamic> setField(
        Map<String, dynamic> root, NodePath path, String key, Object? value) =>
    _rebuild(root, path, (n) => Map<String, dynamic>.of(n)..[key] = value);

/// Insert [newNode] as a child of the container at [parentPath] at [index]
/// (clamped to the child count); returns a new root.
Map<String, dynamic> insertChild(Map<String, dynamic> root, NodePath parentPath,
        int index, Map<String, dynamic> newNode) =>
    _rebuild(root, parentPath, (parent) {
      final kids = childrenOf(parent);
      kids.insert(index.clamp(0, kids.length), newNode);
      return withChildren(parent, kids);
    });

/// Remove the node at [path] from its parent; returns a new root. [path] must be
/// non-empty (the root can't be removed).
Map<String, dynamic> removeAt(Map<String, dynamic> root, NodePath path) {
  if (path.isEmpty) {
    throw ArgumentError('removeAt cannot remove the root (empty path)');
  }
  final parentPath = path.sublist(0, path.length - 1);
  final index = path.last;
  return _rebuild(root, parentPath, (parent) {
    final kids = childrenOf(parent);
    if (index < 0 || index >= kids.length) {
      throw RangeError('removeAt index $index out of range');
    }
    kids.removeAt(index);
    return withChildren(parent, kids);
  });
}

/// Reorder a child of the container at [parentPath] from [oldIndex] to
/// [newIndex] (the common drag-to-reorder case); returns a new root.
Map<String, dynamic> reorderChild(Map<String, dynamic> root, NodePath parentPath,
        int oldIndex, int newIndex) =>
    _rebuild(root, parentPath, (parent) {
      final kids = childrenOf(parent);
      if (oldIndex < 0 || oldIndex >= kids.length) {
        throw RangeError('reorderChild oldIndex $oldIndex out of range');
      }
      final node = kids.removeAt(oldIndex);
      kids.insert(newIndex.clamp(0, kids.length), node);
      return withChildren(parent, kids);
    });
